using Encoder127c.Fdkaac.Builds;
using Encoder127c.Fdkaac.Installation;
using Encoder127c.Fdkaac.Platform;
using Encoder127c.Fdkaac.Validation;

namespace Encoder127c.Fdkaac.Services;

internal interface IFdkaacManager
{
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

    public async Task<string> EnsureAvailableAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var platform = platformResolver.Resolve();
        var installationDirectory = Path.Combine(AppContext.BaseDirectory, "fdkaac", platform.Id);
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
}
