using Huotong.AfterSales.Api.Domain;
using Huotong.AfterSales.Api.Dtos;
using Huotong.AfterSales.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Huotong.AfterSales.Api.Services;

/// <summary>v2.2 收费确认的固定文字与判断</summary>
public static class ChargeRules
{
    /// <summary>进行中：工程师暂时不能开始处理 / 到场打卡 / 提交完工</summary>
    public static bool Pending(ChargeStatus s) =>
        s is ChargeStatus.Quoting or ChargeStatus.DirectorReview or ChargeStatus.SupervisorConfirm;

    public static readonly ChargeStatus[] PendingStatuses =
        { ChargeStatus.Quoting, ChargeStatus.DirectorReview, ChargeStatus.SupervisorConfirm };

    public const string Blocked = "收费确认还没走完，确认后才能继续处理";
    public const string DecideFirst = "请先判断这张单要不要收费";

    public static string StatusText(ChargeStatus? s) => s switch
    {
        null => "还没判断",
        ChargeStatus.Quoting => "等销售报价",
        ChargeStatus.DirectorReview => "等销售总监审核",
        ChargeStatus.SupervisorConfirm => "等售后主管确认",
        ChargeStatus.Confirmed => "已确认收费",
        _ => "不收费"
    };

    public static string PaidText(bool? paid) => paid == null ? "" : paid.Value ? "已付款" : "未付款，先服务";
}

/// <summary>
/// v2.2 收费确认流程：工程师判断 → 销售报价 → 销售总监审核（选客户是否已付款）→ 售后主管确认 → 回到工程师。
/// 每一步都在状态记录里记一笔，并给下一位处理人发微信提醒。
/// 硬规则：提醒和工程师能看到的记录里都不出现金额；带金额的记录用 LogKind.Price（工程师看不到）。
/// </summary>
public partial class OrderService
{
    private async Task<OrderCharge?> LoadCharge(int orderId) =>
        await _db.OrderCharges.Include(c => c.Requester).Include(c => c.Sales).Include(c => c.Director).Include(c => c.Supervisor)
            .FirstOrDefaultAsync(c => c.OrderId == orderId);

    /// <summary>销售总监 = 岗位是「销售主管」的在职销售</summary>
    private async Task<List<Employee>> SalesDirectors() =>
        (await _db.Employees.Where(e => e.Role == Role.Sales && e.Enabled).OrderBy(e => e.Id).ToListAsync())
        .Where(e => e.IsSalesManager).ToList();

    /// <summary>收费确认进行中时拦住「开始处理 / 提交完工」</summary>
    private async Task EnsureChargeNotPending(int orderId)
    {
        if (await _db.OrderCharges.AnyAsync(c => c.OrderId == orderId && ChargeRules.PendingStatuses.Contains(c.Status)))
            throw BizException.Conflict(ChargeRules.Blocked);
    }

    private static void EnsureChargeOrderOpen(WorkOrder o)
    {
        if (!Doing.Contains(o.Status))
            throw BizException.Conflict($"工单当前为「{EnumText.Status(o.Status)}」，不能处理收费确认");
    }

    private static void EnsureChargeAt(OrderCharge? c, ChargeStatus expected)
    {
        if (c == null) throw BizException.Conflict("这张单还没有发起收费确认");
        if (c.Status != expected) throw BizException.Conflict("收费确认已经到了别的步骤，请刷新");
    }

    /// <summary>不收费：还没定价的记为保内免费（0 元）</summary>
    private void MarkFree(WorkOrder o, OrderCharge c, Employee by, DateTime now)
    {
        c.Status = ChargeStatus.Free;
        c.FreeById = by.Id;
        c.FreeAt = now;
        c.ReturnNote = null;
        if (o.PriceSet) return;
        o.Price = 0;
        o.PriceSet = true;
        o.PriceSetAt = now;
        AddLog(o, by, $"{by.Name} 定为不收费，服务价格记为 {EnumText.Money(0)}", o.Status, now, LogKind.Price);
    }

