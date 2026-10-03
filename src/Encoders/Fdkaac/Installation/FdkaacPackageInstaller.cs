using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Encoder127c.Encoders.Fdkaac.Validation;

namespace Encoder127c.Encoders.Fdkaac.Installation;

/// <summary>Downloads, verifies and installs fdkaac together with its notice file.</summary>
internal sealed class FdkaacPackageInstaller(HttpClient httpClient, IFdkaacValidator validator)
{
    private const string ReleaseApi =
        "https://api.github.com/repos/12si27/127c-encoder/releases/tags/deps-fdkaac-v1";

    public async Task<string> InstallAsync(string path, string rid,
        CancellationToken cancellationToken, IProgress<string>? progress)
    {
        progress?.Report("fdkaac 다운로드 정보를 확인하는 중...");
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleaseApi);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var metadata = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var release = await JsonDocument.ParseAsync(metadata, cancellationToken: cancellationToken);
        var assetName = $"fdkaac-{rid}{(OperatingSystem.IsWindows() ? ".zip" : ".tar.gz")}";
        var asset = release.RootElement.GetProperty("assets").EnumerateArray()
            .FirstOrDefault(item => item.GetProperty("name").GetString() == assetName);
        if (asset.ValueKind == JsonValueKind.Undefined)
        {
            throw new FileNotFoundException($"fdkaac 릴리스에 {assetName} 파일이 없습니다.");
        }

        var digest = asset.GetProperty("digest").GetString();
        if (digest is null || !digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.Length != 71)
        {
            throw new InvalidDataException("fdkaac 파일의 SHA-256 정보를 확인할 수 없습니다.");
        }

        progress?.Report($"fdkaac 다운로드: {assetName}");
        var archive = await httpClient.GetByteArrayAsync(
            asset.GetProperty("browser_download_url").GetString()!, cancellationToken);
        var actualHash = Convert.ToHexString(SHA256.HashData(archive));
        if (!string.Equals(actualHash, digest[7..], StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("다운로드한 fdkaac의 SHA-256 검증에 실패했습니다.");
        }

        progress?.Report("fdkaac 압축 해제 및 실행 확인 중...");
        var directory = Path.GetDirectoryName(path)!;
        var staging = $"{directory}.staging-{Guid.NewGuid():N}";
        try
        {
            Directory.CreateDirectory(staging);
            await ExtractAsync(archive, staging, Path.GetFileName(path), cancellationToken);
            var stagedExecutable = Path.Combine(staging, Path.GetFileName(path));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(stagedExecutable,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            await validator.ValidateAsync(stagedExecutable, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(directory);
            File.Move(Path.Combine(staging, "FDK-AAC-NOTICE"),
                Path.Combine(directory, "FDK-AAC-NOTICE"), overwrite: true);
            File.Move(stagedExecutable, path, overwrite: true);
            progress?.Report("fdkaac 설치 완료");
            return path;
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    private static async Task ExtractAsync(
        byte[] archive, string directory, string executableName, CancellationToken cancellationToken)
    {
        var wanted = new HashSet<string>(StringComparer.Ordinal) { executableName, "FDK-AAC-NOTICE" };
        using var stream = new MemoryStream(archive);
        if (OperatingSystem.IsWindows())
        {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName.Replace('\\', '/').Split('/').Last();
                if (!wanted.Remove(name))
                {
                    continue;
                }

                await using var source = entry.Open();
                await using var destination = File.Create(Path.Combine(directory, name));
                await source.CopyToAsync(destination, cancellationToken);
            }
        }
        else
        {
            using var gzip = new GZipStream(stream, CompressionMode.Decompress);
            using var tar = new TarReader(gzip);
            while (tar.GetNextEntry() is { } entry)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                {
                    continue;
                }

                var name = entry.Name.Replace('\\', '/').Split('/').Last();
                if (!wanted.Remove(name))
                {
                    continue;
                }

                await using var destination = File.Create(Path.Combine(directory, name));
                await entry.DataStream!.CopyToAsync(destination, cancellationToken);
            }
        }

        if (wanted.Count != 0)
        {
            throw new InvalidDataException("fdkaac 아카이브에 실행 파일 또는 고지 파일이 없습니다.");
        }
    }

}
