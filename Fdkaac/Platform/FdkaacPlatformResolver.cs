using System.Runtime.InteropServices;
using Encoder127c.Fdkaac.Models;

namespace Encoder127c.Fdkaac.Platform;

internal interface IFdkaacPlatformResolver
{
    FdkaacPlatform Resolve();
}

internal sealed class FdkaacPlatformResolver : IFdkaacPlatformResolver
{
    public FdkaacPlatform Resolve()
    {
        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException($"지원하지 않는 CPU 아키텍처입니다: {RuntimeInformation.OSArchitecture}.")
        };

        var os = OperatingSystem.IsWindows() ? "win"
            : OperatingSystem.IsLinux() ? "linux"
            : throw new PlatformNotSupportedException("fdkaac 자동 설치는 Windows와 Linux에서 지원합니다.");

        return new FdkaacPlatform(
            $"{os}-{arch}",
            OperatingSystem.IsWindows() ? "fdkaac.exe" : "fdkaac");
    }
}