    /// <summary>负责工程师判断要不要收费（认领之后，待启动 / 处理中都可以）</summary>
    public async Task<OrderDetail> DecideCharge(int id, ChargeDecideRequest r, CurrentUser user)
    {
        await using var tx = await WriteLock();
        var o = await Load(id);
        EnsureAssignee(o, user);
        if (r.Charge == null) throw BizException.BadRequest("请选择要不要收费");
        if (!Active.Contains(o.Status))
            throw BizException.Conflict($"工单当前为「{EnumText.Status(o.Status)}」，不能判断收费");
        var c = await LoadCharge(id);
        if (c != null && ChargeRules.Pending(c.Status)) throw BizException.Conflict("收费确认正在进行中");
        if (c != null && c.Status == ChargeStatus.Confirmed) throw BizException.Conflict("收费已经确认，不能再改");
        var me = await _view.LoadEmployee(user.Id);
        var now = _clock.Now;
        // 先把要填的都校验完再动数据
        var reason = r.Charge == true ? Req(r.Reason, "收费原因", 200) : Opt(r.Reason, "说明", 200);
        Employee? sales = null;
        if (r.Charge == true)
        {
            // 销售提的单固定给提单的销售；其他情况由工程师选一位销售
            if (o.Creator is { Role: Role.Sales, Enabled: true }) sales = o.Creator;
            else
            {
                if (r.SalesId == null) throw BizException.BadRequest("请选择交给哪位销售报价");
                sales = await _db.Employees.FirstOrDefaultAsync(e => e.Id == r.SalesId && e.Role == Role.Sales && e.Enabled)
                        ?? throw BizException.BadRequest("所选销售不存在或已停用");
            }
        }
        if (c == null)
        {
            c = new OrderCharge { OrderId = o.Id };
            _db.OrderCharges.Add(c);
        }
        c.RequesterId = me.Id;
        c.Requester = me;
        c.CreatedAt = now;
        c.Amount = null;
        c.QuoteNote = null;
        c.QuotedAt = null;
        c.DirectorId = null;
        c.Director = null;
        c.Paid = null;
        c.DirectorNote = null;
        c.ReviewedAt = null;
        c.SupervisorId = null;
        c.Supervisor = null;
        c.SupervisorNote = null;
        c.ConfirmedAt = null;
        c.FreeById = null;
        c.FreeAt = null;
        c.ReturnNote = null;

        c.Reason = reason;
        if (sales == null)
        {
            c.SalesId = null;
            c.Sales = null;
            AddLog(o, me, $"{me.Name} 判断本单不收费" + (reason == null ? "" : $"（{reason}）"), o.Status, now, LogKind.Info);
            MarkFree(o, c, me, now);
            await _db.SaveChangesAsync();
            await tx.CommitAsync();
            return await _view.BuildDetail(id, user);
        }

        c.Status = ChargeStatus.Quoting;
        c.SalesId = sales.Id;
        c.Sales = sales;
        AddLog(o, me, $"{me.Name} 判断需要收费，交给 {sales.Name} 报价（{reason}）", o.Status, now, LogKind.Info);
        await _db.SaveChangesAsync();
        await tx.CommitAsync();
        await _notifier.NotifyAsync("收费待报价",
            $"{me.Name} 判断 **{o.CustomerName}**（{o.OrderNo}）需要收费：{reason}，请 {sales.Name} 报价。", new[] { sales.Phone });
        await Push(o, me, now, "有收费待你报价", $"{me.Name}判断需要收费：{reason}", sales);
        return await _view.BuildDetail(id, user);
    }

