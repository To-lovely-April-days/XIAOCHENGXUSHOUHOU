using Huotong.AfterSales.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Huotong.AfterSales.Api.Services;

/// <summary>
/// v1.4 微信订阅消息提醒：各工单动作在「事务提交之后」调用这里的方法（与钉钉通知同一位置）。
/// 硬规则：任何消息都不得包含价格、价格建议、受限附件名；说明（Content）只写一句话。
/// 发送失败、超时都只记日志，绝不影响主流程；未启用时不查库、不做任何事。
/// 收件人过滤（启用、已绑定微信、不是操作人本人、一次动作每人只收一条）在 ISubscribeNotifier 的实现里统一做。
/// </summary>
public partial class OrderService
{
    /// <summary>一次工单动作要发的提醒（同一个人只收先加入的那一条）</summary>
    private sealed class PushBatch
    {
        private readonly WorkOrder _order;
        private readonly Employee _actor;
        private readonly DateTime _time;
        public List<SubscribeNotice> Notices { get; } = new();
        public int ActorId => _actor.Id;

        public PushBatch(WorkOrder order, Employee actor, DateTime time)
        {
            _order = order;
            _actor = actor;
            _time = time;
        }

        public PushBatch Add(string title, string content, IEnumerable<Employee?> to)
        {
            var list = to.Where(e => e != null).ToList();
            if (list.Count > 0)
                Notices.Add(new SubscribeNotice(_order.Id, list, new SubscribeSlots(
                    Title: title, OrderNo: _order.OrderNo, Customer: _order.CustomerName, Content: content, Time: _time,
                    Operator: _actor.Name, Status: EnumText.Status(_order.Status), Device: _order.DeviceText)));
            return this;
        }

        public PushBatch Add(string title, string content, params Employee?[] to) => Add(title, content, (IEnumerable<Employee?>)to);
    }

    /// <summary>发送一批提醒。build 里的取数（如查主管）也在保护之内：出任何异常都只记日志</summary>
    private async Task Push(WorkOrder o, Employee actor, DateTime now, Func<PushBatch, Task> build)
    {
        try
        {
            if (!_subscribe.Enabled) return;
            var batch = new PushBatch(o, actor, now);
            await build(batch);
            if (batch.Notices.Count > 0) await _subscribe.NotifyAsync(batch.ActorId, batch.Notices);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("订阅消息提醒异常（已忽略）：{Type}", ex.GetType().Name);
        }
    }

    private Task Push(WorkOrder o, Employee actor, DateTime now, Action<PushBatch> build) =>
        Push(o, actor, now, b => { build(b); return Task.CompletedTask; });

    /// <summary>发给一组人同一条提醒</summary>
    private Task Push(WorkOrder o, Employee actor, DateTime now, string title, string content, params Employee?[] to) =>
        Push(o, actor, now, b => b.Add(title, content, to));

    /// <summary>发给全部启用且已绑定微信的主管</summary>
    private Task PushSupervisors(WorkOrder o, Employee actor, DateTime now, string title, string content) =>
        Push(o, actor, now, async b => b.Add(title, content, await SubscribedSupervisors()));

    private async Task<List<Employee>> SubscribedSupervisors() =>
        await _db.Employees.AsNoTracking()
            .Where(e => e.Role == Role.Supervisor && e.Enabled && e.OpenId != null && e.OpenId != "")
            .OrderBy(e => e.Id).ToListAsync();

    private const string AssistantAddedTitle = "你被加为协助人";
}
