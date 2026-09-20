using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Encoder127c.Encoding.Models;
using Encoder127c.Encoding.Services;
using Encoder127c.Encoding.Validation;
using Encoder127c.Fdkaac.Services;
using Encoder127c.Ffmpeg.Services;
using Encoder127c.Settings;

namespace Encoder127c;

public partial class MainWindow : Window
{
    private readonly IFfmpegManager _ffmpegManager;
    private readonly IFdkaacManager _fdkaacManager;
    private readonly IVideoEncodingRequestValidator _requestValidator;
    private readonly IVideoEncoder _videoEncoder;
    private CancellationTokenSource? _encodingCancellation;
    private string? _ffmpegExecutable;
    private string? _fdkaacExecutable;
    private bool _isFdkaacSupported = true;
    private bool _isPreparingEncoders;
    private bool _isEncoding;
    private bool _encodingControlsEnabled = true;
    private bool _isLogVisible;
    private EncodingQueueItem? _selectedItem;
    private decimal _defaultVideoMaxBitrate = 2000;
    private decimal _defaultVideoBufferSize = 4000;

    public ObservableCollection<EncodingQueueItem> EncodingQueue { get; } = [];

    public MainWindow() : this(
        FfmpegServiceFactory.CreateDefault(),
        VideoEncodingServices.CreateDefault())
    {
    }

    internal MainWindow(
        IFfmpegManager ffmpegManager,
        VideoEncodingServices videoEncodingServices)
    {
        _ffmpegManager = ffmpegManager;
        _fdkaacManager = videoEncodingServices.FdkaacManager;
        _requestValidator = videoEncodingServices.RequestValidator;
        _videoEncoder = videoEncodingServices.Encoder;
        InitializeComponent();
        Title = $"127c-encoder v{GetApplicationVersion()}";
        DataContext = this;
        DragDrop.SetAllowDrop(QueueDropBorder, true);
        DragDrop.AddDragOverHandler(QueueDropBorder, QueueDragOver);
        DragDrop.AddDropHandler(QueueDropBorder, QueueDrop);
        // When the queue has items, the ListBox covers the drop border and can
        // consume the routed event before it reaches the border.
        DragDrop.SetAllowDrop(QueueListBox, true);
        DragDrop.AddDragOverHandler(QueueListBox, QueueDragOver);
        DragDrop.AddDropHandler(QueueListBox, QueueDrop);
        ApplyDefaultSettings();
        RestoreSettings();
        Opened += CheckEncoderAvailability;
        Closing += SaveSettings;
        UpdateQueueUi();
    }

    private static string GetApplicationVersion()
    {
        var assembly = typeof(MainWindow).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString(3)
            ?? "unknown";
    }

    private async void PickInputFiles(object? sender, RoutedEventArgs e)
    {
        var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storageProvider is null || _isEncoding)
        {
            return;
        }

