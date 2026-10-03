using System.Diagnostics;

namespace Encoder127c.Encoders.Fdkaac.Validation;

internal interface IFdkaacValidator
{
    Task<bool> IsUsableAsync(string executablePath, CancellationToken cancellationToken);
    Task ValidateAsync(string executablePath, CancellationToken cancellationToken);
}

internal sealed class FdkaacValidator : IFdkaacValidator
{
    public async Task<bool> IsUsableAsync(string executablePath, CancellationToken cancellationToken)
    {
        try
        {
            await ValidateAsync(executablePath, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    public async Task ValidateAsync(string executablePath, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("--help");

        var result = await EncoderProcess.RunAsync(startInfo, "fdkaac 프로세스를 시작할 수 없습니다.", cancellationToken);
        var output = result.Output;

        if (!output.Contains("fdkaac", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("다운로드한 파일이 정상적인 fdkaac 실행 파일이 아닙니다.");
        }
    }
}
