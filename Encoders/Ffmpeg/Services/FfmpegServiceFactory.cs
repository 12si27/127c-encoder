using Encoder127c.Encoders.Ffmpeg.Builds;
using Encoder127c.Encoders.Ffmpeg.Installation;
using Encoder127c.Encoders.Ffmpeg.Platform;
using Encoder127c.Encoders.Ffmpeg.Validation;

namespace Encoder127c.Encoders.Ffmpeg.Services;

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
