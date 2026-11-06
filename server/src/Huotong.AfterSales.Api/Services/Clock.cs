namespace Huotong.AfterSales.Api.Services;

public interface IClock
{
    /// <summary>当前北京时间（Kind=Unspecified）</summary>
    DateTime Now { get; }
}

public class BeijingClock : IClock
{
    private static readonly TimeZoneInfo Tz = FindTz();

    private static TimeZoneInfo FindTz()
    {
        foreach (var id in new[] { "Asia/Shanghai", "China Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch { }
        }
        return TimeZoneInfo.CreateCustomTimeZone("UTC+8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");
    }

    public DateTime Now
    {
        get
        {
            var t = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Tz);
            // 去掉毫秒，保证序列化/比较一致
            return DateTime.SpecifyKind(new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute, t.Second), DateTimeKind.Unspecified);
        }
    }
}

/// <summary>测试用固定时钟</summary>
public class FixedClock : IClock
{
    public DateTime Now { get; set; }
    public FixedClock(DateTime now) => Now = now;
}