    /// <summary>销售报价（指定的销售本人；销售总监可以代报），或改成不收费</summary>
    public async Task<OrderDetail> QuoteCharge(int id, ChargeQuoteRequest r, CurrentUser user)
    {
        EnsureRole(user, Role.Sales, "只有销售可以报价");
        await using var tx = await WriteLock();
        var o = await Load(id);
        var c = await LoadCharge(id);
        EnsureChargeAt(c, ChargeStatus.Quoting);
        EnsureChargeOrderOpen(o);
        if (c!.SalesId != user.Id && !user.SalesManager)
            throw BizException.Forbidden($"这张单由 {c.Sales?.Name} 报价");
        var me = await _view.LoadEmployee(user.Id);
        var now = _clock.Now;
        var note = Opt(r.Note, "报价说明", 200);

        if (r.Free == true)
        {
            c.QuoteNote = note;
            AddLog(o, me, $"{me.Name} 确认本单不收费" + (note == null ? "" : $"（{note}）"), o.Status, now, LogKind.Info);
            MarkFree(o, c, me, now);
            await _db.SaveChangesAsync();
            await tx.CommitAsync();
            await Push(o, me, now, "本单不收费，可以继续处理", note == null ? $"{me.Name}确认不收费" : $"{me.Name}确认不收费：{note}", o.Assignee);
            return await _view.BuildDetail(id, user);
        }

        if (r.Amount == null) throw BizException.BadRequest("请填写收费金额");
        if (r.Amount <= 0) throw BizException.BadRequest("收费金额要大于 0；不收费请点「不收费」");
        if (r.Amount > MaxPrice) throw BizException.BadRequest("收费金额过大");
        c.Amount = Math.Round(r.Amount.Value, 2, MidpointRounding.AwayFromZero);
        c.QuoteNote = note;
        c.QuotedAt = now;
        c.SalesId = me.Id;
        c.Sales = me;
        c.ReturnNote = null;
        var directors = await SalesDirectors();
        // 公司没有在职的销售总监时不能卡死：直接交给售后主管
        c.Status = directors.Count > 0 ? ChargeStatus.DirectorReview : ChargeStatus.SupervisorConfirm;
        AddLog(o, me, $"{me.Name} 已报价，" + (directors.Count > 0 ? "提交销售总监审核" : "提交售后主管确认"), o.Status, now, LogKind.Info);
        AddLog(o, me, $"{me.Name} 报价 {EnumText.Money(c.Amount.Value)}" + (note == null ? "" : $"（{note}）"), o.Status, now, LogKind.Price);
        await _db.SaveChangesAsync();
        await tx.CommitAsync();
        if (directors.Count > 0)
        {
            await _notifier.NotifyAsync("收费待审核", $"{me.Name} 已对 **{o.CustomerName}**（{o.OrderNo}）报价，请销售总监审核。",
                directors.Select(e => e.Phone).ToList());
            await Push(o, me, now, b => b.Add("有收费待你审核", $"{me.Name}已报价，请审核", directors));
        }
        else
        {
            await _notifier.NotifyAsync("收费待确认", $"{me.Name} 已对 **{o.CustomerName}**（{o.OrderNo}）报价，请售后主管确认。", await SupervisorPhones());
            await PushSupervisors(o, me, now, "有收费待你确认", $"{me.Name}已报价，请确认");
        }
        return await _view.BuildDetail(id, user);
    }

    /// <summary>销售总监审核：同意时选客户是否已付款，转售后主管；不同意退回销售</summary>
    public async Task<OrderDetail> ReviewCharge(int id, ChargeReviewRequest r, CurrentUser user)
    {
        if (!user.SalesManager) throw BizException.Forbidden("只有销售总监可以审核收费");
        if (r.Approve == null) throw BizException.BadRequest("请选择同意还是退回");
        await using var tx = await WriteLock();
        var o = await Load(id);
        var c = await LoadCharge(id);
        EnsureChargeAt(c, ChargeStatus.DirectorReview);
        EnsureChargeOrderOpen(o);
        var me = await _view.LoadEmployee(user.Id);
        var now = _clock.Now;

        if (r.Approve == false)
        {
            var why = Req(r.Note, "退回原因", 200);
            c!.Status = ChargeStatus.Quoting;
            c.ReturnNote = $"{me.Name} 退回：{why}";
            AddLog(o, me, $"{me.Name} 退回报价（{why}）", o.Status, now, LogKind.Info);
            await _db.SaveChangesAsync();
            await tx.CommitAsync();
            await Push(o, me, now, "报价被退回，请重新报价", $"退回原因：{why}", c.Sales);
            return await _view.BuildDetail(id, user);
        }

        if (r.Paid == null) throw BizException.BadRequest("请选择客户是否已付款");
        var note = Opt(r.Note, "审核意见", 200);
        c!.Status = ChargeStatus.SupervisorConfirm;
        c.DirectorId = me.Id;
        c.Director = me;
        c.Paid = r.Paid;
        c.DirectorNote = note;
        c.ReviewedAt = now;
        c.ReturnNote = null;
        var paid = ChargeRules.PaidText(r.Paid);
        AddLog(o, me, $"{me.Name} 审核通过（{paid}），转售后主管确认", o.Status, now, LogKind.Info);
        await _db.SaveChangesAsync();
        await tx.CommitAsync();
        await _notifier.NotifyAsync("收费待确认", $"{me.Name} 已审核 **{o.CustomerName}**（{o.OrderNo}）的收费（{paid}），请售后主管确认。",
            await SupervisorPhones());
        await PushSupervisors(o, me, now, "有收费待你确认", $"{me.Name}已审核，{paid}");
        return await _view.BuildDetail(id, user);
    }

