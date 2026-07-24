using Avalonia.Controls;
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
    private string? _ffmpegExecutable;
    private bool _isPreparingFfmpeg;
    private bool _isEncoding;

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
        OutputDirectoryTextBox.Text = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "encoded"));
        Opened += CheckFfmpegAvailability;
    }

    private async void PickInputFile(object? sender, RoutedEventArgs e)
    {
        var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storageProvider is null)
        {
            return;
        }

        var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "입력 비디오 선택",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("비디오 파일")
                {
                    Patterns = ["*.mp4", "*.mkv", "*.avi", "*.mov", "*.webm", "*.m4v"]
                },
                FilePickerFileTypes.All
            ]
        });

        if (files.Count > 0)
        {
            InputPathTextBox.Text = files[0].TryGetLocalPath();
        }
    }

    private async void PickOutputFolder(object? sender, RoutedEventArgs e)
    {
        var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storageProvider is null)
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

    private async void StartEncoding(object? sender, RoutedEventArgs e)
    {
        if (_isEncoding)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_ffmpegExecutable))
        {
            SetStatus("FFmpeg를 먼저 다운로드하세요.");
            return;
        }

        var validation = _requestValidator.Validate(CreateEncodingRequest());
        if (!validation.IsValid)
        {
            SetStatus(validation.ErrorMessage!);
            return;
        }

        var request = validation.Request!;
        try
        {
            _isEncoding = true;
            EncodeButton.IsEnabled = false;
            ShowIndeterminateProgress("인코딩 정보를 확인하는 중...");
            LogTextBox.Text = string.Empty;
            SetStatus("인코딩 중...");
            var result = await _videoEncoder.EncodeAsync(
                _ffmpegExecutable,
                request,
                new Progress<string>(AppendLog),
                new Progress<EncodingProgress>(UpdateEncodingProgress));

            if (result.ExitCode == 0)
            {
                SetStatus($"완료: {request.OutputPath}");
            }
            else
            {
                SetStatus($"ffmpeg 실행 실패 (종료 코드 {result.ExitCode})");
            }
        }
        catch (Exception exception)
        {
            SetStatus($"오류: {exception.Message}");
        }
        finally
        {
            _isEncoding = false;
            EncodeButton.IsEnabled = true;
            EncodingProgressBar.IsVisible = false;
            EncodingProgressTextBlock.IsVisible = false;
        }
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
                EncodeButton.IsEnabled = true;
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
            EncodeButton.IsEnabled = true;
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
            EncodingProgressBar.IsVisible = false;
            EncodingProgressTextBlock.IsVisible = false;
        }
    }

    private VideoEncodingRequest CreateEncodingRequest()
    {
        return new VideoEncodingRequest(
            InputPathTextBox.Text ?? string.Empty,
            OutputDirectoryTextBox.Text ?? string.Empty,
            GetSelectedTag(VideoPresetComboBox) ?? DefaultEncodingPreset.DefaultVideoPreset,
            VideoMaxBitrateTextBox.Text ?? string.Empty,
            VideoBufferSizeTextBox.Text ?? string.Empty,
            AudioGainTextBox.Text ?? DefaultEncodingPreset.DefaultAudioGainDb,
            DynamicAudioNormalizationCheckBox.IsChecked == true);
    }

    private static string? GetSelectedTag(ComboBox comboBox)
    {
        return (comboBox.SelectedItem as ComboBoxItem)?.Tag as string;
    }

    private void SetStatus(string message)
    {
        StatusTextBlock.Text = message;
    }

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

    private void UpdateEncodingProgress(EncodingProgress progress)
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
            ? "100% · 인코딩 마무리 중..."
            : $"{percentage:0.0}%{speedText}{etaText}";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        return duration.TotalHours >= 1
            ? duration.ToString(@"h\:mm\:ss")
            : duration.ToString(@"m\:ss");
    }

    private void AppendLog(string message)
    {
        LogTextBox.Text += $"{message}{Environment.NewLine}";
        Dispatcher.UIThread.Post(
            () => LogTextBox.GetVisualDescendants()
                .OfType<ScrollViewer>()
                .FirstOrDefault()?
                .ScrollToEnd(),
            DispatcherPriority.Background);
    }
}
