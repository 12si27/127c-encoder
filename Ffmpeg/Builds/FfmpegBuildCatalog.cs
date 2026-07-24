using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Encoder127c.Ffmpeg.Models;

namespace Encoder127c.Ffmpeg.Builds;

internal interface IFfmpegBuildCatalog
{
    Task<FfmpegBuild> ResolveAsync(FfmpegPlatform platform, CancellationToken cancellationToken);
}

/// <summary>Resolves an approved FFmpeg build for the current operating system and architecture.</summary>
internal sealed class FfmpegBuildCatalog(HttpClient httpClient) : IFfmpegBuildCatalog
{
    private const string BtbNLatestReleaseApi =
        "https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/tags/latest";

    // Martin Riedl's signed/notarized macOS release builds (FFmpeg 8.1.2).
    // Update each URL and its hash together when approving a new macOS build.
    private static readonly FfmpegBuild MacOsX64 = new(
        new Uri("https://ffmpeg.martin-riedl.de/download/macos/amd64/1783018342_8.1.2/ffmpeg.zip"),
        "a52ef43883f44c219766d4b3bdde4e635b35465d0b704c01c3a0566b59775df9");

    private static readonly FfmpegBuild MacOsArm64 = new(
        new Uri("https://ffmpeg.martin-riedl.de/download/macos/arm64/1783011502_8.1.2/ffmpeg.zip"),
        "ef1aa60006c7b77ce170c1608c08d8e4ba1c30c5746f2ac986ded932d0ac2c3c");

    public async Task<FfmpegBuild> ResolveAsync(FfmpegPlatform platform, CancellationToken cancellationToken)
    {
        if (platform.OperatingSystem == FfmpegOperatingSystem.MacOS)
        {
            return platform.Architecture == FfmpegArchitecture.Arm64 ? MacOsArm64 : MacOsX64;
        }

        var target = platform.OperatingSystem == FfmpegOperatingSystem.Windows
            ? platform.Architecture == FfmpegArchitecture.Arm64 ? "winarm64" : "win64"
            : platform.Architecture == FfmpegArchitecture.Arm64 ? "linuxarm64" : "linux64";
        var archiveExtension = platform.OperatingSystem == FfmpegOperatingSystem.Windows ? ".zip" : ".tar.xz";
        return await ResolveBtbNReleaseAsync(target, archiveExtension, cancellationToken);
    }

    private async Task<FfmpegBuild> ResolveBtbNReleaseAsync(
        string target,
        string archiveExtension,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(BtbNLatestReleaseApi, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var release = await JsonSerializer.DeserializeAsync<BtbNRelease>(responseStream, cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("BtbN 릴리스 정보를 읽을 수 없습니다.");

        var pattern = new Regex(
            $"^ffmpeg-n(?<version>[0-9]+\\.[0-9]+)-latest-{Regex.Escape(target)}-gpl-[0-9]+\\.[0-9]+{Regex.Escape(archiveExtension)}$",
            RegexOptions.CultureInvariant);

        var asset = release.Assets
            .Select(item => new { Asset = item, Match = pattern.Match(item.Name) })
            .Where(item => item.Match.Success && item.Asset.Digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true)
            .Select(item => new
            {
                item.Asset,
                Version = Version.Parse($"{item.Match.Groups["version"].Value}.0")
            })
            .OrderByDescending(item => item.Version)
            .FirstOrDefault()?.Asset
            ?? throw new InvalidOperationException(
                $"BtbN에서 {target}용 최신 안정 GPL FFmpeg 빌드를 찾을 수 없습니다.");

        return new FfmpegBuild(
            new Uri(asset.BrowserDownloadUrl),
            asset.Digest!["sha256:".Length..]);
    }

    internal static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("127c-encoder", "1.0"));
        return client;
    }

    private sealed class BtbNRelease
    {
        [JsonPropertyName("assets")]
        public List<BtbNAsset> Assets { get; init; } = [];
    }

    private sealed class BtbNAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; init; } = string.Empty;

        [JsonPropertyName("digest")]
        public string? Digest { get; init; }
    }
}
