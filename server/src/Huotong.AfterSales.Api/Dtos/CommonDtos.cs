using Huotong.AfterSales.Api.Domain;

namespace Huotong.AfterSales.Api.Dtos;

public class UserBrief
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Surname { get; set; } = "";
    public string? AvatarUrl { get; set; }
    public Role Role { get; set; }
}

public class UserDto : UserBrief
{
    public string RoleName { get; set; } = "";
    public string Department { get; set; } = "";
    public string PhoneMasked { get; set; } = "";
    public string? Phone { get; set; }
}

public class FileDto
{
    public int Id { get; set; }
    public string Url { get; set; } = "";
    public FileCategory Category { get; set; }
    public string MimeType { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public class OrderListItem
{
    public int Id { get; set; }
    public string OrderNo { get; set; } = "";
    public OrderStatus Status { get; set; }
    public Urgency Urgency { get; set; }
    public ServiceType ServiceType { get; set; }
    public ProblemType ProblemType { get; set; }
    public string CustomerName { get; set; } = "";
    public string City { get; set; } = "";
    public string Region { get; set; } = "";
    public string DeviceModel { get; set; } = "";
    public string DeviceSn { get; set; } = "";
    public string Symptom { get; set; } = "";
    public decimal Price { get; set; }
    public UserBrief? Assignee { get; set; }
    public UserBrief Creator { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public DateTime? ClaimedAt { get; set; }
    public DateTime? AppointmentTime { get; set; }
    public DateTime? SubmittedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
    public int TotalMinutes { get; set; }
    public DateTime? OnSiteSince { get; set; }
    public DateTime? LastArriveTime { get; set; }
    public List<IssueType> Issues { get; set; } = new();
}

/// <summary>首页 next：OrderListItem + 地址联系人</summary>
public class NextOrderDto : OrderListItem
{
    public string Address { get; set; } = "";
    public string ContactName { get; set; } = "";
    public string ContactPhone { get; set; } = "";
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
}

public class CompletionDto
{
    public string FaultCause { get; set; } = "";
    public string Solution { get; set; } = "";
    public string PartsReplaced { get; set; } = "";
    public RunStatus RunStatus { get; set; }
    public DateTime? SubmittedAt { get; set; }
}

public class OrderFilesDto
{
    public List<FileDto> OrderPhotos { get; set; } = new();
    public List<FileDto> PunchPhotos { get; set; } = new();
    public List<FileDto> ProcessPhotos { get; set; } = new();
    public List<FileDto> Signatures { get; set; } = new();
}

public class IssueDetailDto
{
    public IssueType Type { get; set; }
    public string Text { get; set; } = "";
    public int? PunchId { get; set; }
    public DateOnly Date { get; set; }
}

public class OrderDetail : OrderListItem
{
    public OrderSource Source { get; set; }
    public string ContactName { get; set; } = "";
    public string ContactPhone { get; set; } = "";
    public string Address { get; set; } = "";
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string SatDate { get; set; } = "";
    public bool SatSigned { get; set; }
    public string ProductStage { get; set; } = "";
    public string? ProjectNo { get; set; }
    public string FaultTime { get; set; } = "";
    public string CustomerTried { get; set; } = "";
    public string SiteEnvironment { get; set; } = "";
    public string? CustomerRequest { get; set; }
    public string? HoldReason { get; set; }
    public CompletionDto? Completion { get; set; }
    public OrderFilesDto Files { get; set; } = new();
    public List<PunchDto> Punches { get; set; } = new();
    public List<StatusLogDto> Logs { get; set; } = new();
    public List<IssueDetailDto> IssueDetails { get; set; } = new();
    public List<string> Actions { get; set; } = new();
}

public class PunchDto
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public UserBrief Engineer { get; set; } = new();
    public PunchKind Kind { get; set; }
    public DateTime Time { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string Address { get; set; } = "";
    public int? DistanceMeters { get; set; }
    public bool OutOfRange { get; set; }
    public FileDto? Photo { get; set; }
    public bool IsSupplement { get; set; }
    public UserBrief? SupplementBy { get; set; }
    public string? SupplementReason { get; set; }
    public DateTime? SupplementAt { get; set; }
    public int? PairMinutes { get; set; }
}

public class StatusLogDto
{
    public int Id { get; set; }
    public string Text { get; set; } = "";
    public DateTime Time { get; set; }
    public UserBrief Actor { get; set; } = new();
    public OrderStatus ToStatus { get; set; }
}

public class ListResult<T>
{
    public List<T> Items { get; set; } = new();
    public Dictionary<string, int> Counts { get; set; } = new();
}

public class PagedOrders
{
    public List<OrderListItem> Items { get; set; } = new();
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public Dictionary<string, int> Counts { get; set; } = new();
}
