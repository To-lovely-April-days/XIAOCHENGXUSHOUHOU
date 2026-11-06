using Huotong.AfterSales.Api.Domain;

namespace Huotong.AfterSales.Api.Services;

public record CompletionInput(string? FaultCause, string? Solution, RunStatus? RunStatus,
    int ProcessPhotoCount, int SignatureCount);

/// <summary>完工提交前置校验（DESIGN §5）</summary>
public static class CompletionValidator
{
    /// <summary>不通过时抛 BizException（40900 状态类 / 40000 参数类）</summary>
    public static void Validate(OrderStatus status, ServiceType serviceType, bool onSiteRunning, CompletionInput input)
    {
        if (status != OrderStatus.InProgress)
            throw BizException.Conflict("只有处理中的工单可以提交完工");
        if (serviceType == ServiceType.Onsite && onSiteRunning)
            throw BizException.Conflict("请先离场打卡");
        if (string.IsNullOrWhiteSpace(input.FaultCause)) throw BizException.BadRequest("请填写现场确认的故障原因");
        if (string.IsNullOrWhiteSpace(input.Solution)) throw BizException.BadRequest("请填写解决措施");
        if (input.RunStatus == null) throw BizException.BadRequest("请选择设备运行状况");
        if (input.ProcessPhotoCount < 1) throw BizException.BadRequest("请至少上传 1 张处理照片（需包含维修前原状）");
        if (serviceType == ServiceType.Onsite && input.SignatureCount < 1)
            throw BizException.BadRequest("请上传客户签字件");
    }
}
