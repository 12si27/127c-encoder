using System.Diagnostics;

namespace Encoder127c.Platform;

internal static class LinuxDesktopIntegration
{
    public const string ApplicationId = "127c-encoder";

    // Register before creating any windows so the shell can match their WM_CLASS.
    public static void TryRegister()
    {
        if (!OperatingSystem.IsLinux() || Environment.ProcessPath is not { } executablePath
            || Path.GetFileName(executablePath) != ApplicationId)
            return; // Do not register the dotnet host or a test runner as the launcher.

        try
        {
            var dataDirectory = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (string.IsNullOrEmpty(dataDirectory) || !Path.IsPathFullyQualified(dataDirectory))
                dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            using var icon = typeof(LinuxDesktopIntegration).Assembly.GetManifestResourceStream("Encoder127c.LinuxDesktopIcon")
                ?? throw new IOException("Cannot load Linux desktop icon.");
            Register(executablePath, dataDirectory, icon);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            // Desktop integration is optional; a read-only profile must not prevent startup.
            Debug.WriteLine($"Linux desktop registration unavailable: {exception.Message}");
        }
    }

    internal static void Register(string executablePath, string dataDirectory, Stream icon)
    {
        var iconPath = Path.Combine(dataDirectory, ApplicationId, "app-icon.png");
        using var iconBytes = new MemoryStream();
        icon.CopyTo(iconBytes);
        WriteIfChanged(iconPath, iconBytes.ToArray());

        var desktopEntry = $"""
            [Desktop Entry]
            Type=Application
            Name=127c-encoder
            Comment=Video encoding client for 1227 Cloud
            Exec=env -- {QuoteExecutable(executablePath)}
            Icon={EscapeValue(iconPath)}
            Terminal=false
            Categories=AudioVideo;Video;
            StartupWMClass={ApplicationId}

            """;
        WriteIfChanged(Path.Combine(dataDirectory, "applications", ApplicationId + ".desktop"),
            System.Text.Encoding.UTF8.GetBytes(desktopEntry));
    }

    private static string QuoteExecutable(string path)
    {
        // Desktop string escaping is decoded first, then Exec quoting and percent field codes.
        // 'env --' keeps paths containing percent signs out of GLib's executable existence check.
        var quoted = path.Replace("\\", "\\\\").Replace("\"", "\\\"")
            .Replace("`", "\\`").Replace("$", "\\$").Replace("%", "%%");
        return "\"" + EscapeValue(quoted) + "\"";
    }

    private static string EscapeValue(string value) => value.Replace("\\", "\\\\")
        .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");

    private static void WriteIfChanged(string path, byte[] contents)
    {
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(contents)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporaryPath, contents);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }
}
