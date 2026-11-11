using Huotong.AfterSales.Api.Domain;
using Huotong.AfterSales.Api.Dtos;
using Huotong.AfterSales.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Huotong.AfterSales.Api.Services;

/// <summary>
/// v1.1 暂停搁置（直接暂停 / 恢复）与工程师申请（暂停申请、协助人申请）的审批流。
/// 与 OrderService 共用加载、状态记录、自动撤销等内部方法，故以 partial 并入 OrderService（CHANGES-v1.1 §8 允许）。
/// </summary>
public partial class OrderService
{
    // ---------- 暂停搁置 ----------

    /// <summary>解析暂停原因；兼容旧请求体 { reason }（→ Other + note）</summary>
    private static (HoldReasonType Type, string? Note) ParseHold(HoldRequest r)
    {
        HoldReasonType type;
        var note = string.IsNullOrWhiteSpace(r.Note) ? null : r.Note.Trim();
        if (string.IsNullOrWhiteSpace(r.ReasonType))
        {
            if (string.IsNullOrWhiteSpace(r.Reason)) throw BizException.BadRequest("请选择暂停原因");
            type = HoldReasonType.Other;
            note ??= r.Reason.Trim();
        }
        else
        {
            var text = r.ReasonType.Trim();
            if (char.IsDigit(text[0]) || text[0] == '-' || !Enum.TryParse(text, true, out type) || !Enum.IsDefined(type))
                throw BizException.BadRequest("请选择暂停原因");
        }
        if (type == HoldReasonType.Other && note == null) throw BizException.BadRequest("请填写暂停说明");
        if (note != null && note.Length > 200) throw BizException.BadRequest("暂停说明不能超过 200 个字");
        return (type, note);
    }

    /// <summary>暂停前的现场计时检查：负责人 / 协助人今天在本单未离场 → 40900</summary>
    private async Task EnsureNobodyOnSiteForHold(WorkOrder o, DateTime now)
    {
        var running = await RunningPairs(o.Id, now);
        if (running.Count == 0) return;
        // 工程师今天在本单仍在现场计时，搁置后将无法离场打卡
        if (o.AssigneeId != null && running.Any(p => p.Arrive.EngineerId == o.AssigneeId))
            throw BizException.Conflict("工程师正在现场计时，请先离场打卡后再搁置");
        var assistant = o.Assistants.OrderBy(a => a.AddedAt).ThenBy(a => a.Id)
            .FirstOrDefault(a => running.Any(p => p.Arrive.EngineerId == a.EngineerId));
        if (assistant != null)
            throw BizException.Conflict($"协助人{assistant.Engineer?.Name}正在现场计时，请先离场打卡后再搁置");
    }

    private static void ApplyHold(WorkOrder o, Employee by, HoldReasonType type, string? note, DateTime now)
    {
        o.HoldFromStatus = o.Status;
        o.Status = OrderStatus.OnHold;
        o.HoldReasonType = type;
        o.HoldReason = note;
        o.HoldAt = now;
        o.HoldById = by.Id;
    }

    /// <summary>主管直接暂停（待启动 / 处理中）</summary>
    public async Task<OrderDetail> Hold(int id, HoldRequest r, CurrentUser user)
    {
        EnsureRole(user, Role.Supervisor, "只有主管可以暂停工单");
        var (type, note) = ParseHold(r);
        var o = await Load(id);
        var to = OrderStateMachine.Apply(o.Status, OrderAction.Hold);
        var me = await _view.LoadEmployee(user.Id);
        var now = _clock.Now;
        await EnsureNobodyOnSiteForHold(o, now);
        ApplyHold(o, me, type, note, now);
        var text = EnumText.HoldText(type, note);
        AddLog(o, me, $"{me.Name} 标为暂停搁置 → {EnumText.Status(to)}（{text}）", to, now, LogKind.Hold);
        await AutoCancelRequests(o, now, holdReply: "主管已直接暂停");
        await _db.SaveChangesAsync();
        await _notifier.NotifyAsync("工单已暂停搁置", $"**{o.CustomerName}**（{o.OrderNo}）已暂停搁置：{text}", ParticipantPhones(o));
        return await _view.BuildDetail(id, user);
    }

