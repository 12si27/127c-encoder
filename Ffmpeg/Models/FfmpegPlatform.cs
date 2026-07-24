namespace Encoder127c.Ffmpeg.Models;

internal enum FfmpegOperatingSystem
{
    Windows,
    Linux,
    MacOS
}

internal enum FfmpegArchitecture
{
    X64,
    Arm64
}

internal sealed record FfmpegPlatform(
    string Id,
    string ExecutableName,
    FfmpegOperatingSystem OperatingSystem,
    FfmpegArchitecture Architecture);
