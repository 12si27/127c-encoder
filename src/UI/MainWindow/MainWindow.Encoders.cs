using static Encoder127c.UI.Dialogs.DialogFactory;
using Avalonia.Interactivity;

namespace Encoder127c;

public partial class MainWindow
{
    private async void CheckEncoderAvailability(object? sender, EventArgs e)
    {
        if (_isPreparingEncoders)
        {
            return;
        }

        var downloadRequested = false;
        _isPreparingEncoders = true;
        EncodeButton.IsEnabled = false;
        DownloadEncodersButton.IsEnabled = false;
        try
        {
            _ffmpegExecutable = await _ffmpegManager.FindAvailableExecutableAsync();

            _fdkaacExecutable = await _fdkaacManager.FindAvailableExecutableAsync();

            DownloadEncodersButton.IsVisible = _ffmpegExecutable is null || _fdkaacExecutable is null;
            DownloadEncodersButton.IsEnabled = DownloadEncodersButton.IsVisible;
            SetStatus(_ffmpegExecutable is null
                ? "FFmpeg가 없습니다. 다운로드 후 인코딩할 수 있습니다."
                : _fdkaacExecutable is null
                    ? "fdkaac가 없습니다. 인코더 다운로드 후 오디오를 인코딩할 수 있습니다."
                    : "인코더 준비 완료");

            if (DownloadEncodersButton.IsVisible && !_encoderDownloadPromptAnswered)
            {
                var dialog = CreateDialog("인코더 다운로드",
                    "인코딩 작업을 위해 인코더를 다운로드해야 합니다.\n지금 다운로드할까요?",
                    [("네", true), ("아니오", false)],
                    "추가로 약 100MB의 데이터가 다운로드됩니다.");
                var answer = await dialog.ShowDialog<bool?>(this);
                if (answer is bool accepted)
                {
                    _encoderDownloadPromptAnswered = true;
                    SaveSettings();
                    downloadRequested = accepted;
                }
            }
        }
        catch (Exception exception)
        {
            DownloadEncodersButton.IsVisible = _ffmpegExecutable is null || _fdkaacExecutable is null;
            DownloadEncodersButton.IsEnabled = DownloadEncodersButton.IsVisible;
            SetStatus($"인코더 확인 오류: {exception.Message}");
        }
        finally
        {
            _isPreparingEncoders = false;
            UpdateEncodeButton();
        }

        if (downloadRequested)
        {
            await DownloadEncodersAsync();
        }
    }

    private async void DownloadEncoders(object? sender, RoutedEventArgs e)
    {
        await DownloadEncodersAsync();
    }

    private async Task DownloadEncodersAsync()
    {
        if (_isPreparingEncoders || IsEncoding)
        {
            return;
        }

        _isPreparingEncoders = true;
        EncodeButton.IsEnabled = false;
        DownloadEncodersButton.IsEnabled = false;
        ShowIndeterminateProgress();
        ClearLog();
        try
        {
            AppendLog("[인코더 준비] FFmpeg 준비를 시작합니다.");

            _ffmpegExecutable = await _ffmpegManager.EnsureAvailableAsync(
                new EncoderPreparationProgress(message => ReportEncoderPreparation("FFmpeg", message)));

            _fdkaacExecutable = await _fdkaacManager.EnsureAvailableAsync(
                progress: new EncoderPreparationProgress(message => ReportEncoderPreparation("fdkaac", message)));
            DownloadEncodersButton.IsVisible = false;
            DownloadEncodersButton.IsEnabled = false;
            SetStatus("인코더 준비 완료");
            AppendLog("[인코더 준비] 인코딩을 시작할 수 있습니다.");
        }
        catch (Exception exception)
        {
            DownloadEncodersButton.IsVisible = _ffmpegExecutable is null || _fdkaacExecutable is null;
            DownloadEncodersButton.IsEnabled = DownloadEncodersButton.IsVisible;
            SetStatus($"인코더 설치 오류: {exception.Message}");
            AppendLog($"[인코더 준비] 오류: {exception}");
        }
        finally
        {
            _isPreparingEncoders = false;
            UpdateEncodeButton();
            HideEncodingProgress();
        }
    }

}
