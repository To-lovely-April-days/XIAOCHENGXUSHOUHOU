using Huotong.AfterSales.Api.Domain;
using Huotong.AfterSales.Api.Dtos;
using Huotong.AfterSales.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Huotong.AfterSales.Api.Services;

/// <summary>
/// 转交的两小段共用逻辑（OrderService 与 AdminService 共用，不要各写一份）：
/// 「结束一行待确认转交 + 清空工单上的 PendingTransferToId」与「失效记录的文字」。
/// </summary>
public static class TransferRules
{
    /// <summary>
    /// 结束一行待确认的转交，并同时清空工单上的 PendingTransferToId（不变量 I1：两处一起改）。
    /// decidedById：接手、拒绝 = 接手人；撤回 = 发起人或主管；系统失效 = null。
    /// </summary>
    public static void End(WorkOrder order, OrderTransfer transfer, TransferStatus status, int? decidedById, DateTime now, string? reply)
    {
        transfer.Status = status;
        transfer.DecidedAt = now;
        transfer.DecidedById = decidedById;
        transfer.Reply = reply;
        order.PendingTransferToId = null;
    }

    /// <summary>系统失效的状态记录文字：「转交已失效（李工 → 王工）：主管已改派」</summary>
    public static string ExpiredLogText(string? fromName, string? toName, string why) => $"转交已失效（{fromName} → {toName}）：{why}";
}

/// <summary>
/// v1.3 转交（CHANGES-v1.3 §5）：负责工程师把工单转给另一位工程师，对方「确认接手 / 拒绝」，不需要主管批准；
/// 原负责人变成「已退出」（只读，退出记录 OrderExits）。主管不审批，但每一步都通知到，并可撤销待确认的转交。
/// 与 OrderService 共用加载、状态记录、写锁、自动撤销等内部方法，故以 partial 并入 OrderService。
/// 内部方法（StartTransfer / EndPendingTransfer / ExpirePendingTransfer / AddExit / RemoveExit）不自己开事务，由最外层的接口方法开。
/// </summary>
public partial class OrderService
{
    // ---------- 内部方法 ----------

    /// <summary>创建待确认的转交：新增 OrderTransfers 行（Pending）并写工单上的 PendingTransferToId（不变量 I1）</summary>
    private OrderTransfer StartTransfer(WorkOrder o, Employee from, Employee to, string reason, DateTime now)
    {
        var t = new OrderTransfer
        {
            OrderId = o.Id, FromEngineerId = from.Id, From = from, ToEngineerId = to.Id, To = to,
            Reason = reason, Status = TransferStatus.Pending, CreatedAt = now
        };
        _db.OrderTransfers.Add(t);
        o.PendingTransferToId = to.Id;
        return t;
    }

    private IQueryable<OrderTransfer> TransfersWithNames() =>
        _db.OrderTransfers.Include(t => t.From).Include(t => t.To);

    /// <summary>
    /// 结束本单待确认的转交（置为 status，写结束时间 / 结束人 / 回复，并清空工单上的 PendingTransferToId）；
    /// 没有 Pending 行时什么都不做。返回被结束的那一行（带 From / To），或 null。
    /// </summary>
    private async Task<OrderTransfer?> EndPendingTransfer(WorkOrder o, TransferStatus status, Employee? decidedBy, DateTime now, string? reply)
    {
        var pending = await TransfersWithNames().Where(t => t.OrderId == o.Id && t.Status == TransferStatus.Pending)
            .OrderByDescending(t => t.Id).ToListAsync();
        // 不变量 I1 保证最多一行；防御：有多行时全部结束，返回最新的一行
        foreach (var t in pending.Where(t => t.Status == TransferStatus.Pending))
            TransferRules.End(o, t, status, decidedBy?.Id, now, reply);
        return pending.FirstOrDefault();
    }

