using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Encoder127c.Encoding.Models;
using Encoder127c.Encoding.Services;

namespace Encoder127c.Tools;

internal sealed record VideoSettingsDialogResult(VideoSettings? Settings);

public partial class VideoSettingsDialog : Window
{
    private readonly string _inputPath;
    private readonly string? _ffmpegExecutable;
    private readonly VideoOutputSettings _commonOutput;
    private readonly VideoGainSettings _commonGain;
    private readonly string _commonVideoPreset;
    private readonly string _commonDeinterlaceMode;
    private readonly VideoBitrateSettings _commonBitrate;
    private readonly string _commonEncodingProfile;
    private VideoBitrateSettings? _editableBitrate;
    private bool _isSavingBitrate;
    private bool _isRestoringSettings = true;
    private readonly bool _isMultiSelection;
    private CancellationTokenSource? _streamCancellation;
    private AudioGainOptions _analysisOptions;
    private int _audioStreamIndex;
    private IReadOnlyList<AudioStreamInfo>? _streams;
    private bool _isClosed;

    public VideoSettingsDialog() : this(string.Empty, null,
        new VideoOutputSettings(string.Empty, false), new VideoGainSettings(0, true),
        DefaultEncodingPreset.DefaultEncodingProfile, DefaultEncodingPreset.DefaultVideoPreset, DefaultEncodingPreset.DefaultDeinterlaceMode,
        new VideoBitrateSettings(2000, 4000), null, new AudioGainOptions()) { }

    internal VideoSettingsDialog(string inputPath, string? ffmpegExecutable,
        VideoOutputSettings commonOutput, VideoGainSettings commonGain,
        string commonEncodingProfile, string commonVideoPreset, string commonDeinterlaceMode, VideoBitrateSettings commonBitrate,
        VideoSettings? settings, AudioGainOptions analysisOptions, int selectionCount = 1)
    {
        _inputPath = inputPath;
        _ffmpegExecutable = ffmpegExecutable;
        _commonOutput = commonOutput;
        _commonGain = commonGain;
        _commonVideoPreset = commonVideoPreset;
        _commonDeinterlaceMode = commonDeinterlaceMode;
        _commonBitrate = commonBitrate;
        _commonEncodingProfile = commonEncodingProfile;
        _isMultiSelection = selectionCount > 1;
        _analysisOptions = analysisOptions;
        InitializeComponent();
        AudioStreamComboBox.SelectionChanged += (_, _) => UpdateControls();
        var fileName = Path.GetFileName(inputPath);
        var shortFileName = fileName.Length > 60 ? fileName[..57] + "..." : fileName;
        Title = _isMultiSelection
            ? $"{shortFileName} 외 {selectionCount - 1}개 비디오 설정"
            : $"{shortFileName} - 설정";
        StreamReferenceText.IsVisible = _isMultiSelection;
        StreamReferenceText.Text = $"'{shortFileName}' 기준 스트림입니다. 나머지 파일에 해당 인덱스가 없으면 기본 스트림으로 인코딩됩니다.";
        RestoreSettings(settings);
        Opened += LoadAudioStreams;
        Closed += (_, _) =>
        {
            _isClosed = true;
            _streamCancellation?.Cancel();
        };
    }

    private void RestoreSettings(VideoSettings? settings)
    {
        _isRestoringSettings = true;
        _isSavingBitrate = false;
        OverrideProfileCheckBox.IsChecked = settings?.EncodingProfile is not null;
        SelectOption(EncodingProfileComboBox, settings?.EncodingProfile ?? _commonEncodingProfile);
        var output = settings?.Output ?? _commonOutput;
        var gain = settings?.Gain ?? _commonGain;
        OverrideOutputCheckBox.IsChecked = settings?.Output is not null;
        OutputDirectoryTextBox.Text = output.Directory;
        UseSourceDirectoryCheckBox.IsChecked = output.UseSourceDirectory;
        OverrideGainCheckBox.IsChecked = settings?.Gain is not null;
        GainNumeric.Value = gain.GainDb;
        NormalizationCheckBox.IsChecked = gain.DynamicNormalization;
        OverridePresetCheckBox.IsChecked = settings?.VideoPreset is not null;
        SelectOption(VideoPresetComboBox, settings?.VideoPreset ?? _commonVideoPreset);
        OverrideDeinterlaceCheckBox.IsChecked = settings?.DeinterlaceMode is not null;
        SelectOption(DeinterlaceModeComboBox, settings?.DeinterlaceMode ?? _commonDeinterlaceMode);
        OverrideBitrateCheckBox.IsChecked = settings?.Bitrate is not null;
        var bitrate = settings?.Bitrate ?? _commonBitrate;
        MaxBitrateNumeric.Value = bitrate.MaxBitrate;
        BufferSizeNumeric.Value = bitrate.BufferSize;
        _audioStreamIndex = settings?.AudioStreamIndex ?? 0;
        if (_streams is null)
        {
            AudioStreamComboBox.ItemsSource = new[] { new AudioStreamInfo(_audioStreamIndex, $"#{_audioStreamIndex + 1}") };
            AudioStreamComboBox.SelectedIndex = 0;
        }
        else
        {
            AudioStreamComboBox.SelectedItem = _streams.FirstOrDefault(stream => stream.Index == _audioStreamIndex);
            StreamStatusText.Text = _streams.Count == 0 ? "오디오 스트림이 없습니다." : null;
        }
        _isRestoringSettings = false;
        UpdateControls();
    }

