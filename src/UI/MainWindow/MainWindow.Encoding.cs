using Avalonia.Controls;
using Avalonia.Interactivity;
using Encoder127c.Encoding.Models;
using static Encoder127c.UI.Dialogs.DialogFactory;

namespace Encoder127c;

public partial class MainWindow
{
    private async void StartEncoding(object? sender, RoutedEventArgs e)
    {
        if (_closeRequested)
            return;

        if (IsEncoding)
        {
            await StopEncodingAsync();
            return;
        }

        if (string.IsNullOrWhiteSpace(_ffmpegExecutable))
        {
            SetStatus("FFmpeg를 먼저 다운로드하세요.");
            return;
        }

        var filesToEncode = EncodingQueue
            .Where(item => item.Status != EncodingQueueStatus.Completed)
            .ToList();
        if (filesToEncode.Count == 0)
        {
            if (EncodingQueue.Count == 0)
            {
                SetStatus("인코딩할 비디오 파일을 추가하세요.");
                return;
            }

            if (!await ShowConfirmationDialogAsync(
                "모든 비디오가 완료 상태입니다. 모두 초기화 후 다시 시작할까요?"))
            {
                return;
            }

            if (_closeRequested || IsEncoding)
            {
                return;
            }

            ResetQueueStatus(EncodingQueue.ToArray());
            filesToEncode = EncodingQueue.ToList();
        }

        // Check actual per-item destinations, including overrides, before starting the batch.
        var missingDirectories = filesToEncode
            .Select(item => _requestValidator.Validate(CreateEncodingRequest(item)))
            .Where(validation => validation.IsValid)
            .Select(validation => Path.GetDirectoryName(validation.Request!.OutputPath)!)
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .Where(directory => !Directory.Exists(directory))
            .ToArray();
        if (missingDirectories.Length > 0)
        {
            if (!await ShowConfirmationDialogAsync("출력 폴더가 없습니다. 폴더를 만들까요?",
                string.Join(Environment.NewLine, missingDirectories)))
            {
                return;
            }

            if (_closeRequested || IsEncoding || _isClosed) return;

            foreach (var directory in missingDirectories)
            {
                try
                {
                    Directory.CreateDirectory(directory);
                }
                catch (Exception exception) when (exception is ArgumentException or NotSupportedException or
                    PathTooLongException or UnauthorizedAccessException or IOException or System.Security.SecurityException)
                {
                    // The write check below marks only affected items as failed.
                    SetStatus($"출력 폴더를 만들 수 없습니다: {directory}");
                }
            }
        }

        await _session.RunAsync(
            _ffmpegExecutable, EncodingQueue.ToList(), filesToEncode,
            CreateEncodingRequest, _logBuffer,
            async () =>
            {
                SetEncodingControlsEnabled(false);
                EncodeButton.IsEnabled = true;
                EncodeButtonIcon.Icon = FluentIcons.Common.Icon.DismissCircle;
                EncodeButtonText.Text = "중지하기";
                ClearLog();
                ShowIndeterminateProgress();
                await UpdateSleepInhibitionAsync();
            },
            async finalStatus =>
            {
                await UpdateSleepInhibitionAsync();
                SetEncodingControlsEnabled(true);
                UpdateEncodeButton();
                HideEncodingProgress();
                if (finalStatus is not null) SetStatus(finalStatus);
            });
    }

    private async Task<bool> StopEncodingAsync()
    {
        if (_stopDialogOpen)
            return false;
        if (!IsEncoding)
            return true;
        if (_session.IsStopping)
            return true;

        var currentEncoding = _session.CurrentRun;
        bool? save = false;
        if (_hasEncodingProgress && currentEncoding is not null)
        {
            _stopDialogOpen = true;
            try
            {
                var dialog = CreateDialog<bool?>("인코딩 중지", "지금 인코딩한 영상을 저장할까요?",
                    [("네", true), ("아니오", false), ("취소", null)]);
                save = await dialog.ShowDialog<bool?>(this);
            }
            finally
            {
                _stopDialogOpen = false;
            }
            // 다이얼로그를 띄운 동안 다음 파일로 넘어갔다면 해당 파일은 중지하지 않습니다.
            if (save is null || !IsEncoding || currentEncoding != _session.CurrentRun)
                return false;
        }

        EncodeButton.IsEnabled = false;
        if (save == true)
        {
            SetStatus("현재까지의 인코딩을 저장하는 중...");
            _session.Stop(savePartial: true);
        }
        else
        {
            SetStatus("인코딩 프로세스를 중지하는 중...");
            _session.Stop();
        }
        return true;
    }

    private async void PreventSleepChanged(object? sender, RoutedEventArgs e)
    {
        SaveSettings();
        await UpdateSleepInhibitionAsync();
    }

    private async Task UpdateSleepInhibitionAsync()
    {
        await _sleepInhibitorGate.WaitAsync();
        try
        {
            if (IsEncoding && !_isClosed && PreventSleepMenuItem.IsChecked)
            {
                await _sleepInhibitor.StartAsync();
                // A toggle, cancellation completion, or close can arrive while
                // the Linux helper is acquiring its lock.
                if (!IsEncoding || _isClosed || !PreventSleepMenuItem.IsChecked)
                    _sleepInhibitor.Stop();
            }
            else
                _sleepInhibitor.Stop();
        }
        catch (Exception exception)
        {
            AppendLog($"[경고] 절전 방지 요청을 적용할 수 없습니다: {exception.Message}");
        }
        finally { _sleepInhibitorGate.Release(); }
    }

}
