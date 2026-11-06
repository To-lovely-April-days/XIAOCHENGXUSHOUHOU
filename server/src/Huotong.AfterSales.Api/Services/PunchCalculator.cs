using Huotong.AfterSales.Api.Domain;

namespace Huotong.AfterSales.Api.Services;

/// <summary>打卡计算用的精简记录</summary>
public record PunchInfo(int Id, int OrderId, int EngineerId, PunchKind Kind, DateTime Time,
    bool OutOfRange = false, bool IsSupplement = false)
{
    public static PunchInfo From(Punch p) =>
        new(p.Id, p.OrderId, p.EngineerId, p.Kind, p.Time, p.OutOfRange, p.IsSupplement);
}

/// <summary>到场—离场配对；Leave 为 null 表示未离场</summary>
public record PunchPair(PunchInfo Arrive, PunchInfo? Leave)
{
    public DateTime Date => Arrive.Time.Date;
    public int Minutes => Leave == null ? 0 : PunchCalculator.DiffMinutes(Arrive.Time, Leave.Time);
    public bool IsOpen => Leave == null;
    /// <summary>今天未离场 = 现场计时中</summary>
    public bool IsRunning(DateTime today) => Leave == null && Arrive.Time.Date == today.Date;
    /// <summary>早于今天且未配对离场 = 缺卡</summary>
    public bool IsMissing(DateTime today) => Leave == null && Arrive.Time.Date < today.Date;
    public bool IsSupplement => Arrive.IsSupplement || (Leave?.IsSupplement ?? false);
}

public record NextPunchDecision(PunchKind Kind, PunchInfo? OpenArrive, int? BlockedByOrderId);

public static class PunchCalculator
{
    public static int DiffMinutes(DateTime from, DateTime to) =>
        to <= from ? 0 : (int)Math.Floor((to - from).TotalMinutes);

    /// <summary>
    /// 按（工程师, 工单）分组、时间正序配对：Arrive 开启；同一天的下一条 Leave 关闭；
    /// 未关闭又遇到 Arrive，则前一条为未配对；跨天的 Leave 不配对（孤立离场忽略）。
    /// </summary>
    public static List<PunchPair> Pair(IEnumerable<PunchInfo> punches)
    {
        var result = new List<PunchPair>();
        foreach (var g in punches.GroupBy(p => (p.EngineerId, p.OrderId)))
        {
            PunchInfo? open = null;
            foreach (var p in g.OrderBy(x => x.Time).ThenBy(x => x.Kind).ThenBy(x => x.Id))
            {
                if (p.Kind == PunchKind.Arrive)
                {
                    if (open != null) result.Add(new PunchPair(open, null));
                    open = p;
                }
                else if (open != null && open.Time.Date == p.Time.Date && p.Time >= open.Time)
                {
                    result.Add(new PunchPair(open, p));
                    open = null;
                }
                else if (open != null)
                {
                    // 跨天离场：前一个到场视为未配对
                    result.Add(new PunchPair(open, null));
                    open = null;
                }
            }
            if (open != null) result.Add(new PunchPair(open, null));
        }
        return result.OrderBy(x => x.Arrive.Time).ThenBy(x => x.Arrive.Id).ToList();
    }

    /// <summary>每条 Leave 的配对时长（punchId → 分钟）</summary>
    public static Dictionary<int, int> LeavePairMinutes(IEnumerable<PunchPair> pairs) =>
        pairs.Where(p => p.Leave != null).ToDictionary(p => p.Leave!.Id, p => p.Minutes);

