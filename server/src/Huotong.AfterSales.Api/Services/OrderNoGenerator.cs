using System.Globalization;

namespace Huotong.AfterSales.Api.Services;

public static class OrderNoGenerator
{
    public static string Prefix(DateTime date) => $"SH-{date:yyyyMMdd}-";

    /// <summary>根据当日已有最大单号生成下一个（001 起）</summary>
    public static string Next(DateTime date, string? currentMax)
    {
        var seq = 0;
        var prefix = Prefix(date);
        if (!string.IsNullOrEmpty(currentMax) && currentMax.StartsWith(prefix) &&
            int.TryParse(currentMax[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            seq = n;
        return prefix + (seq + 1).ToString("000", CultureInfo.InvariantCulture);
    }
}
