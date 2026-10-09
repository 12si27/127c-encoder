using System.Globalization;
using Encoder127c.Encoding.Models;

namespace Encoder127c.Encoding.Arguments;

internal interface IFfmpegArgumentBuilder
{
    IEnumerable<string> BuildVideoOnly(ValidatedVideoEncodingRequest request, string outputPath);
    IEnumerable<string> BuildVideoAndAudioPipe(ValidatedVideoEncodingRequest request, string outputPath);
    IEnumerable<string> BuildAudioOnlyPipe(ValidatedVideoEncodingRequest request);
    IEnumerable<string> BuildAudioRemux(string audioPath, string outputPath);
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
            .. BuildInputArguments(request),
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
            "-pix_fmt", DefaultEncodingPreset.VideoPixelFormat,
            "-color_primaries", DefaultEncodingPreset.VideoColorPrimaries,
            "-color_trc", DefaultEncodingPreset.VideoColorTransfer,
            "-colorspace", DefaultEncodingPreset.VideoColorMatrix,
            "-color_range", DefaultEncodingPreset.VideoColorRange,
            "-fps_mode", "vfr",
            .. BuildOutputDurationArguments(request),
            "-progress", "pipe:2",
            .. BuildVideoFilterArguments(request, isSavingProfile),
            outputPath
        ];
    }

    public IEnumerable<string> BuildVideoAndAudioPipe(ValidatedVideoEncodingRequest request, string outputPath) =>
    [
        .. BuildVideoOnly(request, outputPath),
        .. BuildAudioPipeOutput(request)
    ];

    public IEnumerable<string> BuildAudioOnlyPipe(ValidatedVideoEncodingRequest request) =>
    [
        "-hide_banner",
        "-nostats",
        "-n",
        .. BuildInputArguments(request),
        "-progress", "pipe:2",
        .. BuildAudioPipeOutput(request)
    ];

    private static IEnumerable<string> BuildAudioPipeOutput(ValidatedVideoEncodingRequest request) =>
    [
        "-map", $"0:a:{request.AudioStreamIndex}",
        "-vn",
        "-ac", DefaultEncodingPreset.AudioChannels,
        "-af", BuildAudioFilter(request),
        .. BuildOutputDurationArguments(request),
        "-c:a", "pcm_s16le",
        "-f", "caf",
        "pipe:1"
    ];

    private static IEnumerable<string> BuildInputArguments(ValidatedVideoEncodingRequest request)
    {
        if (request.TrimStartSeconds <= 0)
        {
            return ["-i", request.InputPath];
        }

        return
        [
            "-ss", request.TrimStartSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            "-i", request.InputPath
        ];
    }

    private static IEnumerable<string> BuildOutputDurationArguments(ValidatedVideoEncodingRequest request) =>
        request.TrimmedDurationSeconds is > 0
            ? ["-t", request.TrimmedDurationSeconds.Value.ToString("0.###", CultureInfo.InvariantCulture)]
            : [];

    public IEnumerable<string> BuildAudioRemux(string audioPath, string outputPath) =>
    [
        "-hide_banner",
        "-nostats",
        "-n",
        "-i", audioPath,
        "-map", "0:a:0",
        "-vn",
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

        var colorFilter = request.ColorConversion switch
        {
            VideoColorConversion.None => null,
            VideoColorConversion.FullRangeToLimited => "scale=in_range=pc:out_range=tv",
            VideoColorConversion.SdrToBt709 =>
                "zscale=p=bt709:t=bt709:m=bt709:r=tv:d=error_diffusion,format=yuv420p",
            VideoColorConversion.HdrToBt709 =>
                "zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709," +
                "tonemap=tonemap=hable:desat=0," +
                "zscale=t=bt709:m=bt709:r=tv:d=error_diffusion,format=yuv420p",
            _ => throw new InvalidOperationException("지원하지 않는 색 공간 변환 옵션입니다.")
        };
        var scaleFilter = isSavingProfile ? "scale=-2:min(720\\,ih)" : null;
        var combinedFilter = string.Join(',', new[] { filter, colorFilter, scaleFilter }.Where(value => value is not null));
        return string.IsNullOrEmpty(combinedFilter) ? [] : ["-vf", combinedFilter];
    }

    private static string BuildAudioFilter(ValidatedVideoEncodingRequest request)
    {
        var gainFilter = $"volume={request.AudioGainDb}dB:precision=float";
        return request.DynamicAudioNormalization
            ? $"{AudioFilterDefaults.Downmix},dynaudnorm,{gainFilter}"
            : $"{AudioFilterDefaults.Downmix},{gainFilter}";
    }
}
