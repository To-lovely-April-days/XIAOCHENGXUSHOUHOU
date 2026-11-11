using Huotong.AfterSales.Api.Domain;
using Huotong.AfterSales.Api.Infrastructure;

namespace Huotong.AfterSales.Api.Services;

/// <summary>
/// v1.2 附件权限规则的唯一来源（CHANGES-v1.2 §4.1）：接口校验与 DTO 里的 can* 共用。
/// 「附件」= 挂在工单上、Category 为 OrderPhoto 或 Attachment 且未删除的文件。
/// </summary>
public static class AttachmentRules
{
    public const int Limit = 20;
    public const string RestrictDenied = "只有销售和主管可以设置「仅销售和主管可见」";

    public static bool IsAttachment(FileCategory c) => c is FileCategory.OrderPhoto or FileCategory.Attachment;

    /// <summary>是否为工单的参与方：建单人 / 负责人 / 协助人（o 须带 Assistants）</summary>
    private static bool IsParty(WorkOrder o, CurrentUser user) =>
        o.CreatorId == user.Id || o.AssigneeId == user.Id || o.Assistants.Any(a => a.EngineerId == user.Id);

    /// <summary>添加附件被拒的原因（§4.4 第 4–6 步，按顺序）；可以添加返回 null</summary>
    public static BizException? AddDenied(WorkOrder o, CurrentUser user)
    {
        if (o.Status == OrderStatus.Voided) return BizException.Conflict("工单已作废，不能添加附件");
        if (user.Role == Role.Supervisor) return null;
        if (o.Status == OrderStatus.Closed) return BizException.Conflict("工单已闭环，只有主管可以添加附件");
        if (!IsParty(o, user)) return BizException.Forbidden("只有建单人、负责工程师、协助人或主管可以添加附件");
        return null;
    }

    public static bool CanAdd(WorkOrder o, CurrentUser user) => AddDenied(o, user) == null;

    /// <summary>上传时可以勾选「仅销售和主管可见」</summary>
    public static bool CanRestrict(CurrentUser user) => user.Role is Role.Sales or Role.Supervisor;

    /// <summary>Sales / Supervisor：全部；Engineer：只有未受限的</summary>
    public static bool Visible(StoredFile f, CurrentUser user) => user.Role != Role.Engineer || !f.Restricted;

    /// <summary>删除被拒的原因（§4.5 第 4 步，按顺序）；可以删除返回 null。调用前须已确认附件对当前用户可见</summary>
    public static BizException? DeleteDenied(StoredFile f, WorkOrder o, CurrentUser user)
    {
        if (user.Role == Role.Supervisor) return null;
        if (f.UploaderId != user.Id) return BizException.Forbidden("只能删除自己上传的附件");
        if (o.Status == OrderStatus.Closed) return BizException.Conflict("工单已闭环，只有主管可以删除附件");
        if (o.Status == OrderStatus.Voided) return BizException.Conflict("工单已作废，不能删除附件");
        return null;
    }

    public static bool CanDelete(StoredFile f, WorkOrder o, CurrentUser user) =>
        IsAttachment(f.Category) && Visible(f, user) && DeleteDenied(f, o, user) == null;

    public static bool CanToggleRestricted(StoredFile f, CurrentUser user) =>
        IsAttachment(f.Category) && user.Role == Role.Supervisor;

    // ---------- 文件来源（文件中心的筛选值与行字段共用，不落库） ----------

    public static readonly string[] Sources = { "Attachment", "OrderPhoto", "ProcessPhoto", "Signature", "PunchPhoto", "Factory" };

    public static string? SourceOf(FileCategory c) => c switch
    {
        FileCategory.Attachment => "Attachment",
        FileCategory.OrderPhoto => "OrderPhoto",
        FileCategory.ProcessPhoto => "ProcessPhoto",
        FileCategory.Signature => "Signature",
        FileCategory.PunchPhoto => "PunchPhoto",
        FileCategory.FactoryReceivePhoto or FileCategory.FactoryShipPhoto or FileCategory.FactorySignPhoto => "Factory",
        _ => null
    };

    public static string SourceLabel(FileCategory c) => c switch
    {
        FileCategory.Attachment => "附件",
        FileCategory.OrderPhoto => "建单照片",
        FileCategory.ProcessPhoto => "完工照片",
        FileCategory.Signature => "签字件",
        FileCategory.PunchPhoto => "打卡照片",
        FileCategory.FactoryReceivePhoto => "返厂·收货照片",
        FileCategory.FactoryShipPhoto => "返厂·寄回照片",
        FileCategory.FactorySignPhoto => "返厂·签收凭证",
        _ => ""
    };

    public static FileCategory[] CategoriesOf(string source) => source switch
    {
        "Attachment" => new[] { FileCategory.Attachment },
        "OrderPhoto" => new[] { FileCategory.OrderPhoto },
        "ProcessPhoto" => new[] { FileCategory.ProcessPhoto },
        "Signature" => new[] { FileCategory.Signature },
        "PunchPhoto" => new[] { FileCategory.PunchPhoto },
        "Factory" => new[] { FileCategory.FactoryReceivePhoto, FileCategory.FactoryShipPhoto, FileCategory.FactorySignPhoto },
        _ => Array.Empty<FileCategory>()
    };
}
