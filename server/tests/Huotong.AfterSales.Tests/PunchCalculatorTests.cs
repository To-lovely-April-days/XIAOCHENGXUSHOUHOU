using Huotong.AfterSales.Api.Domain;
using Huotong.AfterSales.Api.Services;

namespace Huotong.AfterSales.Tests;

public class PunchCalculatorTests
{
    private static readonly DateTime Now = new(2026, 11, 6, 13, 42, 10);
    private int _id;

    private PunchInfo P(int order, PunchKind kind, DateTime t, int engineer = 2, bool supp = false, bool far = false) =>
        new(++_id, order, engineer, kind, t, far, supp);

    private static DateTime T(int day, int h, int m) => new(2026, 11, day, h, m, 0);

    [Fact]
    public void NoPunchesMeansArrive()
    {
        var d = PunchCalculator.DecideNext(new List<PunchInfo>(), 3, Now);
        Assert.Equal(PunchKind.Arrive, d.Kind);
        Assert.Null(d.BlockedByOrderId);
        Assert.Null(d.OpenArrive);
    }

    [Fact]
    public void OpenArriveTodayOnSameOrderMeansLeave()
    {
        var a = P(3, PunchKind.Arrive, T(6, 10, 5));
        var d = PunchCalculator.DecideNext(new[] { a }, 3, Now);
        Assert.Equal(PunchKind.Leave, d.Kind);
        Assert.Equal(a.Id, d.OpenArrive!.Id);
    }

    [Fact]
    public void AfterLeaveNextIsArriveAgain()
    {
        var list = new[] { P(3, PunchKind.Arrive, T(6, 9, 0)), P(3, PunchKind.Leave, T(6, 11, 0)) };
        Assert.Equal(PunchKind.Arrive, PunchCalculator.DecideNext(list, 3, Now).Kind);
    }

    [Fact]
    public void OpenArriveOnOtherOrderTodayBlocks()
    {
        var list = new[] { P(5, PunchKind.Arrive, T(6, 13, 0)) };
        var d = PunchCalculator.DecideNext(list, 3, Now);
        Assert.Equal(5, d.BlockedByOrderId);
    }

    [Fact]
    public void ClosedPairOnOtherOrderDoesNotBlock()
    {
        var list = new[] { P(5, PunchKind.Arrive, T(6, 8, 0)), P(5, PunchKind.Leave, T(6, 9, 0)) };
        var d = PunchCalculator.DecideNext(list, 3, Now);
        Assert.Null(d.BlockedByOrderId);
        Assert.Equal(PunchKind.Arrive, d.Kind);
    }

    [Fact]
    public void YesterdayOpenArriveIsMissingAndDoesNotBlock()
    {
        var y = P(7, PunchKind.Arrive, T(5, 15, 10));
        // 同一工单：昨天未离场，今天再来 → 到场（不是离场）
        var same = PunchCalculator.DecideNext(new[] { y }, 7, Now);
        Assert.Equal(PunchKind.Arrive, same.Kind);
        Assert.Null(same.BlockedByOrderId);
        // 其他工单不被阻止
        var other = PunchCalculator.DecideNext(new[] { y }, 3, Now);
        Assert.Equal(PunchKind.Arrive, other.Kind);
        Assert.Null(other.BlockedByOrderId);
        Assert.Contains(IssueType.MissingPunch, PunchCalculator.Issues(new[] { y }, Now.Date));
    }

    [Fact]
    public void TodayOpenArriveIsRunningNotMissing()
    {
        var a = P(3, PunchKind.Arrive, T(6, 10, 5));
        var pair = Assert.Single(PunchCalculator.Pair(new[] { a }));
        Assert.True(pair.IsRunning(Now.Date));
        Assert.False(pair.IsMissing(Now.Date));
        Assert.Empty(PunchCalculator.Issues(new[] { a }, Now.Date));
    }

    [Fact]
    public void PairMinutesAndMultiplePairsPerDay()
    {
        var list = new[]
        {
            P(3, PunchKind.Arrive, T(5, 15, 40)), P(3, PunchKind.Leave, T(5, 18, 5)),
            P(3, PunchKind.Arrive, T(6, 8, 0)), P(3, PunchKind.Leave, T(6, 9, 30)),
            P(3, PunchKind.Arrive, T(6, 10, 0)), P(3, PunchKind.Leave, T(6, 12, 15)),
        };
        var pairs = PunchCalculator.Pair(list);
        Assert.Equal(3, pairs.Count);
        Assert.Equal(145, pairs[0].Minutes);
        Assert.Equal(90 + 135, PunchCalculator.ClosedMinutesOn(pairs, Now.Date));
        Assert.Equal(145, PunchCalculator.ClosedMinutesOn(pairs, T(5, 0, 0)));
        var leaveMin = PunchCalculator.LeavePairMinutes(pairs);
        Assert.Equal(145, leaveMin[list[1].Id]);
    }

    [Fact]
    public void CrossDayLeaveIsNotPairedAndArriveIsMissing()
    {
        var a = P(3, PunchKind.Arrive, T(5, 20, 0));
        var l = P(3, PunchKind.Leave, T(6, 1, 0));
        var pairs = PunchCalculator.Pair(new[] { a, l });
        var pair = Assert.Single(pairs);
        Assert.Null(pair.Leave);
        Assert.True(pair.IsMissing(Now.Date));
        Assert.Equal(0, pair.Minutes);
    }