    /// <summary>售后主管确认：确认后服务价格按报价记下，工单回到工程师手上；不同意退回上一步</summary>
    public async Task<OrderDetail> ConfirmCharge(int id, ChargeConfirmRequest r, CurrentUser user)
    {
        EnsureRole(user, Role.Supervisor, "只有售后主管可以确认收费");
        if (r.Approve == null) throw BizException.BadRequest("请选择确认还是退回");
        await using var tx = await WriteLock();
        var o = await Load(id);
        var c = await LoadCharge(id);
        EnsureChargeAt(c, ChargeStatus.SupervisorConfirm);
        EnsureChargeOrderOpen(o);
        var me = await _view.LoadEmployee(user.Id);
        var now = _clock.Now;

        if (r.Approve == false)
        {
            var why = Req(r.Note, "退回原因", 200);
            // 退回上一步：有销售总监审核过就回到销售总监，否则回到销售
            var toDirector = c!.DirectorId != null;
            c.Status = toDirector ? ChargeStatus.DirectorReview : ChargeStatus.Quoting;
            c.ReturnNote = $"{me.Name} 退回：{why}";
            AddLog(o, me, $"{me.Name} 退回收费确认（{why}）", o.Status, now, LogKind.Info);
            await _db.SaveChangesAsync();
            await tx.CommitAsync();
            if (toDirector)
            {
                var directors = await SalesDirectors();
                await Push(o, me, now, b => b.Add("收费确认被退回，请重新审核", $"退回原因：{why}", directors));
            }
            else await Push(o, me, now, "收费确认被退回，请重新报价", $"退回原因：{why}", c.Sales);
            return await _view.BuildDetail(id, user);
        }

        var note = Opt(r.Note, "备注", 200);
        var amount = c!.Amount ?? throw BizException.Conflict("还没有报价，不能确认");
        c.Status = ChargeStatus.Confirmed;
        c.SupervisorId = me.Id;
        c.Supervisor = me;
        c.SupervisorNote = note;
        c.ConfirmedAt = now;
        c.ReturnNote = null;
        AddLog(o, me, $"{me.Name} 确认收费，工单回到 {o.Assignee?.Name} 继续处理" + (note == null ? "" : $"（{note}）"), o.Status, now, LogKind.Info);
        AddLog(o, me, !o.PriceSet || o.Price == amount
            ? $"{me.Name} 按收费确认定价 {EnumText.Money(amount)}"
            : $"{me.Name} 按收费确认改价 {EnumText.Money(o.Price)} → {EnumText.Money(amount)}", o.Status, now, LogKind.Price);
        o.Price = amount;
        o.PriceSet = true;
        o.PriceSetAt = now;
        await _db.SaveChangesAsync();
        await tx.CommitAsync();
        if (o.Assignee != null)
            await _notifier.NotifyAsync("收费已确认", $"**{o.CustomerName}**（{o.OrderNo}）的收费已确认，请 {o.Assignee.Name} 继续处理。",
                new[] { o.Assignee.Phone });
        await Push(o, me, now, b => b
            .Add("收费已确认，请继续处理", note == null ? $"{me.Name}已确认收费" : $"{me.Name}已确认收费：{note}", o.Assignee)
            .Add("收费已确认", $"{me.Name}已确认，工单继续处理", c.Sales, c.Director));
        return await _view.BuildDetail(id, user);
    }

    /// <summary>等当前用户处理的收费确认：销售 = 等我报价；销售总监 = 等审核（外加等自己报价的）；售后主管 = 等确认</summary>
    public async Task<List<ChargeTodoDto>> ChargeTodos(CurrentUser user)
    {
        if (user.Role == Role.Engineer) return new();
        var q = _db.OrderCharges.AsNoTracking().Include(c => c.Order).Include(c => c.Requester).Include(c => c.Sales)
            .Where(c => Doing.Contains(c.Order!.Status));
        q = user.Role == Role.Supervisor
            ? q.Where(c => c.Status == ChargeStatus.SupervisorConfirm)
            : user.SalesManager
                ? q.Where(c => c.Status == ChargeStatus.DirectorReview || (c.Status == ChargeStatus.Quoting && c.SalesId == user.Id))
                : q.Where(c => c.Status == ChargeStatus.Quoting && c.SalesId == user.Id);
        var rows = await q.ToListAsync();
        return rows.OrderBy(c => c.QuotedAt ?? c.CreatedAt).ThenBy(c => c.Id).Select(c => new ChargeTodoDto
        {
            OrderId = c.OrderId, OrderNo = c.Order!.OrderNo, CustomerName = c.Order.CustomerName, Urgent = c.Order.Urgency == Urgency.Urgent,
            Step = c.Status == ChargeStatus.Quoting ? "Quote" : c.Status == ChargeStatus.DirectorReview ? "Review" : "Confirm",
            Text = c.Status switch
            {
                ChargeStatus.Quoting => $"{c.Requester?.Name} 判断需要收费，等你报价",
                ChargeStatus.DirectorReview => $"{c.Sales?.Name} 已报价，等你审核",
                _ => $"{c.Sales?.Name} 已报价，等你确认"
            }
        }).ToList();
    }
}
