using Encoder127c.Encoding.Models;
using Encoder127c.Encoding.Validation;

namespace Encoder127c.Encoding.Services;

/// <summary>Runs queued files sequentially and owns cancellation of the current batch.</summary>
internal sealed class EncodingQueueRunner(
    IVideoEncodingRequestValidator requestValidator,
    IVideoEncoder encoder)
{
    private CancellationTokenSource? _encodingCancellation;
    private CancellationTokenSource? _finishEarly;

    public object? CurrentRun => _finishEarly;
    public bool IsStopping => _encodingCancellation?.IsCancellationRequested == true
        || _finishEarly?.IsCancellationRequested == true;
    public event Action<string>? StatusChanged;
    public event Action<EncodingQueueItem, int, string>? ItemStarted;
    public event Action<EncodingProgress, int>? ProgressChanged;

    public void Stop(bool savePartial = false)
    {
        if (savePartial) _finishEarly?.Cancel();
        else _encodingCancellation?.Cancel();
    }

    public async Task<string> RunAsync(
        string ffmpegExecutable,
        List<EncodingQueueItem> queue,
        List<EncodingQueueItem> filesToEncode,
        Func<EncodingQueueItem, VideoEncodingRequest> createRequest,
        IProgress<string> log,
        Func<Task> prepareBatch)
    {
        if (_encodingCancellation is not null)
            throw new InvalidOperationException("An encoding batch is already running.");
        _encodingCancellation = new CancellationTokenSource();
        var completedCount = queue.Count(item => item.Status == EncodingQueueStatus.Completed);
        string? finalStatus = null;
        try
        {
            await prepareBatch();
            foreach (var item in filesToEncode)
            {
                _encodingCancellation.Token.ThrowIfCancellationRequested();
                var itemNumber = queue.IndexOf(item) + 1;
                var validation = requestValidator.Validate(createRequest(item));
                if (!validation.IsValid)
                {
                    item.Status = EncodingQueueStatus.Failed;
                    StatusChanged?.Invoke(FormatEncodingStatus(itemNumber, queue.Count, "인코딩 실패", item.FileName));
                    log.Report($"[오류] {item.FileName}: {validation.ErrorMessage}");
                    continue;
                }

                var request = validation.Request!;
                if (!TryCheckOutputDirectoryWritable(request.OutputPath, out var writeErrorMessage))
                {
                    item.Status = EncodingQueueStatus.Failed;
                    StatusChanged?.Invoke(FormatEncodingStatus(itemNumber, queue.Count, "인코딩 실패", item.FileName));
                    log.Report($"[오류] {item.FileName}: {writeErrorMessage}");
                    continue;
                }

                var currentFinishEarly = new CancellationTokenSource();
                _finishEarly = currentFinishEarly;
                item.BeginEncoding(request.OutputPath);
                var startMessage = FormatEncodingStatus(itemNumber, queue.Count, "인코딩 시작", item.FileName);
                StatusChanged?.Invoke(startMessage);
                log.Report($"[시작] {item.FileName} → {Path.GetFileName(request.OutputPath)}");
                ItemStarted?.Invoke(item, itemNumber, startMessage);

                try
                {
                    var result = await encoder.EncodeAsync(
                        ffmpegExecutable,
                        request,
                        log,
                        new Progress<EncodingProgress>(progress =>
                        {
                            if (_finishEarly == currentFinishEarly) ProgressChanged?.Invoke(progress, itemNumber);
                        }),
                        _encodingCancellation.Token,
                        _finishEarly.Token);

                    if (result.ExitCode == 0)
                    {
                        item.Status = _finishEarly.IsCancellationRequested
                            ? EncodingQueueStatus.Stopped
                            : EncodingQueueStatus.Completed;
                        if (!_finishEarly.IsCancellationRequested) completedCount++;
                        StatusChanged?.Invoke(FormatEncodingStatus(itemNumber, queue.Count, _finishEarly.IsCancellationRequested ? "부분 저장 완료" : "인코딩 완료", item.FileName));
                        log.Report($"[{(_finishEarly.IsCancellationRequested ? "부분 저장" : "완료")}] {item.FileName} → {Path.GetFileName(request.OutputPath)}");
                    }
                    else
                    {
                        item.Status = EncodingQueueStatus.Failed;
                        StatusChanged?.Invoke(FormatEncodingStatus(itemNumber, queue.Count, "인코딩 실패", item.FileName));
                        log.Report($"[오류] {item.FileName}: ffmpeg 종료 코드 {result.ExitCode}");
                    }
                }
                catch (OperationCanceledException) when (_encodingCancellation.IsCancellationRequested)
                {
                    item.Status = EncodingQueueStatus.Stopped;
                    throw;
                }
                catch (Exception exception)
                {
                    item.Status = EncodingQueueStatus.Failed;
                    StatusChanged?.Invoke(FormatEncodingStatus(itemNumber, queue.Count, "인코딩 실패", item.FileName));
                    log.Report($"[오류] {item.FileName}: {exception.Message}");
                }
                if (_finishEarly.IsCancellationRequested)
                {
                    finalStatus = $"[{DateTime.Now:HH:mm:ss}] 인코딩 중지: {item.FileName} {(item.Status == EncodingQueueStatus.Stopped ? "부분 저장 완료" : "부분 저장 실패")}";
                    break;
                }
                _finishEarly.Dispose();
                _finishEarly = null;
            }

            finalStatus ??= $"[{DateTime.Now:HH:mm:ss}] 전체 인코딩 완료: {completedCount}/{queue.Count}개";
        }
        catch (OperationCanceledException) when (_encodingCancellation.IsCancellationRequested)
        {
            finalStatus = $"[{DateTime.Now:HH:mm:ss}] 인코딩 중지: 완료된 {completedCount}개 파일은 다음 실행에서 건너뜁니다.";
            log.Report("[중지] 현재 인코딩을 즉시 중단했습니다.");
        }
        finally
        {
            _finishEarly?.Dispose();
            _finishEarly = null;
            _encodingCancellation.Dispose();
            _encodingCancellation = null;
        }
        return finalStatus!;
    }

    private static bool TryCheckOutputDirectoryWritable(string outputPath, out string errorMessage)
    {
        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            errorMessage = "출력 폴더에 파일을 쓸 수 없습니다.";
            return false;
        }

        var probeCreated = false;
        try
        {
            using (var stream = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.WriteByte(0);
            }

            probeCreated = true;
            File.Delete(outputPath);
            probeCreated = false;
            errorMessage = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is
            ArgumentException or
            NotSupportedException or
            PathTooLongException or
            UnauthorizedAccessException or
            IOException or
            System.Security.SecurityException)
        {
            if (probeCreated)
            {
                try
                {
                    File.Delete(outputPath);
                }
                catch (Exception cleanupException) when (cleanupException is
                    ArgumentException or
                    NotSupportedException or
                    PathTooLongException or
                    UnauthorizedAccessException or
                    IOException or
                    System.Security.SecurityException)
                {
                    // Leave the original write error as the result for this item.
                }
            }

            errorMessage = "출력 폴더에 파일을 쓸 수 없습니다.";
            return false;
        }
    }

    private static string FormatEncodingStatus(int itemNumber, int totalCount, string action, string fileName) =>
        $"[{DateTime.Now:HH:mm:ss}] {itemNumber}/{totalCount} {action}: {fileName}";

}
