using System.Globalization;
using Encoder127c.Encoding.Models;

namespace Encoder127c.Encoding.Services;

/// <summary>Resolves per-file overrides against the current common settings.</summary>
internal static class VideoEncodingRequestFactory
{
    public static VideoEncodingRequest Create(string inputPath, VideoSettings common, VideoSettings? overrides)
    {
        var profile = overrides?.EncodingProfile ?? common.EncodingProfile ?? DefaultEncodingPreset.DefaultEncodingProfile;
        var output = overrides?.Output ?? common.Output
            ?? throw new ArgumentException("Common output settings are required.", nameof(common));
        var gain = overrides?.Gain ?? common.Gain ?? new VideoGainSettings(0, true);
        var bitrate = DefaultEncodingPreset.ResolveBitrate(profile,
            overrides?.Bitrate ?? common.Bitrate ?? DefaultEncodingPreset.DefaultBitrate);
        return new VideoEncodingRequest(
            inputPath,
            output.ResolveDirectory(inputPath),
            profile,
            overrides?.VideoPreset ?? common.VideoPreset ?? DefaultEncodingPreset.DefaultVideoPreset,
            DefaultEncodingPreset.FormatBitrate(bitrate.MaxBitrate),
            DefaultEncodingPreset.FormatBitrate(bitrate.BufferSize),
            overrides?.DeinterlaceMode ?? common.DeinterlaceMode ?? DefaultEncodingPreset.DefaultDeinterlaceMode,
            gain.GainDb.ToString("0", CultureInfo.InvariantCulture),
            gain.DynamicNormalization,
            overrides?.AudioStreamIndex ?? 0,
            overrides?.FallbackToDefaultAudioStream ?? false,
            overrides?.Trim?.StartSeconds ?? 0,
            overrides?.Trim?.EndSeconds ?? 0);
    }
}
