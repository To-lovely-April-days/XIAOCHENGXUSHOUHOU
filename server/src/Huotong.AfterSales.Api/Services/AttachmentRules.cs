using Huotong.AfterSales.Api.Domain;
using Huotong.AfterSales.Api.Infrastructure;

namespace Huotong.AfterSales.Api.Services;

/// <summary>
/// v1.2 附件权限规则的唯一来源（CHANGES-v1.2 §4.1，v1.3 修订见 CHANGES-v1.3 §4.1）：接口校验与 DTO 里的 can* 共用。
/// 「附件」= 挂在工单上、Category 为 OrderPhoto 或 Attachment 且未删除的文件。
/// v1.3：闭环后建单人 / 负责人 / 协助人也能继续添加；「已退出」的人只读；附件分三类（只分类，不做任何强制检查）。
/// 凡用到 WorkOrder 的方法，o 须带 Assistants 与 Exits。
/// </summary>
public static class AttachmentRules
{
    public const int Limit = 50;
    public const string RestrictDenied = "只有销售和主管可以设置「仅销售和主管可见」";

    // ---------- 附件类别（v1.3） ----------

    /// <summary>展示顺序（与枚举声明顺序不同：Other 是 CLR 默认值，排在声明的最前）</summary>
    public static readonly AttachmentType[] TypeOrder = { AttachmentType.Sat, AttachmentType.RepairForm, AttachmentType.Other };

    /// <summary>
    /// 附件类别字符串的唯一解析入口（添加附件、改类别、文件中心筛选共用）。
    /// 空白 / null → true，type = null（含义由调用方决定）；Trim 后大小写不敏感地等于枚举名 → true 与对应值；
    /// 其他任何值（含数字，如 "1"）→ false。
    /// </summary>
    public static bool TryParseType(string? s, out AttachmentType? type)
    {
        type = null;
        if (string.IsNullOrWhiteSpace(s)) return true;
        var text = s.Trim();
        foreach (var t in TypeOrder)
        {
            if (!string.Equals(t.ToString(), text, StringComparison.OrdinalIgnoreCase)) continue;
            type = t;
            return true;
        }
        return false;
    }

    public static bool IsAttachment(FileCategory c) => c is FileCategory.OrderPhoto or FileCategory.Attachment;

    /// <summary>是否为工单的参与方：建单人 / 负责人 / 协助人（o 须带 Assistants）</summary>
    private static bool IsParty(WorkOrder o, CurrentUser user) =>
        o.CreatorId == user.Id || o.AssigneeId == user.Id || o.Assistants.Any(a => a.EngineerId == user.Id);

    /// <summary>
    /// 能看到工单、但不是参与方的工程师（v1.3 审查补充）：实际只有「待确认转交的接手人」会走到这里——
    /// 他曾是本单协助人（或被移除前的参与人）并上传过附件时，「上传人本人」这一条不能成为确认接手前的写权限
    /// （§2.8、§12：接手人确认前只读）。销售 / 主管不受影响。
    /// </summary>
    private static bool ReadOnlyViewer(WorkOrder o, CurrentUser user) => user.Role == Role.Engineer && !IsParty(o, user);

    /// <summary>添加附件被拒的原因（第 4–6 步，按顺序）；可以添加返回 null。v1.3：不再区分是否已闭环</summary>
    public static BizException? AddDenied(WorkOrder o, CurrentUser user)
    {
        if (o.Status == OrderStatus.Voided) return BizException.Conflict("工单已作废，不能添加附件");
        if (user.Role == Role.Supervisor) return null;
        if (OrderViewBuilder.IsFormer(o, user)) return BizException.Forbidden(OrderViewBuilder.FormerDenied);
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
        if (OrderViewBuilder.IsFormer(o, user)) return BizException.Forbidden(OrderViewBuilder.FormerDenied);
        if (f.UploaderId != user.Id) return BizException.Forbidden("只能删除自己上传的附件");
        if (ReadOnlyViewer(o, user)) return BizException.Forbidden("只有建单人、负责工程师、协助人或主管可以删除附件");
        if (o.Status == OrderStatus.Closed) return BizException.Conflict("工单已闭环，只有主管可以删除附件");
        if (o.Status == OrderStatus.Voided) return BizException.Conflict("工单已作废，不能删除附件");
        return null;
    }

    public static bool CanDelete(StoredFile f, WorkOrder o, CurrentUser user) =>
        IsAttachment(f.Category) && Visible(f, user) && DeleteDenied(f, o, user) == null;

    public static bool CanToggleRestricted(StoredFile f, CurrentUser user) =>
        IsAttachment(f.Category) && user.Role == Role.Supervisor;

    /// <summary>
    /// 改附件类别被拒的原因（v1.3 §4.5 第 5 步，按顺序）；可以改返回 null。调用前须已确认附件对当前用户可见。
    /// Supervisor 任何状态都可以；上传人本人在工单未作废、且不是「已退出」时可以（已闭环也可以）。
    /// </summary>
    public static BizException? ChangeTypeDenied(StoredFile f, WorkOrder o, CurrentUser user)
    {
        if (user.Role == Role.Supervisor) return null;
        if (OrderViewBuilder.IsFormer(o, user)) return BizException.Forbidden(OrderViewBuilder.FormerDenied);
        if (f.UploaderId != user.Id) return BizException.Forbidden("只能修改自己上传的附件");
        if (ReadOnlyViewer(o, user)) return BizException.Forbidden("只有建单人、负责工程师、协助人或主管可以修改附件类别");
        if (o.Status == OrderStatus.Voided) return BizException.Conflict("工单已作废，不能修改附件类别");
        return null;
    }

    public static bool CanChangeType(StoredFile f, WorkOrder o, CurrentUser user) =>
        IsAttachment(f.Category) && Visible(f, user) && ChangeTypeDenied(f, o, user) == null;

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
