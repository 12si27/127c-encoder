using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Encoder127c.UI.Dialogs;

internal static class DialogFactory
{
    internal enum DialogKind
    {
        Question,
        Warning,
        Error,
        Information
    }

    internal static Window CreateDialog(
        string title,
        string message,
        (string Text, bool Result)[] buttons,
        string? detail = null,
        DialogKind kind = DialogKind.Question)
        => CreateDialog<bool>(title, message, buttons, detail, kind);

    internal static Window CreateDialog<T>(
        string title,
        string message,
        (string Text, T Result)[] buttons,
        string? detail = null,
        DialogKind kind = DialogKind.Question)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var buttonPanel = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8
        };

        foreach (var (text, result) in buttons)
        {
            var button = new Button
            {
                Content = text,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
                MinWidth = 80
            };
            button.Click += (_, _) => dialog.Close(result);
            buttonPanel.Children.Add(button);
        }

        var messagePanel = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap
                }
            }
        };
        if (detail is not null)
        {
            messagePanel.Children.Add(new TextBlock
            {
                Text = detail,
                FontSize = 12,
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap
            });
        }

        var icon = new FluentIcons.Avalonia.FluentIcon
        {
            Icon = kind switch
            {
                DialogKind.Warning => FluentIcons.Common.Icon.Warning,
                DialogKind.Error => FluentIcons.Common.Icon.DismissCircle,
                DialogKind.Information => FluentIcons.Common.Icon.Info,
                _ => FluentIcons.Common.Icon.QuestionCircle
            },
            IconVariant = FluentIcons.Common.IconVariant.Regular,
            IconSize = FluentIcons.Common.IconSize.Size32
        };
        var iconViewbox = new Viewbox
        {
            Width = 48,
            Height = 48,
            Stretch = Stretch.Uniform,
            Child = icon,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 16, 0)
        };
        var messageRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Children = { iconViewbox, messagePanel }
        };
        Grid.SetColumn(messagePanel, 1);

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 20,
            Children =
            {
                messageRow,
                buttonPanel
            }
        };

        return dialog;
    }

}
