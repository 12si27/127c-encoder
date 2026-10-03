using static Encoder127c.UI.Dialogs.DialogFactory;

namespace Encoder127c;

public partial class MainWindow
{
    private async Task<bool> ShowConfirmationDialogAsync(string message, string? detail = null)
    {
        var dialog = CreateDialog(
            "확인",
            message,
            [
                ("네", true),
                ("아니오", false)
            ],
            detail);

        return await dialog.ShowDialog<bool>(this);
    }

    private async Task ShowMessageDialogAsync(string message, DialogKind kind = DialogKind.Error)
    {
        var title = kind == DialogKind.Warning ? "경고" : kind == DialogKind.Information ? "안내" : "오류";
        var dialog = CreateDialog(title, message, [("확인", true)], kind: kind);
        await dialog.ShowDialog<bool>(this);
    }

}
