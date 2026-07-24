using Encoder127c.Encoding.Arguments;
using Encoder127c.Encoding.Validation;

namespace Encoder127c.Encoding.Services;

/// <summary>Small composition root for validation and FFmpeg encoding services.</summary>
internal sealed record VideoEncodingServices(
    IVideoEncodingRequestValidator RequestValidator,
    IVideoEncoder Encoder)
{
    public static VideoEncodingServices CreateDefault() => new(
        new VideoEncodingRequestValidator(),
        new FfmpegVideoEncoder(new FfmpegArgumentBuilder()));
}
