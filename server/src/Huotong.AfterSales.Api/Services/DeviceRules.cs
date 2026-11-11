using System.Globalization;
using System.Text.RegularExpressions;
using Huotong.AfterSales.Api.Domain;
using Huotong.AfterSales.Api.Dtos;
using Huotong.AfterSales.Api.Infrastructure;

namespace Huotong.AfterSales.Api.Services;

/// <summary>
/// v1.3 设备资料规则的唯一来源（CHANGES-v1.3 §3）：设备清单归一化、设备文字（title / 汇总）、SAT 时间格式与修改权限。
/// 两个前端不得自己拼汇总文字，一律显示服务端的 deviceText。
/// </summary>
public static class DeviceRules
{
    public const int Max = 20;
    public const int FieldMax = 100;
    public const string None = "未填设备";

    private static readonly Regex MonthRe = new(@"^\d{4}-(0[1-9]|1[0-2])$", RegexOptions.Compiled);

    /// <summary>名称与型号都非空 → 「名称 型号」；只有其一 → 那一个；都空 → 编号</summary>
    public static string Title(string? name, string? sn, string? model)
    {
        var n = (name ?? "").Trim();
        var m = (model ?? "").Trim();
        if (n.Length > 0 && m.Length > 0) return $"{n} {m}";
        if (n.Length > 0) return n;
        if (m.Length > 0) return m;
        return (sn ?? "").Trim();
    }

    public static string Title(OrderDevice d) => Title(d.Name, d.Sn, d.Model);

    /// <summary>设备汇总文字（= deviceText）：0 台 → 未填设备；1 台 → 它的 title；n 台 → 「第 1 台的 title 等 n 台」</summary>
    public static string Summary(IReadOnlyList<OrderDevice> devices) => devices.Count switch
    {
        0 => None,
        1 => Title(devices[0]),
        _ => $"{Title(devices[0])} 等 {devices.Count} 台"
    };

    /// <summary>
    /// 设备清单归一化（§3.1）：请求里有 devices（含 []）→ 用它，忽略旧字段；没有（省略 / null）→ 旧客户端，
    /// deviceModel / deviceSn 任一非空白时合成一台。三项各自 Trim，全空的行与 null 元素丢弃，剩下的按原顺序编号。
    /// 不做长度 / 台数校验（见 Validate）。
    /// </summary>
    public static List<OrderDevice> Normalize(CreateOrderRequest r)
    {
        var source = r.Devices ?? new List<DeviceItem> { new() { Name = "", Sn = r.DeviceSn, Model = r.DeviceModel } };
        var result = new List<OrderDevice>();
        foreach (var it in source)
        {
            if (it == null) continue;
            var d = new OrderDevice { Name = (it.Name ?? "").Trim(), Sn = (it.Sn ?? "").Trim(), Model = (it.Model ?? "").Trim() };
            if (d.Name.Length == 0 && d.Sn.Length == 0 && d.Model.Length == 0) continue;
            d.Seq = result.Count + 1;
            result.Add(d);
        }
        return result;
    }

    /// <summary>归一化后的校验（§3.1 第 2、3 条）：台数与各项长度</summary>
    public static void Validate(IReadOnlyList<OrderDevice> devices)
    {
        if (devices.Count > Max) throw BizException.BadRequest($"设备最多 {Max} 台");
        foreach (var d in devices)
        {
            if (d.Name.Length > FieldMax) throw BizException.BadRequest($"设备名称不能超过 {FieldMax} 个字");
            if (d.Sn.Length > FieldMax) throw BizException.BadRequest($"设备编号不能超过 {FieldMax} 个字");
            if (d.Model.Length > FieldMax) throw BizException.BadRequest($"设备型号不能超过 {FieldMax} 个字");
        }
    }

    public static string SatText(bool satSigned) => satSigned ? "已完成" : "未完成";

    /// <summary>时间栏的标签：已完成 SAT → SAT 完成时间；未完成 → 发货时间</summary>
    public static string SatDateLabel(bool satSigned) => satSigned ? "SAT 完成时间" : "发货时间";

    /// <summary>
    /// SAT 完成时间 / 发货时间：空白 → ""；合法日期 yyyy-MM-dd 或旧格式 yyyy-MM → Trim 后的值；其他 → 40000（文案随 satSigned）。
    /// 不限制早晚（可以是将来的日期）。
    /// </summary>
    public static string ParseSatDate(string? satDate, bool satSigned)
    {
        if (string.IsNullOrWhiteSpace(satDate)) return "";
        var s = satDate.Trim();
        if (s.Length == 10 && DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return s;
        if (MonthRe.IsMatch(s)) return s;
        throw BizException.BadRequest($"{SatDateLabel(satSigned)}格式应为 yyyy-MM-dd");
    }

    /// <summary>
    /// 修改 SAT 信息被拒的原因（§3.5 第 5、6 步，按顺序）；可以修改返回 null。canEditSat 与接口校验共用。
    /// 工单不是 Voided，且当前用户是 Supervisor / 建单人 / 当前负责人；已退出的人不可以（即使他是建单人）。o 须带 Exits。
    /// </summary>
    public static BizException? SatEditDenied(WorkOrder o, CurrentUser user)
    {
        if (o.Status == OrderStatus.Voided) return BizException.Conflict("工单已作废，不能修改 SAT 信息");
        if (user.Role == Role.Supervisor) return null;
        if (OrderViewBuilder.IsFormer(o, user)) return BizException.Forbidden(OrderViewBuilder.FormerDenied);
        if (o.CreatorId != user.Id && o.AssigneeId != user.Id)
            return BizException.Forbidden("只有建单人、负责工程师或主管可以修改 SAT 信息");
        return null;
    }
}
