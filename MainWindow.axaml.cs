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
using Encoder127c.Ffmpeg.Services;

namespace Encoder127c;

public partial class MainWindow : Window
{
    private readonly IFfmpegManager _ffmpegManager;
    private readonly IVideoEncodingRequestValidator _requestValidator;
    private readonly IVideoEncoder _videoEncoder;
    private CancellationTokenSource? _encodingCancellation;
    private string? _ffmpegExecutable;
    private bool _isPreparingFfmpeg;
    private bool _isEncoding;
    private bool _isLogVisible;
    private EncodingQueueItem? _selectedItem;

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
        _requestValidator = videoEncodingServices.RequestValidator;
        _videoEncoder = videoEncodingServices.Encoder;
        InitializeComponent();
        Title = $"127c-encoder v{GetApplicationVersion()}";
        DataContext = this;
        DragDrop.SetAllowDrop(QueueDropBorder, true);
        DragDrop.AddDragOverHandler(QueueDropBorder, QueueDragOver);
        DragDrop.AddDropHandler(QueueDropBorder, QueueDrop);
        OutputDirectoryTextBox.Text = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "encoded"));
        Opened += CheckFfmpegAvailability;
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
        e.DragEffects = e.DataTransfer.TryGetFiles()?.Length > 0
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private void QueueDrop(object? sender, DragEventArgs e)
    {
        if (_isEncoding)
        {
            return;
        }

        AddFiles(e.DataTransfer.TryGetFiles()?.Select(file => file.TryGetLocalPath()) ?? []);
    }

    private void AddFiles(IEnumerable<string?> paths)
    {
        var existingPaths = new HashSet<string>(
            EncodingQueue.Select(item => item.Path),
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var added = 0;

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            try
            {
                var fullPath = Path.GetFullPath(path);
                if (!File.Exists(fullPath) || !existingPaths.Add(fullPath))
                {
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

        if (added > 0)
        {
            SetStatus($"{added}개 파일을 추가했습니다. 총 {EncodingQueue.Count}개");
        }

        UpdateQueueUi();
    }

    private void QueueSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
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

        _isEncoding = true;
        _encodingCancellation = new CancellationTokenSource();
        SetEncodingControlsEnabled(false);
        EncodeButton.IsEnabled = true;
        EncodeButtonIcon.Icon = FluentIcons.Common.Icon.DismissCircle;
        EncodeButtonText.Text = "중지하기";
        LogTextBox.Text = string.Empty;
        ShowIndeterminateProgress();

        var completedCount = EncodingQueue.Count(item => item.Status == EncodingQueueStatus.Completed);
        try
        {
            foreach (var item in filesToEncode)
            {
                _encodingCancellation.Token.ThrowIfCancellationRequested();
                var validation = _requestValidator.Validate(CreateEncodingRequest(item.Path));
                if (!validation.IsValid)
                {
                    item.Status = EncodingQueueStatus.Failed;
                    AppendLog($"[오류] {item.FileName}: {validation.ErrorMessage}");
                    continue;
                }

                var request = validation.Request!;
                item.Status = EncodingQueueStatus.Encoding;
                var itemNumber = EncodingQueue.IndexOf(item) + 1;
                SetStatus($"인코딩 중 ({itemNumber}/{EncodingQueue.Count}): {item.FileName}");
                AppendLog($"[시작] {item.FileName} → {Path.GetFileName(request.OutputPath)}");
                ShowIndeterminateProgress($"{itemNumber}/{EncodingQueue.Count} · {item.FileName}");

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
                        AppendLog($"[완료] {item.FileName} → {Path.GetFileName(request.OutputPath)}");
                    }
                    else
                    {
                        item.Status = EncodingQueueStatus.Failed;
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
                    AppendLog($"[오류] {item.FileName}: {exception.Message}");
                }
            }

            SetStatus($"인코딩 완료: {completedCount}/{EncodingQueue.Count}개");
        }
        catch (OperationCanceledException) when (_encodingCancellation.IsCancellationRequested)
        {
            SetStatus($"인코딩을 중지했습니다. 완료된 {completedCount}개 파일은 다음 실행에서 건너뜁니다.");
            AppendLog("[중지] 현재 인코딩을 즉시 중단했습니다.");
        }
        finally
        {
            _encodingCancellation.Dispose();
            _encodingCancellation = null;
            _isEncoding = false;
            SetEncodingControlsEnabled(true);
            UpdateEncodeButton();
            EncodingProgressBar.IsVisible = false;
            EncodingProgressTextBlock.IsVisible = false;
        }
    }

    private void StopEncoding()
    {
        if (!_isEncoding || _encodingCancellation is null)
        {
            return;
        }

        EncodeButton.IsEnabled = false;
        SetStatus("FFmpeg 프로세스를 중지하는 중...");
        _encodingCancellation.Cancel();
    }

    private async void CheckFfmpegAvailability(object? sender, EventArgs e)
    {
        if (_isPreparingFfmpeg)
        {
            return;
        }

        _isPreparingFfmpeg = true;
        EncodeButton.IsEnabled = false;
        DownloadFfmpegButton.IsEnabled = false;
        try
        {
            _ffmpegExecutable = await _ffmpegManager.FindAvailableExecutableAsync();
            if (_ffmpegExecutable is not null)
            {
                DownloadFfmpegButton.IsVisible = false;
                SetStatus("FFmpeg 준비 완료");
            }
            else
            {
                DownloadFfmpegButton.IsVisible = true;
                DownloadFfmpegButton.IsEnabled = true;
                SetStatus("FFmpeg가 없습니다. 다운로드 후 인코딩할 수 있습니다.");
            }
        }
        catch (Exception exception)
        {
            DownloadFfmpegButton.IsVisible = true;
            DownloadFfmpegButton.IsEnabled = true;
            SetStatus($"FFmpeg 확인 오류: {exception.Message}");
        }
        finally
        {
            _isPreparingFfmpeg = false;
            UpdateEncodeButton();
        }
    }

    private async void DownloadFfmpeg(object? sender, RoutedEventArgs e)
    {
        if (_isPreparingFfmpeg || _isEncoding)
        {
            return;
        }

        _isPreparingFfmpeg = true;
        EncodeButton.IsEnabled = false;
        DownloadFfmpegButton.IsEnabled = false;
        ShowIndeterminateProgress();
        LogTextBox.Text = string.Empty;
        try
        {
            AppendLog("[FFmpeg 준비] 다운로드를 시작합니다.");
            _ffmpegExecutable = await _ffmpegManager.EnsureAvailableAsync(
                new Progress<string>(ReportFfmpegPreparation));
            DownloadFfmpegButton.IsVisible = false;
            SetStatus("FFmpeg 준비 완료");
            AppendLog("[FFmpeg 준비] 인코딩을 시작할 수 있습니다.");
        }
        catch (Exception exception)
        {
            _ffmpegExecutable = null;
            DownloadFfmpegButton.IsVisible = true;
            DownloadFfmpegButton.IsEnabled = true;
            SetStatus($"FFmpeg 설치 오류: {exception.Message}");
            AppendLog($"[FFmpeg 준비] 오류: {exception}");
        }
        finally
        {
            _isPreparingFfmpeg = false;
            UpdateEncodeButton();
            EncodingProgressBar.IsVisible = false;
            EncodingProgressTextBlock.IsVisible = false;
        }
    }

    private VideoEncodingRequest CreateEncodingRequest(string inputPath) => new(
        inputPath,
        OutputDirectoryTextBox.Text ?? string.Empty,
        GetSelectedTag(VideoPresetComboBox) ?? DefaultEncodingPreset.DefaultVideoPreset,
        FormatKiloBitrate(VideoMaxBitrateNumericUpDown.Value, DefaultEncodingPreset.DefaultVideoMaxBitrate),
        FormatKiloBitrate(VideoBufferSizeNumericUpDown.Value, DefaultEncodingPreset.DefaultVideoBufferSize),
        (AudioGainNumericUpDown.Value ?? 0).ToString("0", CultureInfo.InvariantCulture),
        DynamicAudioNormalizationCheckBox.IsChecked == true);

    private static string FormatKiloBitrate(decimal? value, string fallback) =>
        value is decimal kiloBitrate
            ? $"{kiloBitrate.ToString("0", CultureInfo.InvariantCulture)}k"
            : fallback;

    private static string? GetSelectedTag(ComboBox comboBox) =>
        (comboBox.SelectedItem as ComboBoxItem)?.Tag as string;

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
        AddFilesButton.IsEnabled = isEnabled;
        ClearFilesButton.IsEnabled = isEnabled;
        PickOutputFolderButton.IsEnabled = isEnabled;
        OutputDirectoryTextBox.IsEnabled = isEnabled;
        VideoPresetComboBox.IsEnabled = isEnabled;
        VideoMaxBitrateNumericUpDown.IsEnabled = isEnabled;
        VideoBufferSizeNumericUpDown.IsEnabled = isEnabled;
        AudioGainNumericUpDown.IsEnabled = isEnabled;
        DynamicAudioNormalizationCheckBox.IsEnabled = isEnabled;
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
        EncodeButton.IsEnabled = !_isPreparingFfmpeg && !string.IsNullOrWhiteSpace(_ffmpegExecutable);
    }

    private void ToggleLogVisibility(object? sender, RoutedEventArgs e)
    {
        _isLogVisible = !_isLogVisible;
        LogPanel.IsVisible = _isLogVisible;
        MainLayoutGrid.RowDefinitions[7].Height = _isLogVisible
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(0);
        ToggleLogButtonText.Text = _isLogVisible ? "로그 숨김" : "로그 표시";

        if (_isLogVisible)
        {
            ScrollLogToEnd();
        }
    }

    private void SetStatus(string message) => StatusTextBlock.Text = message;

    private void ReportFfmpegPreparation(string message)
    {
        SetStatus(message);
        AppendLog($"[FFmpeg 준비] {message}");
    }

    private void ShowIndeterminateProgress(string? message = null)
    {
        EncodingProgressBar.Value = 0;
        EncodingProgressBar.IsIndeterminate = true;
        EncodingProgressBar.IsVisible = true;
        EncodingProgressTextBlock.Text = message ?? string.Empty;
        EncodingProgressTextBlock.IsVisible = !string.IsNullOrEmpty(message);
    }

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
