using System.Globalization;

namespace Encoder127c.Tools;

internal static class VideoTrimInput
{
    internal const string ValidationMessage = "자르기 값은 0~864,000초 범위의 숫자로 입력하세요. 소수점은 '.'을 사용하세요.";

    internal static bool TryParseSeconds(string? text, out decimal seconds) =>
        decimal.TryParse(text?.Trim(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out seconds) && seconds is >= 0 and <= 864000;

    internal static string FormatSeconds(decimal seconds) =>
        seconds.ToString("0.000#########################", CultureInfo.InvariantCulture);
}
