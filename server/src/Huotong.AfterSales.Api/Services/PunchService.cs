using Huotong.AfterSales.Api.Data;
using Huotong.AfterSales.Api.Domain;
using Huotong.AfterSales.Api.Dtos;
using Huotong.AfterSales.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Huotong.AfterSales.Api.Services;

public class PunchService
{
    private readonly AppDbContext _db;
    private readonly IClock _clock;
    private readonly OrderViewBuilder _view;
    private readonly FileService _files;

    public PunchService(AppDbContext db, IClock clock, OrderViewBuilder view, FileService files)
    {
        _db = db;
        _clock = clock;
        _view = view;
        _files = files;
    }

    public async Task<List<PunchInfo>> EngineerPunches(int engineerId) =>
        await _db.Punches.Where(p => p.EngineerId == engineerId)
            .Select(p => new PunchInfo(p.Id, p.OrderId, p.EngineerId, p.Kind, p.Time, p.OutOfRange, p.IsSupplement))
            .ToListAsync();

    private async Task<string?> BlockedReason(NextPunchDecision d)
    {
        if (d.BlockedByOrderId == null) return null;
        var name = await _db.Orders.Where(o => o.Id == d.BlockedByOrderId).Select(o => o.CustomerName).FirstOrDefaultAsync();
        return $"请先在〈{name}〉离场打卡";
    }

    /// <summary>
    /// 受理/处理中可正常打卡；暂停搁置/已作废时仅允许为本单今天未离场的到场卡补上离场卡，避免工程师被锁死。
    /// </summary>
    private static bool CanPunch(OrderStatus status, NextPunchDecision d) =>
        status is OrderStatus.Accepted or OrderStatus.InProgress
        || (status is (OrderStatus.OnHold or OrderStatus.Voided) && d.Kind == PunchKind.Leave && d.OpenArrive != null && d.BlockedByOrderId == null);

    /// <summary>首页「下一单」与打卡入口共用的已受理单选取顺序：预约最早优先，无预约排最后，再按认领时间</summary>
    public static IEnumerable<WorkOrder> OrderAcceptedForNext(IEnumerable<WorkOrder> xs) =>
        xs.OrderBy(o => o.AppointmentTime == null ? 1 : 0).ThenBy(o => o.AppointmentTime).ThenBy(o => o.ClaimedAt);

