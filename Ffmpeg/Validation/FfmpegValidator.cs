using System.Diagnostics;

namespace Encoder127c.Ffmpeg.Validation;

internal interface IFfmpegValidator
{
    Task<bool> IsUsableAsync(string executablePath, CancellationToken cancellationToken);
    Task ValidateAsync(string executablePath, CancellationToken cancellationToken);
}

/// <summary>Checks that FFmpeg can start and supports the codecs exposed by the UI.</summary>
internal sealed class FfmpegValidator : IFfmpegValidator
{
    private static readonly string[] RequiredEncoders = ["libx264", "aac"];
    private static readonly string[] RequiredFilters = ["bwdif", "dynaudnorm", "volume"];

    public async Task<bool> IsUsableAsync(string executablePath, CancellationToken cancellationToken)
    {
        try
        {
            await ValidateAsync(executablePath, cancellationToken);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task ValidateAsync(string executablePath, CancellationToken cancellationToken)
    {
        var version = await RunAsync(executablePath, ["-version"], cancellationToken);
        if (version.ExitCode != 0 || !version.Output.Contains("ffmpeg version", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("다운로드한 파일이 정상적인 FFmpeg 실행 파일이 아닙니다.");
        }

        var encoders = await RunAsync(executablePath, ["-hide_banner", "-encoders"], cancellationToken);
        if (encoders.ExitCode != 0 || RequiredEncoders.Any(encoder =>
                !encoders.Output.Contains(encoder, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("다운로드한 FFmpeg 빌드에 필요한 인코더가 없습니다.");
        }

        var filters = await RunAsync(executablePath, ["-hide_banner", "-filters"], cancellationToken);
        if (filters.ExitCode != 0 || RequiredFilters.Any(filter =>
                !filters.Output.Contains(filter, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("다운로드한 FFmpeg 빌드에 필요한 오디오 또는 비디오 필터가 없습니다.");
        }
    }

    private static async Task<ProcessResult> RunAsync(
        string executablePath,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("FFmpeg 프로세스를 시작할 수 없습니다.");
        var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new ProcessResult(process.ExitCode, await standardOutputTask + await standardErrorTask);
    }

    private sealed record ProcessResult(int ExitCode, string Output);
}