    [Fact]
    public void PairsBelongToArriveDay()
    {
        var a = P(3, PunchKind.Arrive, T(5, 9, 0));
        var l = P(3, PunchKind.Leave, T(5, 23, 59));
        var pair = Assert.Single(PunchCalculator.Pair(new[] { a, l }));
        Assert.Equal(T(5, 0, 0), pair.Date);
        Assert.Equal(899, pair.Minutes);
    }

    [Fact]
    public void ArriveTwiceWithoutLeaveLeavesFirstUnpaired()
    {
        var list = new[] { P(7, PunchKind.Arrive, T(4, 9, 0)), P(7, PunchKind.Arrive, T(5, 9, 0)), P(7, PunchKind.Leave, T(5, 12, 0)) };
        var pairs = PunchCalculator.Pair(list);
        Assert.Equal(2, pairs.Count);
        Assert.Null(pairs[0].Leave);
        Assert.Equal(180, pairs[1].Minutes);
    }

    [Fact]
    public void PairingIsPerEngineer()
    {
        var list = new[] { P(3, PunchKind.Arrive, T(6, 9, 0), engineer: 2), P(3, PunchKind.Leave, T(6, 10, 0), engineer: 4) };
        var pairs = PunchCalculator.Pair(list);
        Assert.Single(pairs);
        Assert.Null(pairs[0].Leave);
    }

    [Fact]
    public void SupplementLeaveRemovesMissingPunch()
    {
        var list = new List<PunchInfo>
        {
            P(7, PunchKind.Arrive, T(4, 9, 30)), P(7, PunchKind.Leave, T(4, 17, 45)),
            P(7, PunchKind.Arrive, T(5, 15, 10)),
        };
        Assert.Contains(IssueType.MissingPunch, PunchCalculator.Issues(list, Now.Date));
        Assert.Equal(495, PunchCalculator.Pair(list).Sum(p => p.Minutes));

        var arriveId = list[2].Id;
        Assert.Null(PunchCalculator.ValidateSupplementLeave(list, arriveId, T(5, 18, 20), Now));
        list.Add(P(7, PunchKind.Leave, T(5, 18, 20), supp: true));

        Assert.DoesNotContain(IssueType.MissingPunch, PunchCalculator.Issues(list, Now.Date));
        var pairs = PunchCalculator.Pair(list);
        Assert.Equal(495 + 190, pairs.Sum(p => p.Minutes)); // 8:15 + 3:10 = 11:25
        Assert.True(pairs.Last().IsSupplement);
    }

    [Fact]
    public void SupplementValidation()
    {
        var list = new List<PunchInfo>
        {
            P(7, PunchKind.Arrive, T(5, 15, 10)),
            P(3, PunchKind.Arrive, T(6, 10, 5)),
        };
        var missingId = list[0].Id;
        Assert.Equal("离场时间必须晚于到场时间", PunchCalculator.ValidateSupplementLeave(list, missingId, T(5, 15, 0), Now));
        Assert.Equal("离场时间必须与到场在同一天", PunchCalculator.ValidateSupplementLeave(list, missingId, T(6, 9, 0), Now));
        Assert.Equal("今天的到场仍在计时中，请工程师自行离场打卡", PunchCalculator.ValidateSupplementLeave(list, list[1].Id, T(6, 12, 0), Now));
        Assert.Equal("未找到对应的到场打卡", PunchCalculator.ValidateSupplementLeave(list, 9999, T(5, 18, 0), Now));

        var closed = new List<PunchInfo> { P(7, PunchKind.Arrive, T(4, 9, 0)), P(7, PunchKind.Leave, T(4, 10, 0)) };
        Assert.Equal("该到场打卡已有离场记录，无需补录", PunchCalculator.ValidateSupplementLeave(closed, closed[0].Id, T(4, 11, 0), Now));

        // 不能晚于之后的下一次打卡
        var seq = new List<PunchInfo> { P(7, PunchKind.Arrive, T(5, 9, 0)), P(8, PunchKind.Arrive, T(5, 14, 0)), P(8, PunchKind.Leave, T(5, 16, 0)) };
        Assert.Equal("离场时间不能晚于该工程师之后的下一次打卡", PunchCalculator.ValidateSupplementLeave(seq, seq[0].Id, T(5, 15, 0), Now));
        Assert.Null(PunchCalculator.ValidateSupplementLeave(seq, seq[0].Id, T(5, 13, 0), Now));
    }

    [Fact]
    public void DistanceAbnormalIssue()
    {
        var list = new[] { P(5, PunchKind.Arrive, T(6, 13, 0), far: true) };
        Assert.Equal(new[] { IssueType.DistanceAbnormal }, PunchCalculator.Issues(list, Now.Date));
    }

    [Fact]
    public void TripDayCountsConsecutiveDaysEndingToday()
    {
        var list = new[]
        {
            P(8, PunchKind.Arrive, T(2, 9, 0)), P(8, PunchKind.Arrive, T(3, 9, 0)),
            P(3, PunchKind.Arrive, T(5, 15, 0)), P(3, PunchKind.Arrive, T(6, 10, 0)),
        };
        Assert.Equal(2, PunchCalculator.TripDay(list, Now.Date));
        Assert.Equal(0, PunchCalculator.TripDay(list.Take(3), Now.Date));
        Assert.Equal(4, PunchCalculator.TripDaysInMonth(list, 2026, 11));
        Assert.Equal(0, PunchCalculator.TripDaysInMonth(list, 2026, 10));
    }
}
