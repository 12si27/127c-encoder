using System.Runtime.InteropServices;
using Encoder127c.Ffmpeg.Models;

namespace Encoder127c.Ffmpeg.Platform;

internal interface IFfmpegPlatformResolver
{
    FfmpegPlatform Resolve();
}

internal sealed class FfmpegPlatformResolver : IFfmpegPlatformResolver
{
    public FfmpegPlatform Resolve()
    {
        var architecture = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => FfmpegArchitecture.X64,
            Architecture.Arm64 => FfmpegArchitecture.Arm64,
            _ => throw new PlatformNotSupportedException(
                $"지원하지 않는 CPU 아키텍처입니다: {RuntimeInformation.OSArchitecture}. " +
                "Windows, Linux, macOS의 x64 및 ARM64만 지원합니다.")
        };

        var operatingSystem = OperatingSystem.IsWindows() ? FfmpegOperatingSystem.Windows
            : OperatingSystem.IsLinux() ? FfmpegOperatingSystem.Linux
            : OperatingSystem.IsMacOS() ? FfmpegOperatingSystem.MacOS
            : throw new PlatformNotSupportedException(
                "Windows, Linux, macOS에서만 FFmpeg 자동 설치를 지원합니다.");

        var osName = operatingSystem switch
        {
            FfmpegOperatingSystem.Windows => "win",
            FfmpegOperatingSystem.Linux => "linux",
            FfmpegOperatingSystem.MacOS => "macos",
            _ => throw new InvalidOperationException("알 수 없는 운영 체제입니다.")
        };
        var architectureName = architecture == FfmpegArchitecture.X64 ? "x64" : "arm64";
        var executableName = operatingSystem == FfmpegOperatingSystem.Windows ? "ffmpeg.exe" : "ffmpeg";

        return new FfmpegPlatform($"{osName}-{architectureName}", executableName, operatingSystem, architecture);
    }
}
