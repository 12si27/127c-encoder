using Encoder127c.Fdkaac.Builds;
using Encoder127c.Fdkaac.Installation;
using Encoder127c.Fdkaac.Platform;
using Encoder127c.Fdkaac.Validation;
using Encoder127c.Settings;

namespace Encoder127c.Fdkaac.Services;

internal interface IFdkaacManager
{
    Task<string?> FindAvailableExecutableAsync(CancellationToken cancellationToken = default);

    Task<string> EnsureAvailableAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}

internal sealed class FdkaacManager(
    IFdkaacPlatformResolver platformResolver,
    IFdkaacBuildCatalog buildCatalog,
    IFdkaacPackageInstaller packageInstaller,
    IFdkaacValidator validator) : IFdkaacManager
{
    private readonly SemaphoreSlim provisioningLock = new(1, 1);

    public async Task<string?> FindAvailableExecutableAsync(CancellationToken cancellationToken = default)
    {
        var bundled = GetBundledMacExecutable();
        if (bundled is not null && await validator.IsUsableAsync(bundled, cancellationToken))
        {
            return bundled;
        }

        var platform = platformResolver.Resolve();
        var installationDirectory = GetInstallationDirectory(platform.Id);
        var executablePath = Path.Combine(installationDirectory, platform.ExecutableName);

        await provisioningLock.WaitAsync(cancellationToken);
        try
        {
            return File.Exists(executablePath) && await validator.IsUsableAsync(executablePath, cancellationToken)
                ? executablePath
                : null;
        }
        finally
        {
            provisioningLock.Release();
        }
    }

    public async Task<string> EnsureAvailableAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var bundled = GetBundledMacExecutable();
        if (bundled is not null && await validator.IsUsableAsync(bundled, cancellationToken))
        {
            return bundled;
        }

        var platform = platformResolver.Resolve();
        var installationDirectory = GetInstallationDirectory(platform.Id);
        var executablePath = Path.Combine(installationDirectory, platform.ExecutableName);

        await provisioningLock.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(executablePath) && await validator.IsUsableAsync(executablePath, cancellationToken))
            {
                return executablePath;
            }

            progress?.Report($"{platform.Id}용 fdkaac 빌드 정보를 확인하는 중...");
            var build = await buildCatalog.ResolveAsync(platform, cancellationToken);
            return await packageInstaller.InstallAsync(
                build,
                platform,
                installationDirectory,
                progress,
                cancellationToken);
        }
        finally
        {
            provisioningLock.Release();
        }
    }

    private static string GetInstallationDirectory(string platformId)
    {
        return Path.Combine(AppPaths.DataDirectory, "encoder", platformId, "fdkaac");
    }

    private static string? GetBundledMacExecutable()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return null;
        }

        // The bundle's executable lives in Contents/MacOS; fdkaac is a signed
        // resource that is shipped alongside the app, not downloaded into it.
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "Resources", "encoder", "fdkaac"));
        return File.Exists(path) ? path : null;
    }
}
