using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Encoder127c.Encoding.Arguments;
using Encoder127c.Encoding.Models;

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

/// <summary>Runs the managed FFmpeg executable for an already validated encoding request.</summary>
internal sealed class FfmpegVideoEncoder(IFfmpegArgumentBuilder argumentBuilder) : IVideoEncoder
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

        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegExecutable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        foreach (var argument in argumentBuilder.Build(request))
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("FFmpeg 프로세스를 시작할 수 없습니다.");
        using var cancellationRegistration = cancellationToken.Register(() =>
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
                // The process exited between the check and the termination request.
            }
        });

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

    private static double? ParseSpeed(string value)
    {
        var normalizedValue = value.Trim().TrimEnd('x');
        return double.TryParse(normalizedValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed)
            && speed > 0
            ? speed
            : null;
    }
}
