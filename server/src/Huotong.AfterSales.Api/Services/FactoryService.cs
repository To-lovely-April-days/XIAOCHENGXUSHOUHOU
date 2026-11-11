using Huotong.AfterSales.Api.Domain;
using Huotong.AfterSales.Api.Dtos;
using Huotong.AfterSales.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Huotong.AfterSales.Api.Services;

/// <summary>
/// v1.1 返厂流程：5 节点物流时间线的记录 / 更正（CHANGES-v1.1 §3.4）。规则判断在 FactoryRules（详情的 canRecord / canEdit 共用）。
/// 与 OrderService 共用加载、状态记录等内部方法，故以 partial 并入 OrderService。
/// </summary>
public partial class OrderService
{
    private static string PhotoLabel(FactoryNode node) => node switch
    {
        FactoryNode.FactoryReceived => "收货照片",
        FactoryNode.ShippedBack => "寄回照片",
        _ => "签收凭证"
    };

    public async Task<OrderDetail> RecordFactoryNode(int id, FactoryNodeRequest r, CurrentUser user)
    {
        // 1. 节点
        var nodeText = (r.Node ?? "").Trim();
        if (nodeText.Length == 0 || char.IsDigit(nodeText[0]) || nodeText[0] == '-'
            || !Enum.TryParse<FactoryNode>(nodeText, true, out var node) || !Enum.IsDefined(node))
            throw BizException.BadRequest("请选择返厂节点");
        if (node == FactoryNode.Repairing) throw BizException.BadRequest("检测/维修无需记录");

        // 2. 返厂单（先取写锁：并发的重复提交排队，后到的按「更正」处理而不是撞唯一索引）
        await using var tx = await WriteLock();
        var o = await Load(id);
        if (o.ServiceType != ServiceType.Factory) throw BizException.Conflict("只有返厂工单需要记录返厂流程");

        // 3. 权限
        var denied = FactoryRules.Permission(node, o, user);
        if (denied != null) throw denied;

        var records = await _db.FactoryRecords.Where(x => x.OrderId == id).ToListAsync();
        var recorded = records.Select(x => x.Node).ToHashSet();
        var existing = records.FirstOrDefault(x => x.Node == node);
        // 4. 更正 / 5. 状态 / 6. 前置
        var blocked = existing != null
            ? FactoryRules.EditStatus(o.Status)
            : FactoryRules.RecordStatus(node, o.Status) ?? FactoryRules.Prerequisite(node, recorded, user);
        if (blocked != null) throw blocked;

        // 7. 字段
        string? courier = null, trackingNo = null;
        if (FactoryRules.NeedsCourier(node))
        {
            courier = (r.Courier ?? "").Trim();
            if (courier.Length == 0) throw BizException.BadRequest("请填写快递公司");
            if (courier.Length > 50) throw BizException.BadRequest("快递公司不能超过 50 个字");
            trackingNo = new string((r.TrackingNo ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray());
            if (trackingNo.Length == 0) throw BizException.BadRequest("请填写快递单号");
            if (trackingNo.Length > 50) throw BizException.BadRequest("快递单号不能超过 50 个字");
        }
        var category = FactoryRules.PhotoCategory(node);
        var photoIds = category == null ? new List<int>() : (r.PhotoIds ?? new()).Distinct().ToList();
        if (node == FactoryNode.FactoryReceived && photoIds.Count < 1) throw BizException.BadRequest("请至少上传 1 张收货照片（外观/配件）");
        if (node == FactoryNode.ShippedBack && photoIds.Count < 1) throw BizException.BadRequest("请至少上传 1 张寄回照片");
        if (photoIds.Count > 9) throw BizException.BadRequest("照片最多 9 张");
        var note = string.IsNullOrWhiteSpace(r.Note) ? null : r.Note.Trim();
        if (note != null && note.Length > 500) throw BizException.BadRequest("备注不能超过 500 个字");

        // 8. 照片：photoIds 为该节点照片的最终全集
        if (category != null)
        {
            // 已挂在本单其他资料（建单照片 / 完工照片 / 其他返厂节点）下的文件不能挪用，否则会把那一处的必填照片抽走
            if (await _db.Files.AnyAsync(f => photoIds.Contains(f.Id) && f.OrderId == id && f.Category != category))
                throw BizException.BadRequest($"{PhotoLabel(node)}已用于本单其他资料，请重新上传");
            var stale = await _db.Files.Where(f => f.OrderId == id && f.Category == category).ToListAsync();
            foreach (var f in stale.Where(f => !photoIds.Contains(f.Id))) f.OrderId = null;
            await _files.Attach(photoIds, id, category.Value, user, PhotoLabel(node));
        }

        var me = await _view.LoadEmployee(user.Id);
        var now = _clock.Now;
        var first = existing == null;
        if (existing == null)
        {
            existing = new FactoryRecord { OrderId = o.Id, Node = node, OperatorId = me.Id, Time = now };
            _db.FactoryRecords.Add(existing);
        }
        else
        {
            existing.UpdatedById = me.Id;
            existing.UpdatedAt = now;
        }
        existing.Courier = courier;
        existing.TrackingNo = trackingNo;
        existing.Note = note;

        var express = $"（{courier} {trackingNo}）";
        string text;
        switch (node)
        {
            case FactoryNode.CustomerShipped:
                text = first ? $"{me.Name} 记录客户寄出{express}" : $"{me.Name} 更正客户寄出{express}";
                break;
            case FactoryNode.FactoryReceived:
                if (!first) text = $"{me.Name} 更正工厂收货";
                else if (o.Status == OrderStatus.Accepted)
                {
                    // 工厂收货取代返厂单的「开始处理」
                    var to = OrderStateMachine.Apply(o.Status, OrderAction.Start);
                    o.Status = to;
                    o.StartedAt ??= now;
                    text = $"{me.Name} 工厂收货 → {EnumText.Status(to)}";
                }
                else text = $"{me.Name} 工厂收货";
                break;
            case FactoryNode.ShippedBack:
                text = first ? $"{me.Name} 寄回客户{express}" : $"{me.Name} 更正寄回客户{express}";
                break;
            default:
                text = first ? $"{me.Name} 记录客户签收" : $"{me.Name} 更正客户签收";
                break;
        }
        AddLog(o, me, text, o.Status, now, LogKind.Factory);
        await _db.SaveChangesAsync();
        await tx.CommitAsync();

        // 通知（仅首次记录）
        if (first)
        {
            var head = $"**{o.CustomerName}**（{o.OrderNo}）";
            if (node == FactoryNode.CustomerShipped && o.Assignee != null)
                await _notifier.NotifyAsync("返厂设备已寄出", $"{head}客户已寄出：{courier} {trackingNo}", new[] { o.Assignee.Phone });
            else if (node == FactoryNode.FactoryReceived && o.Creator != null)
                await _notifier.NotifyAsync("工厂已收货", $"{head}返厂设备已到厂，开始检测/维修", new[] { o.Creator.Phone });
            else if (node == FactoryNode.ShippedBack && o.Creator != null)
                await _notifier.NotifyAsync("返厂设备已寄回", $"{head}已寄回客户：{courier} {trackingNo}", new[] { o.Creator.Phone });
        }
        return await _view.BuildDetail(id, user);
    }
}
