using Huotong.AfterSales.Api.Domain;

namespace Huotong.AfterSales.Api.Services;

/// <summary>
/// 完工提交的输入。v2.0 新增的几项只在 FormVersion ≥ 2 时参与校验：
/// BeforeCount / AfterCount = 维修前 / 后照片数；RepairFormCount = 本单已有 + 本次提交的对应维修登记表数。
/// </summary>
public record CompletionInput(string? FaultCause, string? Solution, RunStatus? RunStatus,
    int ProcessPhotoCount, int SignatureCount,
    int FormVersion = 1, int BeforeCount = 0, int AfterCount = 0, int RepairFormCount = 0,
    string? CustomerConfirm = null, string? TestResult = null);

/// <summary>完工提交前置校验（DESIGN §5；v2.0 按服务方式区分资料）</summary>
public static class CompletionValidator
{
    /// <summary>远程服务可选的客户确认方式</summary>
    public static readonly string[] ConfirmMethods = { "电话确认", "微信确认", "邮件确认", "其他" };

    /// <summary>不通过时抛 BizException（40900 状态类 / 40000 参数类）</summary>
    public static void Validate(OrderStatus status, ServiceType serviceType, bool onSiteRunning, CompletionInput input)
    {
        if (status != OrderStatus.InProgress)
            throw BizException.Conflict("只有处理中的工单可以提交完工");
        if (serviceType == ServiceType.Onsite && onSiteRunning)
            throw BizException.Conflict("请先离场打卡");
        if (string.IsNullOrWhiteSpace(input.FaultCause))
            throw BizException.BadRequest(serviceType == ServiceType.Onsite || input.FormVersion < 2 ? "请填写现场确认的故障原因" : "请填写故障原因");
        if (string.IsNullOrWhiteSpace(input.Solution)) throw BizException.BadRequest("请填写解决措施");
        if (input.RunStatus == null) throw BizException.BadRequest("请选择设备运行状况");

        if (input.FormVersion < 2)
        {
            // 旧版客户端：沿用旧规则，升级前照常能提交
            if (input.ProcessPhotoCount < 1) throw BizException.BadRequest("请至少上传 1 张处理照片（需包含维修前原状）");
            if (serviceType == ServiceType.Onsite && input.SignatureCount < 1)
                throw BizException.BadRequest("请上传客户签字件");
            return;
        }

        switch (serviceType)
        {
            case ServiceType.Onsite:
                if (input.BeforeCount < 1) throw BizException.BadRequest("请至少上传 1 张维修前照片（设备原始状态）");
                if (input.AfterCount < 1) throw BizException.BadRequest("请至少上传 1 张维修后照片");
                if (input.RepairFormCount < 1) throw BizException.BadRequest("请上传现场维修登记表（客户签字版）");
                break;
            case ServiceType.Remote:
                // 远程拍不到设备：截图 / 录屏选填，但要说明客户是怎么确认问题已解决的
                if (string.IsNullOrWhiteSpace(input.CustomerConfirm) || !ConfirmMethods.Contains(input.CustomerConfirm.Trim()))
                    throw BizException.BadRequest("请选择客户确认方式");
                break;
            case ServiceType.Factory:
                if (input.ProcessPhotoCount < 1) throw BizException.BadRequest("请至少上传 1 张维修照片");
                if (string.IsNullOrWhiteSpace(input.TestResult)) throw BizException.BadRequest("请填写寄回前的测试结果");
                if (input.RepairFormCount < 1) throw BizException.BadRequest("请上传返厂维修登记表");
                break;
        }
    }
}

/// <summary>v2.0 升级服务方式：只能往更重的方向升（远程 → 上门 / 返厂；上门 → 返厂）</summary>
public static class EscalateRules
{
    public static bool Allowed(ServiceType from, ServiceType to) =>
        (from == ServiceType.Remote && to is ServiceType.Onsite or ServiceType.Factory) ||
        (from == ServiceType.Onsite && to == ServiceType.Factory);

    public static string TargetName(ServiceType? t) => t switch
    {
        ServiceType.Onsite => "上门服务",
        ServiceType.Factory => "返厂维修",
        ServiceType.Remote => "远程服务",
        _ => "其他服务方式"
    };
}
