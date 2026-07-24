using Encoder127c.Ffmpeg.Builds;
using Encoder127c.Ffmpeg.Installation;
using Encoder127c.Ffmpeg.Platform;
using Encoder127c.Ffmpeg.Validation;

namespace Encoder127c.Ffmpeg.Services;

/// <summary>Composition root for FFmpeg-related application services.</summary>
internal static class FfmpegServiceFactory
{
    private static readonly HttpClient HttpClient = FfmpegBuildCatalog.CreateHttpClient();

    public static IFfmpegManager CreateDefault()
    {
        var validator = new FfmpegValidator();
        return new FfmpegManager(
            new FfmpegPlatformResolver(),
            new FfmpegBuildCatalog(HttpClient),
            new FfmpegPackageInstaller(HttpClient, validator),
            validator);
    }
}
