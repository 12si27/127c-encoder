namespace Encoder127c.Encoding.Models;

/// <summary>FFmpeg-equivalent defaults derived from the supplied HandBrake preset.</summary>
internal static class DefaultEncodingPreset
{
    public const string VideoCodec = "libx264";
    public const string VideoTune = "animation";
    public const string VideoProfile = "high";
    public const string VideoLevel = "4.0";
    public const string VideoCrf = "28";
    public const string VideoFilter = "bwdif=mode=send_frame:deint=interlaced";
    public const string DefaultVideoPreset = "fast";
    public const string DefaultVideoMaxBitrate = "2000k";
    public const string DefaultVideoBufferSize = "4000k";
    public const string AudioCodec = "libopus";
    public const string AudioBitrate = "64k";
    public const string AudioChannels = "2";
    public const string DefaultAudioGainDb = "0";
}
