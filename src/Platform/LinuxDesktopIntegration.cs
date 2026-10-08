using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Encoder127c.Platform;

internal static class LinuxDesktopIntegration
{
    public const string ApplicationId = "127c-encoder";

    internal enum ChangeKind
    {
        UpToDate,
        Missing,
        Changed,
        LocationChanged,
        Customized
    }

    private sealed class Preferences
    {
        public Preferences() { }

        public bool Approved { get; set; }
        public bool SuppressPrompts { get; set; }
        public string? LastDesktopHash { get; set; }
        public string? LastIconHash { get; set; }
        public string? LastExecutablePath { get; set; }
    }

    private sealed record Registration(
        string ExecutablePath,
        string DesktopPath,
        string IconPath,
        byte[] DesktopBytes,
        byte[] IconBytes);

    private static bool IsSupported => OperatingSystem.IsLinux()
        && Environment.ProcessPath is { } path
        && Path.GetFileName(path) == ApplicationId;

    // Previously approved, unchanged launchers may be refreshed before the first window
    // so Linux docks can associate the window with its desktop entry at startup.
    public static void TryRefreshApproved()
    {
        if (!IsSupported)
            return;

        try
        {
            var preferences = LoadPreferences();
            if (!preferences.Approved)
                return;

            var registration = CreateRegistration(Environment.ProcessPath!);
            var kind = Inspect(registration, preferences);
            if (kind == ChangeKind.Changed ||
                kind == ChangeKind.LocationChanged && preferences.SuppressPrompts)
                Install(registration, preferences);
        }
        catch (Exception exception) when (IsOptionalIntegrationFailure(exception))
        {
            Debug.WriteLine($"Linux desktop refresh unavailable: {exception.Message}");
        }
    }

    internal static ChangeKind? GetPendingChange(bool force = false)
    {
        if (!IsSupported)
            return null;

        try
        {
            var preferences = LoadPreferences();
            var registration = CreateRegistration(Environment.ProcessPath!);
            var kind = Inspect(registration, preferences);
            return kind == ChangeKind.UpToDate && !force ||
                preferences.SuppressPrompts && !force ? null : kind;
        }
        catch (Exception exception) when (IsOptionalIntegrationFailure(exception))
        {
            Debug.WriteLine($"Linux desktop inspection unavailable: {exception.Message}");
            return null;
        }
    }

    internal static bool TryRegister(bool suppressPrompts)
    {
        if (!IsSupported)
            return false;

        try
        {
            var preferences = LoadPreferences();
            Install(CreateRegistration(Environment.ProcessPath!), preferences);
            preferences.Approved = true;
            preferences.SuppressPrompts = suppressPrompts;
            SavePreferences(preferences);
            return true;
        }
        catch (Exception exception) when (IsOptionalIntegrationFailure(exception))
        {
            Debug.WriteLine($"Linux desktop registration unavailable: {exception.Message}");
            return false;
        }
    }

    internal static void Decline(bool suppressPrompts)
    {
        try
        {
            var preferences = LoadPreferences();
            preferences.Approved = false;
            preferences.SuppressPrompts = suppressPrompts;
            SavePreferences(preferences);
        }
        catch (Exception exception) when (IsOptionalIntegrationFailure(exception))
        {
            Debug.WriteLine($"Linux desktop preferences unavailable: {exception.Message}");
        }
    }

    private static ChangeKind Inspect(Registration registration, Preferences preferences)
    {
        var desktopHash = File.Exists(registration.DesktopPath)
            ? Hash(File.ReadAllBytes(registration.DesktopPath)) : null;
        var iconHash = File.Exists(registration.IconPath)
            ? Hash(File.ReadAllBytes(registration.IconPath)) : null;
        return Classify(desktopHash, iconHash, Hash(registration.DesktopBytes),
            Hash(registration.IconBytes), preferences.LastDesktopHash,
            preferences.LastIconHash, preferences.LastExecutablePath, registration.ExecutablePath);
    }

