namespace Huotong.AfterSales.Api.Domain;

// 所有 DateTime 均为北京时间（Kind=Unspecified）

public class Employee
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Phone { get; set; } = "";
    public Role Role { get; set; }
    /// <summary>v1.7 岗位（显示用）；与 Role 对不上时（旧数据 / 直接改了 Role）以 Role 为准，见 DisplayPosition</summary>
    public Position Position { get; set; }
    /// <summary>v1.7 管理员权限：能登录电脑后台、管理账号、审批注册。管理员岗位恒为 true</summary>
    public bool IsAdmin { get; set; }
    /// <summary>v1.8 注册审批权限：能在小程序里审批注册申请，并在有新申请时收到微信提醒。管理员不勾也能审批，但不收提醒</summary>
    public bool CanApprove { get; set; }
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
    /// <summary>v2.2 关掉的微信提醒类别（NoticeRules 里的 key，逗号分隔）；空 = 全部都收</summary>
    public string MutedNotices { get; set; } = "";

    public string Surname => string.IsNullOrEmpty(Name) ? "" : Name.Substring(0, 1);
    /// <summary>显示用岗位：Position 与 Role 一致时用 Position，否则按 Role 推出</summary>
    public Position DisplayPosition => Positions.ToRole(Position) == Role ? Position : Positions.FromRole(Role);
    public bool IsSalesManager => DisplayPosition == Position.SalesManager;
}

/// <summary>
/// v1.5 小程序注册申请：员工自己填姓名 / 手机号 / 密码，主管在后台审批通过后才生成 Employee。
/// 一个手机号只有一行（驳回后重新提交会复用这一行）。
/// </summary>
public class Registration
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Phone { get; set; } = "";
    /// <summary>与 Employee.PasswordHash 同一种哈希；通过后原样转给 Employee 并在这里清空</summary>
    public string? PasswordHash { get; set; }
    /// <summary>提交申请时的微信 openid（只用于限流与排查，不作为登录凭据）</summary>
    public string? OpenId { get; set; }
    public RegistrationStatus Status { get; set; }
    public string? RejectReason { get; set; }
    public int? ReviewedById { get; set; }
    public Employee? ReviewedBy { get; set; }
    public DateTime? ReviewedAt { get; set; }
    /// <summary>通过后生成的员工</summary>
    public int? EmployeeId { get; set; }
    public DateTime CreatedAt { get; set; }
    /// <summary>最近一次提交时间（重新提交会更新）</summary>
    public DateTime SubmittedAt { get; set; }
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
    /// <summary>v2.4 工单种类。不要配置 HasDefaultValue；列默认值 'Repair' 只写在迁移里（旧数据都是维修工单）</summary>
    public OrderKind Kind { get; set; }

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
    /// <summary>设备汇总文字（v1.3，DeviceRules.Summary）：建单时写入，之后不变；列表 / 通知 / 导出一律读它。不要配置 HasDefaultValue</summary>
    public string DeviceText { get; set; } = "";

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
    /// <summary>
    /// 待确认转交的接手人 id（v1.3）；null = 没有待确认的转交。普通整数列，无外键、无导航。
    /// 与 OrderTransfers 的 Pending 行保持一致（不变量 I1）：只通过 TransferService.cs 里的 StartTransfer / TransferRules.End 修改
    /// </summary>
    public int? PendingTransferToId { get; set; }

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
    /// <summary>v2.0 远程服务：客户怎么确认问题已解决（电话确认 / 微信确认 / 邮件确认 / 其他）</summary>
    public string? CustomerConfirm { get; set; }
    /// <summary>v2.0 返厂维修：寄回前的整机功能测试结果</summary>
    public string? TestResult { get; set; }
    /// <summary>v2.4 安装调试：遗留问题（选填）</summary>
    public string? OpenIssues { get; set; }

    public List<Punch> Punches { get; set; } = new();
    public List<StatusLog> Logs { get; set; } = new();
    public List<OrderAssistant> Assistants { get; set; } = new();
    /// <summary>设备清单（v1.3）：建单时写入，本期没有修改入口</summary>
    public List<OrderDevice> Devices { get; set; } = new();
    /// <summary>退出记录（v1.3）：曾是本单负责人、后来被转交或改派换掉的工程师（只读）</summary>
    public List<OrderExit> Exits { get; set; } = new();
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
    /// <summary>附件类别（v1.3）：只对附件有意义，其余文件恒为 Other。不要配置 HasDefaultValue；列默认值 'Other' 只写在迁移里</summary>
    public AttachmentType AttachmentType { get; set; }
    /// <summary>v2.0 上门完工照片分「维修前 / 维修后」：Before | After；其他文件为 null</summary>
    public string? PhotoStage { get; set; }
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
/// <summary>
/// v2.1 每日工作内容：工程师认领后按天写「今天做了什么」。同一张单、同一个人、同一天只有一条（当天可改，过了当天不能改）。
/// 上门：打离场卡时必须写；远程 / 返厂：提交完工前负责人至少写过一条。
/// </summary>
public class WorkLog
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public WorkOrder? Order { get; set; }
    public int EngineerId { get; set; }
    public Employee? Engineer { get; set; }
    /// <summary>哪一天（只有日期）</summary>
    public DateTime Date { get; set; }
    public string Content { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

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
    /// <summary>v2.0 升级服务方式申请（Type = Escalate）要改成的服务方式；Note 里是「原因；已排查：…」</summary>
    public ServiceType? TargetServiceType { get; set; }
    public int? DeciderId { get; set; }
    public Employee? Decider { get; set; }
    public DateTime? DecidedAt { get; set; }
    public string? Reply { get; set; }
}

