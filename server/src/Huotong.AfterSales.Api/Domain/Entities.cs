namespace Huotong.AfterSales.Api.Domain;

// 所有 DateTime 均为北京时间（Kind=Unspecified）

public class Employee
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Phone { get; set; } = "";
    public Role Role { get; set; }
    public string Department { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string? OpenId { get; set; }
    /// <summary>令牌版本：停用/解绑/改角色/换手机号时递增，旧 JWT 随即失效</summary>
    public int TokenVersion { get; set; }
    /// <summary>PC 后台登录密码哈希（PasswordHasher&lt;Employee&gt;，PBKDF2）；为空 = 未设置密码，不能在 PC 登录</summary>
    public string? PasswordHash { get; set; }
    public int? AvatarFileId { get; set; }
    public StoredFile? AvatarFile { get; set; }
    public DateTime CreatedAt { get; set; }

    public string Surname => string.IsNullOrEmpty(Name) ? "" : Name.Substring(0, 1);
}

public class WorkOrder
{
    public int Id { get; set; }
    public string OrderNo { get; set; } = "";
    public OrderStatus Status { get; set; }

    public OrderSource Source { get; set; }
    public ProblemType ProblemType { get; set; }
    public Urgency Urgency { get; set; }
    public ServiceType ServiceType { get; set; }

    public string CustomerName { get; set; } = "";
    public string ContactName { get; set; } = "";
    public string ContactPhone { get; set; } = "";
    public string Address { get; set; } = "";
    public string City { get; set; } = "";
    public string Region { get; set; } = "";
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }

    public string DeviceModel { get; set; } = "";
    public string DeviceSn { get; set; } = "";
    public string SatDate { get; set; } = "";
    public bool SatSigned { get; set; }
    public string? ProjectNo { get; set; }

    public string FaultTime { get; set; } = "";
    public string Symptom { get; set; } = "";
    public string CustomerTried { get; set; } = "";
    public string SiteEnvironment { get; set; } = "";
    public string? CustomerRequest { get; set; }

    public decimal Price { get; set; }
    /// <summary>是否已定价（v1.1）。不要配置 HasDefaultValue；迁移里列默认值为 true（已有数据视为已定价），新建工单显式写 false</summary>
    public bool PriceSet { get; set; }
    /// <summary>最近一次主管定价时间</summary>
    public DateTime? PriceSetAt { get; set; }

    public int CreatorId { get; set; }
    public Employee? Creator { get; set; }
    public int? AssigneeId { get; set; }
    public Employee? Assignee { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? ClaimedAt { get; set; }
    public DateTime? AppointmentTime { get; set; }
    /// <summary>首次进入处理中的时间</summary>
    public DateTime? StartedAt { get; set; }
    public DateTime? SubmittedAt { get; set; }
    public DateTime? ClosedAt { get; set; }

    /// <summary>暂停说明（note）</summary>
    public string? HoldReason { get; set; }
    public HoldReasonType? HoldReasonType { get; set; }
    /// <summary>暂停前的状态（Accepted / InProgress），恢复时回到它</summary>
    public OrderStatus? HoldFromStatus { get; set; }
    public DateTime? HoldAt { get; set; }
    /// <summary>执行暂停的主管 id（普通整数列，无外键）</summary>
    public int? HoldById { get; set; }
    public string? VoidReason { get; set; }

    // 完工信息
    public string? FaultCause { get; set; }
    public string? Solution { get; set; }
    public string? PartsReplaced { get; set; }
    public RunStatus? RunStatus { get; set; }

    public List<Punch> Punches { get; set; } = new();
    public List<StatusLog> Logs { get; set; } = new();
    public List<OrderAssistant> Assistants { get; set; } = new();
}

