using System.Globalization;
using System.Text.RegularExpressions;
using Encoder127c.Encoding.Models;

namespace Encoder127c.Encoding.Validation;

internal interface IVideoEncodingRequestValidator
{
    VideoEncodingValidationResult Validate(VideoEncodingRequest request);
}

internal sealed record VideoEncodingValidationResult(
    ValidatedVideoEncodingRequest? Request,
    string? ErrorMessage)
{
    public bool IsValid => Request is not null;
}

/// <summary>Validates user input before any FFmpeg process is started.</summary>
internal sealed partial class VideoEncodingRequestValidator : IVideoEncodingRequestValidator
{
    private static readonly HashSet<string> VideoPresets = ["fast", "medium", "slow"];
    private static readonly HashSet<string> EncodingProfiles =
    [
        DefaultEncodingPreset.EncodingProfileDefault,
        DefaultEncodingPreset.EncodingProfileSaving
    ];
    private static readonly HashSet<string> DeinterlaceModes =
    [
        DefaultEncodingPreset.DeinterlaceModeAuto,
        DefaultEncodingPreset.DeinterlaceModeAlways,
        DefaultEncodingPreset.DeinterlaceModeOff
    ];

    public VideoEncodingValidationResult Validate(VideoEncodingRequest request)
    {
        var inputPath = request.InputPath.Trim();
        if (string.IsNullOrWhiteSpace(inputPath))
        {
            return Invalid("입력 비디오 파일을 먼저 선택하세요.");
        }

        if (string.IsNullOrWhiteSpace(request.OutputDirectory))
        {
            return Invalid("출력 폴더를 입력하세요.");
        }

        var videoMaxBitrate = request.VideoMaxBitrate.Trim();
        var videoBufferSize = request.VideoBufferSize.Trim();
        if (!BitratePattern().IsMatch(videoMaxBitrate) || !BitratePattern().IsMatch(videoBufferSize))
        {
            return Invalid("최대 비트레이트와 버퍼 크기를 입력하세요. 예: 2000k, 4000k");
        }

        if (!double.TryParse(
                request.AudioGainDb.Trim(),
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var audioGainDb) ||
            !double.IsFinite(audioGainDb))
        {
            return Invalid("오디오 게인은 dB 단위의 숫자로 입력하세요. 예: -3, 0, 6.5");
        }

        if (!VideoPresets.Contains(request.VideoPreset))
        {
            return Invalid("지원하지 않는 인코딩 옵션입니다.");
        }

        if (!EncodingProfiles.Contains(request.EncodingProfile))
        {
            return Invalid("지원하지 않는 인코딩 프로필입니다.");
        }

        if (!DeinterlaceModes.Contains(request.DeinterlaceMode))
        {
            return Invalid("지원하지 않는 디인터레이싱 옵션입니다.");
        }

        try
        {
            var fullInputPath = Path.GetFullPath(inputPath);
            if (!File.Exists(fullInputPath))
            {
                return Invalid("입력 비디오 파일을 먼저 선택하세요.");
            }

            if (new FileInfo(fullInputPath).Length == 0)
            {
                return Invalid("입력 비디오 파일이 비어 있습니다.");
            }

            var fullOutputDirectory = Path.GetFullPath(request.OutputDirectory.Trim());
            var outputPath = GetAvailableOutputPath(fullOutputDirectory, fullInputPath);
            var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

            if (string.Equals(fullInputPath, outputPath, pathComparison))
            {
                return Invalid("입력 파일과 출력 파일은 같을 수 없습니다.");
            }

            return new VideoEncodingValidationResult(
                new ValidatedVideoEncodingRequest(
                    fullInputPath,
                    outputPath,
                    request.EncodingProfile,
                    request.VideoPreset,
                    videoMaxBitrate,
                    videoBufferSize,
                    request.DeinterlaceMode,
                    audioGainDb.ToString("0.########", CultureInfo.InvariantCulture),
                    request.DynamicAudioNormalization),
                null);
        }
        catch (Exception exception) when (exception is
            ArgumentException or
            NotSupportedException or
            PathTooLongException or
            UnauthorizedAccessException or
            IOException)
        {
            return Invalid("입력 파일 또는 출력 경로에 접근할 수 없습니다.");
        }
    }

    private static VideoEncodingValidationResult Invalid(string message) => new(null, message);

    private static string GetAvailableOutputPath(string outputDirectory, string inputPath)
    {
        var baseFileName = $"[127c]{Path.GetFileNameWithoutExtension(inputPath)}";
        var outputPath = Path.Combine(outputDirectory, $"{baseFileName}.mp4");

        for (var index = 1; File.Exists(outputPath) || Directory.Exists(outputPath); index++)
        {
            outputPath = Path.Combine(outputDirectory, $"{baseFileName} ({index}).mp4");
        }

        return outputPath;
    }

    [GeneratedRegex("^[0-9]+(?:\\.[0-9]+)?[kKmMgG]?$")]
    private static partial Regex BitratePattern();
}
