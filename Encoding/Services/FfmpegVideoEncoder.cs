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
    bool IsCompleted);

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

        var fdkaacExecutable = await fdkaacManager.EnsureAvailableAsync(
            new Progress<string>(message => logProgress?.Report($"[fdkaac 준비] {message}")),
            cancellationToken);

        var isSavingProfile = request.EncodingProfile == DefaultEncodingPreset.EncodingProfileSaving;
        var fdkaacProfile = isSavingProfile
            ? DefaultEncodingPreset.SavingFdkaacProfile
            : DefaultEncodingPreset.DefaultFdkaacProfile;
        var fdkaacBitrate = isSavingProfile
            ? DefaultEncodingPreset.SavingFdkaacBitrateKbps
            : DefaultEncodingPreset.DefaultFdkaacBitrateKbps;

        logProgress?.Report(
            isSavingProfile
                ? $"[오디오] HE-AAC v2 {fdkaacBitrate} kbps"
                : $"[오디오] HE-AAC v1 {fdkaacBitrate} kbps");

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
                argumentBuilder.BuildVideoOnly(request, videoPath),
                logProgress,
                encodingProgress,
                cancellationToken);
            AppendLog(log, videoResult.Log);
            if (videoResult.ExitCode != 0)
            {
                return new VideoEncodingResult(videoResult.ExitCode, log.ToString().Trim());
            }

            logProgress?.Report("[오디오] PCM 파이프 → fdkaac 인코딩");
            var audioResult = await EncodeAudioAsync(
                ffmpegExecutable,
                fdkaacExecutable,
                request,
                audioPath,
                fdkaacProfile,
                fdkaacBitrate,
                logProgress,
                cancellationToken);
            AppendLog(log, audioResult.Log);
            if (audioResult.ExitCode != 0)
            {
                return new VideoEncodingResult(audioResult.ExitCode, log.ToString().Trim());
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

    private async Task<VideoEncodingResult> EncodeAudioAsync(
        string ffmpegExecutable,
        string fdkaacExecutable,
        ValidatedVideoEncodingRequest request,
        string outputPath,
        string profile,
        string bitrateKbps,
        IProgress<string>? logProgress,
        CancellationToken cancellationToken)
    {
        var ffmpegStartInfo = CreateStartInfo(
            ffmpegExecutable,
            argumentBuilder.BuildAudioPipe(request),
            redirectStandardOutput: true);
        var fdkaacStartInfo = CreateStartInfo(
            fdkaacExecutable,
            [
                "-p", profile,
                "-b", bitrateKbps,
                "-S",
                "-",
                "-o", outputPath
            ],
            redirectStandardInput: true);

        using var ffmpeg = Process.Start(ffmpegStartInfo)
            ?? throw new InvalidOperationException("오디오용 FFmpeg 프로세스를 시작할 수 없습니다.");
        using var fdkaac = Process.Start(fdkaacStartInfo)
            ?? throw new InvalidOperationException("fdkaac 프로세스를 시작할 수 없습니다.");

        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            TryKill(ffmpeg);
            TryKill(fdkaac);
        });

        var log = new StringBuilder();
        var ffmpegErrorTask = ReadLinesAsync(
            ffmpeg.StandardError, "ffmpeg", log, logProgress, cancellationToken);
        var fdkaacOutputTask = ReadLinesAsync(
            fdkaac.StandardOutput, "fdkaac", log, logProgress, cancellationToken);
        var fdkaacErrorTask = ReadLinesAsync(
            fdkaac.StandardError, "fdkaac", log, logProgress, cancellationToken);

        try
        {
            await ffmpeg.StandardOutput.BaseStream.CopyToAsync(
                fdkaac.StandardInput.BaseStream,
                cancellationToken);
        }
        finally
        {
            fdkaac.StandardInput.Close();
        }

        await Task.WhenAll(
            ffmpeg.WaitForExitAsync(cancellationToken),
            fdkaac.WaitForExitAsync(cancellationToken),
            ffmpegErrorTask,
            fdkaacOutputTask,
            fdkaacErrorTask);

        var exitCode = ffmpeg.ExitCode != 0 ? ffmpeg.ExitCode : fdkaac.ExitCode;
        return new VideoEncodingResult(exitCode, log.ToString().Trim());
    }

    private async Task<VideoEncodingResult> RunFfmpegWithProgressAsync(
        string ffmpegExecutable,
        IEnumerable<string> arguments,
        IProgress<string>? logProgress,
        IProgress<EncodingProgress>? encodingProgress,
        CancellationToken cancellationToken)
    {
        var startInfo = CreateStartInfo(ffmpegExecutable, arguments, redirectStandardOutput: true);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("FFmpeg 프로세스를 시작할 수 없습니다.");
        using var cancellationRegistration = cancellationToken.Register(() => TryKill(process));

        var log = new StringBuilder();
        long totalDurationTicks = 0;
        var standardErrorTask = ReadStandardErrorAsync();
        var standardOutputTask = ReadProgressAsync();

        await Task.WhenAll(standardErrorTask, standardOutputTask);
        await process.WaitForExitAsync(cancellationToken);
        return new VideoEncodingResult(process.ExitCode, log.ToString().Trim());

        async Task ReadStandardErrorAsync()
        {
            while (await process.StandardError.ReadLineAsync(cancellationToken) is { } line)
            {
                log.AppendLine(line);
                logProgress?.Report(line);

                var durationMatch = DurationPattern.Match(line);
                if (durationMatch.Success && TimeSpan.TryParse(
                        durationMatch.Groups["duration"].Value,
                        CultureInfo.InvariantCulture,
                        out var duration))
                {
                    Interlocked.Exchange(ref totalDurationTicks, duration.Ticks);
                }
            }
        }

        async Task ReadProgressAsync()
        {
            var processedDuration = TimeSpan.Zero;
            double? speed = null;

            while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
            {
                var separatorIndex = line.IndexOf('=');
                if (separatorIndex < 1)
                {
                    continue;
                }

                var key = line[..separatorIndex];
                var value = line[(separatorIndex + 1)..];
                switch (key)
                {
                    case "out_time_us" when long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds):
                        processedDuration = TimeSpan.FromTicks(microseconds * 10);
                        break;
                    case "speed":
                        speed = ParseSpeed(value);
                        break;
                    case "progress":
                        var totalTicks = Interlocked.Read(ref totalDurationTicks);
                        encodingProgress?.Report(new EncodingProgress(
                            totalTicks > 0 ? TimeSpan.FromTicks(totalTicks) : null,
                            processedDuration,
                            speed,
                            value == "end"));
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
            log.Append('[').Append(source).Append("] ").AppendLine(line);
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
