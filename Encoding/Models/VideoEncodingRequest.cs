namespace Encoder127c.Encoding.Models;

/// <summary>Raw values captured from the encoding form.</summary>
internal sealed record VideoEncodingRequest(
    string InputPath,
    string OutputDirectory,
    string EncodingProfile,
    string VideoPreset,
    string VideoMaxBitrate,
    string VideoBufferSize,
    string DeinterlaceMode,
    string AudioGainDb,
    bool DynamicAudioNormalization);

/// <summary>A normalized, validated request that is safe to send to FFmpeg.</summary>
internal sealed record ValidatedVideoEncodingRequest(
    string InputPath,
    string OutputPath,
    string EncodingProfile,
    string VideoPreset,
    string VideoMaxBitrate,
    string VideoBufferSize,
    string DeinterlaceMode,
    string AudioGainDb,
    bool DynamicAudioNormalization);