    private async void LoadAudioStreams(object? sender, EventArgs e)
    {
        using var cancellation = new CancellationTokenSource();
        _streamCancellation = cancellation;
        try
        {
            if (_ffmpegExecutable is null)
            {
                StreamStatusText.Text = "오디오 스트림 선택과 게인 분석은 FFmpeg 다운로드 후 사용할 수 있습니다.";
                return;
            }

            StreamStatusText.Text = "오디오 스트림을 확인하는 중…";
            var streams = await AudioStreamReader.ReadAsync(_ffmpegExecutable, _inputPath, cancellation.Token);
            if (_isClosed) return;
            _streams = streams;
            AudioStreamComboBox.ItemsSource = streams;
            AudioStreamComboBox.SelectedItem = streams.FirstOrDefault(stream => stream.Index == _audioStreamIndex);
            AudioStreamComboBox.IsEnabled = streams.Count > 0;
            StreamStatusText.Text = streams.Count == 0 ? "오디오 스트림이 없습니다." : null;
            if (_audioStreamIndex != 0 && AudioStreamComboBox.SelectedItem is null)
            {
                StreamStatusText.Text = $"저장된 오디오 스트림 #{_audioStreamIndex + 1}이 없습니다. 스트림을 다시 선택하거나 초기화하세요.";
            }
            UpdateControls();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (!_isClosed) StreamStatusText.Text = $"스트림 확인 오류: {exception.Message}";
        }
        finally
        {
            _streamCancellation = null;
        }
    }

    private void OverrideChanged(object? sender, RoutedEventArgs e) => UpdateControls();

    private void ProfileChanged(object? sender, SelectionChangedEventArgs e) => UpdateControls();

