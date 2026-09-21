using Encoder127c.Ffmpeg.Builds;
using Encoder127c.Ffmpeg.Installation;
using Encoder127c.Ffmpeg.Platform;
using Encoder127c.Ffmpeg.Validation;

namespace Encoder127c.Ffmpeg.Services;

internal interface IFfmpegManager
{
    Task<string?> FindAvailableExecutableAsync(CancellationToken cancellationToken = default);

    Task<string> EnsureAvailableAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Application-level workflow for reusing or installing the managed FFmpeg executable.</summary>
internal sealed class FfmpegManager(
    IFfmpegPlatformResolver platformResolver,
    IFfmpegBuildCatalog buildCatalog,
    IFfmpegPackageInstaller packageInstaller,
    IFfmpegValidator validator) : IFfmpegManager
{
    private readonly SemaphoreSlim provisioningLock = new(1, 1);

    public async Task<string?> FindAvailableExecutableAsync(CancellationToken cancellationToken = default)
    {
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

            progress?.Report($"{platform.Id}용 FFmpeg 안정 빌드 정보를 확인하는 중...");
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
        return Path.Combine(AppContext.BaseDirectory, "encoder", platformId, "ffmpeg");
    }
}
