using Encoder127c.Encoding.Models;

namespace Encoder127c.Encoding.Arguments;

internal interface IFfmpegArgumentBuilder
{
    IEnumerable<string> BuildVideoOnly(ValidatedVideoEncodingRequest request, string outputPath);
    IEnumerable<string> BuildVideoAndAudioPipe(ValidatedVideoEncodingRequest request, string outputPath);
    IEnumerable<string> BuildVideoRemux(string videoPath, string outputPath);
    IEnumerable<string> BuildRemux(string videoPath, string audioPath, string outputPath);
}

/// <summary>Translates a validated application request into FFmpeg CLI arguments.</summary>
internal sealed class FfmpegArgumentBuilder : IFfmpegArgumentBuilder
{
    public IEnumerable<string> BuildVideoOnly(ValidatedVideoEncodingRequest request, string outputPath)
    {
        var isSavingProfile = request.EncodingProfile == DefaultEncodingPreset.EncodingProfileSaving;

        return
        [
            "-hide_banner",
            "-nostats",
            "-n",
            "-i", request.InputPath,
            "-map", "0:v:0",
            "-an",
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
            "-progress", "pipe:2",
            .. BuildVideoFilterArguments(request, isSavingProfile),
            outputPath
        ];
    }

    public IEnumerable<string> BuildVideoAndAudioPipe(ValidatedVideoEncodingRequest request, string outputPath) =>
    [
        .. BuildVideoOnly(request, outputPath),
        "-map", "0:a:0",
        "-vn",
        "-ac", DefaultEncodingPreset.AudioChannels,
        "-af", BuildAudioFilter(request),
        "-c:a", "pcm_s16le",
        "-f", "caf",
        "pipe:1"
    ];

    public IEnumerable<string> BuildVideoRemux(string videoPath, string outputPath) =>
    [
        "-hide_banner",
        "-nostats",
        "-n",
        "-i", videoPath,
        "-map", "0:v:0",
        "-c", "copy",
        "-movflags", "+faststart",
        "-map_metadata", "-1",
        "-map_metadata:s", "-1",
        "-map_chapters", "-1",
        "-fflags", "+bitexact",
        "-metadata", "encoder=127c-encoder",
        "-metadata", "description=Encoded with 127c-encoder",
        outputPath
    ];

    public IEnumerable<string> BuildRemux(string videoPath, string audioPath, string outputPath) =>
    [
        "-hide_banner",
        "-nostats",
        "-n",
        "-i", videoPath,
        "-i", audioPath,
        "-map", "0:v:0",
        "-map", "1:a:0",
        "-c", "copy",
        "-movflags", "+faststart",
        "-map_metadata", "-1",
        "-map_metadata:s", "-1",
        "-map_chapters", "-1",
        "-fflags", "+bitexact",
        "-metadata", "encoder=127c-encoder",
        "-metadata", "description=Encoded with 127c-encoder",
        outputPath
    ];

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
