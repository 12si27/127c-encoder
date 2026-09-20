using System.Security.Cryptography;
using Encoder127c.Fdkaac.Models;
using Encoder127c.Fdkaac.Validation;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace Encoder127c.Fdkaac.Installation;

internal interface IFdkaacPackageInstaller
{
    Task<string> InstallAsync(
        FdkaacBuild build,
        FdkaacPlatform platform,
        string installationDirectory,
        IProgress<string>? progress,
        CancellationToken cancellationToken);
}

internal sealed class FdkaacPackageInstaller(
    HttpClient httpClient,
    IFdkaacValidator validator) : IFdkaacPackageInstaller
{
    public async Task<string> InstallAsync(
        FdkaacBuild build,
        FdkaacPlatform platform,
        string installationDirectory,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var stagingDirectory = $"{installationDirectory}.staging-{Guid.NewGuid():N}";
        var archivePath = Path.Combine(stagingDirectory, "fdkaac-package");
        var executablePath = Path.Combine(installationDirectory, platform.ExecutableName);

        try
        {
            Directory.CreateDirectory(stagingDirectory);
            progress?.Report($"fdkaac 바이너리 다운로드: {build.DownloadUri.Host}");
            await DownloadAsync(build.DownloadUri, archivePath, cancellationToken);

            progress?.Report("fdkaac 다운로드 무결성을 확인하는 중...");
            await VerifySha256Async(archivePath, build.Sha256, cancellationToken);

            var extractedDirectory = Path.Combine(stagingDirectory, "extracted");
            ExtractArchive(archivePath, extractedDirectory);
            var extractedExecutable = Directory
                .EnumerateFiles(extractedDirectory, platform.ExecutableName, SearchOption.AllDirectories)
                .SingleOrDefault()
                ?? throw new FileNotFoundException("fdkaac 아카이브에서 실행 파일을 찾을 수 없습니다.");

            MakeExecutable(extractedExecutable);
            await validator.ValidateAsync(extractedExecutable, cancellationToken);

            if (Directory.Exists(installationDirectory))
            {
                Directory.Delete(installationDirectory, recursive: true);
            }

            Directory.CreateDirectory(installationDirectory);
            File.Move(extractedExecutable, executablePath, overwrite: true);
            MakeExecutable(executablePath);
            progress?.Report("fdkaac 설치 완료");
            return executablePath;
        }
        finally
        {
            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }
        }
    }

    private async Task DownloadAsync(Uri uri, string destinationPath, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await source.CopyToAsync(destination, cancellationToken);
    }

    private static async Task VerifySha256Async(string filePath, string expectedHash, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(filePath);
        var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("다운로드한 fdkaac 빌드의 SHA-256 검증에 실패했습니다.");
        }
    }

    private static void ExtractArchive(string archivePath, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        var destinationRoot = Path.GetFullPath(destinationDirectory) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        using var reader = ReaderFactory.OpenReader(archivePath);
        while (reader.MoveToNextEntry())
        {
            var entry = reader.Entry;
            if (entry.IsDirectory)
            {
                continue;
            }

            var key = entry.Key ?? throw new InvalidDataException("fdkaac 아카이브에 이름 없는 항목이 있습니다.");
            var destinationPath = Path.GetFullPath(Path.Combine(destinationDirectory, key));
            if (!destinationPath.StartsWith(destinationRoot, comparison))
            {
                throw new InvalidDataException("fdkaac 아카이브에 허용되지 않는 파일 경로가 있습니다.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            reader.WriteEntryToFile(destinationPath, new ExtractionOptions { Overwrite = true });
        }
    }

    private static void MakeExecutable(string executablePath)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(
            executablePath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }
}
