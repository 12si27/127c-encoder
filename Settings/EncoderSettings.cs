namespace Encoder127c.Settings;

internal sealed class EncoderSettings
{
    public string? OutputDirectory { get; init; }
    public string? VideoPreset { get; init; }
    public string? DeinterlaceMode { get; init; }
    public decimal? VideoMaxBitrate { get; init; }
    public decimal? VideoBufferSize { get; init; }
    public decimal? AudioGain { get; init; }
    public bool DynamicAudioNormalization { get; init; }
}
