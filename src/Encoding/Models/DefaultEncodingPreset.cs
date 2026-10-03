using System.Globalization;

namespace Encoder127c.Encoding.Models;

/// <summary>FFmpeg-equivalent defaults derived from the supplied HandBrake preset.</summary>
internal static class DefaultEncodingPreset
{
    public const string EncodingProfileDefault = "default";
    public const string EncodingProfileSaving = "saving";
    public const string EncodingProfileAudioOnly = "audio-only";
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
    public const string DefaultVideoPreset = "slow";
    public static string DefaultVideoMaxBitrate => FormatBitrate(DefaultBitrate.MaxBitrate);
    public static string DefaultVideoBufferSize => FormatBitrate(DefaultBitrate.BufferSize);
    public const string AudioChannels = "2";
    public const string DefaultAudioGainDb = "0";
    public const string DefaultFdkaacProfile = "5";
    public const string DefaultFdkaacBitrateKbps = "64";

    public static readonly VideoBitrateSettings DefaultBitrate = new(2000, 4000);
    public static readonly VideoBitrateSettings SavingBitrate = new(900, 900);

    public static string FormatBitrate(decimal value) => $"{value.ToString("0", CultureInfo.InvariantCulture)}k";

    public static VideoBitrateSettings ResolveBitrate(string profile, VideoBitrateSettings bitrate) =>
        profile == EncodingProfileSaving ? SavingBitrate : bitrate;

    public const string SavingVideoCrf = "29";
    public static string SavingVideoMaxBitrate => FormatBitrate(SavingBitrate.MaxBitrate);
    public static string SavingVideoBufferSize => FormatBitrate(SavingBitrate.BufferSize);
    public const string SavingFdkaacProfile = "29";
    public const string SavingFdkaacBitrateKbps = "32";
}
