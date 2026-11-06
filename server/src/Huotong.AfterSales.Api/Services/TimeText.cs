namespace Huotong.AfterSales.Api.Services;

/// <summary>中文时间文案</summary>
public static class TimeText
{
    private static readonly string[] Weeks = { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };

    public static string Md(DateTime t) => $"{t.Month}月{t.Day}日";
    public static string Hm(DateTime t) => t.ToString("HH:mm");
    public static string MdHm(DateTime t) => $"{Md(t)} {Hm(t)}";
    public static string Week(DateTime t) => Weeks[(int)t.DayOfWeek];

    /// <summary>今天只显示 HH:mm，否则 M月d日 HH:mm</summary>
    public static string Smart(DateTime t, DateTime now) => t.Date == now.Date ? Hm(t) : MdHm(t);

    /// <summary>分钟 → H:mm</summary>
    public static string Hmm(int minutes) => $"{minutes / 60}:{minutes % 60:00}";

    /// <summary>分钟 → 小时，保留 1 位小数</summary>
    public static double Hours1(int minutes) => Math.Round(minutes / 60d, 1, MidpointRounding.AwayFromZero);

    public static string Ago(DateTime t, DateTime now)
    {
        var span = now - t;
        if (span.TotalMinutes < 1) return "刚刚";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} 分钟";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours} 小时";
        return $"{(int)span.TotalDays} 天";
    }

    /// <summary>本周一（周一为一周开始）</summary>
    public static DateTime WeekStart(DateTime today)
    {
        var d = today.Date;
        var diff = ((int)d.DayOfWeek + 6) % 7;
        return d.AddDays(-diff);
    }
}
