using System.Diagnostics;

namespace Encoder127c.Encoders;

internal sealed record EncoderProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public string Output => StandardOutput + StandardError;
}

/// <summary>Runs a text-output encoder command and drains both streams before returning.</summary>
internal static class EncoderProcess
{
    public static async Task<EncoderProcessResult> RunAsync(
        ProcessStartInfo startInfo, string startError, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(startError);
        using var registration = cancellationToken.Register(() => TryKill(process));
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await Task.WhenAll(process.WaitForExitAsync(), stdout, stderr);
        cancellationToken.ThrowIfCancellationRequested();
        return new EncoderProcessResult(process.ExitCode, await stdout, await stderr);
    }

    public static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
}
