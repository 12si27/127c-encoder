using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Encoder127c.Encoding.Services;

internal sealed record AudioStreamInfo(int Index, string DisplayName)
{
    public override string ToString() => DisplayName;
}

/// <summary>Reads input stream descriptions using the FFmpeg executable already installed by the app.</summary>
internal static partial class AudioStreamReader
{
    public static async Task<IReadOnlyList<AudioStreamInfo>> ReadAsync(
        string ffmpegExecutable, string inputPath, CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo(ffmpegExecutable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { "-hide_banner", "-nostdin", "-i", inputPath })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("오디오 스트림을 확인할 수 없습니다.");
        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        });
        // FFmpeg prints input metadata to stderr, then exits with 1 because no output was requested.
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await Task.WhenAll(process.WaitForExitAsync(), outputTask, errorTask);
        cancellationToken.ThrowIfCancellationRequested();
        var description = await errorTask;
        if (process.ExitCode != 1 || !description.Contains("Input #0,", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("오디오 스트림을 읽지 못했습니다. 입력 파일을 확인하세요.");
        }

        return Parse(description);
    }

    internal static IReadOnlyList<AudioStreamInfo> Parse(string description)
    {
        var streams = new List<AudioStreamInfo>();
        foreach (Match match in AudioStreamPattern().Matches(description))
        {
            var language = match.Groups["language"].Value;
            var name = GetLanguageName(language);
            streams.Add(new AudioStreamInfo(streams.Count,
                $"#{streams.Count + 1} {name} · {match.Groups["description"].Value.Trim()}"));
        }
        return streams;
    }

    private static string GetLanguageName(string language)
    {
        if (string.IsNullOrWhiteSpace(language) || language == "und") return "언어 미상";
        var normalized = language.ToLowerInvariant() switch
        {
            "ger" => "deu", "fre" => "fra", "chi" => "zho", "dut" => "nld",
            "cze" => "ces", "gre" => "ell", "rum" => "ron", "slo" => "slk",
            "wel" => "cym", "alb" => "sqi", "arm" => "hye", "baq" => "eus",
            "bur" => "mya", "geo" => "kat", "ice" => "isl", "mac" => "mkd",
            "mao" => "mri", "may" => "msa", "per" => "fas", "tib" => "bod",
            var value => value
        };
        var culture = CultureInfo.GetCultures(CultureTypes.NeutralCultures).FirstOrDefault(candidate =>
            candidate.ThreeLetterISOLanguageName == normalized || candidate.TwoLetterISOLanguageName == normalized);
        return culture is null ? language : $"{culture.EnglishName} ({language})";
    }

    [GeneratedRegex(@"^\s*Stream #0:\d+(?:\[[^\]]*\])?(?:\((?<language>[^)]+)\))?: Audio: (?<description>[^\r\n]+)", RegexOptions.Multiline)]
    private static partial Regex AudioStreamPattern();
}
