using Huotong.AfterSales.Api.Domain;
using Huotong.AfterSales.Api.Infrastructure;

namespace Huotong.AfterSales.Api.Services;

/// <summary>
/// 返厂流程规则（CHANGES-v1.1 §3.4）。FactoryService 的提交校验与详情里的 canRecord / canEdit 共用同一套判断。
/// 各方法返回 null 表示通过，否则返回要抛出的 BizException。
/// </summary>
public static class FactoryRules
{
    /// <summary>可提交的 4 个节点（Repairing 为隐含节点）</summary>
    public static readonly FactoryNode[] Recordable =
        { FactoryNode.CustomerShipped, FactoryNode.FactoryReceived, FactoryNode.ShippedBack, FactoryNode.CustomerSigned };

    public static readonly FactoryNode[] All =
        { FactoryNode.CustomerShipped, FactoryNode.FactoryReceived, FactoryNode.Repairing, FactoryNode.ShippedBack, FactoryNode.CustomerSigned };

    public static FileCategory? PhotoCategory(FactoryNode node) => node switch
    {
        FactoryNode.FactoryReceived => FileCategory.FactoryReceivePhoto,
        FactoryNode.ShippedBack => FileCategory.FactoryShipPhoto,
        FactoryNode.CustomerSigned => FileCategory.FactorySignPhoto,
        _ => null
    };

    public static bool IsFactoryPhoto(FileCategory c) =>
        c is FileCategory.FactoryReceivePhoto or FileCategory.FactoryShipPhoto or FileCategory.FactorySignPhoto;

    public static bool NeedsCourier(FactoryNode node) => node is FactoryNode.CustomerShipped or FactoryNode.ShippedBack;

    /// <summary>
    /// 第 3 步：权限。节点 1、5：建单人 / 负责人 / 主管；节点 2、4：负责人 / 主管。
    /// v1.3：本单「已退出」的工程师一律不可以（即使他是建单人）；o 须带 Exits。
    /// </summary>
    public static BizException? Permission(FactoryNode node, WorkOrder o, CurrentUser user)
    {
        if (OrderViewBuilder.IsFormer(o, user)) return BizException.Forbidden(OrderViewBuilder.FormerDenied);
        var lead = o.AssigneeId == user.Id;
        var supervisor = user.Role == Role.Supervisor;
        if (node is FactoryNode.CustomerShipped or FactoryNode.CustomerSigned)
            return lead || supervisor || o.CreatorId == user.Id ? null : BizException.Forbidden("只有建单人、负责工程师或主管可以记录此节点");
        return lead || supervisor ? null : BizException.Forbidden("只有负责工程师或主管可以记录此节点");
    }

    /// <summary>第 4 步：更正已记录的节点，工单不是已闭环 / 已作废即可</summary>
    public static BizException? EditStatus(OrderStatus status) =>
        status is OrderStatus.Closed or OrderStatus.Voided
            ? BizException.Conflict($"工单当前为「{EnumText.Status(status)}」，不能更正返厂记录")
            : null;

    /// <summary>第 5 步：首次记录时的工单状态</summary>
    public static BizException? RecordStatus(FactoryNode node, OrderStatus status)
    {
        if (status == OrderStatus.OnHold && node is FactoryNode.FactoryReceived or FactoryNode.ShippedBack)
            return BizException.Conflict("工单已暂停搁置，恢复后才能继续返厂流程");
        if (status == OrderStatus.Pending && node == FactoryNode.FactoryReceived)
            return BizException.Conflict("请先认领或指派工程师");
        var allowed = node switch
        {
            FactoryNode.CustomerShipped => status is OrderStatus.Pending or OrderStatus.Accepted or OrderStatus.InProgress or OrderStatus.OnHold,
            FactoryNode.FactoryReceived => status is OrderStatus.Accepted or OrderStatus.InProgress,
            FactoryNode.ShippedBack => status == OrderStatus.InProgress,
            FactoryNode.CustomerSigned => status is OrderStatus.InProgress or OrderStatus.OnHold or OrderStatus.Submitted,
            _ => false
        };
        return allowed ? null : BizException.Conflict($"工单当前为「{EnumText.Status(status)}」，不能记录「{EnumText.Factory(node)}」");
    }

    /// <summary>第 6 步：首次记录时的前置节点</summary>
    public static BizException? Prerequisite(FactoryNode node, IReadOnlySet<FactoryNode> recorded, CurrentUser user) => node switch
    {
        FactoryNode.CustomerShipped when recorded.Contains(FactoryNode.FactoryReceived) =>
            BizException.Conflict("工厂已收货，无需再记录客户寄出"),
        FactoryNode.FactoryReceived when !recorded.Contains(FactoryNode.CustomerShipped) && user.Role != Role.Supervisor =>
            BizException.Conflict("请先记录「客户寄出」"),
        FactoryNode.ShippedBack when !recorded.Contains(FactoryNode.FactoryReceived) =>
            BizException.Conflict("请先记录「工厂收货」"),
        FactoryNode.CustomerSigned when !recorded.Contains(FactoryNode.ShippedBack) =>
            BizException.Conflict("请先记录「寄回客户」"),
        _ => null
    };

    /// <summary>当前用户现在可以首次记录此节点（第 3、5、6 步全部通过）</summary>
    public static bool CanRecord(FactoryNode node, WorkOrder o, IReadOnlySet<FactoryNode> recorded, CurrentUser user) =>
        node != FactoryNode.Repairing && !recorded.Contains(node)
        && Permission(node, o, user) == null && RecordStatus(node, o.Status) == null && Prerequisite(node, recorded, user) == null;

    /// <summary>当前用户现在可以更正此节点（第 3、4 步通过）</summary>
    public static bool CanEdit(FactoryNode node, WorkOrder o, IReadOnlySet<FactoryNode> recorded, CurrentUser user) =>
        node != FactoryNode.Repairing && recorded.Contains(node)
        && Permission(node, o, user) == null && EditStatus(o.Status) == null;

    /// <summary>节点展示状态 done / current / todo / skipped（§2.5）</summary>
    public static Dictionary<FactoryNode, string> States(IReadOnlySet<FactoryNode> recorded, OrderStatus status)
    {
        var result = new Dictionary<FactoryNode, string>();
        var ended = status is OrderStatus.Closed or OrderStatus.Voided;
        var currentTaken = false;
        foreach (var n in All)
        {
            if (n == FactoryNode.Repairing)
            {
                result[n] = recorded.Contains(FactoryNode.ShippedBack) ? "done"
                    : recorded.Contains(FactoryNode.FactoryReceived) ? "current" : "todo";
                continue;
            }
            if (recorded.Contains(n)) result[n] = "done";
            else if (n == FactoryNode.CustomerShipped && recorded.Contains(FactoryNode.FactoryReceived)) result[n] = "skipped";
            else if (!currentTaken && !ended) { result[n] = "current"; currentTaken = true; }
            else result[n] = "todo";
        }
        return result;
    }
}
