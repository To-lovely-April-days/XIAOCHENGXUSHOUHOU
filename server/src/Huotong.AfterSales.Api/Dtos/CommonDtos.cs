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
    /// <summary>展示名与下载名（v1.2）</summary>
    public string Name { get; set; } = "";
    public long Size { get; set; }
    public FileKind Kind { get; set; }
    /// <summary>存储扩展名，小写不带点</summary>
    public string Ext { get; set; } = "";
}

/// <summary>工单附件（v1.2）：建单照片 + 新附件</summary>
public class AttachmentDto : FileDto
{
    public string Source { get; set; } = "";
    public string SourceLabel { get; set; } = "";
    public bool Restricted { get; set; }
    public UserBrief? Uploader { get; set; }
    public bool CanDelete { get; set; }
    public bool CanToggleRestricted { get; set; }
    // ---------- v1.3：附件类别（非附件来源的行：null / "" / "" / false） ----------
    public AttachmentType? AttachmentType { get; set; }
    /// <summary>完整标题</summary>
    public string AttachmentTypeLabel { get; set; } = "";
    /// <summary>短标题</summary>
    public string AttachmentTypeShort { get; set; } = "";
    public bool CanChangeType { get; set; }
}

/// <summary>工单的一台设备（v1.3）</summary>
public class DeviceDto
{
    public int Seq { get; set; }
    public string Name { get; set; } = "";
    public string Sn { get; set; } = "";
    public string Model { get; set; } = "";
    /// <summary>DeviceRules.Title</summary>
    public string Title { get; set; } = "";
}

/// <summary>转交记录（v1.3）。没有 can* 字段：能不能接手 / 拒绝 / 撤回只看 OrderDetail.actions</summary>
public class TransferDto
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public TransferStatus Status { get; set; }
    public string StatusText { get; set; } = "";
    public UserBrief From { get; set; } = new();
    public UserBrief To { get; set; } = new();
    public string Reason { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? DecidedAt { get; set; }
    /// <summary>结束它的人；系统失效时为 null</summary>
    public UserBrief? DecidedBy { get; set; }
    /// <summary>拒绝理由 / 失效原因</summary>
    public string? Reply { get; set; }
    /// <summary>可直接展示的一句话</summary>
    public string Text { get; set; } = "";
}

/// <summary>文件中心的统一行（v1.2）</summary>
public class FileCenterItem : AttachmentDto
{
    public int OrderId { get; set; }
    public string OrderNo { get; set; } = "";
    public string CustomerName { get; set; } = "";
    public OrderStatus OrderStatus { get; set; }
}

