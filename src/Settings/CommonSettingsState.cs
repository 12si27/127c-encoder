using Encoder127c.Encoding.Models;
using Encoder127c.Encoding.Services;

namespace Encoder127c.Settings;

/// <summary>Keeps common defaults and validates saved values independently of UI controls.</summary>
internal sealed class CommonSettingsState
{
    public decimal DefaultMaxBitrate { get; private set; } = DefaultEncodingPreset.DefaultBitrate.MaxBitrate;
    public decimal DefaultBufferSize { get; private set; } = DefaultEncodingPreset.DefaultBitrate.BufferSize;
    public AudioGainOptions AudioGainOptions { get; set; } = new();

    public void Reset()
    {
        DefaultMaxBitrate = DefaultEncodingPreset.DefaultBitrate.MaxBitrate;
        DefaultBufferSize = DefaultEncodingPreset.DefaultBitrate.BufferSize;
        AudioGainOptions = new();
    }

    public void CaptureBitrates(decimal? maxBitrate, decimal? bufferSize)
    {
        DefaultMaxBitrate = ClampToRange(maxBitrate, 1, 1_000_000, DefaultEncodingPreset.DefaultBitrate.MaxBitrate);
        DefaultBufferSize = ClampToRange(bufferSize, 1, 1_000_000, DefaultEncodingPreset.DefaultBitrate.BufferSize);
    }

    public void Restore(EncoderSettings settings)
    {
        var isDefault = (settings.EncodingProfile ?? DefaultEncodingPreset.DefaultEncodingProfile)
            == DefaultEncodingPreset.EncodingProfileDefault;
        CaptureBitrates(
            settings.DefaultVideoMaxBitrate ?? (isDefault ? settings.VideoMaxBitrate : null),
            settings.DefaultVideoBufferSize ?? (isDefault ? settings.VideoBufferSize : null));
        if (settings.AudioGainAnalysis is { } options)
        {
            try
            {
                options.Validate();
                AudioGainOptions = options;
            }
            catch (ArgumentException)
            {
                // Keep current defaults when saved analysis conditions are invalid.
            }
        }
    }

    public static decimal ClampToRange(decimal? value, decimal minimum, decimal maximum, decimal fallback) =>
        value is decimal number && number >= minimum && number <= maximum ? number : fallback;
}