    public async Task<OrderDetail> Resume(int id, CurrentUser user)
    {
        EnsureRole(user, Role.Supervisor, "只有主管可以恢复工单");
        var o = await Load(id);
        var to = OrderStateMachine.Apply(o.Status, OrderAction.Resume, o.HoldFromStatus);
        var me = await _view.LoadEmployee(user.Id);
        var now = _clock.Now;
        o.Status = to;
        o.HoldReason = null;
        o.HoldReasonType = null;
        o.HoldFromStatus = null;
        o.HoldAt = null;
        o.HoldById = null;
        AddLog(o, me, $"{me.Name} 恢复处理 → {EnumText.Status(to)}", to, now, LogKind.Hold);
        await _db.SaveChangesAsync();
        await _notifier.NotifyAsync("工单已恢复处理", $"**{o.CustomerName}**（{o.OrderNo}）已恢复为「{EnumText.Status(to)}」", ParticipantPhones(o));
        return await _view.BuildDetail(id, user);
    }

    /// <summary>负责工程师申请暂停，待主管审批</summary>
    public async Task<OrderDetail> RequestHold(int id, HoldRequest r, CurrentUser user)
    {
        await using var tx = await WriteLock();
        var o = await Load(id);
        if (o.AssigneeId != user.Id) throw BizException.Forbidden("只有负责工程师可以申请暂停");
        var (type, note) = ParseHold(r);
        if (!Active.Contains(o.Status))
            throw BizException.Conflict($"工单当前为「{EnumText.Status(o.Status)}」，不能申请暂停");
        if (await _db.OrderRequests.AnyAsync(x => x.OrderId == id && x.Type == OrderRequestType.Hold && x.Status == RequestStatus.Pending))
            throw BizException.Conflict("已有待审批的暂停申请");
        var me = await _view.LoadEmployee(user.Id);
        var now = _clock.Now;
        _db.OrderRequests.Add(new OrderRequest
        {
            OrderId = o.Id, Type = OrderRequestType.Hold, Status = RequestStatus.Pending, RequesterId = me.Id, CreatedAt = now,
            HoldReasonType = type, Note = note
        });
        var text = EnumText.HoldText(type, note);
        AddLog(o, me, $"{me.Name} 申请暂停搁置（{text}）", o.Status, now, LogKind.Hold);
        await _db.SaveChangesAsync();
        await tx.CommitAsync();
        await _notifier.NotifyAsync("暂停搁置申请",
            $"{me.Name} 申请暂停 **{o.CustomerName}**（{o.OrderNo}）：{text}，请审批。", await SupervisorPhones());
        return await _view.BuildDetail(id, user);
    }

    // ---------- 协助人申请 ----------

    /// <summary>负责工程师申请添加协助人，待主管审批</summary>
    public async Task<OrderDetail> RequestAssistant(int id, AssistantRequestRequest r, CurrentUser user)
    {
        await using var tx = await WriteLock();
        var o = await Load(id);
        if (o.AssigneeId != user.Id) throw BizException.Forbidden("只有负责工程师可以申请协助人");
        if (r.EngineerId == null) throw BizException.BadRequest("请选择协助工程师");
        if (r.EngineerId == user.Id) throw BizException.BadRequest("协助人不能是负责人");
        var target = await _db.Employees.FirstOrDefaultAsync(e => e.Id == r.EngineerId && e.Role == Role.Engineer && e.Enabled)
                     ?? throw BizException.BadRequest("所选协助人不存在或已停用");
        var note = Opt(r.Note, "说明", 200);
        if (!Active.Contains(o.Status))
            throw BizException.Conflict($"工单当前为「{EnumText.Status(o.Status)}」，不能申请协助人");
        if (o.Assistants.Any(a => a.EngineerId == target.Id)) throw BizException.Conflict($"{target.Name}已是本单协助人");
        if (await _db.OrderRequests.AnyAsync(x => x.OrderId == id && x.Type == OrderRequestType.AddAssistant
                && x.Status == RequestStatus.Pending && x.TargetEngineerId == target.Id))
            throw BizException.Conflict($"已有针对{target.Name}的待审批申请");
        if (o.Assistants.Count >= MaxAssistants) throw BizException.Conflict("协助人最多 5 人");
        var me = await _view.LoadEmployee(user.Id);
        var now = _clock.Now;
        _db.OrderRequests.Add(new OrderRequest
        {
            OrderId = o.Id, Type = OrderRequestType.AddAssistant, Status = RequestStatus.Pending, RequesterId = me.Id, CreatedAt = now,
            TargetEngineerId = target.Id, Note = note
        });
        AddLog(o, me, $"{me.Name} 申请添加协助人 {target.Name}" + (note == null ? "" : $"（{note}）"), o.Status, now, LogKind.Assist);
        await _db.SaveChangesAsync();
        await tx.CommitAsync();
        await _notifier.NotifyAsync("协助人申请",
            $"{me.Name} 申请为 **{o.CustomerName}**（{o.OrderNo}）添加协助人 {target.Name}" + (note == null ? "" : $"：{note}") + "，请审批。",
            await SupervisorPhones());
        return await _view.BuildDetail(id, user);
    }

