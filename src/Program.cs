using Avalonia;
using Encoder127c.Platform;

namespace Encoder127c;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        LinuxDesktopIntegration.TryRegister();
        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    private static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new X11PlatformOptions { WmClass = LinuxDesktopIntegration.ApplicationId })
            .LogToTrace();
}