public class Punch
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public WorkOrder? Order { get; set; }
    public int EngineerId { get; set; }
    public Employee? Engineer { get; set; }
    public PunchKind Kind { get; set; }
    public DateTime Time { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string Address { get; set; } = "";
    public int? DistanceMeters { get; set; }
    public bool OutOfRange { get; set; }
    public int? PhotoFileId { get; set; }
    public StoredFile? PhotoFile { get; set; }

    public bool IsSupplement { get; set; }
    public int? SupplementById { get; set; }
    public Employee? SupplementBy { get; set; }
    public string? SupplementReason { get; set; }
    public DateTime? SupplementAt { get; set; }
}

public class StatusLog
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public WorkOrder? Order { get; set; }
    public int ActorId { get; set; }
    public Employee? Actor { get; set; }
    public string Text { get; set; } = "";
    public DateTime Time { get; set; }
    public OrderStatus ToStatus { get; set; }
    /// <summary>记录类别；null = Status（v1.1 之前的数据）</summary>
    public LogKind? Kind { get; set; }
}

public class StoredFile
{
    public int Id { get; set; }
    /// <summary>相对 uploads 目录的路径，如 2026/11/xxx.jpg</summary>
    public string Path { get; set; } = "";
    public string OriginalName { get; set; } = "";
    public FileCategory Category { get; set; }
    public string MimeType { get; set; } = "";
    public long Size { get; set; }
    public int UploaderId { get; set; }
    public int? OrderId { get; set; }
    public DateTime CreatedAt { get; set; }
    /// <summary>客户端明确给出的原始文件名（已清洗）；null = 展示名由服务端生成（v1.2）</summary>
    public string? DisplayName { get; set; }
    /// <summary>仅销售和主管可见（只对附件有意义）。不要配置 HasDefaultValue；列默认值 false 只写在迁移里</summary>
    public bool Restricted { get; set; }
    /// <summary>软删除时间；非 null = 已删除（磁盘文件保留）。AppDbContext 的全局查询过滤器让已删除文件对所有读取路径不可见</summary>
    public DateTime? DeletedAt { get; set; }
    /// <summary>删除人 id（普通整数列，无外键）</summary>
    public int? DeletedById { get; set; }
}

/// <summary>工单协助人（当前有效的；移除 = 删除行）</summary>
public class OrderAssistant
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public WorkOrder? Order { get; set; }
    public int EngineerId { get; set; }
    public Employee? Engineer { get; set; }
    public int AddedById { get; set; }
    public Employee? AddedBy { get; set; }
    public DateTime AddedAt { get; set; }
}

/// <summary>工程师发起、需主管审批的申请：暂停 / 添加协助人</summary>
public class OrderRequest
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public WorkOrder? Order { get; set; }
    public OrderRequestType Type { get; set; }
    public RequestStatus Status { get; set; }
    public int RequesterId { get; set; }
    public Employee? Requester { get; set; }
    public DateTime CreatedAt { get; set; }
    public HoldReasonType? HoldReasonType { get; set; }
    public string? Note { get; set; }
    public int? TargetEngineerId { get; set; }
    public Employee? TargetEngineer { get; set; }
    public int? DeciderId { get; set; }
    public Employee? Decider { get; set; }
    public DateTime? DecidedAt { get; set; }
    public string? Reply { get; set; }
}

/// <summary>销售的价格建议（保留历史，详情只展示最新一条）</summary>
public class PriceSuggestion
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public WorkOrder? Order { get; set; }
    public int SalesId { get; set; }
    public Employee? Sales { get; set; }
    public decimal Amount { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>返厂节点记录，每单每节点最多 1 行</summary>
public class FactoryRecord
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public WorkOrder? Order { get; set; }
    public FactoryNode Node { get; set; }
    public int OperatorId { get; set; }
    public Employee? Operator { get; set; }
    public DateTime Time { get; set; }
    /// <summary>最近更正人（普通整数列，无外键）</summary>
    public int? UpdatedById { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string? Courier { get; set; }
    public string? TrackingNo { get; set; }
    public string? Note { get; set; }
}