public class FileCenterPage
{
    public List<FileCenterItem> Items { get; set; } = new();
    public int Total { get; set; }
    public long TotalSize { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public Dictionary<string, int> Counts { get; set; } = new();
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
    /// <summary>v1.3：旧单为 null（界面展示「—」）</summary>
    public string? ProjectNo { get; set; }
    /// <summary>v1.3：设备汇总文字，永不为空（没有设备 = 「未填设备」）</summary>
    public string DeviceText { get; set; } = "";
    /// <summary>保留（旧客户端用）：第一台设备的 title</summary>
    public string DeviceModel { get; set; } = "";
    /// <summary>保留（旧客户端用）：第一台设备的编号</summary>
    public string DeviceSn { get; set; } = "";
    public string Symptom { get; set; } = "";
    /// <summary>Engineer 永远为 null；未定价为 null</summary>
    public decimal? Price { get; set; }
    /// <summary>Engineer 永远为 null</summary>
    public bool? PriceSet { get; set; }
    public UserBrief? Assignee { get; set; }
    public List<UserBrief> Assistants { get; set; } = new();
    /// <summary>Lead / Assistant；v1.3 新增 Former = 当前用户是本单「已退出」的人（有退出记录）</summary>
    public ParticipantRole? MyRole { get; set; }
    /// <summary>已转交 / 已改派；仅 MyRole = Former 时非 null</summary>
    public string? MyExitText { get; set; }
    /// <summary>本单待确认的转交（v1.3）；任何能看到这张单的人都能看到</summary>
    public TransferDto? PendingTransfer { get; set; }
    public int? MyMinutes { get; set; }
    public DateTime? MyOnSiteSince { get; set; }
    public int ProgressStep { get; set; }
    public HoldReasonType? HoldReasonType { get; set; }
    public string? HoldText { get; set; }
    public OrderStatus? HoldFromStatus { get; set; }
    public int PendingRequestCount { get; set; }
    public string? FactoryText { get; set; }
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
    public List<FileDto> FactoryPhotos { get; set; } = new();
}

public class IssueDetailDto
{
    public IssueType Type { get; set; }
    public string Text { get; set; } = "";
    public int? PunchId { get; set; }
    public DateOnly Date { get; set; }
    public UserBrief? Engineer { get; set; }
}

public class PriceSuggestionDto
{
    public int Id { get; set; }
    public decimal Amount { get; set; }
    public string? Note { get; set; }
    public UserBrief By { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    /// <summary>主管尚未在此建议之后定价</summary>
    public bool Pending { get; set; }
}

public class HoldDto
{
    public HoldReasonType ReasonType { get; set; }
    public string ReasonText { get; set; } = "";
    public string? Note { get; set; }
    public string Text { get; set; } = "";
    public OrderStatus FromStatus { get; set; }
    public DateTime? At { get; set; }
    public UserBrief? By { get; set; }
}

public class RequestDto
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public OrderRequestType Type { get; set; }
    public RequestStatus Status { get; set; }
    public UserBrief Requester { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public HoldReasonType? ReasonType { get; set; }
    public string? ReasonText { get; set; }
    public string? Note { get; set; }
    public string Text { get; set; } = "";
    public UserBrief? Engineer { get; set; }
    public UserBrief? Decider { get; set; }
    public DateTime? DecidedAt { get; set; }
    public string? Reply { get; set; }
    public bool CanDecide { get; set; }
    public bool CanCancel { get; set; }
}

public class ParticipantDto
{
    public UserBrief User { get; set; } = new();
    public ParticipantRole Role { get; set; }
    public int TotalMinutes { get; set; }
    public DateTime? OnSiteSince { get; set; }
    public bool MissingPunch { get; set; }
    /// <summary>v1.3：已转交 / 已改派；只有带退出记录的 Former 有值</summary>
    public string? ExitText { get; set; }
    public DateTime? ExitedAt { get; set; }
}

public class FactoryNodeDto
{
    public FactoryNode Node { get; set; }
    public string Label { get; set; } = "";
    /// <summary>done / current / todo / skipped</summary>
    public string State { get; set; } = "todo";
    public bool Recorded { get; set; }
    public DateTime? Time { get; set; }
    public UserBrief? Operator { get; set; }
    public string? Courier { get; set; }
    public string? TrackingNo { get; set; }
    public string? Note { get; set; }
    public List<FileDto> Photos { get; set; } = new();
    public bool CanRecord { get; set; }
    public bool CanEdit { get; set; }
}

public class FactoryDto
{
    public List<FactoryNodeDto> Nodes { get; set; } = new();
}

public class OrderDetail : OrderListItem
{
    public OrderSource Source { get; set; }
    public string ContactName { get; set; } = "";
    public string ContactPhone { get; set; } = "";
    public string Address { get; set; } = "";
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    /// <summary>v1.3：SAT 完成时间（satSigned = true）/ 发货时间（false）；""、yyyy-MM-dd 或旧数据的 yyyy-MM</summary>
    public string SatDate { get; set; } = "";
    /// <summary>v1.3 含义：是否已完成 SAT</summary>
    public bool SatSigned { get; set; }
    public string SatText { get; set; } = "";
    public string SatDateLabel { get; set; } = "";
    public string ProductStage { get; set; } = "";
    /// <summary>当前用户现在可以修改 SAT 信息</summary>
    public bool CanEditSat { get; set; }
    /// <summary>设备清单，按 seq 正序</summary>
    public List<DeviceDto> Devices { get; set; } = new();
    /// <summary>本单全部转交记录（含已结束的），按 createdAt、id 正序</summary>
    public List<TransferDto> Transfers { get; set; } = new();
    /// <summary>仅当前用户是「已退出」的工程师时非 null</summary>
    public string? ReadOnlyText { get; set; }
    public string FaultTime { get; set; } = "";
    public string CustomerTried { get; set; } = "";
    public string SiteEnvironment { get; set; } = "";
    public string? CustomerRequest { get; set; }
    public DateTime? PriceSetAt { get; set; }
    public PriceSuggestionDto? PriceSuggestion { get; set; }
    /// <summary>保留（旧客户端用）：= holdText</summary>
    public string? HoldReason { get; set; }
    public HoldDto? Hold { get; set; }
    public List<RequestDto> Requests { get; set; } = new();
    public List<ParticipantDto> Participants { get; set; } = new();
    public FactoryDto? Factory { get; set; }
    public string? CloseBlockedReason { get; set; }
    public List<string> CloseHints { get; set; } = new();
    public CompletionDto? Completion { get; set; }
    public OrderFilesDto Files { get; set; } = new();
    public List<PunchDto> Punches { get; set; } = new();
    public List<StatusLogDto> Logs { get; set; } = new();
    public List<IssueDetailDto> IssueDetails { get; set; } = new();
    public List<string> Actions { get; set; } = new();
    // ---------- v1.2：附件 ----------
    /// <summary>当前用户可见的附件，按 createdAt、id 正序</summary>
    public List<AttachmentDto> Attachments { get; set; } = new();
    public bool CanAddAttachment { get; set; }
    public bool CanRestrictAttachment { get; set; }
    public int AttachmentLimit { get; set; } = 50;
    /// <summary>还能再加几个（含当前用户看不到的受限附件）</summary>
    public int AttachmentRemaining { get; set; }
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
    public LogKind Kind { get; set; }
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
