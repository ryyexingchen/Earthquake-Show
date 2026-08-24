using EarthquakeShow.Core.Models;

namespace EarthquakeShow.Core.Services;

public static class JmaTsunamiClassifier
{
    public static TsunamiLevel Classify(string? comment, string? code)
    {
        string normalizedCode = code?.Trim() ?? string.Empty;
        if (normalizedCode == "0215")
        {
            return TsunamiLevel.NoConcern;
        }

        if (string.IsNullOrWhiteSpace(comment))
        {
            return TsunamiLevel.Investigating;
        }

        if (IsGenericTemplate(comment))
        {
            return TsunamiLevel.Investigating;
        }

        // 解除报文表示上一状态结束，当前模型没有单独的“解除”等级，暂不推断为新的警报等级。
        if (comment.Contains("解除", StringComparison.Ordinal))
        {
            return TsunamiLevel.Investigating;
        }

        if (comment.Contains("大津波警報", StringComparison.Ordinal) ||
            comment.Contains("大海嘯警報", StringComparison.Ordinal))
        {
            return TsunamiLevel.MajorWarning;
        }

        if (comment.Contains("津波警報", StringComparison.Ordinal) ||
            comment.Contains("海嘯警報", StringComparison.Ordinal))
        {
            return TsunamiLevel.Warning;
        }

        if (comment.Contains("津波注意報", StringComparison.Ordinal) ||
            comment.Contains("海嘯注意報", StringComparison.Ordinal))
        {
            return TsunamiLevel.Advisory;
        }

        if (comment.Contains("若干の海面変動", StringComparison.Ordinal) ||
            comment.Contains("若干の潮位変化", StringComparison.Ordinal))
        {
            return TsunamiLevel.MinorChange;
        }

        if (comment.Contains("津波の心配はありません", StringComparison.Ordinal) ||
            comment.Contains("津波の心配なし", StringComparison.Ordinal) ||
            comment.Contains("津波なし", StringComparison.Ordinal) ||
            comment.Contains("海嘯の心配はありません", StringComparison.Ordinal))
        {
            return TsunamiLevel.NoConcern;
        }

        return TsunamiLevel.Investigating;
    }

    public static bool IsGenericTemplate(string? comment)
    {
        return !string.IsNullOrWhiteSpace(comment) &&
            (comment.Contains("津波警報等（", StringComparison.Ordinal) ||
                comment.Contains("津波警報等(", StringComparison.Ordinal) ||
                comment.Contains("海嘯警報等（", StringComparison.Ordinal) ||
                comment.Contains("海嘯警報等(", StringComparison.Ordinal));
    }
}
