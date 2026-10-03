using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Encoder127c;

public partial class LogWindow : Window
{
    public LogWindow()
    {
        InitializeComponent();
    }

    internal void SetLogText(string text)
    {
        LogTextBox.Text = text;
        Dispatcher.UIThread.Post(
            () => LogTextBox.GetVisualDescendants()
                .OfType<ScrollViewer>()
                .FirstOrDefault()?
                .ScrollToEnd(),
            DispatcherPriority.Background);
    }
}
