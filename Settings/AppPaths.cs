namespace Encoder127c.Settings;

internal static class AppPaths
{
    // A macOS .app bundle (and a mounted DMG) must be treated as read-only.
    public static string DataDirectory => OperatingSystem.IsMacOS()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "Application Support", "127c-encoder")
        : AppContext.BaseDirectory;

    public static string DefaultOutputDirectory => OperatingSystem.IsMacOS()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Movies", "127c-encoder")
        : Path.Combine(AppContext.BaseDirectory, "encoded");
}