/// <summary>
/// v2.2 收费确认：工程师认领后判断要不要收费。要收费 → 销售报价 → 销售总监审核（选客户是否已付款）→ 售后主管确认 → 回到工程师。
/// 每张工单最多一行；每一步的经办人和时间都记在这一行里，同时在状态记录里各记一笔。
/// </summary>
public class OrderCharge
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public WorkOrder? Order { get; set; }
    public ChargeStatus Status { get; set; }
    /// <summary>发起的工程师</summary>
    public int RequesterId { get; set; }
    public Employee? Requester { get; set; }
    public string? Reason { get; set; }
    public DateTime CreatedAt { get; set; }
    /// <summary>负责报价的销售（销售提的单 = 提单的销售；否则是工程师选的）</summary>
    public int? SalesId { get; set; }
    public Employee? Sales { get; set; }
    public decimal? Amount { get; set; }
    public string? QuoteNote { get; set; }
    public DateTime? QuotedAt { get; set; }
    public int? DirectorId { get; set; }
    public Employee? Director { get; set; }
    /// <summary>销售总监审核时选的：客户是否已付款（false = 未付款，先服务）</summary>
    public bool? Paid { get; set; }
    public string? DirectorNote { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public int? SupervisorId { get; set; }
    public Employee? Supervisor { get; set; }
    public string? SupervisorNote { get; set; }
    public DateTime? ConfirmedAt { get; set; }
    /// <summary>定为不收费的人（工程师或销售）</summary>
    public int? FreeById { get; set; }
    public Employee? FreeBy { get; set; }
    public DateTime? FreeAt { get; set; }
    /// <summary>最近一次被退回的说明（「钱主管 退回：金额偏高」）；往下走一步就清空</summary>
    public string? ReturnNote { get; set; }
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

/// <summary>工单的设备（v1.3）：一张工单可以有多台；三项至少一项非空（服务层保证）</summary>
public class OrderDevice
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public WorkOrder? Order { get; set; }
    /// <summary>从 1 开始的序号（= 表单里的顺序）</summary>
    public int Seq { get; set; }
    public string Name { get; set; } = "";
    public string Sn { get; set; } = "";
    public string Model { get; set; } = "";
}

/// <summary>转交记录（v1.3），保留全部历史；每张工单同一时间最多一行 Pending</summary>
public class OrderTransfer
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public WorkOrder? Order { get; set; }
    public int FromEngineerId { get; set; }
    public Employee? From { get; set; }
    public int ToEngineerId { get; set; }
    public Employee? To { get; set; }
    public string Reason { get; set; } = "";
    public TransferStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    /// <summary>结束时间（接手 / 拒绝 / 撤回 / 失效）</summary>
    public DateTime? DecidedAt { get; set; }
    /// <summary>结束它的人：接手、拒绝 = 接手人；撤回 = 发起人或主管；系统失效 = null</summary>
    public int? DecidedById { get; set; }
    public Employee? DecidedBy { get; set; }
    /// <summary>拒绝理由，或系统失效的原因</summary>
    public string? Reply { get; set; }
}

/// <summary>退出记录（v1.3）：每张工单每个「已退出」的人最多一行；一定不是当前负责人 / 协助人（不变量 I2）</summary>
public class OrderExit
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public WorkOrder? Order { get; set; }
    public int EngineerId { get; set; }
    public Employee? Engineer { get; set; }
    public ExitVia Via { get; set; }
    public DateTime ExitedAt { get; set; }
}
