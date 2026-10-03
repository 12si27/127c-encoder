using System.IO.Compression;
using System.Security.Cryptography;
using Encoder127c.Encoders.Ffmpeg.Models;
using Encoder127c.Encoders.Ffmpeg.Validation;
using SharpCompress.Readers;

namespace Encoder127c.Encoders.Ffmpeg.Installation;

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

            progress?.Report("FFmpeg 압축 해제하는 중...");
            var extractedDirectory = Path.Combine(stagingDirectory, "extracted");
            var extractedExecutable = await ExtractExecutableAsync(
                archivePath, extractedDirectory, platform.ExecutableName, cancellationToken);
            progress?.Report("압축 해제 완료: FFmpeg");
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

    private static async Task<string> ExtractExecutableAsync(
        string archivePath,
        string destinationDirectory,
        string executableName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(destinationDirectory);
        var destinationPath = Path.Combine(destinationDirectory, executableName);
        using var archiveStream = File.OpenRead(archivePath);
        var signature = new byte[4];
        var signatureLength = archiveStream.Read(signature, 0, signature.Length);
        archiveStream.Position = 0;

        // ZIP's central directory lets us open only the executable without decompressing
        // ffprobe, ffplay, documentation, or any other unrelated entry.
        if (signatureLength == 4 && signature[0] == 'P' && signature[1] == 'K' &&
            signature[2] == 3 && signature[3] == 4)
        {
            using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read);
            var entry = archive.Entries.SingleOrDefault(item =>
                IsExecutableEntry(item.FullName, executableName));
            if (entry is not null)
            {
                using var source = entry.Open();
                await CopyExecutableAsync(source, destinationPath, cancellationToken);
                return destinationPath;
            }

            throw new FileNotFoundException("FFmpeg 아카이브에서 실행 파일을 찾을 수 없습니다.");
        }

        // BtbN distributes Linux binaries as .tar.xz. ReaderFactory handles the XZ stream
        // and its nested TAR entries. Earlier entries still need to be read, but are never
        // written to disk, and we stop as soon as the executable has been extracted.
        using var reader = ReaderFactory.OpenReader(archiveStream);
        while (reader.MoveToNextEntry())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = reader.Entry;
            if (entry.IsDirectory || !IsExecutableEntry(entry.Key, executableName))
            {
                continue;
            }

            using var source = reader.OpenEntryStream();
            await CopyExecutableAsync(source, destinationPath, cancellationToken);
            return destinationPath;
        }

        throw new FileNotFoundException("FFmpeg 아카이브에서 실행 파일을 찾을 수 없습니다.");
    }

    private static bool IsExecutableEntry(string? entryKey, string executableName)
    {
        // Normalize archive separators on every host. Archive paths are never used as
        // output paths: only the known platform executable name is written to staging.
        var fileName = entryKey?.Replace('\\', '/').Split('/').Last();
        return string.Equals(fileName, executableName, StringComparison.Ordinal);
    }

    private static async Task CopyExecutableAsync(
        Stream source, string destinationPath, CancellationToken cancellationToken)
    {
        await using var destination = new FileStream(
            destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await source.CopyToAsync(destination, cancellationToken);
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
