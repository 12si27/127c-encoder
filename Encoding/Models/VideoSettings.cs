namespace Encoder127c.Encoding.Models;

/// <summary>Overrides held by a queue item, separate from saved application settings.</summary>
public sealed record VideoSettings(
    VideoOutputSettings? Output = null,
    VideoGainSettings? Gain = null,
    int? AudioStreamIndex = null,
    bool FallbackToDefaultAudioStream = false,
    string? VideoPreset = null,
    string? DeinterlaceMode = null,
    VideoBitrateSettings? Bitrate = null,
    string? EncodingProfile = null)
{
    public bool HasOverrides => Output is not null || Gain is not null || AudioStreamIndex is not null
        || VideoPreset is not null || DeinterlaceMode is not null || Bitrate is not null || EncodingProfile is not null;
}

public sealed record VideoOutputSettings(string Directory, bool UseSourceDirectory)
{
    public string ResolveDirectory(string inputPath) => UseSourceDirectory
        ? Path.GetDirectoryName(Path.GetFullPath(inputPath)) ?? string.Empty
        : Directory;
}

public sealed record VideoGainSettings(decimal GainDb, bool DynamicNormalization);

public sealed record VideoBitrateSettings(decimal MaxBitrate, decimal BufferSize);
