using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Encoder127c.Encoding.Services;

namespace Encoder127c.Tools;

public partial class AudioGainDialog : Window
{
    private readonly string _ffmpegExecutable;
    private readonly string _inputPath;
    private readonly int _audioStreamIndex;
    private CancellationTokenSource? _analysisCancellation;
    private AudioGainReport? _report;
    private bool _isClosed;

    internal AudioGainOptions Options { get; private set; }

    // Required by the XAML designer; normal use supplies the installed FFmpeg path.
    public AudioGainDialog() : this("ffmpeg", string.Empty) { }

    internal AudioGainDialog(string ffmpegExecutable, string inputPath, AudioGainOptions? options = null,
        int audioStreamIndex = 0)
    {
        _ffmpegExecutable = ffmpegExecutable;
        _inputPath = inputPath;
        _audioStreamIndex = audioStreamIndex;
        InitializeComponent();
        RestoreOptions(options ?? new AudioGainOptions());
        Options = ReadOptions();
        Closing += (_, _) =>
        {
            try
            {
                var currentOptions = ReadOptions();
                currentOptions.Validate();
                Options = currentOptions;
            }
            catch (ArgumentException)
            {
                // An unfinished numeric input must not replace valid saved conditions.
            }
        };
        Closed += (_, _) =>
        {
            _isClosed = true;
            _analysisCancellation?.Cancel();
        };
    }

    private void RestoreOptions(AudioGainOptions options)
    {
        static void Restore(NumericUpDown control, double value)
        {
            if (double.IsFinite(value) && value >= (double)control.Minimum && value <= (double)control.Maximum)
            {
                control.Value = (decimal)value;
            }
        }

        Restore(IgnoreTopNumeric, options.IgnoreTopPercent);
        Restore(WindowNumeric, options.WindowSeconds);
        Restore(SilenceNumeric, options.SilenceDb);
        Restore(TargetNumeric, options.TargetDb);
        Restore(MaxGainNumeric, options.MaxGainDb);
        Restore(StartNumeric, options.StartSeconds);
        Restore(DurationNumeric, options.DurationSeconds ?? 0);
    }

    private void OptionsChanged(object? sender, NumericUpDownValueChangedEventArgs e) => InvalidateReport();

    private void InvalidateReport()
    {
        _report = null;
        if (ApplyButton is null || ResultHintPanel is null || ResultContentPanel is null || StatusText is null)
        {
            return;
        }

        ApplyButton.IsEnabled = false;
        ResultHintPanel.IsVisible = true;
        ResultContentPanel.IsVisible = false;
        ResultHintTextBlock.Text = "측정 시작을 눌러 게인 분석을 시작합니다";
        AnalyzeButton.Content = "측정 시작";
        StatusText.IsVisible = false;
    }

    private AudioGainOptions ReadOptions()
    {
        static double Read(NumericUpDown control) => (double)(control.Value
            ?? throw new ArgumentException("분석 조건에 숫자를 입력하세요."));

        var duration = Read(DurationNumeric);
        return new AudioGainOptions(Read(IgnoreTopNumeric), Read(WindowNumeric), Read(SilenceNumeric),
            Read(TargetNumeric), Read(MaxGainNumeric), Read(StartNumeric), duration == 0 ? null : duration);
    }

    private async void Analyze(object? sender, RoutedEventArgs e)
    {
        if (_analysisCancellation is not null)
        {
            AnalyzeButton.IsEnabled = false;
            StatusText.Text = "측정을 취소하는 중...";
            _analysisCancellation.Cancel();
            return;
        }

        InvalidateReport();
        StatusText.IsVisible = true;

        using var cancellation = new CancellationTokenSource();
        try
        {
            var options = ReadOptions();
            options.Validate();
            _analysisCancellation = cancellation;
            AnalysisSettingsPanel.IsEnabled = false;
            AnalyzeButton.Content = "측정 취소";
            AnalysisProgressBar.IsVisible = true;
            ResultHintTextBlock.Text = "게인을 분석하고 있습니다";
            StatusText.Text = "오디오를 측정하는 중...";
            var progress = new Progress<double>(seconds =>
            {
                if (!_isClosed && _analysisCancellation == cancellation && !cancellation.IsCancellationRequested)
                {
                    StatusText.Text = $"오디오 측정 중 · {seconds:N1}초 분석";
                }
            });
            var report = await Task.Run(() => new AudioGainAnalyzer().AnalyzeAsync(
                _ffmpegExecutable, _inputPath, options, progress, cancellation.Token, _audioStreamIndex));
            cancellation.Token.ThrowIfCancellationRequested();
            if (_isClosed)
            {
                return;
            }

            _report = report;
            ShowReport(report);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "측정을 취소했습니다.";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"측정 오류: {exception.Message}";
        }
        finally
        {
            _analysisCancellation = null;
            if (!_isClosed)
            {
                AnalysisSettingsPanel.IsEnabled = true;
                AnalyzeButton.IsEnabled = true;
                AnalyzeButton.Content = _report is null ? "측정 시작" : "다시 측정";
                AnalysisProgressBar.IsVisible = false;
                if (_report is null)
                {
                    ResultHintTextBlock.Text = "측정 시작을 눌러 게인 분석을 시작합니다";
                }
            }
        }
    }

    private void ShowReport(AudioGainReport report)
    {
        static string Peak(double? value) => value is { } number ? $"{number:F2} dBFS" : "없음";
        GainText.Text = $"게인 {report.GainDb.ToString("+0;-0;0", CultureInfo.CurrentCulture)} dB";
        ResultText.Text = $"기준 피크: {Peak(report.ReferencePeakDb)}\n" +
            $"전체 최대 피크: {Peak(report.GlobalPeakDb)} → 적용 후 {Peak(report.PredictedGlobalPeakDb)}\n" +
            $"분석 {report.AnalyzedSeconds:N2}초 · 전체 {report.WindowCount:N0}구간 · 활성 {report.ActiveWindowCount:N0}구간\n" +
            $"큰 피크 제외: {report.IgnoredWindowCount:N0}구간\n" +
            $"클리핑 예상: {report.ClippingWindowCount:N0}/{report.WindowCount:N0}구간 ({report.ClippingWindowPercent:F2}%)";
        var canApply = report.GainDb is >= -60 and <= 60;
        ResultHintPanel.IsVisible = false;
        ResultContentPanel.IsVisible = true;
        ApplyButton.IsEnabled = canApply;
        StatusText.IsVisible = !canApply;
        StatusText.Text = canApply ? null : "게인 설정 범위(-60~60 dB)를 벗어나 적용할 수 없습니다.";
    }

    private void ApplyGain(object? sender, RoutedEventArgs e)
    {
        if (_report is { GainDb: >= -60 and <= 60 } report && _analysisCancellation is null)
        {
            Close((decimal?)report.GainDb);
        }
    }

    private void CloseDialog(object? sender, RoutedEventArgs e) => Close();
}
