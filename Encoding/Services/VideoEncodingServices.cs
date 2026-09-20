using Encoder127c.Encoding.Arguments;
using Encoder127c.Encoding.Validation;
using Encoder127c.Fdkaac.Services;

namespace Encoder127c.Encoding.Services;

/// <summary>Small composition root for validation and encoding services.</summary>
internal sealed record VideoEncodingServices(
    IVideoEncodingRequestValidator RequestValidator,
    IVideoEncoder Encoder,
    IFdkaacManager FdkaacManager)
{
    public static VideoEncodingServices CreateDefault()
    {
        var fdkaacManager = FdkaacServiceFactory.CreateDefault();
        return new VideoEncodingServices(
            new VideoEncodingRequestValidator(),
            new FfmpegVideoEncoder(
                new FfmpegArgumentBuilder(),
                fdkaacManager),
            fdkaacManager);
    }
}