    /// <summary>待确认的转交因别的动作（改派 / 作废）自动失效，并在确实结束了一行时写状态记录（在触发动作自身的记录之前调用）</summary>
    private async Task ExpirePendingTransfer(WorkOrder o, Employee actor, DateTime now, string why)
    {
        var t = await EndPendingTransfer(o, TransferStatus.Expired, null, now, why);
        if (t != null) AddLog(o, actor, TransferRules.ExpiredLogText(t.From?.Name, t.To?.Name, why), o.Status, now, LogKind.Assist);
    }

    /// <summary>写退出记录：该人在本单已有退出记录 → 更新方式与时间；否则新增（o 须带 Exits）</summary>
    private void AddExit(WorkOrder o, int engineerId, ExitVia via, DateTime now)
    {
        var x = o.Exits.FirstOrDefault(e => e.EngineerId == engineerId);
        if (x == null)
        {
            x = new OrderExit { OrderId = o.Id, EngineerId = engineerId, Via = via, ExitedAt = now };
            o.Exits.Add(x);
            // 显式交给 DbContext 跟踪（Added）：同一次请求里稍后的 RemoveExit 才能干净地取消它
            _db.OrderExits.Add(x);
        }
        else
        {
            x.Via = via;
            x.ExitedAt = now;
        }
    }

    /// <summary>
    /// 删除退出记录（有就删）。不变量 I2：任何让某人成为负责人 / 协助人的写操作都要调用（o 须带 Exits）。
    /// </summary>
    private void RemoveExit(WorkOrder o, int engineerId)
    {
        foreach (var x in o.Exits.Where(e => e.EngineerId == engineerId).ToList())
        {
            var entry = _db.Entry(x);
            o.Exits.Remove(x);
            // 本次请求里刚加、尚未保存的记录（如改派时原负责人同时被加为协助人）：取消新增即可；已落库的才删除
            if (entry.State == EntityState.Added) entry.State = EntityState.Detached;
            else if (entry.State != EntityState.Detached) _db.OrderExits.Remove(x);
        }
    }

    private async Task<OrderTransfer> LoadTransfer(int orderId, int transferId) =>
        await TransfersWithNames().FirstOrDefaultAsync(t => t.Id == transferId && t.OrderId == orderId)
        ?? throw BizException.NotFound("转交不存在");

    private static void EnsureTransferPending(OrderTransfer t)
    {
        if (t.Status != TransferStatus.Pending) throw BizException.Conflict("该转交已处理或已失效");
    }

    /// <summary>通知 @：先列指定的人，再列全部启用的主管</summary>
    private async Task<List<string>> WithSupervisors(params string?[] first)
    {
        var phones = first.Where(p => !string.IsNullOrEmpty(p)).Select(p => p!).ToList();
        phones.AddRange(await SupervisorPhones());
        return phones;
    }

    // ---------- 接口 ----------

