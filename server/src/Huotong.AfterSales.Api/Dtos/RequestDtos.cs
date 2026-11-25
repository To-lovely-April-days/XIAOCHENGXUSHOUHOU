using Huotong.AfterSales.Api.Domain;

namespace Huotong.AfterSales.Api.Dtos;

// 请求体：校验在服务层完成（统一中文错误）

public class LoginRequest { public string? Code { get; set; } }
public class BindRequest { public string? BindTicket { get; set; } public string? PhoneCode { get; set; } }
public class DevLoginRequest { public string? Phone { get; set; } }
public class PasswordLoginRequest { public string? Phone { get; set; } public string? Password { get; set; } }
public class ChangePasswordRequest { public string? OldPassword { get; set; } public string? NewPassword { get; set; } }
public class SetPasswordRequest { public string? Password { get; set; } }

/// <summary>v1.5 小程序账号密码登录；Code = wx.login 的 code（可空，有则顺带把当前微信绑到账号上，用于订阅消息）</summary>
public class AccountLoginRequest { public string? Phone { get; set; } public string? Password { get; set; } public string? Code { get; set; } }
/// <summary>v1.5 小程序注册申请；Code = wx.login 的 code（必填）</summary>
public class RegisterRequest
{
    public string? Name { get; set; }
    public string? Phone { get; set; }
    public string? Password { get; set; }
    public string? Code { get; set; }
}
public class RegistrationApproveRequest
{
    public Role? Role { get; set; }
    public Position? Position { get; set; }
    public bool? IsAdmin { get; set; }
    public string? Name { get; set; }
    public string? Department { get; set; }
}
public class RegistrationRejectRequest { public string? Reason { get; set; } }

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
    /// <summary>
    /// v2.0 完工表版本：2 = 按服务方式区分资料（新版客户端都传 2）；不传 / 1 = 旧规则（旧版客户端升级前照常能提交）。
    /// </summary>
    public int? FormVersion { get; set; }
    /// <summary>v2.0 上门：维修前照片（设备原始状态）、维修后照片；存成完工照片并记下前 / 后</summary>
    public List<int>? BeforePhotoIds { get; set; }
    public List<int>? AfterPhotoIds { get; set; }
    /// <summary>v2.0 上门 = 现场维修登记表（签字版），返厂 = 返厂维修登记表；提交时挂成对应类别的附件。之前已经传过的也算</summary>
    public List<int>? RepairFormIds { get; set; }
    /// <summary>v2.0 远程：客户确认方式</summary>
    public string? CustomerConfirm { get; set; }
    /// <summary>v2.0 返厂：寄回前测试结果</summary>
    public string? TestResult { get; set; }
}

/// <summary>v2.0 申请升级服务方式（远程 → 上门 / 返厂；上门 → 返厂），需主管同意</summary>
public class EscalateRequest
{
    public string? TargetServiceType { get; set; }
    public string? Reason { get; set; }
    /// <summary>已经排查 / 处理了什么</summary>
    public string? Findings { get; set; }
}

public class AssignRequest
{
    public int? EngineerId { get; set; }
    /// <summary>省略 / null = 不调整协助人；数组（含空数组）= 协助人最终全集</summary>
    public List<int>? AssistantIds { get; set; }
}
public class ReasonRequest { public string? Reason { get; set; } }
/// <summary>v1.9 确认闭环时的备注（选填，最多 200 字）</summary>
public class CloseRequest { public string? Note { get; set; } }

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
    /// <summary>v1.7 岗位；传了就以它为准，没传按 Role 推</summary>
    public Position? Position { get; set; }
    /// <summary>v1.7 管理员权限（管理员岗位恒为 true）</summary>
    public bool? IsAdmin { get; set; }
    /// <summary>v1.8 注册审批权限</summary>
    public bool? CanApprove { get; set; }
    public string? Department { get; set; }
    /// <summary>v1.5 新增账号时可顺带设初始密码（可空）；编辑时忽略</summary>
    public string? Password { get; set; }
}

public class EmployeeUpdateRequest : EmployeeCreateRequest
{
    public bool? Enabled { get; set; }
}
