using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Encoder127c.Diagnostics;
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
    private bool _isPreparingEncoders;
    private bool _isEncoding;
    private bool _encodingControlsEnabled = true;
    private bool _isLogVisible;
    private readonly BoundedLogBuffer _logBuffer = new();
    private readonly DispatcherTimer _logTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
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
        _logTimer.Tick += (_, _) => FlushLog();
        Opened += (_, _) => _logTimer.Start();
        Closed += (_, _) => _logTimer.Stop();
        UpdateQueueUi();
    }

    private static string GetApplicationVersion()
    {
        var assembly = typeof(MainWindow).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString(3)
            ?? "unknown";
    }

    private async void PickInputFiles(object? sender, RoutedEventArgs e) =>
        await PickInputFilesAsync();

    private async void OpenFilesFromMenu(object? sender, EventArgs e) =>
        await PickInputFilesAsync();

    private void CloseFromMenu(object? sender, EventArgs e) => Close();

    private async Task PickInputFilesAsync()
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
            var startInfo = OperatingSystem.IsMacOS()
                ? new ProcessStartInfo("open") { UseShellExecute = false }
                : new ProcessStartInfo(fullOutputDirectory) { UseShellExecute = true };
            if (OperatingSystem.IsMacOS())
            {
                startInfo.ArgumentList.Add(fullOutputDirectory);
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
            SetStatus("FFmpeg를 먼저 다운로드하세요.");
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

        if (UseSourceDirectoryCheckBox.IsChecked != true)
        {
            var outputDirectory = Path.GetFullPath(OutputDirectoryTextBox.Text!.Trim());
            if (!Directory.Exists(outputDirectory))
            {
                var shouldCreateOutputDirectory = await ShowConfirmationDialogAsync(
                    "출력 폴더가 없습니다. 폴더를 만들까요?");
                if (!shouldCreateOutputDirectory)
                {
                    return;
                }

                try
                {
                    Directory.CreateDirectory(outputDirectory);
                }
                catch (Exception exception) when (exception is
                    ArgumentException or
                    NotSupportedException or
                    PathTooLongException or
                    UnauthorizedAccessException or
                    IOException or
                    System.Security.SecurityException)
                {
                    await ShowMessageDialogAsync("폴더를 만들 수 없습니다. 다른 경로를 지정해 주세요.");
                    return;
                }
            }
        }

        _isEncoding = true;
        _encodingCancellation = new CancellationTokenSource();
        SetEncodingControlsEnabled(false);
        EncodeButton.IsEnabled = true;
        EncodeButtonIcon.Icon = FluentIcons.Common.Icon.DismissCircle;
        EncodeButtonText.Text = "중지하기";
        ClearLog();
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
                if (!TryCheckOutputDirectoryWritable(request.OutputPath, out var writeErrorMessage))
                {
                    item.Status = EncodingQueueStatus.Failed;
                    SetStatus(FormatEncodingStatus(itemNumber, "영상 인코딩 실패", item.FileName));
                    AppendLog($"[오류] {item.FileName}: {writeErrorMessage}");
                    continue;
                }

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
                        _logBuffer,
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

            _fdkaacExecutable = await _fdkaacManager.FindAvailableExecutableAsync();

            DownloadEncodersButton.IsVisible = _ffmpegExecutable is null;
            DownloadEncodersButton.IsEnabled = DownloadEncodersButton.IsVisible;
            SetStatus(_ffmpegExecutable is null
                ? "FFmpeg가 없습니다. 다운로드 후 인코딩할 수 있습니다."
                : _fdkaacExecutable is null
                    ? "동봉된 fdkaac가 없거나 실행할 수 없습니다. 오디오 인코딩에는 앱 재설치가 필요합니다."
                    : "인코더 준비 완료");
        }
        catch (Exception exception)
        {
            DownloadEncodersButton.IsVisible = _ffmpegExecutable is null;
            DownloadEncodersButton.IsEnabled = DownloadEncodersButton.IsVisible;
            SetStatus($"인코더 확인 오류: {exception.Message}");
        }
        finally
        {
            _isPreparingEncoders = false;
            UpdateEncodeButton();
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
        ClearLog();
        try
        {
            AppendLog("[인코더 준비] FFmpeg 준비를 시작합니다.");

            _ffmpegExecutable = await _ffmpegManager.EnsureAvailableAsync(
                new Progress<string>(message => ReportEncoderPreparation("FFmpeg", message)));

            _fdkaacExecutable = await _fdkaacManager.FindAvailableExecutableAsync();
            DownloadEncodersButton.IsVisible = false;
            DownloadEncodersButton.IsEnabled = false;
            if (_fdkaacExecutable is null)
            {
                SetStatus("동봉된 fdkaac가 없거나 실행할 수 없습니다. 오디오 인코딩에는 앱 재설치가 필요합니다.");
                AppendLog("[fdkaac 확인] 오디오 인코딩에는 앱 재설치가 필요합니다.");
            }
            else
            {
                SetStatus("인코더 준비 완료");
                AppendLog("[인코더 준비] 인코딩을 시작할 수 있습니다.");
            }
        }
        catch (Exception exception)
        {
            DownloadEncodersButton.IsVisible = _ffmpegExecutable is null;
            DownloadEncodersButton.IsEnabled = DownloadEncodersButton.IsVisible;
            SetStatus($"FFmpeg 설치 오류: {exception.Message}");
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
        GetOutputDirectory(inputPath),
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

    private string GetOutputDirectory(string inputPath) =>
        UseSourceDirectoryCheckBox.IsChecked == true
            ? Path.GetDirectoryName(Path.GetFullPath(inputPath)) ?? string.Empty
            : OutputDirectoryTextBox.Text ?? string.Empty;

    private static bool TryCheckOutputDirectoryWritable(string outputPath, out string errorMessage)
    {
        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            errorMessage = "출력 폴더에 파일을 쓸 수 없습니다.";
            return false;
        }

        var probeCreated = false;
        try
        {
            using (var stream = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.WriteByte(0);
            }

            probeCreated = true;
            File.Delete(outputPath);
            probeCreated = false;
            errorMessage = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is
            ArgumentException or
            NotSupportedException or
            PathTooLongException or
            UnauthorizedAccessException or
            IOException or
            System.Security.SecurityException)
        {
            if (probeCreated)
            {
                try
                {
                    File.Delete(outputPath);
                }
                catch (Exception cleanupException) when (cleanupException is
                    ArgumentException or
                    NotSupportedException or
                    PathTooLongException or
                    UnauthorizedAccessException or
                    IOException or
                    System.Security.SecurityException)
                {
                    // Leave the original write error as the result for this item.
                }
            }

            errorMessage = "출력 폴더에 파일을 쓸 수 없습니다.";
            return false;
        }
    }

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

    private void UseSourceDirectoryChanged(object? sender, RoutedEventArgs e) =>
        UpdateOutputDirectoryControls();

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
        OutputDirectoryTextBox.Text = Path.GetFullPath(AppPaths.DefaultOutputDirectory);
        UseSourceDirectoryCheckBox.IsChecked = false;
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

        UseSourceDirectoryCheckBox.IsChecked = settings.UseSourceDirectory;

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
            UseSourceDirectory = UseSourceDirectoryCheckBox.IsChecked == true,
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

    private async Task<bool> ShowConfirmationDialogAsync(string message)
    {
        var dialog = CreateDialog(
            "확인",
            message,
            [
                ("예", true),
                ("아니오", false)
            ]);

        return await dialog.ShowDialog<bool>(this);
    }

    private async Task ShowMessageDialogAsync(string message)
    {
        var dialog = CreateDialog("오류", message, [("확인", true)]);
        await dialog.ShowDialog<bool>(this);
    }

    private static Window CreateDialog(
        string title,
        string message,
        (string Text, bool Result)[] buttons)
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
                MinWidth = 80
            };
            button.Click += (_, _) => dialog.Close(result);
            buttonPanel.Children.Add(button);
        }

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 20,
            Children =
            {
                new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap
                },
                buttonPanel
            }
        };

        return dialog;
    }

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
        UseSourceDirectoryCheckBox.IsEnabled = isEnabled;
        OutputDirectoryTextBox.IsEnabled = isEnabled;
        UpdateOutputDirectoryControls();
        EncodingProfileComboBox.IsEnabled = isEnabled;
        VideoPresetComboBox.IsEnabled = isEnabled;
        UpdateEncodingProfileControls();
        DeinterlaceModeComboBox.IsEnabled = isEnabled;
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
        OpenOutputFolderButton.IsEnabled = _encodingControlsEnabled && !useSourceDirectory;
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
            FlushLog();
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

    private string FormatEncodingStatus(int itemNumber, string action, string fileName) =>
        $"[{DateTime.Now:HH:mm:ss}] {itemNumber}/{EncodingQueue.Count} {action}: {fileName}";

    private void AppendLog(string message)
    {
        _logBuffer.Report(message);
    }

    private void ClearLog()
    {
        _logBuffer.Clear();
        LogTextBox.Text = string.Empty;
    }

    private void FlushLog()
    {
        if (!_isLogVisible || !_logBuffer.TryGetChangedText(out var text))
        {
            return;
        }

        LogTextBox.Text = text;
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
