using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Threading;
using Encoder127c.Encoding.Services;

namespace Encoder127c;

public partial class MainWindow
{
    private static void SelectComboBoxItem(ComboBox comboBox, string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return;
        }

        var item = comboBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(candidate => string.Equals(candidate.Tag as string, tag, StringComparison.Ordinal));
        if (item is not null)
        {
            comboBox.SelectedItem = item;
        }
    }

    private void UpdateQueueUi()
    {
        DropHintPanel.IsVisible = EncodingQueue.Count == 0;
        var selectionCount = GetSelectedQueueItems().Length;
        var hasSelection = selectionCount > 0;
        RemoveSelectedButton.IsEnabled = !IsEncoding && hasSelection;
    }

    private void SetEncodingControlsEnabled(bool isEnabled)
    {
        if (!isEnabled)
        {
            EndQueueDrag();
        }
        _encodingControlsEnabled = isEnabled;
        AddFilesButton.IsEnabled = isEnabled;
        ClearFilesButton.IsEnabled = isEnabled;
        FileQueueHeader.IsEnabled = isEnabled;
        SizeQueueHeader.IsEnabled = isEnabled;
        StatusQueueHeader.IsEnabled = isEnabled;
        // ListBox 자체를 끄면 내부 ScrollViewer도 비활성화되므로 스크롤은 유지
        QueueDropBorder.IsEnabled = true;
        QueueListBox.IsEnabled = true;
        QueueListBox.Opacity = isEnabled ? 1 : 0.65;
        DragDrop.SetAllowDrop(QueueDropBorder, isEnabled);
        DragDrop.SetAllowDrop(QueueListBox, isEnabled);
        UseSourceDirectoryCheckBox.IsEnabled = isEnabled;
        OutputDirectoryTextBox.IsEnabled = isEnabled;
        UpdateOutputDirectoryControls();
        EncodingProfileComboBox.IsEnabled = isEnabled;
        UpdateEncodingProfileControls();
        AudioGainNumericUpDown.IsEnabled = isEnabled;
        DynamicAudioNormalizationCheckBox.IsEnabled = isEnabled;
        ResetSettingsButton.IsEnabled = isEnabled;
        UpdateQueueUi();
    }

    private void UpdateOutputDirectoryControls()
    {
        var useSourceDirectory = UseSourceDirectoryCheckBox.IsChecked == true;
        OutputDirectoryTextBox.IsReadOnly = useSourceDirectory;
        PickOutputFolderButton.IsEnabled = _encodingControlsEnabled && !useSourceDirectory;
    }

    private void UpdateEncodeButton()
    {
        if (IsEncoding)
        {
            EncodeButton.IsEnabled = true;
            EncodeButtonIcon.Icon = FluentIcons.Common.Icon.DismissCircle;
            EncodeButtonText.Text = "중지하기";
            return;
        }

        EncodeButtonIcon.Icon = FluentIcons.Common.Icon.PlayCircle;
        EncodeButtonText.Text = "인코딩 시작";
        EncodeButton.IsEnabled = !_isPreparingEncoders
            && !string.IsNullOrWhiteSpace(_ffmpegExecutable);
    }

    private void ToggleDetailedSettings(object? sender, RoutedEventArgs e)
    {
        _isDetailedSettingsExpanded = !_isDetailedSettingsExpanded;
        ToggleDetailedSettingsText.Text = _isDetailedSettingsExpanded ? "▲ 닫기" : "▼ 세부 설정";
        UpdateDetailedSettingsVisibility();
    }

    private void UpdateDetailedSettingsVisibility()
    {
        var showDetails = _isDetailedSettingsExpanded && EncodingProfilePanel.IsVisible;
        ToggleDetailedSettingsButton.IsVisible = EncodingProfilePanel.IsVisible;
        DetailedSettingsPanel.IsVisible = showDetails;
    }

    private void ShowLogWindow(object? sender, RoutedEventArgs e)
    {
        if (_logWindow is { } existingWindow)
        {
            if (existingWindow.WindowState == WindowState.Minimized)
                existingWindow.WindowState = WindowState.Normal;
            existingWindow.Activate();
            return;
        }

        var window = new LogWindow { Icon = Icon };
        _logWindow = window;
        window.Closed += (_, _) =>
        {
            _logTimer.Stop();
            _logWindow = null;
        };
        window.Show(this);
        window.SetLogText(_logBuffer.ToString());
        _logTimer.Start();
    }

    private void SetStatus(string message)
    {
        if (IsEncoding)
        {
            EncodingProgressTextBlock.Text = message;
            EncodingProgressTextBlock.IsVisible = true;
            return;
        }

        StatusTextBlock.Text = message;
    }

    private void ReportEncoderPreparation(string encoder, string message)
    {
        SetStatus(message);
        AppendLog($"[{encoder} 준비] {message}");
    }

    private sealed class EncoderPreparationProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string message)
        {
            // Finish each UI update before installation can report overall readiness.
            if (Dispatcher.UIThread.CheckAccess())
            {
                report(message);
            }
            else
            {
                Dispatcher.UIThread.Invoke(() => report(message));
            }
        }
    }

    private void ShowIndeterminateProgress(string? message = null)
    {
        if (IsEncoding)
        {
            EncodingProfilePanel.IsVisible = false;
            UpdateDetailedSettingsVisibility();
        }

        EncodingProgressBar.Value = 0;
        EncodingProgressBar.IsIndeterminate = true;
        EncodingProgressBar.IsVisible = true;
        _taskbarProgress.ShowIndeterminate();
        EncodingProgressTextBlock.Text = message ?? string.Empty;
        EncodingProgressTextBlock.IsVisible = !string.IsNullOrEmpty(message);
        UpdateAuxiliaryPanelVisibility();
    }

    private void HideEncodingProgress()
    {
        _taskbarProgress.Clear();
        EncodingProgressBar.IsVisible = false;
        EncodingProgressTextBlock.IsVisible = false;
        EncodingProfilePanel.IsVisible = true;
        UpdateDetailedSettingsVisibility();
        UpdateAuxiliaryPanelVisibility();
    }

    private void UpdateAuxiliaryPanelVisibility() =>
        AuxiliaryPanel.IsVisible = EncodingProgressBar.IsVisible;

    private void UpdateEncodingProgress(EncodingProgress progress, int itemNumber)
    {
        _hasEncodingProgress |= progress.ProcessedDuration > TimeSpan.Zero;
        if (progress.IsCompleted)
        {
            ShowIndeterminateProgress($"{itemNumber}/{EncodingQueue.Count} · {progress.Stage ?? "인코딩 마무리 중..."}");
            return;
        }

        if (progress.TotalDuration is not { } totalDuration || totalDuration <= TimeSpan.Zero)
        {
            return;
        }

        var percentage = Math.Clamp(
            progress.ProcessedDuration.TotalSeconds / totalDuration.TotalSeconds * 100,
            0,
            100);
        EncodingProgressBar.IsIndeterminate = false;
        EncodingProgressBar.Value = percentage;
        _taskbarProgress.SetValue(percentage);
        EncodingProgressTextBlock.IsVisible = true;

        var speedText = progress.Speed is { } speed ? $" · {speed:0.00}x" : string.Empty;
        var etaText = progress.Speed is { } processingSpeed
            ? $" · 예상 남은 시간 {FormatDuration(TimeSpan.FromSeconds(
                Math.Max(0, (totalDuration - progress.ProcessedDuration).TotalSeconds / processingSpeed)))}"
            : " · 예상 남은 시간 계산 중...";
        EncodingProgressTextBlock.Text = $"{itemNumber}/{EncodingQueue.Count} · {percentage:0.0}%{speedText}{etaText}";
    }

    private static string FormatDuration(TimeSpan duration) => duration.TotalHours >= 1
        ? duration.ToString(@"h\:mm\:ss")
        : duration.ToString(@"m\:ss");

    private void AppendLog(string message)
    {
        _logBuffer.Report(message);
    }

    private void ClearLog()
    {
        _logBuffer.Clear();
        _logWindow?.SetLogText(string.Empty);
    }

    private void FlushLog()
    {
        if (_logWindow is not { } window || !_logBuffer.TryGetChangedText(out var text))
        {
            return;
        }

        window.SetLogText(text);
    }}
