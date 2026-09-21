using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Encoder127c.Encoding.Arguments;
using Encoder127c.Encoding.Models;
using Encoder127c.Fdkaac.Services;

namespace Encoder127c.Encoding.Services;

internal interface IVideoEncoder
{
    Task<VideoEncodingResult> EncodeAsync(
        string ffmpegExecutable,
        ValidatedVideoEncodingRequest request,
        IProgress<string>? logProgress = null,
        IProgress<EncodingProgress>? encodingProgress = null,
        CancellationToken cancellationToken = default);
}

internal sealed record VideoEncodingResult(int ExitCode, string Log);

/// <summary>Current FFmpeg encoding position and its reported processing speed.</summary>
internal sealed record EncodingProgress(
    TimeSpan? TotalDuration,
    TimeSpan ProcessedDuration,
    double? Speed,
    bool IsCompleted,
    string? Stage = null);

/// <summary>Runs the FFmpeg + fdkaac encoding pipeline for an already validated request.</summary>
internal sealed class FfmpegVideoEncoder(
    IFfmpegArgumentBuilder argumentBuilder,
    IFdkaacManager fdkaacManager) : IVideoEncoder
{
    private static readonly Regex DurationPattern = new(
        @"Duration:\s*(?<duration>\d{2}:\d{2}:\d{2}(?:\.\d+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public async Task<VideoEncodingResult> EncodeAsync(
        string ffmpegExecutable,
        ValidatedVideoEncodingRequest request,
        IProgress<string>? logProgress = null,
        IProgress<EncodingProgress>? encodingProgress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(request.OutputPath)!);

        var hasAudioStream = await HasAudioStreamAsync(
            ffmpegExecutable,
            request.InputPath,
            cancellationToken);

        string? fdkaacExecutable = null;
        string? fdkaacProfile = null;
        string? fdkaacBitrate = null;

        if (hasAudioStream)
        {
            fdkaacExecutable = await fdkaacManager.EnsureAvailableAsync(
                new Progress<string>(message => logProgress?.Report($"[fdkaac 준비] {message}")),
                cancellationToken);

            var isSavingProfile = request.EncodingProfile == DefaultEncodingPreset.EncodingProfileSaving;
            fdkaacProfile = isSavingProfile
                ? DefaultEncodingPreset.SavingFdkaacProfile
                : DefaultEncodingPreset.DefaultFdkaacProfile;
            fdkaacBitrate = isSavingProfile
                ? DefaultEncodingPreset.SavingFdkaacBitrateKbps
                : DefaultEncodingPreset.DefaultFdkaacBitrateKbps;

            logProgress?.Report(
                isSavingProfile
                    ? $"[오디오] HE-AAC v2 {fdkaacBitrate} kbps"
                    : $"[오디오] HE-AAC v1 {fdkaacBitrate} kbps");
        }
        else
        {
            logProgress?.Report("[오디오] 오디오 스트림 없음, 오디오 인코딩을 건너뜁니다.");
        }

        var workingDirectory = Path.Combine(
            Path.GetDirectoryName(request.OutputPath)!,
            $".127c-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workingDirectory);

        var videoPath = Path.Combine(workingDirectory, "video.mp4");
        var audioPath = Path.Combine(workingDirectory, "audio.m4a");
        var log = new StringBuilder();
        var completed = false;

        try
        {
            var videoResult = await RunFfmpegWithProgressAsync(
                ffmpegExecutable,
                hasAudioStream
                    ? argumentBuilder.BuildVideoAndAudioPipe(request, videoPath)
                    : argumentBuilder.BuildVideoOnly(request, videoPath),
                logProgress,
                encodingProgress,
                cancellationToken,
                fdkaacExecutable,
                hasAudioStream
                    ? ["-p", fdkaacProfile!, "-b", fdkaacBitrate!, "-S", "-", "-o", audioPath]
                    : null);
            AppendLog(log, videoResult.Log);
            if (videoResult.ExitCode != 0)
            {
                return new VideoEncodingResult(videoResult.ExitCode, log.ToString().Trim());
            }

            encodingProgress?.Report(new EncodingProgress(null, TimeSpan.Zero, null, true, "MP4 파일 마무리 중..."));
            if (!hasAudioStream)
            {
                logProgress?.Report("[리먹싱] 비디오 출력을 마무리하는 중...");
                var videoRemuxResult = await RunProcessAsync(
                    ffmpegExecutable,
                    argumentBuilder.BuildVideoRemux(videoPath, request.OutputPath),
                    logProgress,
                    cancellationToken);
                AppendLog(log, videoRemuxResult.Log);
                completed = videoRemuxResult.ExitCode == 0;
                return new VideoEncodingResult(videoRemuxResult.ExitCode, log.ToString().Trim());
            }

            logProgress?.Report("[리먹싱] 비디오와 오디오를 합치는 중...");
            var remuxResult = await RunProcessAsync(
                ffmpegExecutable,
                argumentBuilder.BuildRemux(videoPath, audioPath, request.OutputPath),
                logProgress,
                cancellationToken);
            AppendLog(log, remuxResult.Log);
            completed = remuxResult.ExitCode == 0;
            return new VideoEncodingResult(remuxResult.ExitCode, log.ToString().Trim());
        }
        finally
        {
            if (!completed)
            {
                TryDelete(request.OutputPath);
            }

            try
            {
                Directory.Delete(workingDirectory, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logProgress?.Report($"[정리] 임시 파일을 지우지 못했습니다: {workingDirectory}");
            }
        }
    }

    private static async Task<bool> HasAudioStreamAsync(
        string ffmpegExecutable,
        string inputPath,
        CancellationToken cancellationToken)
    {
        var startInfo = CreateStartInfo(
            ffmpegExecutable,
            [
                "-hide_banner",
                "-v", "error",
                "-i", inputPath,
                "-map", "0:a:0",
                "-t", "0",
                "-f", "null",
                "-"
            ]);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("오디오 스트림 확인용 FFmpeg 프로세스를 시작할 수 없습니다.");
        using var cancellationRegistration = cancellationToken.Register(() => TryKill(process));

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await Task.WhenAll(
            process.WaitForExitAsync(cancellationToken),
            outputTask,
            errorTask);

        if (process.ExitCode == 0)
        {
            return true;
        }

        var error = await errorTask;
        if (error.Contains("matches no streams", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(error)
                ? $"오디오 스트림 확인 중 FFmpeg가 종료 코드 {process.ExitCode}로 종료되었습니다."
                : $"오디오 스트림 확인 실패: {error.Trim()}");
    }

    private async Task<VideoEncodingResult> RunFfmpegWithProgressAsync(
        string ffmpegExecutable,
        IEnumerable<string> arguments,
        IProgress<string>? logProgress,
        IProgress<EncodingProgress>? encodingProgress,
        CancellationToken cancellationToken,
        string? fdkaacExecutable = null,
        IEnumerable<string>? fdkaacArguments = null)
    {
        using var process = new Process { StartInfo = CreateStartInfo(ffmpegExecutable, arguments) };
        using var audio = fdkaacExecutable is null ? null : new Process
        {
            StartInfo = CreateStartInfo(fdkaacExecutable, fdkaacArguments!, redirectStandardInput: true)
        };
        var log = new StringBuilder();
        var tasks = new List<Task>();
        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            TryKill(process);
            if (audio is not null) TryKill(audio);
        });

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (audio is not null)
            {
                audio.Start();
                tasks.Add(ReadLinesAsync(audio.StandardOutput, "fdkaac", log, logProgress, cancellationToken));
                tasks.Add(ReadLinesAsync(audio.StandardError, "fdkaac", log, logProgress, cancellationToken));
                logProgress?.Report("[오디오] PCM 파이프 → fdkaac 동시 인코딩");
            }
            process.Start();
            cancellationToken.ThrowIfCancellationRequested();
            tasks.Add(ReadProgressAsync());
            tasks.Add(CopyOutputAsync());
            tasks.Add(process.WaitForExitAsync(cancellationToken));
            if (audio is not null) tasks.Add(WatchAudioAsync());
            await Task.WhenAll(tasks);
            var exitCode = audio is not null && audio.ExitCode != 0 ? audio.ExitCode : process.ExitCode;
            return new VideoEncodingResult(exitCode, log.ToString().Trim());
        }
        finally
        {
            TryKill(process);
            if (audio is not null) TryKill(audio);
            // 프로세스를 회수한 뒤 임시 파일을 정리합니다.
            try { await process.WaitForExitAsync(); } catch (InvalidOperationException) { }
            if (audio is not null)
            {
                try { await audio.WaitForExitAsync(); } catch (InvalidOperationException) { }
            }
            try { await Task.WhenAll(tasks); } catch { }
        }

        async Task WatchAudioAsync()
        {
            await audio!.WaitForExitAsync(cancellationToken);
            if (audio.ExitCode != 0) TryKill(process);
        }

        async Task CopyOutputAsync()
        {
            try
            {
                await process.StandardOutput.BaseStream.CopyToAsync(
                    audio?.StandardInput.BaseStream ?? Stream.Null, cancellationToken);
            }
            catch
            {
                TryKill(process);
                if (audio is not null) TryKill(audio);
                throw;
            }
            finally
            {
                if (audio is not null) audio.StandardInput.Close();
            }
        }

        async Task ReadProgressAsync()
        {
            TimeSpan? totalDuration = null;
            var processedDuration = TimeSpan.Zero;
            double? speed = null;
            while (await process.StandardError.ReadLineAsync(cancellationToken) is { } line)
            {
                var durationMatch = DurationPattern.Match(line);
                if (durationMatch.Success && TimeSpan.TryParse(
                    durationMatch.Groups["duration"].Value, CultureInfo.InvariantCulture, out var duration))
                {
                    totalDuration = duration;
                }

                var separatorIndex = line.IndexOf('=');
                var key = separatorIndex > 0 ? line[..separatorIndex] : string.Empty;
                var value = separatorIndex > 0 ? line[(separatorIndex + 1)..] : string.Empty;
                switch (key)
                {
                    case "out_time_us" when long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds):
                        processedDuration = TimeSpan.FromTicks(microseconds * 10);
                        break;
                    case "speed":
                        speed = ParseSpeed(value);
                        break;
                    case "progress":
                        encodingProgress?.Report(new EncodingProgress(
                            totalDuration, processedDuration, speed, value == "end"));
                        break;
                    default:
                        lock (log) log.AppendLine(line);
                        logProgress?.Report(line);
                        break;
                }
            }
        }
    }
    private static async Task<VideoEncodingResult> RunProcessAsync(
        string executable,
        IEnumerable<string> arguments,
        IProgress<string>? logProgress,
        CancellationToken cancellationToken)
    {
        var startInfo = CreateStartInfo(executable, arguments);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"{executable} 프로세스를 시작할 수 없습니다.");
        using var cancellationRegistration = cancellationToken.Register(() => TryKill(process));

        var log = new StringBuilder();
        var outputTask = ReadLinesAsync(
            process.StandardOutput, "ffmpeg", log, logProgress, cancellationToken);
        var errorTask = ReadLinesAsync(
            process.StandardError, "ffmpeg", log, logProgress, cancellationToken);

        await Task.WhenAll(process.WaitForExitAsync(cancellationToken), outputTask, errorTask);
        return new VideoEncodingResult(process.ExitCode, log.ToString().Trim());
    }

    private static ProcessStartInfo CreateStartInfo(
        string executable,
        IEnumerable<string> arguments,
        bool redirectStandardOutput = true,
        bool redirectStandardInput = false)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = redirectStandardInput,
            RedirectStandardOutput = redirectStandardOutput,
            RedirectStandardError = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static async Task ReadLinesAsync(
        StreamReader reader,
        string source,
        StringBuilder log,
        IProgress<string>? logProgress,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            lock (log) log.Append('[').Append(source).Append("] ").AppendLine(line);
            logProgress?.Report($"[{source}] {line}");
        }
    }

    private static void AppendLog(StringBuilder builder, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            builder.AppendLine(value);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static double? ParseSpeed(string value)
    {
        var normalizedValue = value.Trim().TrimEnd('x');
        return double.TryParse(normalizedValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed)
            && speed > 0
            ? speed
            : null;
    }
}