    public async Task<PunchContextDto?> Context(CurrentUser user, int? orderId)
    {
        var now = _clock.Now;
        var punches = await EngineerPunches(user.Id);
        WorkOrder? order;
        if (orderId != null)
        {
            order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == orderId) ?? throw BizException.NotFound("工单不存在");
            if (order.AssigneeId != user.Id) throw BizException.Forbidden("只有负责工程师可以打卡");
            if (order.ServiceType != ServiceType.Onsite) throw BizException.Conflict("远程/返厂工单无需打卡");
        }
        else
        {
            order = null;
            var open = PunchCalculator.Pair(punches).Where(p => p.IsRunning(now.Date)).OrderByDescending(p => p.Arrive.Time).FirstOrDefault();
            if (open != null) order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == open.Arrive.OrderId);
            order ??= await _db.Orders.Where(o => o.AssigneeId == user.Id && o.ServiceType == ServiceType.Onsite && o.Status == OrderStatus.InProgress)
                .OrderByDescending(o => o.StartedAt).ThenByDescending(o => o.Id).FirstOrDefaultAsync();
            if (order == null)
            {
                var accepted = await _db.Orders.Where(o => o.AssigneeId == user.Id && o.ServiceType == ServiceType.Onsite && o.Status == OrderStatus.Accepted).ToListAsync();
                order = OrderAcceptedForNext(accepted).FirstOrDefault();
            }
            if (order == null) return null;
        }

        var d = PunchCalculator.DecideNext(punches, order.Id, now);
        var me = await _view.LoadEmployee(user.Id);
        var blocked = await BlockedReason(d);
        if (blocked == null && !CanPunch(order.Status, d))
            blocked = $"工单当前为「{EnumText.Status(order.Status)}」，不能打卡";
        return new PunchContextDto
        {
            OrderId = order.Id, OrderNo = order.OrderNo, CustomerName = order.CustomerName, Address = order.Address,
            Latitude = order.Latitude, Longitude = order.Longitude, NextKind = d.Kind,
            OpenArriveTime = d.OpenArrive?.Time, BlockedReason = blocked, RangeMeters = GeoUtil.RangeMeters,
            ServerTime = now, EngineerName = me.Name
        };
    }

    public async Task<PunchResultDto> Create(CurrentUser user, PunchRequest r)
    {
        if (r.OrderId == null) throw BizException.BadRequest("缺少工单");
        if (r.Kind == null) throw BizException.BadRequest("缺少打卡类型");
        if (r.Latitude == null || r.Longitude == null) throw BizException.BadRequest("未获取到定位，请开启定位后重试");
        if (r.Latitude is < -90 or > 90 || r.Longitude is < -180 or > 180) throw BizException.BadRequest("定位坐标不正确");
        if (r.PhotoFileId == null) throw BizException.BadRequest("请现场拍照后再打卡");

        var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == r.OrderId) ?? throw BizException.NotFound("工单不存在");
        if (order.AssigneeId != user.Id) throw BizException.Forbidden("只有负责工程师可以打卡");
        if (order.ServiceType != ServiceType.Onsite) throw BizException.Conflict("远程/返厂工单无需打卡");
        var now = _clock.Now;
        var punches = await EngineerPunches(user.Id);
        var d = PunchCalculator.DecideNext(punches, order.Id, now);
        if (!CanPunch(order.Status, d))
            throw BizException.Conflict($"工单当前为「{EnumText.Status(order.Status)}」，不能打卡");
        var blocked = await BlockedReason(d);
        if (blocked != null) throw BizException.Conflict(blocked);
        if (d.Kind != r.Kind) throw BizException.Conflict("打卡状态已变化，请刷新");

        var me = await _view.LoadEmployee(user.Id);
        var photo = (await _files.Attach(new[] { r.PhotoFileId.Value }, order.Id, FileCategory.PunchPhoto, user, "打卡照片")).First();

        int? distance = null;
        var outOfRange = false;
        if (order.Latitude != null && order.Longitude != null)
        {
            var m = GeoUtil.DistanceMeters(order.Latitude.Value, order.Longitude.Value, r.Latitude.Value, r.Longitude.Value);
            distance = (int)Math.Round(m);
            outOfRange = GeoUtil.IsOutOfRange(m);
        }

        var punch = new Punch
        {
            OrderId = order.Id, EngineerId = me.Id, Kind = d.Kind, Time = now, Latitude = r.Latitude, Longitude = r.Longitude,
            Address = (r.Address ?? "").Trim(), DistanceMeters = distance, OutOfRange = outOfRange, PhotoFileId = photo.Id
        };
        _db.Punches.Add(punch);

        if (d.Kind == PunchKind.Arrive && order.Status == OrderStatus.Accepted)
        {
            var to = OrderStateMachine.Apply(order.Status, OrderAction.FirstArrive);
            order.Status = to;
            order.StartedAt ??= now;
            _db.StatusLogs.Add(new StatusLog { OrderId = order.Id, ActorId = me.Id, Text = $"{me.Name} 首次到场 → {EnumText.Status(to)}", Time = now, ToStatus = to });
        }
        await _db.SaveChangesAsync();

        var full = await _view.PunchesFull().Where(p => p.OrderId == order.Id).ToListAsync();
        var dto = _view.PunchDtos(full).First(p => p.Id == punch.Id);
        var orderFull = await _view.Orders().FirstAsync(o => o.Id == order.Id);
        return new PunchResultDto { Punch = dto, Order = await _view.BuildOne(orderFull) };
    }
}
