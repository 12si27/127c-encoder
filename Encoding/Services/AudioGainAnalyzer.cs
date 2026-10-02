using System.Diagnostics;
using System.Globalization;
using Encoder127c.Diagnostics;
using Encoder127c.Encoding.Arguments;

namespace Encoder127c.Encoding.Services;

internal sealed record AudioGainOptions(
    double IgnoreTopPercent = 5,
    double WindowSeconds = 1,
    double SilenceDb = -50,
    double TargetDb = 0,
    double MaxGainDb = 20,
    double StartSeconds = 0,
    double? DurationSeconds = null)
{
    public void Validate()
    {
        if (!double.IsFinite(IgnoreTopPercent) || IgnoreTopPercent < 0 || IgnoreTopPercent >= 100 ||
            !double.IsFinite(WindowSeconds) || WindowSeconds < 1d / AudioFilterDefaults.SampleRate ||
            WindowSeconds * AudioFilterDefaults.SampleRate > int.MaxValue ||
            !double.IsFinite(SilenceDb) || !double.IsFinite(TargetDb) || TargetDb > 0 ||
            !double.IsFinite(MaxGainDb) || MaxGainDb < 0 ||
            !double.IsFinite(StartSeconds) || StartSeconds < 0 ||
            (DurationSeconds is { } duration && (!double.IsFinite(duration) || duration <= 0)))
        {
            throw new ArgumentException("분석 조건을 확인하세요. 제외 비율은 0 이상 100 미만, 목표 피크는 0 dBFS 이하, 분석 길이는 양수여야 합니다.");
        }
    }
}

internal sealed record AudioGainWindow(double PeakDb, double RmsDb, double Samples);

internal sealed record AudioGainReport(
    double GainDb,
    double? RawGainDb,
    double? ReferencePeakDb,
    double? GlobalPeakDb,
    double? PredictedGlobalPeakDb,
    double AnalyzedSeconds,
    int WindowCount,
    int ActiveWindowCount,
    int IgnoredWindowCount,
    int ClippingWindowCount,
    IReadOnlyList<string> Warnings)
{
    public double ClippingWindowPercent => 100d * ClippingWindowCount / WindowCount;
}

/// <summary>Port of auto_gain.py: sample peaks per window, nearest-rank exclusion, whole-dB floor.</summary>
internal sealed class AudioGainAnalyzer
{
    public async Task<AudioGainReport> AnalyzeAsync(
        string ffmpegExecutable,
        string inputPath,
        AudioGainOptions options,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options.Validate();
        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException("분석할 파일이 없습니다.", inputPath);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = new ProcessStartInfo(ffmpegExecutable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in BuildArguments(inputPath, options))
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("FFmpeg 분석을 시작할 수 없습니다.");
        using var cancellationRegistration = cancellationToken.Register(() => TryKill(process));
        var errors = new BoundedLogBuffer();
        var errorTask = DrainErrorsAsync(process.StandardError, errors);
        var windows = new List<AudioGainWindow>();
        double? peak = null, rms = null, samples = null;
        var hasFrame = false;
        var analyzedSamples = 0d;

        void AddWindow()
        {
            if (!hasFrame)
            {
                return;
            }

            if (peak is null || rms is null || samples is null)
            {
                throw new InvalidOperationException("FFmpeg astats 필수 통계가 누락되었습니다.");
            }

            windows.Add(new AudioGainWindow(peak.Value, rms.Value, samples.Value));
            analyzedSamples += samples.Value;
            // At most one UI update per second of analyzed audio.
            if (windows.Count == 1 || analyzedSamples / AudioFilterDefaults.SampleRate >=
                Math.Floor((analyzedSamples - samples.Value) / AudioFilterDefaults.SampleRate) + 1)
            {
                progress?.Report(analyzedSamples / AudioFilterDefaults.SampleRate);
            }
        }

        try
        {
            while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
            {
                if (line.StartsWith("frame:", StringComparison.Ordinal))
                {
                    AddWindow();
                    hasFrame = true;
                    peak = rms = samples = null;
                }
                else if (hasFrame && line.StartsWith("lavfi.astats.Overall.", StringComparison.Ordinal))
                {
                    var separator = line.IndexOf('=');
                    if (separator < 0)
                    {
                        throw new InvalidOperationException("FFmpeg 통계 형식이 올바르지 않습니다.");
                    }

                    var value = ParseStatistic(line[(separator + 1)..]);
                    switch (line[..separator])
                    {
                        case "lavfi.astats.Overall.Peak_level": peak = value; break;
                        case "lavfi.astats.Overall.RMS_level": rms = value; break;
                        case "lavfi.astats.Overall.Number_of_samples": samples = value; break;
                    }
                }
            }

            await process.WaitForExitAsync(cancellationToken);
            await errorTask;
            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"FFmpeg 분석 실패. 첫 번째 오디오 트랙과 파일을 확인하세요.\n{errors.ToString().Trim()}");
            }

