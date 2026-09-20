using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Encoder127c.Fdkaac.Models;

namespace Encoder127c.Fdkaac.Builds;

internal interface IFdkaacBuildCatalog
{
    Task<FdkaacBuild> ResolveAsync(FdkaacPlatform platform, CancellationToken cancellationToken);
}

internal sealed class FdkaacBuildCatalog(HttpClient httpClient) : IFdkaacBuildCatalog
{
    private const string ReleaseApi =
        "https://api.github.com/repos/pdjdev/127c-encoder/releases/tags/deps-fdkaac-v1";

    public async Task<FdkaacBuild> ResolveAsync(FdkaacPlatform platform, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(ReleaseApi, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var release = await JsonSerializer.DeserializeAsync<Release>(stream, cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("fdkaac 릴리스 정보를 읽을 수 없습니다.");

        var extension = platform.Id.StartsWith("win-", StringComparison.Ordinal) ? ".zip" : ".tar.gz";
        var name = $"fdkaac-{platform.Id}{extension}";
        var asset = release.Assets.FirstOrDefault(item =>
            string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase) &&
            item.Digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true)
            ?? throw new InvalidOperationException($"{platform.Id}용 fdkaac 빌드를 찾을 수 없습니다.");

        return new FdkaacBuild(
            new Uri(asset.BrowserDownloadUrl),
            asset.Digest!["sha256:".Length..]);
    }

    internal static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("127c-encoder", "1.0"));
        return client;
    }

    private sealed class Release
    {
        [JsonPropertyName("assets")]
        public List<Asset> Assets { get; init; } = [];
    }

    private sealed class Asset
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; init; } = string.Empty;

        [JsonPropertyName("digest")]
        public string? Digest { get; init; }
    }
}
