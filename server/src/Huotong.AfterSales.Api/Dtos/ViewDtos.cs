using Huotong.AfterSales.Api.Domain;

namespace Huotong.AfterSales.Api.Dtos;

public class MeDto
{
    public UserDto User { get; set; } = new();
    public int MonthMinutes { get; set; }
    public int ActiveOrderCount { get; set; }
    public int MonthTripDays { get; set; }
}

public class RecentCustomerDto
{
    public string CustomerName { get; set; } = "";
    public string ContactName { get; set; } = "";
    public string ContactPhone { get; set; } = "";
    public string Address { get; set; } = "";
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string DeviceModel { get; set; } = "";
    public string DeviceSn { get; set; } = "";
    public string SatDate { get; set; } = "";
    public bool SatSigned { get; set; }
    public string? ProjectNo { get; set; }
}

public class PunchContextDto
{
    public int OrderId { get; set; }
    public string OrderNo { get; set; } = "";
    public string CustomerName { get; set; } = "";
    public string Address { get; set; } = "";
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public PunchKind NextKind { get; set; }
    public DateTime? OpenArriveTime { get; set; }
    public string? BlockedReason { get; set; }
    public int RangeMeters { get; set; }
    public DateTime ServerTime { get; set; }
    public string EngineerName { get; set; } = "";
    public ParticipantRole? MyRole { get; set; }
}

public class PunchResultDto
{
    public PunchDto Punch { get; set; } = new();
    public OrderListItem Order { get; set; } = new();
}

public class TodayEntryDto
{
    public int OrderId { get; set; }
    public string OrderNo { get; set; } = "";
    public string CustomerName { get; set; } = "";
    public PunchDto Arrive { get; set; } = new();
    public PunchDto? Leave { get; set; }
    public int Minutes { get; set; }
    public bool IsSupplement { get; set; }
    /// <summary>此工程师当前在该工单的角色</summary>
    public ParticipantRole Role { get; set; }
}

public class TodayHoursDto
{
    public DateOnly Date { get; set; }
    public int ClosedMinutes { get; set; }
    public bool Running { get; set; }
    public DateTime? RunningSince { get; set; }
    public DateTime ServerTime { get; set; }
    public DateTime? FirstArriveTime { get; set; }
    public int TripDay { get; set; }
    public List<TodayEntryDto> Entries { get; set; } = new();
}

public class MonthEntryDto
{
    public int OrderId { get; set; }
    public string OrderNo { get; set; } = "";
    public string CustomerName { get; set; } = "";
    public DateTime ArriveTime { get; set; }
    public DateTime? LeaveTime { get; set; }
    public int Minutes { get; set; }
    public bool IsSupplement { get; set; }
    public string? SupplementReason { get; set; }
    public bool Missing { get; set; }
    /// <summary>此工程师当前在该工单的角色</summary>
    public ParticipantRole Role { get; set; }
}

public class MonthDayDto
{
    public DateOnly Date { get; set; }
    public int Minutes { get; set; }
    public bool Running { get; set; }
    public bool HasSupplement { get; set; }
    public List<MonthEntryDto> Entries { get; set; } = new();
}

public class MonthHoursDto
{
    public int Year { get; set; }
    public int Month { get; set; }
    public int TotalMinutes { get; set; }
    public int TripDays { get; set; }
    public List<MonthDayDto> Days { get; set; } = new();
}

public class HallSummaryDto
{
    public int Count { get; set; }
    public int UrgentCount { get; set; }
    public List<OrderListItem> Items { get; set; } = new();
}

public class EngineerHomeDto
{
    public UserDto User { get; set; } = new();
    public TodayHoursDto Today { get; set; } = new();
    public string State { get; set; } = "idle";
    public OrderListItem? Current { get; set; }
    public NextOrderDto? Next { get; set; }
    public List<OrderListItem> Upcoming { get; set; } = new();
    public HallSummaryDto Hall { get; set; } = new();
    public List<OrderListItem> RecentDone { get; set; } = new();
    /// <summary>本人参与的暂停搁置工单，按 HoldAt 倒序</summary>
    public List<OrderListItem> OnHold { get; set; } = new();
    public int MonthMinutes { get; set; }
}

public class TimelineSegmentDto
{
    public DateTime Start { get; set; }
    public DateTime? End { get; set; }
    public string CustomerName { get; set; } = "";
    public int OrderId { get; set; }
}

public class TimelineRowDto
{
    public UserBrief Engineer { get; set; } = new();
    public List<TimelineSegmentDto> Segments { get; set; } = new();
    public string Note { get; set; } = "";
}

public class TodoDto
{
    public string Type { get; set; } = "";
    public int OrderId { get; set; }
    public int? RequestId { get; set; }
    public string Title { get; set; } = "";
    public bool Urgent { get; set; }
    public string Subtitle { get; set; } = "";
}

public class SupervisorHomeDto
{
    public UserDto User { get; set; } = new();
    public DateTime Now { get; set; }
    public int OnSiteCount { get; set; }
    public List<TimelineRowDto> Timeline { get; set; } = new();
    public List<TodoDto> Todos { get; set; } = new();
}

public class EngineerLoadDto
{
    public UserBrief User { get; set; } = new();
    public int DoingCount { get; set; }
    public int AcceptedCount { get; set; }
    public int SubmittedCount { get; set; }
    public int AssistCount { get; set; }
    public bool OnSiteNow { get; set; }
    public string LoadText { get; set; } = "";
}

public class OverviewDto
{
    public int Pending { get; set; }
    public int UrgentPending { get; set; }
    public int InProgress { get; set; }
    public int OnSiteEngineers { get; set; }
    public int Submitted { get; set; }
    public int ClosedThisWeek { get; set; }
    public int WeekMinutes { get; set; }
    public int OnHold { get; set; }
    public int PendingRequests { get; set; }
    public int Unpriced { get; set; }
    public int PriceSuggestions { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class EmployeeDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Phone { get; set; } = "";
    public Role Role { get; set; }
    public string RoleName { get; set; } = "";
    public string Department { get; set; } = "";
    public bool Enabled { get; set; }
    public bool Bound { get; set; }
    public string? AvatarUrl { get; set; }
    public bool HasPassword { get; set; }
}

// ---------- PC 主管后台（API.md §9） ----------

public class AdminPunchItemDto : PunchDto
{
    public string OrderNo { get; set; } = "";
    public string CustomerName { get; set; } = "";
    /// <summary>缺卡：没有配对离场且早于今天的到场卡</summary>
    public bool Missing { get; set; }
}

public class AdminPunchPageDto
{
    public List<AdminPunchItemDto> Items { get; set; } = new();
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public Dictionary<string, int> Counts { get; set; } = new();
}

public class AdminHoursDayDto
{
    public DateOnly Date { get; set; }
    public int Minutes { get; set; }
}

public class AdminHoursEngineerDto
{
    public UserBrief Engineer { get; set; } = new();
    public int TotalMinutes { get; set; }
    public int TripDays { get; set; }
    public int OrderCount { get; set; }
    public int SupplementCount { get; set; }
    public int MissingCount { get; set; }
    public bool RunningNow { get; set; }
    public List<AdminHoursDayDto> Days { get; set; } = new();
}

public class AdminHoursDto
{
    public int Year { get; set; }
    public int Month { get; set; }
    public int TotalMinutes { get; set; }
    public List<AdminHoursEngineerDto> Engineers { get; set; } = new();
}