    private string EffectiveEncodingProfile => OverrideProfileCheckBox.IsChecked == true
        ? (EncodingProfileComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? _commonEncodingProfile
        : _commonEncodingProfile;

    private void UpdateControls()
    {
        if (_isRestoringSettings || OutputDirectoryTextBox is null || GainPanel is null || BitratePanel is null) return;
        var isSaving = EffectiveEncodingProfile == DefaultEncodingPreset.EncodingProfileSaving;
        var isAudioOnly = EffectiveEncodingProfile == DefaultEncodingPreset.EncodingProfileAudioOnly;
        EncodingProfileComboBox.IsEnabled = OverrideProfileCheckBox.IsChecked == true;
        VideoOptionsGrid.IsVisible = !isAudioOnly;
        BitrateSettingsPanel.IsVisible = !isAudioOnly;
        OverrideBitrateCheckBox.IsVisible = !isSaving;
        SavingBitrateLabel.IsVisible = isSaving;
        if (isSaving && !_isSavingBitrate)
        {
            _editableBitrate = new VideoBitrateSettings(MaxBitrateNumeric.Value ?? _commonBitrate.MaxBitrate,
                BufferSizeNumeric.Value ?? _commonBitrate.BufferSize);
        }
        if (isSaving)
        {
            MaxBitrateNumeric.Value = 900;
            BufferSizeNumeric.Value = 900;
        }
        else if (_isSavingBitrate && _editableBitrate is { } editable)
        {
            MaxBitrateNumeric.Value = editable.MaxBitrate;
            BufferSizeNumeric.Value = editable.BufferSize;
        }
        _isSavingBitrate = isSaving;
        MaxBitrateNumeric.IsReadOnly = isSaving;
        BufferSizeNumeric.IsReadOnly = isSaving;
        var outputEnabled = OverrideOutputCheckBox.IsChecked == true;
        var useSource = UseSourceDirectoryCheckBox.IsChecked == true;
        OutputDirectoryTextBox.IsEnabled = outputEnabled;
        OutputDirectoryTextBox.IsReadOnly = useSource;
        UseSourceDirectoryCheckBox.IsEnabled = outputEnabled;
        PickOutputFolderButton.IsEnabled = outputEnabled && !useSource;
        GainPanel.IsEnabled = OverrideGainCheckBox.IsChecked == true;
        VideoPresetComboBox.IsEnabled = OverridePresetCheckBox.IsChecked == true;
        DeinterlaceModeComboBox.IsEnabled = OverrideDeinterlaceCheckBox.IsChecked == true;
        BitratePanel.IsEnabled = isSaving || OverrideBitrateCheckBox.IsChecked == true;
        AnalyzeGainButton.IsEnabled = !_isMultiSelection && _streams is { Count: > 0 }
            && AudioStreamComboBox.SelectedItem is AudioStreamInfo;
    }

    private async void PickOutputFolder(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "출력 폴더 선택",
            AllowMultiple = false
        });
        if (folders.Count > 0) OutputDirectoryTextBox.Text = folders[0].TryGetLocalPath();
    }

    private async void AnalyzeGain(object? sender, RoutedEventArgs e)
    {
        if (_isMultiSelection || _ffmpegExecutable is null ||
            AudioStreamComboBox.SelectedItem is not AudioStreamInfo stream) return;
        var dialog = new AudioGainDialog(_ffmpegExecutable, _inputPath, _analysisOptions, stream.Index);
        var gain = await dialog.ShowDialog<decimal?>(this);
        _analysisOptions = dialog.Options;
        if (gain is null || _isClosed) return;
        if (NormalizationCheckBox.IsChecked == true &&
            await MainWindow.CreateDialog("확인", "노멀라이징이 체크되어 있습니다. 해제할까요?",
                [("네", true), ("아니오", false)], "노멀라이징을 해제하지 않으면 과도한 클리핑이 발생할 수 있습니다.")
                .ShowDialog<bool>(this))
        {
            NormalizationCheckBox.IsChecked = false;
        }
        GainNumeric.Value = gain;
    }

    private void ResetSettings(object? sender, RoutedEventArgs e)
    {
        RestoreSettings(null);
        ErrorText.IsVisible = false;
    }

    private void ApplySettings(object? sender, RoutedEventArgs e)
    {
        ErrorText.IsVisible = false;
        VideoOutputSettings? output = null;
        VideoGainSettings? gain = null;
        if (OverrideOutputCheckBox.IsChecked == true)
        {
            output = new VideoOutputSettings(OutputDirectoryTextBox.Text?.Trim() ?? string.Empty,
                UseSourceDirectoryCheckBox.IsChecked == true);
            try
            {
                var directory = output.ResolveDirectory(_inputPath);
                if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException();
                Path.GetFullPath(directory);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                ShowError("저장 위치를 확인하세요.");
                return;
            }
        }
        if (OverrideGainCheckBox.IsChecked == true)
        {
            if (GainNumeric.Value is not { } value || value is < -60 or > 60)
            {
                ShowError("게인은 -60~60 dB 범위의 숫자로 입력하세요.");
                return;
            }
            gain = new VideoGainSettings(value, NormalizationCheckBox.IsChecked == true);
        }
        var profile = OverrideProfileCheckBox.IsChecked == true
            ? (EncodingProfileComboBox.SelectedItem as ComboBoxItem)?.Tag as string : null;
        if (OverrideProfileCheckBox.IsChecked == true && profile is null)
        {
            ShowError("인코딩 프로필을 확인하세요.");
            return;
        }
        var isAudioOnly = EffectiveEncodingProfile == DefaultEncodingPreset.EncodingProfileAudioOnly;
        var isSaving = EffectiveEncodingProfile == DefaultEncodingPreset.EncodingProfileSaving;
        var preset = !isAudioOnly && OverridePresetCheckBox.IsChecked == true
            ? (VideoPresetComboBox.SelectedItem as ComboBoxItem)?.Tag as string : null;
        var deinterlace = !isAudioOnly && OverrideDeinterlaceCheckBox.IsChecked == true
            ? (DeinterlaceModeComboBox.SelectedItem as ComboBoxItem)?.Tag as string : null;
        if (!isAudioOnly && (OverridePresetCheckBox.IsChecked == true && preset is null ||
            OverrideDeinterlaceCheckBox.IsChecked == true && deinterlace is null))
        {
            ShowError("프리셋과 디인터레이싱 옵션을 확인하세요.");
            return;
        }
        VideoBitrateSettings? bitrate = null;
        if (!isSaving && !isAudioOnly && OverrideBitrateCheckBox.IsChecked == true)
        {
            if (MaxBitrateNumeric.Value is not { } maxBitrate || maxBitrate is < 1 or > 1000000 ||
                BufferSizeNumeric.Value is not { } bufferSize || bufferSize is < 1 or > 1000000)
            {
                ShowError("MBR과 버퍼는 1~1,000,000 k 범위의 숫자로 입력하세요.");
                return;
            }
            bitrate = new VideoBitrateSettings(maxBitrate, bufferSize);
        }
        var streamIndex = (AudioStreamComboBox.SelectedItem as AudioStreamInfo)?.Index ?? _audioStreamIndex;
        if (_streams is not null && streamIndex != 0 && !_streams.Any(stream => stream.Index == streamIndex))
        {
            ShowError("오디오 스트림을 다시 선택하거나 초기화하세요.");
            return;
        }
        var settings = new VideoSettings(output, gain, streamIndex == 0 ? null : streamIndex,
            FallbackToDefaultAudioStream: _isMultiSelection && streamIndex != 0,
            VideoPreset: preset, DeinterlaceMode: deinterlace, Bitrate: bitrate, EncodingProfile: profile);
        Close(new VideoSettingsDialogResult(settings.HasOverrides ? settings : null));
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }

    private static void SelectOption(ComboBox comboBox, string tag) =>
        comboBox.SelectedItem = comboBox.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag as string == tag);

    private void CloseDialog(object? sender, RoutedEventArgs e) => Close();
}
