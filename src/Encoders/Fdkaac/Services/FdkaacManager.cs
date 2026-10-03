using System.Runtime.InteropServices;
using Encoder127c.Encoders.Fdkaac.Installation;
using Encoder127c.Encoders.Fdkaac.Validation;
using Encoder127c.Settings;

namespace Encoder127c.Encoders.Fdkaac.Services;

internal interface IFdkaacManager
{
    Task<string?> FindAvailableExecutableAsync(CancellationToken cancellationToken = default);

    Task<string> EnsureAvailableAsync(
        CancellationToken cancellationToken = default,
        IProgress<string>? progress = null);
}

internal sealed class FdkaacManager(FdkaacPackageInstaller packageInstaller, IFdkaacValidator validator) : IFdkaacManager
{
    private readonly SemaphoreSlim provisioningLock = new(1, 1);

    public async Task<string?> FindAvailableExecutableAsync(CancellationToken cancellationToken = default)
    {
        var path = GetExecutablePath();
        await provisioningLock.WaitAsync(cancellationToken);
        try
        {
            return File.Exists(path) && await validator.IsUsableAsync(path, cancellationToken) ? path : null;
        }
        finally
        {
            provisioningLock.Release();
        }
    }

    public async Task<string> EnsureAvailableAsync(
        CancellationToken cancellationToken = default,
        IProgress<string>? progress = null)
    {
        var path = GetExecutablePath();
        await provisioningLock.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(path) && await validator.IsUsableAsync(path, cancellationToken))
            {
                return path;
            }

            return await packageInstaller.InstallAsync(path, GetRuntimeIdentifier(), cancellationToken, progress);
        }
        finally
        {
            provisioningLock.Release();
        }
    }

    private static string GetRuntimeIdentifier()
    {
        var os = OperatingSystem.IsWindows() ? "win"
            : OperatingSystem.IsMacOS() ? "osx"
            : OperatingSystem.IsLinux() ? "linux"
            : throw new PlatformNotSupportedException("지원하지 않는 운영체제입니다.");
        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException("fdkaac는 x64 또는 ARM64에서 사용할 수 있습니다.")
        };
        return $"{os}-{architecture}";
    }

    private static string GetExecutablePath() => Path.Combine(
        AppPaths.DataDirectory, "encoder", GetRuntimeIdentifier(), "fdkaac",
        OperatingSystem.IsWindows() ? "fdkaac.exe" : "fdkaac");
}
