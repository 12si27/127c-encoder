using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Encoder127c.Encoding.Models;
using Encoder127c.Encoding.Services;
using Encoder127c.Settings;

namespace Encoder127c;

public partial class MainWindow
{
    private async void PickOutputFolder(object? sender, RoutedEventArgs e)
    {
        var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storageProvider is null || IsEncoding)
        {
            return;
        }

        var folders = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "출력 폴더 선택",
            AllowMultiple = false
        });

        if (folders.Count > 0)
        {
            OutputDirectoryTextBox.Text = folders[0].TryGetLocalPath();
        }
    }

    private void OpenOutputFolder(object? sender, RoutedEventArgs e)
        => OpenDirectory(OutputDirectoryTextBox.Text?.Trim(), createIfMissing: true);

    private void OpenDirectory(string? directory, bool createIfMissing = false)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            SetStatus("열 폴더를 먼저 선택하세요.");
            return;
        }

        try
        {
            var fullDirectory = Path.GetFullPath(directory);
            if (createIfMissing)
            {
                Directory.CreateDirectory(fullDirectory);
            }

            if (!Directory.Exists(fullDirectory))
            {
                SetStatus("폴더가 존재하지 않습니다.");
                return;
            }

            var startInfo = OperatingSystem.IsWindows()
                ? new ProcessStartInfo(fullDirectory) { UseShellExecute = true }
                : new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open")
                {
                    UseShellExecute = false
                };
            if (!OperatingSystem.IsWindows())
            {
                startInfo.ArgumentList.Add(fullDirectory);
            }
            Process.Start(startInfo);
        }
        catch (Exception exception) when (exception is
            ArgumentException or
            NotSupportedException or
            PathTooLongException or
            UnauthorizedAccessException or
            IOException or
            System.ComponentModel.Win32Exception)
        {
            SetStatus("폴더를 열 수 없습니다.");
        }
    }

    private void ResetSettings(object? sender, RoutedEventArgs e)
    {
        if (IsEncoding)
        {
            return;
        }

        ApplyDefaultSettings();
        SetStatus("설정을 기본값으로 되돌렸습니다.");
    }

    private VideoSettings ReadCommonEncodingSettings() => new(
        Output: ReadCommonOutputSettings(),
        Gain: ReadCommonGainSettings(),
        VideoPreset: GetSelectedTag(VideoPresetComboBox) ?? DefaultEncodingPreset.DefaultVideoPreset,
        DeinterlaceMode: GetSelectedTag(DeinterlaceModeComboBox) ?? DefaultEncodingPreset.DefaultDeinterlaceMode,
        Bitrate: new VideoBitrateSettings(
            VideoMaxBitrateNumericUpDown.Value ?? DefaultEncodingPreset.DefaultBitrate.MaxBitrate,
            VideoBufferSizeNumericUpDown.Value ?? DefaultEncodingPreset.DefaultBitrate.BufferSize),
        EncodingProfile: GetSelectedTag(EncodingProfileComboBox) ?? DefaultEncodingPreset.DefaultEncodingProfile);

    private VideoEncodingRequest CreateEncodingRequest(EncodingQueueItem item) =>
        VideoEncodingRequestFactory.Create(item.Path, ReadCommonEncodingSettings(), item.Settings);

    private VideoOutputSettings ReadCommonOutputSettings() => new(
        OutputDirectoryTextBox.Text ?? string.Empty, UseSourceDirectoryCheckBox.IsChecked == true);

    private VideoGainSettings ReadCommonGainSettings() => new(
        AudioGainNumericUpDown.Value ?? 0, DynamicAudioNormalizationCheckBox.IsChecked == true);

    private string GetOutputDirectory(EncodingQueueItem item) =>
        (item.Settings?.Output ?? ReadCommonOutputSettings()).ResolveDirectory(item.Path);

    private static string? GetSelectedTag(ComboBox comboBox) =>
        (comboBox.SelectedItem as ComboBoxItem)?.Tag as string;

    private bool IsSavingEncodingProfile() =>
        string.Equals(
            GetSelectedTag(EncodingProfileComboBox),
            DefaultEncodingPreset.EncodingProfileSaving,
            StringComparison.Ordinal);

    private bool IsAudioOnlyEncodingProfile() =>
        GetSelectedTag(EncodingProfileComboBox) == DefaultEncodingPreset.EncodingProfileAudioOnly;

    private void UseSourceDirectoryChanged(object? sender, RoutedEventArgs e) =>
        UpdateOutputDirectoryControls();

    private void EncodingProfileChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.RemovedItems.OfType<ComboBoxItem>().Any(item =>
                item.Tag as string == DefaultEncodingPreset.EncodingProfileDefault))
        {
            CaptureDefaultVideoBitrates();
        }

        if (IsSavingEncodingProfile())
        {
            VideoMaxBitrateNumericUpDown.Value = DefaultEncodingPreset.SavingBitrate.MaxBitrate;
            VideoBufferSizeNumericUpDown.Value = DefaultEncodingPreset.SavingBitrate.BufferSize;
        }
        else
        {
            VideoMaxBitrateNumericUpDown.Value = _settings.DefaultMaxBitrate;
            VideoBufferSizeNumericUpDown.Value = _settings.DefaultBufferSize;
        }

        UpdateEncodingProfileControls();
    }

    private void UpdateEncodingProfileControls()
    {
        var isSavingProfile = IsSavingEncodingProfile();
        var isAudioOnlyProfile = IsAudioOnlyEncodingProfile();
        VideoPresetComboBox.IsEnabled = _encodingControlsEnabled && !isAudioOnlyProfile;
        DeinterlaceModeComboBox.IsEnabled = _encodingControlsEnabled && !isAudioOnlyProfile;
        VideoMaxBitrateNumericUpDown.IsReadOnly = isSavingProfile || isAudioOnlyProfile;
        VideoBufferSizeNumericUpDown.IsReadOnly = isSavingProfile || isAudioOnlyProfile;
        VideoMaxBitrateNumericUpDown.IsEnabled = _encodingControlsEnabled && !isSavingProfile && !isAudioOnlyProfile;
        VideoBufferSizeNumericUpDown.IsEnabled = _encodingControlsEnabled && !isSavingProfile && !isAudioOnlyProfile;
    }

    private void ApplyDefaultSettings()
    {
        _settings.Reset();
        OutputDirectoryTextBox.Text = Path.GetFullPath(AppPaths.DefaultOutputDirectory);
        UseSourceDirectoryCheckBox.IsChecked = false;
        SelectComboBoxItem(EncodingProfileComboBox, DefaultEncodingPreset.DefaultEncodingProfile);
        SelectComboBoxItem(VideoPresetComboBox, DefaultEncodingPreset.DefaultVideoPreset);
        SelectComboBoxItem(DeinterlaceModeComboBox, DefaultEncodingPreset.DefaultDeinterlaceMode);
        VideoMaxBitrateNumericUpDown.Value = DefaultEncodingPreset.DefaultBitrate.MaxBitrate;
        VideoBufferSizeNumericUpDown.Value = DefaultEncodingPreset.DefaultBitrate.BufferSize;
        AudioGainNumericUpDown.Value = 0;
        DynamicAudioNormalizationCheckBox.IsChecked = true;
    }

    private void RestoreSettings()
    {
        var settings = EncoderSettingsStore.Load();
        if (settings is null)
        {
            return;
        }

        _encoderDownloadPromptAnswered = settings.EncoderDownloadPromptAnswered;
        PreventSleepMenuItem.IsChecked = settings.PreventSleepDuringEncoding;

        if (!string.IsNullOrWhiteSpace(settings.OutputDirectory))
        {
            OutputDirectoryTextBox.Text = settings.OutputDirectory;
        }

        UseSourceDirectoryCheckBox.IsChecked = settings.UseSourceDirectory;

        var encodingProfile = settings.EncodingProfile ?? DefaultEncodingPreset.DefaultEncodingProfile;
        SelectComboBoxItem(EncodingProfileComboBox, encodingProfile);
        _settings.Restore(settings);

        SelectComboBoxItem(VideoPresetComboBox, settings.VideoPreset);
        SelectComboBoxItem(DeinterlaceModeComboBox, settings.DeinterlaceMode);
        if (!IsSavingEncodingProfile())
        {
            VideoMaxBitrateNumericUpDown.Value = _settings.DefaultMaxBitrate;
            VideoBufferSizeNumericUpDown.Value = _settings.DefaultBufferSize;
        }
        AudioGainNumericUpDown.Value = CommonSettingsState.ClampToRange(settings.AudioGain, -60, 60, 0);
        DynamicAudioNormalizationCheckBox.IsChecked = settings.DynamicAudioNormalization;
        UpdateEncodingProfileControls();
    }

    private void SaveSettings()
    {
        if (!IsSavingEncodingProfile())
        {
            CaptureDefaultVideoBitrates();
        }

        EncoderSettingsStore.Save(new EncoderSettings
        {
            EncoderDownloadPromptAnswered = _encoderDownloadPromptAnswered,
            PreventSleepDuringEncoding = PreventSleepMenuItem.IsChecked,
            OutputDirectory = OutputDirectoryTextBox.Text?.Trim(),
            UseSourceDirectory = UseSourceDirectoryCheckBox.IsChecked == true,
            EncodingProfile = GetSelectedTag(EncodingProfileComboBox),
            VideoPreset = GetSelectedTag(VideoPresetComboBox),
            DeinterlaceMode = GetSelectedTag(DeinterlaceModeComboBox),
            VideoMaxBitrate = VideoMaxBitrateNumericUpDown.Value,
            VideoBufferSize = VideoBufferSizeNumericUpDown.Value,
            DefaultVideoMaxBitrate = _settings.DefaultMaxBitrate,
            DefaultVideoBufferSize = _settings.DefaultBufferSize,
            AudioGain = AudioGainNumericUpDown.Value,
            DynamicAudioNormalization = DynamicAudioNormalizationCheckBox.IsChecked == true,
            AudioGainAnalysis = _settings.AudioGainOptions
        });
    }

    private void CaptureDefaultVideoBitrates() =>
        _settings.CaptureBitrates(VideoMaxBitrateNumericUpDown.Value, VideoBufferSizeNumericUpDown.Value);

}