    // ---------- 审批 ----------

    /// <summary>
    /// 「读-校验-写」串行化：SQLite 的事务一开始就取写锁（BEGIN IMMEDIATE），并发的同类请求（如连点两次）排队，
    /// 后到的在前一个提交后才读，于是能看到「已有待审批申请 / 该申请已处理 / 节点已记录」而不是重复写入。
    /// 调用方在 SaveChanges 后提交；抛异常时随 dispose 回滚。通知与返回详情放在提交之后。
    /// </summary>
    private Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction> WriteLock() => _db.Database.BeginTransactionAsync();

    private async Task<OrderRequest> LoadRequest(int orderId, int requestId) =>
        await _db.OrderRequests.Include(x => x.Requester).Include(x => x.TargetEngineer)
            .FirstOrDefaultAsync(x => x.Id == requestId && x.OrderId == orderId) ?? throw BizException.NotFound("申请不存在");

    private static void EnsurePending(OrderRequest req)
    {
        if (req.Status != RequestStatus.Pending) throw BizException.Conflict("该申请已处理");
    }

    /// <summary>主管同意申请（Hold 与 AddAssistant 共用）</summary>
    public async Task<OrderDetail> ApproveRequest(int id, int requestId, CurrentUser user)
    {
        EnsureRole(user, Role.Supervisor, "只有主管可以审批申请");
        await using var tx = await WriteLock();
        var o = await Load(id);
        var req = await LoadRequest(id, requestId);
        EnsurePending(req);
        var me = await _view.LoadEmployee(user.Id);
        var now = _clock.Now;

        if (req.Type == OrderRequestType.Hold)
        {
            // 与直接暂停完全相同的状态与现场计时检查
            var to = OrderStateMachine.Apply(o.Status, OrderAction.Hold);
            await EnsureNobodyOnSiteForHold(o, now);
            var type = req.HoldReasonType ?? HoldReasonType.Other;
            ApplyHold(o, me, type, req.Note, now);
            Decide(req, RequestStatus.Approved, me, now, null);
            var text = EnumText.HoldText(type, req.Note);
            AddLog(o, me, $"{me.Name} 同意暂停申请 → {EnumText.Status(to)}（{text}）", to, now, LogKind.Hold);
            await AutoCancelRequests(o, now);
            await _db.SaveChangesAsync();
            await tx.CommitAsync();
            var phones = o.Assistants.Where(a => a.Engineer != null).Select(a => a.Engineer!.Phone).ToList();
            if (req.Requester != null) phones.Insert(0, req.Requester.Phone);
            await _notifier.NotifyAsync("暂停申请已同意", $"**{o.CustomerName}**（{o.OrderNo}）已暂停搁置：{text}", phones);
            return await _view.BuildDetail(id, user);
        }

        // AddAssistant
        if (!Doing.Contains(o.Status))
            throw BizException.Conflict($"工单当前为「{EnumText.Status(o.Status)}」，不能调整协助人");
        var target = req.TargetEngineer;
        if (target == null || !target.Enabled || target.Role != Role.Engineer)
            throw BizException.BadRequest("所选协助人不存在或已停用");
        var already = o.AssigneeId == target.Id || o.Assistants.Any(a => a.EngineerId == target.Id);
        if (already)
        {
            // 目标已是负责人或协助人：直接置为已同意，不重复添加
            Decide(req, RequestStatus.Approved, me, now, null);
            await _db.SaveChangesAsync();
            await tx.CommitAsync();
            return await _view.BuildDetail(id, user);
        }
        if (o.Assistants.Count >= MaxAssistants) throw BizException.Conflict("协助人最多 5 人");
        o.Assistants.Add(new OrderAssistant { OrderId = o.Id, EngineerId = target.Id, Engineer = target, AddedById = me.Id, AddedAt = now });
        Decide(req, RequestStatus.Approved, me, now, null);
        AddLog(o, me, $"{me.Name} 同意添加协助人 {target.Name}", o.Status, now, LogKind.Assist);
        await AutoCancelRequests(o, now);
        await _db.SaveChangesAsync();
        await tx.CommitAsync();
        var at = new List<string> { target.Phone };
        if (req.Requester != null) at.Insert(0, req.Requester.Phone);
        await _notifier.NotifyAsync("工单协助提醒",
            $"{me.Name} 同意 {target.Name} 协助 **{o.CustomerName}**（{o.OrderNo}），负责人 {o.Assignee?.Name}。", at);
        return await _view.BuildDetail(id, user);
    }

