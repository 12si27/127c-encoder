using System.Security.Cryptography;
using Encoder127c.Ffmpeg.Models;
using Encoder127c.Ffmpeg.Validation;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace Encoder127c.Ffmpeg.Installation;

internal interface IFfmpegPackageInstaller
{
    Task<string> InstallAsync(
        FfmpegBuild build,
        FfmpegPlatform platform,
        string installationDirectory,
        IProgress<string>? progress,
        CancellationToken cancellationToken);
}

/// <summary>Downloads, verifies, extracts, validates, and atomically promotes an FFmpeg package.</summary>
internal sealed class FfmpegPackageInstaller(
    HttpClient httpClient,
    IFfmpegValidator validator) : IFfmpegPackageInstaller
{
    public Task<string> InstallAsync(
        FfmpegBuild build,
        FfmpegPlatform platform,
        string installationDirectory,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        return Task.Run(
            () => InstallCoreAsync(build, platform, installationDirectory, progress, cancellationToken),
            cancellationToken);
    }

    private async Task<string> InstallCoreAsync(
        FfmpegBuild build,
        FfmpegPlatform platform,
        string installationDirectory,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var stagingDirectory = $"{installationDirectory}.staging-{Guid.NewGuid():N}";
        var archivePath = Path.Combine(stagingDirectory, "ffmpeg-package");
        var executablePath = Path.Combine(installationDirectory, platform.ExecutableName);

        try
        {
            Directory.CreateDirectory(stagingDirectory);
            progress?.Report($"FFmpeg 바이너리 아카이브 다운로드: {build.DownloadUri.Host}");
            await DownloadAsync(build.DownloadUri, archivePath, build.Sha256, progress, cancellationToken);
            progress?.Report("SHA-256 검증 완료");

            progress?.Report("FFmpeg 아카이브 압축 해제 시작...");
            var extractedDirectory = Path.Combine(stagingDirectory, "extracted");
            var extractedFileCount = ExtractArchive(archivePath, extractedDirectory);
            progress?.Report($"압축 해제 완료: 파일 {extractedFileCount}개");
            var extractedExecutable = FindExecutable(extractedDirectory, platform.ExecutableName);
            MakeExecutable(extractedExecutable);
            progress?.Report($"FFmpeg 실행 파일 발견: {Path.GetFileName(extractedExecutable)}");

            progress?.Report("FFmpeg 인코더를 확인하는 중...");
            await validator.ValidateAsync(extractedExecutable, cancellationToken);

            if (Directory.Exists(installationDirectory))
            {
                Directory.Delete(installationDirectory, recursive: true);
            }

            Directory.CreateDirectory(installationDirectory);
            File.Move(extractedExecutable, executablePath, overwrite: true);
            MakeExecutable(executablePath);
            progress?.Report("FFmpeg 설치 완료");
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

    private async Task DownloadAsync(
        Uri downloadUri,
        string destinationPath,
        string expectedHash,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(downloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var totalBytes = response.Content.Headers.ContentLength;
        var buffer = new byte[64 * 1024];
        long downloadedBytes = 0;
        var lastReportedPercent = -1;

        while (true)
        {
            var bytesRead = await source.ReadAsync(buffer, cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            hash.AppendData(buffer, 0, bytesRead);
            downloadedBytes += bytesRead;

            if (totalBytes is not > 0)
            {
                continue;
            }

            var percent = (int)(downloadedBytes * 100 / totalBytes.Value);
            if (percent >= lastReportedPercent + 5 || percent == 100)
            {
                lastReportedPercent = percent;
                progress?.Report($"FFmpeg 바이너리 다운로드: {percent}% ({downloadedBytes / 1024 / 1024} MB)");
            }
        }

        var actualHash = Convert.ToHexString(hash.GetHashAndReset());
        if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("다운로드한 FFmpeg 빌드의 SHA-256 검증에 실패했습니다.");
        }
    }

    private static int ExtractArchive(string archivePath, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        var destinationRoot = Path.GetFullPath(destinationDirectory) + Path.DirectorySeparatorChar;
        var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var extractedFileCount = 0;

        // BtbN distributes Linux binaries as .tar.xz. ReaderFactory handles the XZ stream
        // and then its nested TAR entries, unlike ArchiveFactory which expects a top-level archive.
        using var reader = ReaderFactory.OpenReader(archivePath);
        while (reader.MoveToNextEntry())
        {
            var entry = reader.Entry;
            if (entry.IsDirectory)
            {
                continue;
            }

            var entryKey = entry.Key
                ?? throw new InvalidDataException("FFmpeg 아카이브에 이름 없는 항목이 있습니다.");
            var destinationPath = Path.GetFullPath(Path.Combine(destinationDirectory, entryKey));
            if (!destinationPath.StartsWith(destinationRoot, pathComparison))
            {
                throw new InvalidDataException("FFmpeg 아카이브에 허용되지 않는 파일 경로가 있습니다.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            reader.WriteEntryToFile(destinationPath, new ExtractionOptions { Overwrite = true });
            extractedFileCount++;
        }

        return extractedFileCount;
    }

    private static string FindExecutable(string directory, string executableName)
    {
        return Directory.EnumerateFiles(directory, executableName, SearchOption.AllDirectories).SingleOrDefault()
            ?? throw new FileNotFoundException("FFmpeg 아카이브에서 실행 파일을 찾을 수 없습니다.");
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
