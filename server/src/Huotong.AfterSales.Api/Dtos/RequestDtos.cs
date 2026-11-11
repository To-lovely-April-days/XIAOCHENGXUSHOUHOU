using Huotong.AfterSales.Api.Domain;

namespace Huotong.AfterSales.Api.Dtos;

// 请求体：校验在服务层完成（统一中文错误）

public class LoginRequest { public string? Code { get; set; } }
public class BindRequest { public string? BindTicket { get; set; } public string? PhoneCode { get; set; } }
public class DevLoginRequest { public string? Phone { get; set; } }
public class PasswordLoginRequest { public string? Phone { get; set; } public string? Password { get; set; } }
public class ChangePasswordRequest { public string? OldPassword { get; set; } public string? NewPassword { get; set; } }
public class SetPasswordRequest { public string? Password { get; set; } }

public class LoginResult
{
    public string? Token { get; set; }
    public UserDto? User { get; set; }
    public bool NeedBind { get; set; }
    public string? BindTicket { get; set; }
}

public class AvatarRequest { public int FileId { get; set; } }

public class CreateOrderRequest
{
    public OrderSource? Source { get; set; }
    public ProblemType? ProblemType { get; set; }
    public Urgency? Urgency { get; set; }
    public ServiceType? ServiceType { get; set; }
    public string? CustomerName { get; set; }
    public string? ContactName { get; set; }
    public string? ContactPhone { get; set; }
    public string? Address { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string? DeviceModel { get; set; }
    public string? DeviceSn { get; set; }
    public string? SatDate { get; set; }
    public bool? SatSigned { get; set; }
    public string? ProjectNo { get; set; }
    /// <summary>v1.3：设备清单。null（省略）= 旧客户端，按 deviceModel / deviceSn 合成一台；[] = 明确没有设备</summary>
    public List<DeviceItem>? Devices { get; set; }
    public string? FaultTime { get; set; }
    public string? Symptom { get; set; }
    public string? CustomerTried { get; set; }
    public string? SiteEnvironment { get; set; }
    public string? CustomerRequest { get; set; }
    /// <summary>v1.1 起忽略（旧客户端仍会传）；用 object 接收，任何形态都不报错</summary>
    public object? Price { get; set; }
    public List<int>? PhotoFileIds { get; set; }
    /// <summary>v1.2：附件（文件先以 category=Attachment 上传）</summary>
    public List<AttachmentItem>? Attachments { get; set; }
}

public class DeviceItem { public string? Name { get; set; } public string? Sn { get; set; } public string? Model { get; set; } }

/// <summary>Type（v1.3）用字符串接收，非法值由服务层报「附件类别不正确」；省略 = Other</summary>
public class AttachmentItem { public int? FileId { get; set; } public bool? Restricted { get; set; } public string? Type { get; set; } }
public class AttachmentsRequest { public List<AttachmentItem>? Attachments { get; set; } }
public class RestrictedRequest { public bool? Restricted { get; set; } }
public class AttachmentTypeRequest { public string? Type { get; set; } }
public class SatRequest { public bool? SatSigned { get; set; } public string? SatDate { get; set; } }
public class TransferRequest { public int? EngineerId { get; set; } public string? Reason { get; set; } }

public class AppointmentRequest { public DateTime? AppointmentTime { get; set; } }

public class CompleteRequest
{
    public string? FaultCause { get; set; }
    public string? Solution { get; set; }
    public string? PartsReplaced { get; set; }
    public RunStatus? RunStatus { get; set; }
    public List<int>? ProcessPhotoIds { get; set; }
    public List<int>? SignaturePhotoIds { get; set; }
}

public class AssignRequest
{
    public int? EngineerId { get; set; }
    /// <summary>省略 / null = 不调整协助人；数组（含空数组）= 协助人最终全集</summary>
    public List<int>? AssistantIds { get; set; }
}
public class ReasonRequest { public string? Reason { get; set; } }

public class PriceRequest { public decimal? Price { get; set; } }
public class PriceSuggestRequest { public decimal? Amount { get; set; } public string? Note { get; set; } }

/// <summary>暂停 / 暂停申请。ReasonType 用字符串接收，非法值由服务层报「请选择暂停原因」；Reason 为旧客户端字段</summary>
public class HoldRequest
{
    public string? ReasonType { get; set; }
    public string? Note { get; set; }
    public string? Reason { get; set; }
}

public class ReplyRequest { public string? Reply { get; set; } }
public class AssistantsRequest { public List<int>? EngineerIds { get; set; } }
public class AssistantRequestRequest { public int? EngineerId { get; set; } public string? Note { get; set; } }

/// <summary>返厂节点记录 / 更正。Node 用字符串接收，非法值由服务层报「请选择返厂节点」</summary>
public class FactoryNodeRequest
{
    public string? Node { get; set; }
    public string? Courier { get; set; }
    public string? TrackingNo { get; set; }
    public string? Note { get; set; }
    public List<int>? PhotoIds { get; set; }
}

public class PunchRequest
{
    public int? OrderId { get; set; }
    public PunchKind? Kind { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string? Address { get; set; }
    public int? PhotoFileId { get; set; }
}

public class SupplementRequest
{
    public int? OrderId { get; set; }
    public int? ArrivePunchId { get; set; }
    public PunchKind? Kind { get; set; }
    public DateTime? Time { get; set; }
    public string? Reason { get; set; }
}

public class EmployeeCreateRequest
{
    public string? Name { get; set; }
    public string? Phone { get; set; }
    public Role? Role { get; set; }
    public string? Department { get; set; }
}

public class EmployeeUpdateRequest : EmployeeCreateRequest
{
    public bool? Enabled { get; set; }
}