    private static void Decide(OrderRequest req, RequestStatus status, Employee? decider, DateTime now, string? reply)
    {
        req.Status = status;
        req.DeciderId = decider?.Id;
        req.DecidedAt = now;
        req.Reply = reply;
    }

    /// <summary>主管驳回申请，工单状态不变</summary>
    public async Task<OrderDetail> RejectRequest(int id, int requestId, ReplyRequest r, CurrentUser user)
    {
        EnsureRole(user, Role.Supervisor, "只有主管可以审批申请");
        var reply = Opt(r.Reply, "回复", 200);
        var o = await Load(id);
        var req = await LoadRequest(id, requestId);
        EnsurePending(req);
        var me = await _view.LoadEmployee(user.Id);
        var now = _clock.Now;
        Decide(req, RequestStatus.Rejected, me, now, reply);
        var suffix = reply == null ? "" : $"（{reply}）";
        if (req.Type == OrderRequestType.Hold)
            AddLog(o, me, $"{me.Name} 驳回暂停申请{suffix}", o.Status, now, LogKind.Hold);
        else
            AddLog(o, me, $"{me.Name} 驳回添加协助人 {req.TargetEngineer?.Name}{suffix}", o.Status, now, LogKind.Assist);
        await _db.SaveChangesAsync();
        if (req.Requester != null)
            await _notifier.NotifyAsync("申请被驳回",
                $"**{o.CustomerName}**（{o.OrderNo}）的{(req.Type == OrderRequestType.Hold ? "暂停申请" : "协助人申请")}被驳回" + (reply == null ? "" : $"：{reply}"),
                new[] { req.Requester.Phone });
        return await _view.BuildDetail(id, user);
    }

    /// <summary>申请人本人撤回</summary>
    public async Task<OrderDetail> CancelRequest(int id, int requestId, CurrentUser user)
    {
        var o = await Load(id);
        var req = await LoadRequest(id, requestId);
        if (req.RequesterId != user.Id) throw BizException.Forbidden("只能撤回本人的申请");
        EnsurePending(req);
        var me = await _view.LoadEmployee(user.Id);
        var now = _clock.Now;
        Decide(req, RequestStatus.Cancelled, null, now, null);
        if (req.Type == OrderRequestType.Hold)
            AddLog(o, me, $"{me.Name} 撤回暂停申请", o.Status, now, LogKind.Hold);
        else
            AddLog(o, me, $"{me.Name} 撤回协助人申请（{req.TargetEngineer?.Name}）", o.Status, now, LogKind.Assist);
        await _db.SaveChangesAsync();
        return await _view.BuildDetail(id, user);
    }
}
