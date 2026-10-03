using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Encoder127c.Encoders;

namespace Encoder127c.Encoding.Services;

internal sealed record VideoMediaInfo(TimeSpan Duration, double FrameRate);

/// <summary>Reads lightweight media metadata and preview frames through the managed FFmpeg executable.</summary>
internal static class VideoPreviewReader
{
    private static readonly Regex DurationPattern = new(
        @"Duration:\s*(?<duration>\d{2,}:\d{2}:\d{2}(?:\.\d+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex FrameRatePattern = new(
        @"^\s*Stream #0:\d+.*Video:.*?(?<fps>\d+(?:\.\d+)?) fps",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    public static async Task<VideoMediaInfo> ReadInfoAsync(
        string ffmpegExecutable,
        string inputPath,
        CancellationToken cancellationToken = default)
    {
        var description = await ReadDescriptionAsync(ffmpegExecutable, inputPath, cancellationToken);
        var durationMatch = DurationPattern.Match(description);
        if (!durationMatch.Success ||
            !TimeSpan.TryParse(durationMatch.Groups["duration"].Value, CultureInfo.InvariantCulture, out var duration) ||
            duration <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("비디오 길이를 확인할 수 없습니다.");
        }

        var frameRate = 30d;
        var frameRateMatch = FrameRatePattern.Match(description);
        if (frameRateMatch.Success &&
            double.TryParse(frameRateMatch.Groups["fps"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedFrameRate) &&
            double.IsFinite(parsedFrameRate) &&
            parsedFrameRate > 0)
        {
            frameRate = parsedFrameRate;
        }

        return new VideoMediaInfo(duration, frameRate);
    }

    public static async Task<MemoryStream> ReadFrameAsync(
        string ffmpegExecutable,
        string inputPath,
        TimeSpan position,
        CancellationToken cancellationToken = default)
    {
        var seconds = Math.Max(0, position.TotalSeconds).ToString("0.###", CultureInfo.InvariantCulture);
        var startInfo = CreateStartInfo(ffmpegExecutable,
        [
            "-hide_banner",
            "-loglevel", "error",
            "-nostdin",
            "-ss", seconds,
            "-i", inputPath,
            "-map", "0:v:0",
            "-frames:v", "1",
            "-vf", "scale=640:360:force_original_aspect_ratio=decrease",
            "-f", "image2pipe",
            "-vcodec", "png",
            "pipe:1"
        ]);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("미리보기용 FFmpeg 프로세스를 시작할 수 없습니다.");
        using var registration = cancellationToken.Register(() => TryKill(process));

        var frame = new MemoryStream();
        try
        {
            var outputTask = process.StandardOutput.BaseStream.CopyToAsync(frame, cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync();
            await Task.WhenAll(process.WaitForExitAsync(cancellationToken), outputTask, errorTask);
            cancellationToken.ThrowIfCancellationRequested();

            if (process.ExitCode != 0 || frame.Length == 0)
            {
                var error = await errorTask;
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                    ? "미리보기 프레임을 읽지 못했습니다."
                    : $"미리보기 프레임을 읽지 못했습니다: {error.Trim()}");
            }

            frame.Position = 0;
            return frame;
        }
        catch
        {
            frame.Dispose();
            throw;
        }
    }

    private static async Task<string> ReadDescriptionAsync(
        string ffmpegExecutable,
        string inputPath,
        CancellationToken cancellationToken)
    {
        var startInfo = CreateStartInfo(ffmpegExecutable,
        [
            "-hide_banner",
            "-nostdin",
            "-i", inputPath
        ]);

        var result = await EncoderProcess.RunAsync(startInfo, "비디오 정보를 확인할 수 없습니다.", cancellationToken);
        var description = result.StandardError;
        if (!description.Contains("Input #0,", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("비디오 정보를 읽지 못했습니다. 입력 파일을 확인하세요.");
        }

        return description;
    }

    private static ProcessStartInfo CreateStartInfo(string executable, IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }
}
