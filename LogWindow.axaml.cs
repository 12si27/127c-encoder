using Avalonia.Controls;
using Avalonia.Threading;

namespace Encoder127c;

public partial class LogWindow : Window
{
    public LogWindow()
    {
        InitializeComponent();
    }

    internal void SetLogText(string text)
    {
        LogText.Text = text;
        Dispatcher.UIThread.Post(
            () => LogScrollViewer.ScrollToEnd(),
            DispatcherPriority.Background);
    }
}