    /// <summary>负责工程师发起转交（待启动 / 处理中 / 暂停搁置；原因必填；不需要主管批准）</summary>
    public async Task<OrderDetail> Transfer(int id, TransferRequest r, CurrentUser user)
    {
        if (r.EngineerId == null) throw BizException.BadRequest("请选择接手工程师");
        if (string.IsNullOrWhiteSpace(r.Reason)) throw BizException.BadRequest("请填写转交原因");
        var reason = r.Reason.Trim();
        if (reason.Length > 200) throw BizException.BadRequest("转交原因不能超过 200 个字");

        await using var tx = await WriteLock();
        var o = await Load(id);
        if (o.AssigneeId != user.Id) throw BizException.Forbidden("只有负责工程师可以转交工单");
        if (!OrderStateMachine.CanTransfer(o.Status))
            throw BizException.Conflict($"工单当前为「{EnumText.Status(o.Status)}」，不能转交");
        if (r.EngineerId == user.Id) throw BizException.BadRequest("不能转交给自己");
        // 目标只能是启用中的工程师（销售、主管不能成为接手人）；可以是本单协助人，也可以是本单已退出的人
        var to = await _db.Employees.FirstOrDefaultAsync(e => e.Id == r.EngineerId && e.Role == Role.Engineer && e.Enabled)
                 ?? throw BizException.BadRequest("所选工程师不存在或已停用");
        if (o.PendingTransferToId != null
            || await _db.OrderTransfers.AnyAsync(t => t.OrderId == id && t.Status == TransferStatus.Pending))
            throw BizException.Conflict("已有待确认的转交，请先撤回");
        var now = _clock.Now;
        // 本人今天在本单仍在现场计时：先打离场卡（保证「已退出」的人在退出那一刻没有正在计时的到场卡）
        if ((await RunningPairs(id, now)).Any(p => p.Arrive.EngineerId == user.Id))
            throw BizException.Conflict("你正在现场计时，请先离场打卡后再转交");

        var me = await _view.LoadEmployee(user.Id);
        StartTransfer(o, me, to, reason, now);
        AddLog(o, me, $"{me.Name} 发起转交给 {to.Name}：{reason}", o.Status, now, LogKind.Assist);
        await _db.SaveChangesAsync();
        await tx.CommitAsync();

        // 正文只有姓名与用户填写的原因：不出现金额、不出现附件名
        await _notifier.NotifyAsync("工单转交",
            $"{me.Name} 想把 **{o.CustomerName}**（{o.OrderNo}）转交给 {to.Name}，原因：{reason}。\n\n请 {to.Name} 在小程序里确认接手或拒绝；确认前工单仍由 {me.Name} 负责。",
            await WithSupervisors(to.Phone));
        return await _view.BuildDetail(id, user);
    }

    /// <summary>接手人确认接手：成为负责人，原负责人写入退出记录（只读）</summary>
    public async Task<OrderDetail> AcceptTransfer(int id, int transferId, CurrentUser user)
    {
        await using var tx = await WriteLock();
        var o = await Load(id);
        var t = await LoadTransfer(id, transferId);
        if (t.ToEngineerId != user.Id) throw BizException.Forbidden("只有接手人可以确认接手");
        EnsureTransferPending(t);
        // 防御：正常情况下（不变量 I1）不会出现
        if (!OrderStateMachine.CanTransfer(o.Status) || o.AssigneeId != t.FromEngineerId)
            throw BizException.Conflict("该转交已处理或已失效");
        var now = _clock.Now;
        // 原负责人发起之后又到场、今天仍在现场计时：请他先离场（往日缺卡不拦，仍由主管补录）
        if ((await RunningPairs(id, now)).Any(p => p.Arrive.EngineerId == t.FromEngineerId))
            throw BizException.Conflict($"{t.From?.Name}正在现场计时，请其先离场打卡后再确认接手");

        var me = await _view.LoadEmployee(user.Id);
        var from = t.From;
        var fromId = t.FromEngineerId;
        // 与改派一致：换负责人、更新认领时间；状态、预约时间、暂停信息不变
        o.AssigneeId = me.Id;
        o.Assignee = me;
        o.ClaimedAt = now;
        // 接手人原本是协助人：从协助人中移除（不算移除，不写记录）
        var promoted = o.Assistants.FirstOrDefault(a => a.EngineerId == me.Id);
        if (promoted != null)
        {
            o.Assistants.Remove(promoted);
            _db.OrderAssistants.Remove(promoted);
        }
        AddExit(o, fromId, ExitVia.Transfer, now);
        RemoveExit(o, me.Id);
        await EndPendingTransfer(o, TransferStatus.Accepted, me, now, null);
        // 原负责人发起的待审批申请自动撤销（回复「负责人已变更」）；目标已是参与人的协助人申请自动撤销
        await AutoCancelRequests(o, now, oldLeadId: fromId);
        AddLog(o, me, $"{me.Name} 确认接手（原负责人 {from?.Name}）", o.Status, now, LogKind.Assist);
        await _db.SaveChangesAsync();
        await tx.CommitAsync();

        await _notifier.NotifyAsync("工单已转交",
            $"{me.Name} 已接手 **{o.CustomerName}**（{o.OrderNo}），原负责人 {from?.Name} 退出本单。\n\n{o.DeviceText} · {o.Symptom}",
            await WithSupervisors(from?.Phone));
        return await _view.BuildDetail(id, user);
    }

