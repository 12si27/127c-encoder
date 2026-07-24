using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Encoder127c.Encoding.Services;
using Encoder127c.Ffmpeg.Services;
using HotAvalonia;

namespace Encoder127c;

public partial class App : Application
{
    public override void Initialize()
    {
        this.UseHotReload();
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow(
                FfmpegServiceFactory.CreateDefault(),
                VideoEncodingServices.CreateDefault());
        }

        base.OnFrameworkInitializationCompleted();
    }
}