            AddWindow();
            progress?.Report(analyzedSamples / AudioFilterDefaults.SampleRate);
            return EstimateGain(windows, options);
        }
        finally
        {
            TryKill(process);
            await process.WaitForExitAsync();
            await errorTask;
        }
    }

    internal static IEnumerable<string> BuildArguments(string inputPath, AudioGainOptions options)
    {
        var filters = new List<string> { AudioFilterDefaults.Downmix };
        if (options.StartSeconds > 0 || options.DurationSeconds is not null)
        {
            var trim = $"atrim=start={Format(options.StartSeconds)}";
            if (options.DurationSeconds is { } duration)
            {
                trim += $":duration={Format(duration)}";
            }

            filters.Add(trim);
        }

        filters.Add("asetpts=N/SR/TB");
        filters.Add($"asetnsamples=n={Math.Max(1, (int)Math.Round(options.WindowSeconds * AudioFilterDefaults.SampleRate))}:p=0");
        filters.Add("astats=metadata=1:reset=1:measure_perchannel=none:measure_overall=Peak_level+RMS_level+Number_of_samples");
        filters.Add("ametadata=mode=print:file=-");
        return
        [
            "-hide_banner", "-nostdin", "-v", "error", "-xerror",
            "-i", inputPath, "-map", "0:a:0", "-vn", "-sn", "-dn",
            "-af", string.Join(',', filters), "-f", "null", "-"
        ];
    }

    internal static AudioGainReport EstimateGain(IReadOnlyList<AudioGainWindow> windows, AudioGainOptions options)
    {
        options.Validate();
        if (windows.Count == 0)
        {
            throw new InvalidOperationException("분석할 오디오가 없습니다. 분석 시작과 길이를 확인하세요.");
        }

        if (windows.Any(w => double.IsNaN(w.PeakDb) || double.IsPositiveInfinity(w.PeakDb) ||
            double.IsNaN(w.RmsDb) || double.IsPositiveInfinity(w.RmsDb) ||
            !double.IsFinite(w.Samples) || w.Samples <= 0))
        {
            throw new InvalidOperationException("오디오에 유효하지 않은 샘플 통계가 있습니다.");
        }

        var active = windows.Where(w => w.RmsDb > options.SilenceDb && double.IsFinite(w.PeakDb))
            .OrderBy(w => w.PeakDb).ToArray();
        var warnings = new List<string>();
        var globalPeak = windows.Max(w => w.PeakDb);
        double? referencePeak = null, rawGain = null;
        var ignored = 0;
        var gain = 0d;
        if (active.Length > 0)
        {
            var keep = Math.Max(1, (int)Math.Ceiling(active.Length * (1 - options.IgnoreTopPercent / 100)));
            ignored = active.Length - keep;
            referencePeak = active[keep - 1].PeakDb;
            rawGain = options.TargetDb - referencePeak.Value;
            gain = Math.Floor(Math.Min(rawGain.Value, options.MaxGainDb));
            if (rawGain > options.MaxGainDb)
            {
                warnings.Add($"게인 상한 {Format(options.MaxGainDb)} dB를 적용했습니다.");
            }

            if (active.Length < 20)
            {
                warnings.Add("활성 구간이 20개 미만이므로 상위 피크 제외 결과가 거칠 수 있습니다.");
            }

            if (options.IgnoreTopPercent > 0)
            {
                warnings.Add("큰 피크를 통계적으로 제외한 결과이며, 효과음과 대사를 판별하지 않습니다.");
            }
        }
        else
        {
            warnings.Add("모든 구간이 무음 기준 이하이므로 게인을 0 dB로 유지합니다.");
        }

        return new AudioGainReport(gain, rawGain, referencePeak,
            double.IsFinite(globalPeak) ? globalPeak : null,
            double.IsFinite(globalPeak) ? globalPeak + gain : null,
            windows.Sum(w => w.Samples) / AudioFilterDefaults.SampleRate,
            windows.Count, active.Length, ignored,
            windows.Count(w => w.PeakDb + gain > 0), warnings);
    }

    private static double ParseStatistic(string text) => text.Trim() switch
    {
        "-inf" => double.NegativeInfinity,
        "inf" or "+inf" => double.PositiveInfinity,
        "nan" or "-nan" => double.NaN,
        var value => double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture)
    };

    private static string Format(double value) => value.ToString("G", CultureInfo.InvariantCulture);

    private static async Task DrainErrorsAsync(StreamReader reader, BoundedLogBuffer errors)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            errors.Report(line);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }
}
