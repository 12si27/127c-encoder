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
        return
        [
            "-hide_banner",
            "-nostats",
            "-y",
            "-i", request.InputPath,
            "-map", "0:v:0",
            "-map", "0:a:0?",
            "-c:v", DefaultEncodingPreset.VideoCodec,
            "-preset", request.VideoPreset,
            "-tune", DefaultEncodingPreset.VideoTune,
            "-profile:v", DefaultEncodingPreset.VideoProfile,
            "-level:v", DefaultEncodingPreset.VideoLevel,
            "-crf", DefaultEncodingPreset.VideoCrf,
            "-maxrate", request.VideoMaxBitrate,
            "-bufsize", request.VideoBufferSize,
            "-vf", DefaultEncodingPreset.VideoFilter,
            "-pix_fmt", "yuv420p",
            "-fps_mode", "vfr",
            "-c:a", DefaultEncodingPreset.AudioCodec,
            "-b:a", DefaultEncodingPreset.AudioBitrate,
            "-ac", DefaultEncodingPreset.AudioChannels,
            "-af", BuildAudioFilter(request),
            "-movflags", "+faststart",
            "-progress", "pipe:1",
            request.OutputPath
        ];
    }

    private static string BuildAudioFilter(ValidatedVideoEncodingRequest request)
    {
        var gainFilter = $"volume={request.AudioGainDb}dB";
        return request.DynamicAudioNormalization
            ? $"dynaudnorm,{gainFilter}"
            : gainFilter;
    }
}
