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
    public string? FaultTime { get; set; }
    public string? Symptom { get; set; }
    public string? CustomerTried { get; set; }
    public string? SiteEnvironment { get; set; }
    public string? CustomerRequest { get; set; }
    public decimal? Price { get; set; }
    public List<int>? PhotoFileIds { get; set; }
}

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

public class AssignRequest { public int? EngineerId { get; set; } }
public class ReasonRequest { public string? Reason { get; set; } }

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