    internal static ChangeKind Classify(string? desktopHash, string? iconHash,
        string expectedDesktopHash, string expectedIconHash,
        string? previousDesktopHash, string? previousIconHash,
        string? previousExecutablePath, string executablePath)
    {
        if (desktopHash == expectedDesktopHash && iconHash == expectedIconHash)
            return ChangeKind.UpToDate;
        if (desktopHash is null || iconHash is null)
            return ChangeKind.Missing;

        // Unknown modifications must not be overwritten by automatic refresh.
        if (desktopHash != expectedDesktopHash && desktopHash != previousDesktopHash ||
            iconHash != expectedIconHash && iconHash != previousIconHash)
            return ChangeKind.Customized;

        if (previousExecutablePath is { } previousPath &&
            !string.Equals(previousPath, executablePath, StringComparison.Ordinal))
            return ChangeKind.LocationChanged;

        return ChangeKind.Changed;
    }

    private static Registration CreateRegistration(string executablePath)
    {
        var dataDirectory = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrEmpty(dataDirectory) || !Path.IsPathFullyQualified(dataDirectory))
            dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");

        var iconPath = Path.Combine(dataDirectory, ApplicationId, "app-icon.png");
        var desktopPath = Path.Combine(dataDirectory, "applications", ApplicationId + ".desktop");
        using var icon = typeof(LinuxDesktopIntegration).Assembly.GetManifestResourceStream("Encoder127c.LinuxDesktopIcon")
            ?? throw new IOException("Cannot load Linux desktop icon.");
        using var iconBytes = new MemoryStream();
        icon.CopyTo(iconBytes);
        return new Registration(executablePath, desktopPath, iconPath,
            System.Text.Encoding.UTF8.GetBytes(CreateDesktopEntry(executablePath, iconPath)), iconBytes.ToArray());
    }

    private static void Install(Registration registration, Preferences preferences)
    {
        WriteIfChanged(registration.IconPath, registration.IconBytes);
        WriteIfChanged(registration.DesktopPath, registration.DesktopBytes);
        preferences.LastIconHash = Hash(registration.IconBytes);
        preferences.LastDesktopHash = Hash(registration.DesktopBytes);
        preferences.LastExecutablePath = registration.ExecutablePath;
        SavePreferences(preferences);
    }

    // Kept as a file-system-level registration helper for regression tests.
    internal static void Register(string executablePath, string dataDirectory, Stream icon)
    {
        var iconPath = Path.Combine(dataDirectory, ApplicationId, "app-icon.png");
        using var iconBytes = new MemoryStream();
        icon.CopyTo(iconBytes);
        WriteIfChanged(iconPath, iconBytes.ToArray());
        WriteIfChanged(Path.Combine(dataDirectory, "applications", ApplicationId + ".desktop"),
            System.Text.Encoding.UTF8.GetBytes(CreateDesktopEntry(executablePath, iconPath)));
    }

    private static string CreateDesktopEntry(string executablePath, string iconPath) => $"""
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

    private static string Hash(byte[] contents) => Convert.ToHexString(SHA256.HashData(contents));

    private static string PreferencesPath
    {
        get
        {
            var configDirectory = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrEmpty(configDirectory) || !Path.IsPathFullyQualified(configDirectory))
                configDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return Path.Combine(configDirectory, ApplicationId, "desktop-integration.json");
        }
    }

    private static Preferences LoadPreferences()
    {
        var path = PreferencesPath;
        return File.Exists(path)
            ? JsonSerializer.Deserialize<Preferences>(File.ReadAllText(path)) ?? new Preferences()
            : new Preferences();
    }

    private static void SavePreferences(Preferences preferences)
    {
        var path = PreferencesPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        WriteIfChanged(path, System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(preferences)));
    }

    private static bool IsOptionalIntegrationFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or JsonException or System.Security.SecurityException;
    
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