    /// <summary>
    /// 判断工程师在某工单的下一次打卡类型（DESIGN §4.4）：
    /// 今天在本工单有未离场到场 → 离场；今天在其他工单有未离场到场 → 阻止；否则 → 到场。
    /// 前一天及更早的未配对到场不阻止。
    /// </summary>
    public static NextPunchDecision DecideNext(IEnumerable<PunchInfo> engineerPunches, int orderId, DateTime now)
    {
        var today = now.Date;
        var openToday = Pair(engineerPunches).Where(p => p.IsRunning(today)).OrderByDescending(p => p.Arrive.Time).ToList();
        var other = openToday.FirstOrDefault(p => p.Arrive.OrderId != orderId);
        if (other != null) return new NextPunchDecision(PunchKind.Arrive, null, other.Arrive.OrderId);
        var mine = openToday.FirstOrDefault(p => p.Arrive.OrderId == orderId);
        return mine != null
            ? new NextPunchDecision(PunchKind.Leave, mine.Arrive, null)
            : new NextPunchDecision(PunchKind.Arrive, null, null);
    }

    /// <summary>某天已配对分钟数（按到场日归属）</summary>
    public static int ClosedMinutesOn(IEnumerable<PunchPair> pairs, DateTime date) =>
        pairs.Where(p => p.Date == date.Date && p.Leave != null).Sum(p => p.Minutes);

    /// <summary>出差第 N 天：截止今天连续有到场打卡的天数（今天无到场为 0）</summary>
    public static int TripDay(IEnumerable<PunchInfo> engineerPunches, DateTime today)
    {
        var days = engineerPunches.Where(p => p.Kind == PunchKind.Arrive).Select(p => p.Time.Date).ToHashSet();
        var n = 0;
        var d = today.Date;
        while (days.Contains(d)) { n++; d = d.AddDays(-1); }
        return n;
    }

    /// <summary>某月有到场打卡的不同天数</summary>
    public static int TripDaysInMonth(IEnumerable<PunchInfo> engineerPunches, int year, int month) =>
        engineerPunches.Where(p => p.Kind == PunchKind.Arrive && p.Time.Year == year && p.Time.Month == month)
            .Select(p => p.Time.Date).Distinct().Count();

    /// <summary>工单异常：缺卡、距离异常</summary>
    public static List<IssueType> Issues(IEnumerable<PunchInfo> orderPunches, DateTime today)
    {
        var list = orderPunches.ToList();
        var issues = new List<IssueType>();
        if (Pair(list).Any(p => p.IsMissing(today))) issues.Add(IssueType.MissingPunch);
        if (list.Any(p => p.OutOfRange)) issues.Add(IssueType.DistanceAbnormal);
        return issues;
    }

    /// <summary>
    /// 校验主管补录离场：到场必须存在且为缺卡；离场时间晚于到场、同一天、不晚于现在，
    /// 且不能晚于该工程师此后的下一条打卡。返回错误信息或 null。
    /// </summary>
    public static string? ValidateSupplementLeave(IEnumerable<PunchInfo> engineerPunches, int arrivePunchId, DateTime leaveTime, DateTime now)
    {
        var all = engineerPunches.ToList();
        var arrive = all.FirstOrDefault(p => p.Id == arrivePunchId);
        if (arrive == null || arrive.Kind != PunchKind.Arrive) return "未找到对应的到场打卡";
        var pair = Pair(all).FirstOrDefault(p => p.Arrive.Id == arrivePunchId);
        if (pair == null || pair.Leave != null) return "该到场打卡已有离场记录，无需补录";
        if (!pair.IsMissing(now.Date)) return "今天的到场仍在计时中，请工程师自行离场打卡";
        if (leaveTime <= arrive.Time) return "离场时间必须晚于到场时间";
        if (leaveTime.Date != arrive.Time.Date) return "离场时间必须与到场在同一天";
        if (leaveTime > now) return "离场时间不能晚于当前时间";
        var nextPunch = all.Where(p => p.Time > arrive.Time && p.Id != arrive.Id).OrderBy(p => p.Time).FirstOrDefault();
        if (nextPunch != null && leaveTime > nextPunch.Time) return "离场时间不能晚于该工程师之后的下一次打卡";
        return null;
    }
}