        var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "입력 비디오 추가",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("비디오 파일")
                {
                    Patterns = ["*.mp4", "*.mkv", "*.avi", "*.mov", "*.webm", "*.m4v"]
                },
                FilePickerFileTypes.All
            ]
        });

        AddFiles(files.Select(file => file.TryGetLocalPath()));
    }

    private void QueueDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = !_isEncoding && e.DataTransfer.TryGetFiles()?.Length > 0
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void QueueDrop(object? sender, DragEventArgs e)
    {
        if (_isEncoding)
        {
            return;
        }

        AddFiles(e.DataTransfer.TryGetFiles()?.Select(file => file.TryGetLocalPath()) ?? []);
        e.Handled = true;
    }

    private void AddFiles(IEnumerable<string?> paths)
    {
        var existingPaths = new HashSet<string>(
            EncodingQueue.Select(item => item.Path),
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var added = 0;
        var requeued = 0;

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            try
            {
                var fullPath = Path.GetFullPath(path);
                if (!File.Exists(fullPath))
                {
                    continue;
                }

                if (!existingPaths.Add(fullPath))
                {
                    var existingItem = EncodingQueue.First(item =>
                        string.Equals(item.Path, fullPath, OperatingSystem.IsWindows()
                            ? StringComparison.OrdinalIgnoreCase
                            : StringComparison.Ordinal));
                    if (existingItem.Status == EncodingQueueStatus.Completed)
                    {
                        existingItem.Status = EncodingQueueStatus.Pending;
                        requeued++;
                    }

                    continue;
                }

                EncodingQueue.Add(new EncodingQueueItem(fullPath));
                added++;
            }
            catch (Exception exception) when (exception is
                ArgumentException or
                NotSupportedException or
                PathTooLongException or
                UnauthorizedAccessException or
                IOException)
            {
                // Ignore paths that cannot be represented locally.
            }
        }

        if (added > 0 || requeued > 0)
        {
            SetStatus(requeued > 0
                ? $"{added}개 파일을 추가하고 완료된 {requeued}개 파일을 다시 대기열에 넣었습니다. 총 {EncodingQueue.Count}개"
                : $"{added}개 파일을 추가했습니다. 총 {EncodingQueue.Count}개");
        }

        UpdateQueueUi();
    }

    private void QueueSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isEncoding)
        {
            // 인코딩 중에는 선택을 바꾸지 않되 ListBox의 스크롤은 유지
            QueueListBox.SelectedItem = _selectedItem;
            return;
        }

        _selectedItem = QueueListBox.SelectedItem as EncodingQueueItem;
        UpdateQueueUi();
    }

    private void RemoveSelectedFile(object? sender, RoutedEventArgs e)
    {
        if (_isEncoding || _selectedItem is null)
        {
            return;
        }

        EncodingQueue.Remove(_selectedItem);
        _selectedItem = null;
        UpdateQueueUi();
    }

    private void ClearFiles(object? sender, RoutedEventArgs e)
    {
        if (_isEncoding)
        {
            return;
        }

        EncodingQueue.Clear();
        _selectedItem = null;
        SetStatus("파일 목록을 비웠습니다.");
        UpdateQueueUi();
    }

    private void MoveSelectedUp(object? sender, RoutedEventArgs e) => MoveSelected(-1);

    private void MoveSelectedDown(object? sender, RoutedEventArgs e) => MoveSelected(1);

    private void MoveSelected(int direction)
    {
        if (_isEncoding || _selectedItem is null)
        {
            return;
        }

        var oldIndex = EncodingQueue.IndexOf(_selectedItem);
        var newIndex = oldIndex + direction;
        if (oldIndex < 0 || newIndex < 0 || newIndex >= EncodingQueue.Count)
        {
            return;
        }

        EncodingQueue.Move(oldIndex, newIndex);
        QueueListBox.SelectedItem = _selectedItem;
        UpdateQueueUi();
    }

    private async void PickOutputFolder(object? sender, RoutedEventArgs e)
    {
        var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storageProvider is null || _isEncoding)
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
    {
        var outputDirectory = OutputDirectoryTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            SetStatus("열 출력 폴더를 먼저 선택하세요.");
            return;
        }

        try
        {
            var fullOutputDirectory = Path.GetFullPath(outputDirectory);
            Directory.CreateDirectory(fullOutputDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = fullOutputDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception exception) when (exception is
            ArgumentException or
            NotSupportedException or
            PathTooLongException or
            UnauthorizedAccessException or
            IOException or
            System.ComponentModel.Win32Exception)
        {
            SetStatus("출력 폴더를 열 수 없습니다.");
        }
    }

    private void ResetSettings(object? sender, RoutedEventArgs e)
    {
        if (_isEncoding)
        {
            return;
        }

        ApplyDefaultSettings();
        SetStatus("설정을 기본값으로 되돌렸습니다.");
    }

    private async void StartEncoding(object? sender, RoutedEventArgs e)
    {
        if (_isEncoding)
        {
            StopEncoding();
            return;
        }

        if (string.IsNullOrWhiteSpace(_ffmpegExecutable))
        {
            SetStatus("인코더를 먼저 다운로드하세요.");
            return;
        }

        var filesToEncode = EncodingQueue
            .Where(item => item.Status != EncodingQueueStatus.Completed)
            .ToList();
        if (filesToEncode.Count == 0)
        {
            SetStatus(EncodingQueue.Count == 0
                ? "인코딩할 비디오 파일을 추가하세요."
                : "모든 파일이 이미 완료되었습니다.");
            return;
        }

        var initialValidation = _requestValidator.Validate(CreateEncodingRequest(filesToEncode[0].Path));
        if (!initialValidation.IsValid)
        {
            SetStatus(initialValidation.ErrorMessage!);
            return;
        }

        _isEncoding = true;
        _encodingCancellation = new CancellationTokenSource();
        SetEncodingControlsEnabled(false);
        EncodeButton.IsEnabled = true;
        EncodeButtonIcon.Icon = FluentIcons.Common.Icon.DismissCircle;
        EncodeButtonText.Text = "중지하기";
        LogTextBox.Text = string.Empty;
        ShowIndeterminateProgress();

        var completedCount = EncodingQueue.Count(item => item.Status == EncodingQueueStatus.Completed);
        string? finalStatus = null;
        try
        {
            foreach (var item in filesToEncode)
            {
                _encodingCancellation.Token.ThrowIfCancellationRequested();
                var itemNumber = EncodingQueue.IndexOf(item) + 1;
                var validation = _requestValidator.Validate(CreateEncodingRequest(item.Path));
                if (!validation.IsValid)
                {
                    item.Status = EncodingQueueStatus.Failed;
                    SetStatus(FormatEncodingStatus(itemNumber, "영상 인코딩 실패", item.FileName));
                    AppendLog($"[오류] {item.FileName}: {validation.ErrorMessage}");
                    continue;
                }

                var request = validation.Request!;
                item.Status = EncodingQueueStatus.Encoding;
                var startMessage = FormatEncodingStatus(itemNumber, "영상 인코딩 시작", item.FileName);
                SetStatus(startMessage);
                AppendLog($"[시작] {item.FileName} → {Path.GetFileName(request.OutputPath)}");
                ShowIndeterminateProgress(startMessage);

                try
                {
                    var result = await _videoEncoder.EncodeAsync(
                        _ffmpegExecutable,
                        request,
                        new Progress<string>(AppendLog),
                        new Progress<EncodingProgress>(progress => UpdateEncodingProgress(progress, itemNumber)),
                        _encodingCancellation.Token);

                    if (result.ExitCode == 0)
                    {
                        item.Status = EncodingQueueStatus.Completed;
                        completedCount++;
                        SetStatus(FormatEncodingStatus(itemNumber, "영상 인코딩 완료", item.FileName));
                        AppendLog($"[완료] {item.FileName} → {Path.GetFileName(request.OutputPath)}");
                    }
                    else
                    {
                        item.Status = EncodingQueueStatus.Failed;
                        SetStatus(FormatEncodingStatus(itemNumber, "영상 인코딩 실패", item.FileName));
                        AppendLog($"[오류] {item.FileName}: ffmpeg 종료 코드 {result.ExitCode}");
                    }
                }
                catch (OperationCanceledException) when (_encodingCancellation.IsCancellationRequested)
                {
                    item.Status = EncodingQueueStatus.Stopped;
                    throw;
                }
                catch (Exception exception)
                {
                    item.Status = EncodingQueueStatus.Failed;
                    SetStatus(FormatEncodingStatus(itemNumber, "영상 인코딩 실패", item.FileName));
                    AppendLog($"[오류] {item.FileName}: {exception.Message}");
                }
            }

            finalStatus = $"[{DateTime.Now:HH:mm:ss}] 전체 인코딩 완료: {completedCount}/{EncodingQueue.Count}개";
        }
        catch (OperationCanceledException) when (_encodingCancellation.IsCancellationRequested)
        {
            finalStatus = $"[{DateTime.Now:HH:mm:ss}] 인코딩 중지: 완료된 {completedCount}개 파일은 다음 실행에서 건너뜁니다.";
            AppendLog("[중지] 현재 인코딩을 즉시 중단했습니다.");
        }
        finally
        {
            _encodingCancellation.Dispose();
            _encodingCancellation = null;
            _isEncoding = false;
            SetEncodingControlsEnabled(true);
            UpdateEncodeButton();
            HideEncodingProgress();
            if (finalStatus is not null)
            {
                SetStatus(finalStatus);
            }
        }
    }

    private void StopEncoding()
    {
        if (!_isEncoding || _encodingCancellation is null)
        {
            return;
        }

        EncodeButton.IsEnabled = false;
        SetStatus("인코딩 프로세스를 중지하는 중...");
        _encodingCancellation.Cancel();
    }

    private async void CheckEncoderAvailability(object? sender, EventArgs e)
    {
        if (_isPreparingEncoders)
        {
            return;
        }

        _isPreparingEncoders = true;
        EncodeButton.IsEnabled = false;
        DownloadEncodersButton.IsEnabled = false;
        try
        {
            _ffmpegExecutable = await _ffmpegManager.FindAvailableExecutableAsync();

            try
            {
                _fdkaacExecutable = await _fdkaacManager.FindAvailableExecutableAsync();
            }
            catch (PlatformNotSupportedException)
            {
                _fdkaacExecutable = null;
                _isFdkaacSupported = false;
            }

            DownloadEncodersButton.IsVisible = _ffmpegExecutable is null
                || (_isFdkaacSupported && _fdkaacExecutable is null);
            DownloadEncodersButton.IsEnabled = DownloadEncodersButton.IsVisible;
            SetStatus(_ffmpegExecutable is not null
                ? "인코더 준비 완료"
                : "FFmpeg가 없습니다. 다운로드 후 인코딩할 수 있습니다.");
        }
        catch (Exception exception)
        {
            DownloadEncodersButton.IsVisible = true;
            DownloadEncodersButton.IsEnabled = true;
            SetStatus($"인코더 확인 오류: {exception.Message}");
        }
        finally
        {
            _isPreparingEncoders = false;
            UpdateEncodeButton();
            if (_ffmpegExecutable is not null)
            {
                SetStatus("인코더 준비됨");
            }
        }
    }

    private async void DownloadEncoders(object? sender, RoutedEventArgs e)
    {
        if (_isPreparingEncoders || _isEncoding)
        {
            return;
        }

        _isPreparingEncoders = true;
        EncodeButton.IsEnabled = false;
        DownloadEncodersButton.IsEnabled = false;
        ShowIndeterminateProgress();
        LogTextBox.Text = string.Empty;
        try
        {
            AppendLog("[인코더 준비] FFmpeg와 fdkaac 준비를 시작합니다.");

            _ffmpegExecutable = await _ffmpegManager.EnsureAvailableAsync(
                new Progress<string>(message => ReportEncoderPreparation("FFmpeg", message)));

            try
            {
                _fdkaacExecutable = await _fdkaacManager.EnsureAvailableAsync(
                    new Progress<string>(message => ReportEncoderPreparation("fdkaac", message)));
            }
            catch (PlatformNotSupportedException)
            {
                _fdkaacExecutable = null;
                _isFdkaacSupported = false;
                AppendLog("[fdkaac 준비] 현재 플랫폼에서는 fdkaac 자동 설치를 지원하지 않습니다.");
            }

            DownloadEncodersButton.IsVisible = _isFdkaacSupported && _fdkaacExecutable is null;
            DownloadEncodersButton.IsEnabled = DownloadEncodersButton.IsVisible;
            SetStatus("인코더 준비 완료");
            AppendLog("[인코더 준비] 인코딩을 시작할 수 있습니다.");
        }
        catch (Exception exception)
        {
            DownloadEncodersButton.IsVisible = true;
            DownloadEncodersButton.IsEnabled = true;
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

    private VideoEncodingRequest CreateEncodingRequest(string inputPath) => new(
        inputPath,
        OutputDirectoryTextBox.Text ?? string.Empty,
        GetSelectedTag(EncodingProfileComboBox) ?? DefaultEncodingPreset.DefaultEncodingProfile,
        GetSelectedTag(VideoPresetComboBox) ?? DefaultEncodingPreset.DefaultVideoPreset,
        IsSavingEncodingProfile()
            ? DefaultEncodingPreset.SavingVideoMaxBitrate
            : FormatKiloBitrate(VideoMaxBitrateNumericUpDown.Value, DefaultEncodingPreset.DefaultVideoMaxBitrate),
        IsSavingEncodingProfile()
            ? DefaultEncodingPreset.SavingVideoBufferSize
            : FormatKiloBitrate(VideoBufferSizeNumericUpDown.Value, DefaultEncodingPreset.DefaultVideoBufferSize),
        GetSelectedTag(DeinterlaceModeComboBox) ?? DefaultEncodingPreset.DefaultDeinterlaceMode,
        (AudioGainNumericUpDown.Value ?? 0).ToString("0", CultureInfo.InvariantCulture),
        DynamicAudioNormalizationCheckBox.IsChecked == true);

    private static string FormatKiloBitrate(decimal? value, string fallback) =>
        value is decimal kiloBitrate
            ? $"{kiloBitrate.ToString("0", CultureInfo.InvariantCulture)}k"
            : fallback;

    private static string? GetSelectedTag(ComboBox comboBox) =>
        (comboBox.SelectedItem as ComboBoxItem)?.Tag as string;

    private bool IsSavingEncodingProfile() =>
        string.Equals(
            GetSelectedTag(EncodingProfileComboBox),
            DefaultEncodingPreset.EncodingProfileSaving,
            StringComparison.Ordinal);

    private void EncodingProfileChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (IsSavingEncodingProfile())
        {
            CaptureDefaultVideoBitrates();
            VideoMaxBitrateNumericUpDown.Value = 900;
            VideoBufferSizeNumericUpDown.Value = 900;
        }
        else
        {
            VideoMaxBitrateNumericUpDown.Value = _defaultVideoMaxBitrate;
            VideoBufferSizeNumericUpDown.Value = _defaultVideoBufferSize;
        }

        UpdateEncodingProfileControls();
    }

    private void UpdateEncodingProfileControls()
    {
        var isSavingProfile = IsSavingEncodingProfile();
        VideoMaxBitrateNumericUpDown.IsReadOnly = isSavingProfile;
        VideoBufferSizeNumericUpDown.IsReadOnly = isSavingProfile;
        VideoMaxBitrateNumericUpDown.IsEnabled = _encodingControlsEnabled && !isSavingProfile;
        VideoBufferSizeNumericUpDown.IsEnabled = _encodingControlsEnabled && !isSavingProfile;
    }

    private void ApplyDefaultSettings()
    {
        _defaultVideoMaxBitrate = 2000;
        _defaultVideoBufferSize = 4000;
        OutputDirectoryTextBox.Text = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "encoded"));
        SelectComboBoxItem(EncodingProfileComboBox, DefaultEncodingPreset.DefaultEncodingProfile);
        SelectComboBoxItem(VideoPresetComboBox, "slow");
        SelectComboBoxItem(DeinterlaceModeComboBox, DefaultEncodingPreset.DefaultDeinterlaceMode);
        VideoMaxBitrateNumericUpDown.Value = 2000;
        VideoBufferSizeNumericUpDown.Value = 4000;
        AudioGainNumericUpDown.Value = 0;
        DynamicAudioNormalizationCheckBox.IsChecked = false;
    }

    private void RestoreSettings()
    {
        var settings = EncoderSettingsStore.Load();
        if (settings is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(settings.OutputDirectory))
        {
            OutputDirectoryTextBox.Text = settings.OutputDirectory;
        }

        var encodingProfile = settings.EncodingProfile ?? DefaultEncodingPreset.DefaultEncodingProfile;
        _defaultVideoMaxBitrate = ClampToRange(
            settings.DefaultVideoMaxBitrate ?? (IsDefaultEncodingProfile(encodingProfile) ? settings.VideoMaxBitrate : null),
            1, 1_000_000, 2000);
        _defaultVideoBufferSize = ClampToRange(
            settings.DefaultVideoBufferSize ?? (IsDefaultEncodingProfile(encodingProfile) ? settings.VideoBufferSize : null),
            1, 1_000_000, 4000);

        SelectComboBoxItem(VideoPresetComboBox, settings.VideoPreset);
        SelectComboBoxItem(EncodingProfileComboBox, encodingProfile);
        SelectComboBoxItem(DeinterlaceModeComboBox, settings.DeinterlaceMode);
        if (!IsSavingEncodingProfile())
        {
            VideoMaxBitrateNumericUpDown.Value = _defaultVideoMaxBitrate;
            VideoBufferSizeNumericUpDown.Value = _defaultVideoBufferSize;
        }
        AudioGainNumericUpDown.Value = ClampToRange(settings.AudioGain, -60, 60, 0);
        DynamicAudioNormalizationCheckBox.IsChecked = settings.DynamicAudioNormalization;

        UpdateEncodingProfileControls();
    }

    private void SaveSettings(object? sender, WindowClosingEventArgs e)
    {
        if (!IsSavingEncodingProfile())
        {
            CaptureDefaultVideoBitrates();
        }

        EncoderSettingsStore.Save(new EncoderSettings
        {
            OutputDirectory = OutputDirectoryTextBox.Text?.Trim(),
            EncodingProfile = GetSelectedTag(EncodingProfileComboBox),
            VideoPreset = GetSelectedTag(VideoPresetComboBox),
            DeinterlaceMode = GetSelectedTag(DeinterlaceModeComboBox),
            VideoMaxBitrate = VideoMaxBitrateNumericUpDown.Value,
            VideoBufferSize = VideoBufferSizeNumericUpDown.Value,
            DefaultVideoMaxBitrate = _defaultVideoMaxBitrate,
            DefaultVideoBufferSize = _defaultVideoBufferSize,
            AudioGain = AudioGainNumericUpDown.Value,
            DynamicAudioNormalization = DynamicAudioNormalizationCheckBox.IsChecked == true
        });
    }

    private void CaptureDefaultVideoBitrates()
    {
        _defaultVideoMaxBitrate = ClampToRange(VideoMaxBitrateNumericUpDown.Value, 1, 1_000_000, 2000);
        _defaultVideoBufferSize = ClampToRange(VideoBufferSizeNumericUpDown.Value, 1, 1_000_000, 4000);
    }

    private static bool IsDefaultEncodingProfile(string profile) =>
        string.Equals(profile, DefaultEncodingPreset.EncodingProfileDefault, StringComparison.Ordinal);

    private static decimal ClampToRange(decimal? value, decimal minimum, decimal maximum, decimal fallback) =>
        value is decimal number && number >= minimum && number <= maximum ? number : fallback;

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
        DropHintTextBlock.IsVisible = EncodingQueue.Count == 0;
        var hasSelection = _selectedItem is not null;
        RemoveSelectedButton.IsEnabled = !_isEncoding && hasSelection;
        MoveUpButton.IsEnabled = !_isEncoding && hasSelection && EncodingQueue.IndexOf(_selectedItem!) > 0;
        MoveDownButton.IsEnabled = !_isEncoding && hasSelection && EncodingQueue.IndexOf(_selectedItem!) < EncodingQueue.Count - 1;
    }

    private void SetEncodingControlsEnabled(bool isEnabled)
    {
        _encodingControlsEnabled = isEnabled;
        AddFilesButton.IsEnabled = isEnabled;
        ClearFilesButton.IsEnabled = isEnabled;
        // ListBox 자체를 끄면 내부 ScrollViewer도 비활성화되므로 스크롤은 유지
        QueueDropBorder.IsEnabled = true;
        QueueListBox.IsEnabled = true;
        QueueListBox.Opacity = isEnabled ? 1 : 0.65;
        DragDrop.SetAllowDrop(QueueDropBorder, isEnabled);
        DragDrop.SetAllowDrop(QueueListBox, isEnabled);
        PickOutputFolderButton.IsEnabled = isEnabled;
        OutputDirectoryTextBox.IsEnabled = isEnabled;
        EncodingProfileComboBox.IsEnabled = isEnabled;
        VideoPresetComboBox.IsEnabled = isEnabled;
        UpdateEncodingProfileControls();
        DeinterlaceModeComboBox.IsEnabled = isEnabled;
        AudioGainNumericUpDown.IsEnabled = isEnabled;
        DynamicAudioNormalizationCheckBox.IsEnabled = isEnabled;
        ResetSettingsButton.IsEnabled = isEnabled;
        UpdateQueueUi();
    }

    private void UpdateEncodeButton()
    {
        if (_isEncoding)
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

    private void ToggleLogVisibility(object? sender, RoutedEventArgs e)
    {
        _isLogVisible = !_isLogVisible;
        LogPanel.IsVisible = _isLogVisible;
        UpdateAuxiliaryPanelVisibility();
        ToggleLogButtonText.Text = _isLogVisible ? "로그 숨김" : "로그 표시";

        if (_isLogVisible)
        {
            ScrollLogToEnd();
        }
    }

    private void SetStatus(string message)
    {
        if (_isEncoding)
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

    private void ShowIndeterminateProgress(string? message = null)
    {
        if (_isEncoding)
        {
            EncodingSettingsPanel.IsVisible = false;
        }

        EncodingProgressBar.Value = 0;
        EncodingProgressBar.IsIndeterminate = true;
        EncodingProgressBar.IsVisible = true;
        EncodingProgressTextBlock.Text = message ?? string.Empty;
        EncodingProgressTextBlock.IsVisible = !string.IsNullOrEmpty(message);
        UpdateAuxiliaryPanelVisibility();
    }

    private void HideEncodingProgress()
    {
        EncodingProgressBar.IsVisible = false;
        EncodingProgressTextBlock.IsVisible = false;
        EncodingSettingsPanel.IsVisible = true;
        UpdateAuxiliaryPanelVisibility();
    }

    private void UpdateAuxiliaryPanelVisibility() =>
        AuxiliaryPanel.IsVisible = _isLogVisible || EncodingProgressBar.IsVisible;

    private void UpdateEncodingProgress(EncodingProgress progress, int itemNumber)
    {
        if (progress.TotalDuration is not { } totalDuration || totalDuration <= TimeSpan.Zero)
        {
            return;
        }

        var percentage = Math.Clamp(
            progress.ProcessedDuration.TotalSeconds / totalDuration.TotalSeconds * 100,
            0,
            100);
        EncodingProgressBar.IsIndeterminate = false;
        EncodingProgressBar.Value = progress.IsCompleted ? 100 : percentage;
        EncodingProgressTextBlock.IsVisible = true;

        var speedText = progress.Speed is { } speed ? $" · {speed:0.00}x" : string.Empty;
        var etaText = progress.Speed is { } processingSpeed
            ? $" · 예상 남은 시간 {FormatDuration(TimeSpan.FromSeconds(
                Math.Max(0, (totalDuration - progress.ProcessedDuration).TotalSeconds / processingSpeed)))}"
            : " · 예상 남은 시간 계산 중...";
        EncodingProgressTextBlock.Text = progress.IsCompleted
            ? $"{itemNumber}/{EncodingQueue.Count} · 100% · 인코딩 마무리 중..."
            : $"{itemNumber}/{EncodingQueue.Count} · {percentage:0.0}%{speedText}{etaText}";
    }

    private static string FormatDuration(TimeSpan duration) => duration.TotalHours >= 1
        ? duration.ToString(@"h\:mm\:ss")
        : duration.ToString(@"m\:ss");

    private string FormatEncodingStatus(int itemNumber, string action, string fileName) =>
        $"[{DateTime.Now:HH:mm:ss}] {itemNumber}/{EncodingQueue.Count} {action}: {fileName}";

    private void AppendLog(string message)
    {
        LogTextBox.Text += $"{message}{Environment.NewLine}";
        ScrollLogToEnd();
    }

    private void ScrollLogToEnd()
    {
        Dispatcher.UIThread.Post(
            () => LogTextBox.GetVisualDescendants()
                .OfType<ScrollViewer>()
                .FirstOrDefault()?
                .ScrollToEnd(),
            DispatcherPriority.Background);
    }
}
