using Encoder127c.Encoding.Models;

namespace Encoder127c.Encoding.Arguments;

internal interface IFfmpegArgumentBuilder
{
    IEnumerable<string> Build(ValidatedVideoEncodingRequest request);
}

/// <summary>Translates a validated application request into FFmpeg CLI arguments.</summary>
internal sealed class FfmpegArgumentBuilder : IFfmpegArgumentBuilder
{
    public IEnumerable<string> Build(ValidatedVideoEncodingRequest request)
    {
        var isSavingProfile = request.EncodingProfile == DefaultEncodingPreset.EncodingProfileSaving;
        var audioArguments = isSavingProfile
            ? new[] { "-c:a", DefaultEncodingPreset.SavingAudioCodec, "-b:a", DefaultEncodingPreset.SavingAudioBitrate, "-vbr", DefaultEncodingPreset.SavingAudioVbr, "-compression_level", DefaultEncodingPreset.SavingAudioCompressionLevel }
            : new[] { "-c:a", DefaultEncodingPreset.AudioCodec, "-profile:a", DefaultEncodingPreset.AudioProfile, "-b:a", DefaultEncodingPreset.AudioBitrate };

        return
        [
            "-hide_banner",
            "-nostats",
            "-n",
            "-i", request.InputPath,
            "-map", "0:v:0",
            "-map", "0:a:0?",
            "-c:v", DefaultEncodingPreset.VideoCodec,
            "-preset", request.VideoPreset,
            "-tune", DefaultEncodingPreset.VideoTune,
            "-profile:v", DefaultEncodingPreset.VideoProfile,
            "-level:v", DefaultEncodingPreset.VideoLevel,
            "-crf", isSavingProfile ? DefaultEncodingPreset.SavingVideoCrf : DefaultEncodingPreset.VideoCrf,
            "-maxrate", request.VideoMaxBitrate,
            "-bufsize", request.VideoBufferSize,
            "-pix_fmt", "yuv420p",
            "-fps_mode", "vfr",
            .. audioArguments,
            "-ac", DefaultEncodingPreset.AudioChannels,
            "-af", BuildAudioFilter(request),
            "-movflags", "+faststart",
            "-metadata", "encoder=127c-encoder",
            "-progress", "pipe:1",
            .. BuildVideoFilterArguments(request, isSavingProfile),
            request.OutputPath
        ];
    }

    private static IEnumerable<string> BuildVideoFilterArguments(ValidatedVideoEncodingRequest request, bool isSavingProfile)
    {
        var filter = request.DeinterlaceMode switch
        {
            DefaultEncodingPreset.DeinterlaceModeAuto => "bwdif=mode=send_frame:deint=interlaced",
            DefaultEncodingPreset.DeinterlaceModeAlways => "bwdif=mode=send_frame:deint=all",
            DefaultEncodingPreset.DeinterlaceModeOff => null,
            _ => throw new InvalidOperationException("지원하지 않는 디인터레이싱 옵션입니다.")
        };

        var scaleFilter = isSavingProfile ? "scale=-2:min(720\\,ih)" : null;
        var combinedFilter = string.Join(',', new[] { filter, scaleFilter }.Where(value => value is not null));
        return string.IsNullOrEmpty(combinedFilter) ? [] : ["-vf", combinedFilter];
    }

    private static string BuildAudioFilter(ValidatedVideoEncodingRequest request)
    {
        var gainFilter = $"volume={request.AudioGainDb}dB";
        return request.DynamicAudioNormalization
            ? $"dynaudnorm,{gainFilter}"
            : gainFilter;
    }
}
