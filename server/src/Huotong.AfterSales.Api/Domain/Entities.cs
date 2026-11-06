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

    public string? HoldReason { get; set; }
    public string? VoidReason { get; set; }

    // 完工信息
    public string? FaultCause { get; set; }
    public string? Solution { get; set; }
    public string? PartsReplaced { get; set; }
    public RunStatus? RunStatus { get; set; }

    public List<Punch> Punches { get; set; } = new();
    public List<StatusLog> Logs { get; set; } = new();
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
}
