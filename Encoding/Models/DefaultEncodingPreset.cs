namespace Encoder127c.Encoding.Models;

/// <summary>FFmpeg-equivalent defaults derived from the supplied HandBrake preset.</summary>
internal static class DefaultEncodingPreset
{
    public const string EncodingProfileDefault = "default";
    public const string EncodingProfileSaving = "saving";
    public const string DefaultEncodingProfile = EncodingProfileDefault;
    public const string VideoCodec = "libx264";
    public const string VideoTune = "animation";
    public const string VideoProfile = "high";
    public const string VideoLevel = "4.0";
    public const string VideoCrf = "28";
    public const string DeinterlaceModeAuto = "auto";
    public const string DeinterlaceModeAlways = "always";
    public const string DeinterlaceModeOff = "off";
    public const string DefaultDeinterlaceMode = DeinterlaceModeAuto;
    public const string DefaultVideoPreset = "fast";
    public const string DefaultVideoMaxBitrate = "2000k";
    public const string DefaultVideoBufferSize = "4000k";
    public const string AudioChannels = "2";
    public const string DefaultAudioGainDb = "0";
    public const string DefaultFdkaacProfile = "5";
    public const string DefaultFdkaacBitrateKbps = "64";

    public const string SavingVideoCrf = "29";
    public const string SavingVideoMaxBitrate = "900k";
    public const string SavingVideoBufferSize = "900k";
    public const string SavingFdkaacProfile = "29";
    public const string SavingFdkaacBitrateKbps = "32";
}
