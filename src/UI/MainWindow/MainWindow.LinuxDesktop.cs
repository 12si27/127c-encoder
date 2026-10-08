using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Encoder127c.Platform;
using static Encoder127c.UI.Dialogs.DialogFactory;

namespace Encoder127c;

public partial class MainWindow
{
    private readonly record struct DesktopRegistrationChoice(bool Register, bool DontAskAgain);

    private async void ManageLinuxDesktopIntegration(object? sender, RoutedEventArgs e) =>
        await CheckLinuxDesktopIntegrationAsync(force: true);

    private async Task CheckLinuxDesktopIntegrationAsync(bool force = false)
    {
        var change = LinuxDesktopIntegration.GetPendingChange(force);
        if (change is null)
            return;

        if (change == LinuxDesktopIntegration.ChangeKind.UpToDate)
        {
            await ShowMessageDialogAsync("127c-encoder가 이미 Linux 앱 메뉴에 등록되어 있습니다.",
                DialogKind.Information);
            return;
        }

        var message = change switch
        {
            LinuxDesktopIntegration.ChangeKind.Missing =>
                "127c-encoder를 Linux 앱 메뉴에 등록하시겠습니까?",
            LinuxDesktopIntegration.ChangeKind.LocationChanged =>
                "실행 파일의 위치가 변경되었습니다. 앱 메뉴의 실행 경로를 갱신하시겠습니까?",
            LinuxDesktopIntegration.ChangeKind.Customized =>
                "데스크톱 등록 파일이 직접 수정되었거나 다른 프로그램에서 생성되었습니다. 덮어쓰시겠습니까?",
            _ => "127c-encoder의 데스크톱 등록 정보를 갱신하시겠습니까?"
        };
        var detail = change == LinuxDesktopIntegration.ChangeKind.Customized
            ? "등록하면 기존 .desktop 파일과 아이콘이 127c-encoder의 기본 내용으로 교체됩니다."
            : "등록하면 앱 메뉴에서 실행할 수 있으며 Dock에 아이콘이 올바르게 표시됩니다.";

        var dialog = new Window
        {
            Title = "Linux 데스크톱 통합",
            Width = 440,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var rememberCheckBox = new CheckBox
        {
            Content = "다시 묻지 않음",
            VerticalAlignment = VerticalAlignment.Center
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };
        foreach (var (text, register) in new[] { ("아니요", false), ("등록", true) })
        {
            var button = new Button { Content = text, MinWidth = 80 };
            button.Click += (_, _) =>
                dialog.Close(new DesktopRegistrationChoice(register, rememberCheckBox.IsChecked == true));
            buttons.Children.Add(button);
        }

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        footer.Children.Add(rememberCheckBox);
        Grid.SetColumn(buttons, 1);
        footer.Children.Add(buttons);

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 20,
            Children =
            {
                new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                        new TextBlock
                        {
                            Text = detail,
                            TextWrapping = TextWrapping.Wrap,
                            FontSize = 12,
                            Opacity = 0.7
                        }
                    }
                },
                footer
            }
        };

        var choice = await dialog.ShowDialog<DesktopRegistrationChoice?>(this);
        if (choice is not { } decision)
            return;

        if (!decision.Register)
        {
            LinuxDesktopIntegration.Decline(decision.DontAskAgain);
            return;
        }

        if (!LinuxDesktopIntegration.TryRegister(decision.DontAskAgain))
            await ShowMessageDialogAsync("Linux 데스크톱 등록 정보를 저장하지 못했습니다. 앱은 계속 사용할 수 있습니다.",
                DialogKind.Warning);
    }
}
