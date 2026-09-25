using Encoder127c.Fdkaac.Validation;

namespace Encoder127c.Fdkaac.Services;

internal interface IFdkaacManager
{
    Task<string?> FindAvailableExecutableAsync(CancellationToken cancellationToken = default);

    Task<string> EnsureAvailableAsync(CancellationToken cancellationToken = default);
}

internal sealed class FdkaacManager(IFdkaacValidator validator) : IFdkaacManager
{
    public async Task<string?> FindAvailableExecutableAsync(CancellationToken cancellationToken = default)
    {
        var path = GetBundledExecutable();
        return File.Exists(path) && await validator.IsUsableAsync(path, cancellationToken)
            ? path
            : null;
    }

    public async Task<string> EnsureAvailableAsync(CancellationToken cancellationToken = default)
    {
        var path = GetBundledExecutable();
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("fdkaac가 앱 패키지에 없습니다. 앱을 다시 설치해 주세요.", path);
        }

        if (!await validator.IsUsableAsync(path, cancellationToken))
        {
            throw new InvalidDataException("동봉된 fdkaac를 실행할 수 없습니다. 앱을 다시 설치해 주세요.");
        }

        return path;
    }

    private static string GetBundledExecutable()
    {
        // A macOS .app stores resources beside Contents/MacOS. Portable ZIPs
        // keep the executable in the encoder folder next to the application.
        return OperatingSystem.IsMacOS()
            ? Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "Resources", "encoder", "fdkaac"))
            : Path.Combine(AppContext.BaseDirectory, "encoder",
                OperatingSystem.IsWindows() ? "fdkaac.exe" : "fdkaac");
    }
}
