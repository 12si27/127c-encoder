using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Encoder127c.Encoding.Models;
using Encoder127c.Encoding.Services;

namespace Encoder127c.Tools;

public partial class VideoTrimDialog : Window
{
    private readonly string _inputPath;
    private readonly string _ffmpegExecutable;
    private readonly DispatcherTimer _previewTimer;
    private CancellationTokenSource? _previewCancellation;
    private VideoMediaInfo? _mediaInfo;
    private Bitmap? _previewBitmap;
    private bool _isClosed;
    private bool _updatingPosition;

    public VideoTrimDialog() : this(string.Empty, string.Empty, null)
    {
    }

    internal VideoTrimDialog(string inputPath, string ffmpegExecutable, VideoTrimSettings? trim)
    {
        _inputPath = inputPath;
        _ffmpegExecutable = ffmpegExecutable;
        InitializeComponent();

        StartTrimTextBox.Text = VideoTrimInput.FormatSeconds(trim?.StartSeconds ?? 0);
        EndTrimTextBox.Text = VideoTrimInput.FormatSeconds(trim?.EndSeconds ?? 0);

        var fileName = Path.GetFileName(inputPath);
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            Title = $"{fileName} - 자르기 세부 조정";
        }

        _previewTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(140)
        };
        _previewTimer.Tick += PreviewTimerTick;

        Opened += LoadMediaInfo;
        Closed += (_, _) =>
        {
            _isClosed = true;
            _previewTimer.Stop();
            _previewCancellation?.Cancel();
            _previewCancellation?.Dispose();
            _previewCancellation = null;
            _previewBitmap?.Dispose();
            _previewBitmap = null;
        };
    }

    private async void LoadMediaInfo(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_ffmpegExecutable) || string.IsNullOrWhiteSpace(_inputPath))
        {
            ShowPreviewMessage("FFmpeg를 사용할 수 없어 미리보기를 표시할 수 없습니다.");
            return;
        }

        try
        {
            ShowPreviewMessage("미리보기를 준비하는 중…");
            _mediaInfo = await VideoPreviewReader.ReadInfoAsync(_ffmpegExecutable, _inputPath);
            if (_isClosed) return;

            PreviewSlider.Maximum = Math.Max(0.001, _mediaInfo.Duration.TotalSeconds);
            PreviewSlider.IsEnabled = true;
            DurationTextBlock.Text = $"길이 {FormatTimestamp(_mediaInfo.Duration)}";

            var initialPosition = Math.Clamp(
                VideoTrimInput.TryParseSeconds(StartTrimTextBox.Text, out var startSeconds)
                    ? (double)startSeconds : 0,
                0,
                _mediaInfo.Duration.TotalSeconds);
            SetPosition(initialPosition, refreshPreview: true);
        }
        catch (Exception exception)
        {
            if (!_isClosed) ShowPreviewMessage($"미리보기 준비 오류: {exception.Message}");
        }
    }

    private void PreviewSliderChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingPosition) return;
        UpdateTimestamp(PreviewSlider.Value);
        SchedulePreview();
    }

    private void TimestampKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ApplyTimestampText();
        e.Handled = true;
    }

    private void TimestampLostFocus(object? sender, RoutedEventArgs e) => ApplyTimestampText();

    private void ApplyTimestampText()
    {
        if (!TryParseTimestamp(TimestampTextBox.Text, out var position))
        {
            UpdateTimestamp(PreviewSlider.Value);
            return;
        }

        SetPosition(position.TotalSeconds, refreshPreview: true);
    }

    private void SeekMinus30(object? sender, RoutedEventArgs e) => AdjustPosition(-30);
    private void SeekMinus10(object? sender, RoutedEventArgs e) => AdjustPosition(-10);
    private void SeekPlus10(object? sender, RoutedEventArgs e) => AdjustPosition(10);
    private void SeekPlus30(object? sender, RoutedEventArgs e) => AdjustPosition(30);

    private void SeekMinusFrame(object? sender, RoutedEventArgs e) =>
        AdjustPosition(-1d / (_mediaInfo?.FrameRate ?? 30d));

    private void SeekPlusFrame(object? sender, RoutedEventArgs e) =>
        AdjustPosition(1d / (_mediaInfo?.FrameRate ?? 30d));

    private void SeekMinus10Frames(object? sender, RoutedEventArgs e) =>
        AdjustPosition(-10d / (_mediaInfo?.FrameRate ?? 30d));

    private void SeekPlus10Frames(object? sender, RoutedEventArgs e) =>
        AdjustPosition(10d / (_mediaInfo?.FrameRate ?? 30d));

    private void AdjustPosition(double deltaSeconds) =>
        SetPosition(PreviewSlider.Value + deltaSeconds, refreshPreview: true);

    private void SetPosition(double seconds, bool refreshPreview)
    {
        var maximum = _mediaInfo?.Duration.TotalSeconds ?? PreviewSlider.Maximum;
        var value = Math.Clamp(seconds, 0, Math.Max(0, maximum));

        _updatingPosition = true;
        PreviewSlider.Value = value;
        _updatingPosition = false;

        UpdateTimestamp(value);
        if (refreshPreview) SchedulePreview();
    }

    private void UpdateTimestamp(double seconds)
    {
        TimestampTextBox.Text = FormatTimestamp(TimeSpan.FromSeconds(Math.Max(0, seconds)));
    }

    private void SchedulePreview()
    {
        if (_mediaInfo is null || _isClosed) return;
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private async void PreviewTimerTick(object? sender, EventArgs e)
    {
        _previewTimer.Stop();
        await RefreshPreviewAsync();
    }

    private async Task RefreshPreviewAsync()
    {
        if (_mediaInfo is null || _isClosed) return;

        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _previewCancellation = cancellation;

        try
        {
            PreviewStatusText.Text = "프레임을 불러오는 중…";
            PreviewStatusText.IsVisible = true;

            using var frame = await VideoPreviewReader.ReadFrameAsync(
                _ffmpegExecutable,
                _inputPath,
                TimeSpan.FromSeconds(PreviewSlider.Value),
                cancellation.Token);
            if (_isClosed || cancellation.IsCancellationRequested) return;

            var bitmap = new Bitmap(frame);
            var previous = _previewBitmap;
            _previewBitmap = bitmap;
            PreviewImage.Source = bitmap;
            PreviewStatusText.IsVisible = false;
            previous?.Dispose();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (!_isClosed && !cancellation.IsCancellationRequested)
            {
                ShowPreviewMessage($"미리보기 오류: {exception.Message}");
            }
        }
        finally
        {
            if (ReferenceEquals(_previewCancellation, cancellation))
            {
                _previewCancellation.Dispose();
                _previewCancellation = null;
            }
        }
    }

    private void SetStartFromCurrent(object? sender, RoutedEventArgs e)
    {
        StartTrimTextBox.Text = VideoTrimInput.FormatSeconds((decimal)Math.Round(PreviewSlider.Value, 3));
    }

    private void SetEndFromCurrent(object? sender, RoutedEventArgs e)
    {
        if (_mediaInfo is null) return;
        var remaining = Math.Max(0, _mediaInfo.Duration.TotalSeconds - PreviewSlider.Value);
        EndTrimTextBox.Text = VideoTrimInput.FormatSeconds((decimal)Math.Round(remaining, 3));
    }

    private void ApplyTrim(object? sender, RoutedEventArgs e)
    {
        if (!VideoTrimInput.TryParseSeconds(StartTrimTextBox.Text, out var startSeconds) ||
            !VideoTrimInput.TryParseSeconds(EndTrimTextBox.Text, out var endSeconds))
        {
            ShowPreviewMessage(VideoTrimInput.ValidationMessage);
            return;
        }

        if (_mediaInfo is { } mediaInfo &&
            (double)(startSeconds + endSeconds) >= mediaInfo.Duration.TotalSeconds)
        {
            ShowPreviewMessage("앞/뒤 자르기 합계는 전체 비디오 길이보다 짧아야 합니다.");
            return;
        }

        Close(new VideoTrimSettings(startSeconds, endSeconds));
    }

    private void CloseDialog(object? sender, RoutedEventArgs e) => Close();

    private void ShowPreviewMessage(string message)
    {
        PreviewStatusText.Text = message;
        PreviewStatusText.IsVisible = true;
    }

    private static string FormatTimestamp(TimeSpan value)
    {
        var totalHours = (int)Math.Floor(value.TotalHours);
        return $"{totalHours:00}:{value.Minutes:00}:{value.Seconds:00}.{value.Milliseconds:000}";
    }

    private static bool TryParseTimestamp(string? text, out TimeSpan value)
    {
        value = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Trim().Split(':');
        if (parts.Length != 3 ||
            !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) ||
            !decimal.TryParse(parts[2], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds) ||
            hours < 0 || minutes is < 0 or >= 60 || seconds is < 0 or >= 60)
        {
            return false;
        }

        value = TimeSpan.FromHours(hours)
            + TimeSpan.FromMinutes(minutes)
            + TimeSpan.FromSeconds((double)seconds);
        return true;
    }
}