    /// <summary>
    /// 接手人拒绝（理由选填）。拒绝后当前用户若仍能查看这张工单（协助人 / 建单人 / 已退出的人）→ 返回最新详情；
    /// 否则返回 null（全系统唯一可能返回 null 的工单动作）。
    /// </summary>
    public async Task<OrderDetail?> RejectTransfer(int id, int transferId, ReplyRequest r, CurrentUser user)
    {
        var reply = Opt(r.Reply, "拒绝理由", 200);
        await using var tx = await WriteLock();
        var o = await Load(id);
        var t = await LoadTransfer(id, transferId);
        if (t.ToEngineerId != user.Id) throw BizException.Forbidden("只有接手人可以拒绝");
        EnsureTransferPending(t);

        var me = await _view.LoadEmployee(user.Id);
        var now = _clock.Now;
        var from = t.From;
        await EndPendingTransfer(o, TransferStatus.Rejected, me, now, reply);
        AddLog(o, me, $"{me.Name} 拒绝接手" + (reply == null ? "" : $"：{reply}"), o.Status, now, LogKind.Assist);
        await _db.SaveChangesAsync();
        await tx.CommitAsync();

        await _notifier.NotifyAsync("转交被拒绝",
            $"{me.Name} 拒绝接手 **{o.CustomerName}**（{o.OrderNo}）" + (reply == null ? "" : $"：{reply}") + $"。工单仍由 {from?.Name} 负责。",
            await WithSupervisors(from?.Phone));
        // 不用「BuildDetail 再捕获 40300」：直接按查看权限决定返回详情还是 null
        return OrderViewBuilder.CanView(o, user) ? await _view.BuildDetail(id, user) : null;
    }

    /// <summary>发起人撤回，或主管撤销（状态都记为 Cancelled，文字上区分）</summary>
    public async Task<OrderDetail> CancelTransfer(int id, int transferId, CurrentUser user)
    {
        await using var tx = await WriteLock();
        var o = await Load(id);
        var t = await LoadTransfer(id, transferId);
        var byRequester = t.FromEngineerId == user.Id;
        if (!byRequester && user.Role != Role.Supervisor) throw BizException.Forbidden("只有发起人或主管可以撤回转交");
        EnsureTransferPending(t);

        var me = await _view.LoadEmployee(user.Id);
        var now = _clock.Now;
        var from = t.From;
        var to = t.To;
        await EndPendingTransfer(o, TransferStatus.Cancelled, me, now, null);
        AddLog(o, me, byRequester
            ? $"{me.Name} 撤回转交（{to?.Name}）"
            : $"{me.Name} 撤销转交（{from?.Name} → {to?.Name}）", o.Status, now, LogKind.Assist);
        await _db.SaveChangesAsync();
        await tx.CommitAsync();

        if (byRequester)
            await _notifier.NotifyAsync("转交已撤回",
                $"{me.Name} 撤回了 **{o.CustomerName}**（{o.OrderNo}）转交给 {to?.Name} 的请求。", await WithSupervisors(to?.Phone));
        else
            await _notifier.NotifyAsync("转交已撤销",
                $"{me.Name} 撤销了 **{o.CustomerName}**（{o.OrderNo}）由 {from?.Name} 转交给 {to?.Name} 的请求，工单仍由 {from?.Name} 负责。",
                new[] { from?.Phone, to?.Phone }.Where(p => !string.IsNullOrEmpty(p)).Select(p => p!).ToList());
        return await _view.BuildDetail(id, user);
    }
}
