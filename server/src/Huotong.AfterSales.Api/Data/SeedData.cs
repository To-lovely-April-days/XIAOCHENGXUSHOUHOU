using Huotong.AfterSales.Api.Domain;
using Huotong.AfterSales.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace Huotong.AfterSales.Api.Data;

/// <summary>
/// 演示数据：按原型示例构造。原型的「今天」是 2026-11-06，
/// 所有日期按 (北京时间今天 − 2026-11-06) 整体平移。
/// </summary>
public class SeedData
{
    private static readonly DateTime ProtoToday = new(2026, 11, 6);
    // 1x1 灰色 PNG，作为演示照片占位
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mN8/5+hHgAHggJ/PchI7wAAAABJRU5ErkJggg==");

    private readonly AppDbContext _db;
    private readonly string _uploadRoot;
    private readonly int _offset;
    private readonly string? _supervisorPassword;

    public SeedData(AppDbContext db, string uploadRoot, DateTime today, string? supervisorPassword = null)
    {
        _db = db;
        _uploadRoot = uploadRoot;
        _offset = (today.Date - ProtoToday).Days;
        _supervisorPassword = supervisorPassword;
    }

    private DateTime D(int month, int day, int h = 0, int m = 0) => new DateTime(2026, month, day, h, m, 0).AddDays(_offset);
    private string No(int month, int day, string seq) => $"SH-{D(month, day):yyyyMMdd}-{seq}";

    /// <param name="supervisorPassword">赵主管的 PC 后台初始密码（配置 Seed:SupervisorPassword），为空则不设密码</param>
    public static async Task RunAsync(AppDbContext db, string uploadRoot, DateTime today, string? supervisorPassword = null)
    {
        if (await db.Employees.AnyAsync()) return;
        await new SeedData(db, uploadRoot, today, supervisorPassword).Run();
    }

    private Employee E(string name, Role role, string phone, string dept) => new()
    {
        Name = name, Role = role, Phone = phone, Department = dept, Enabled = true, CreatedAt = D(10, 1, 9)
    };

    private async Task Run()
    {
        var zhao = E("赵主管", Role.Supervisor, "13900000001", "售后部");
        var li = E("李工", Role.Engineer, "13900000000", "售后部");
        var chen = E("陈工", Role.Engineer, "13900000002", "售后部");
        var liu = E("刘工", Role.Engineer, "13900000003", "售后部");
        var wang = E("王工", Role.Engineer, "13900000004", "售后部");
        var wm = E("王敏", Role.Sales, "13900000010", "销售部");
        var sl = E("孙磊", Role.Sales, "13900000011", "销售部");
        if (!string.IsNullOrEmpty(_supervisorPassword)) zhao.PasswordHash = PasswordPolicy.Hash(zhao, _supervisorPassword);
        _db.Employees.AddRange(zhao, li, chen, liu, wang, wm, sl);
        await _db.SaveChangesAsync();

        // 1. 江苏某化工研究院：处理中，今天 10:05 到场计时中
        var o1 = Order(No(11, 5, "003"), OrderStatus.InProgress, wm, li, "江苏某化工研究院", "张工", "13800000000",
            "苏州市工业园区星湖街 [示例地址] 3 号楼 2 层实验室", 31.30, 120.72, "高压反应釜 HTR-500", "SN2408017", "2024-08", true,
            "11月4日 下午", "升温到 180℃ 左右就不再加热，面板报 E03", "断电重启、检查电源线", "通风橱内，220V 单相电", 1800,
            created: D(11, 5, 9, 20), claimed: D(11, 5, 9, 48));
        // 2. 无锡某制药企业：紧急待认领
        var o2 = Order(No(11, 6, "002"), OrderStatus.Pending, wm, null, "无锡某制药企业", "钱工", "13800000002",
            "无锡市新吴区 [示例地址] 制剂车间", 31.55, 120.36, "高压反应釜 HTR-1000", "SN2409033", "2024-09", true,
            "11月6日 上午", "釜内压力降不下来，安全阀一天起跳好几次", "手动泄压、检查进气阀", "GMP 车间，需更衣入场", 2400,
            created: D(11, 6, 10, 42), urgency: Urgency.Urgent, request: "希望今天下午能到现场");
        // 3. 南通某试剂厂：待启动，预约 11月9日 10:00
        var o3 = Order(No(11, 5, "014"), OrderStatus.Accepted, wm, li, "南通某试剂厂", "赵工", "13900000000",
            "南通市崇川区 [示例地址]", 32.00, 120.89, "微型反应釜 HTM-100", "SN2501021", "2025-01", true,
            "11月5日 上午", "搅拌电机异响，转速上 600 转就有咔咔声", "降低转速使用", "实验室台面，220V", 800,
            created: D(11, 5, 14, 10), claimed: D(11, 5, 16, 2));
        o3.AppointmentTime = D(11, 9, 10, 0);
        // 4. 浙江某新材料公司：待认领，保内
        var o4 = Order(No(11, 5, "011"), OrderStatus.Pending, sl, null, "浙江某新材料公司", "周工", "13800000004",
            "杭州市钱塘区 [示例地址]", 30.30, 120.45, "连续流反应装置", "SN2311102", "2023-11", true,
            "11月4日 全天", "计量泵流量忽高忽低，已经换过过滤芯", "更换过滤芯、重新排气", "中试车间，380V 三相电", 0,
            created: D(11, 5, 11, 30));
        // 5. 南京某检测中心：处理中，今天 13:00 到场距离 2.4km
        var o5 = Order(No(11, 5, "007"), OrderStatus.InProgress, sl, chen, "南京某检测中心", "吴工", "13800000005",
            "南京市玄武区 [示例地址]", 32.06, 118.79, "高压反应釜 HTR-250", "SN2405009", "2024-05", true,
            "11月4日 上午", "釜盖密封处有泄漏，保压 30 分钟掉 0.5MPa", "重新拧紧釜盖螺栓", "检测实验室，通风橱内", 960,
            created: D(11, 4, 16, 20), claimed: D(11, 4, 17, 5));
        // 6. 上海某高校化学系：远程咨询待认领
        var o6 = Order(No(11, 4, "019"), OrderStatus.Pending, sl, null, "上海某高校化学系", "陈老师", "13800000006",
            "上海市杨浦区 [示例地址]", 31.26, 121.53, "微型反应釜 HTM-100", "SN2502015", "2025-02", true,
            "11月4日", "压力传感器多久需要校准一次？", "查阅说明书", "教学实验室", 0,
            created: D(11, 4, 10, 5), urgency: Urgency.Consult, service: ServiceType.Remote, problem: ProblemType.Consult);
        // 7. 合肥某研究所：待验收，11月5日缺离场卡
        var o7 = Order(No(11, 4, "012"), OrderStatus.Submitted, wm, liu, "合肥某研究所", "孔工", "13800000007",
            "合肥市蜀山区 [示例地址]", 31.82, 117.23, "连续流反应装置", "SN2403011", "2024-03", true,
            "11月3日 夜间", "温控漂移，设定 80℃ 实际在 76~84℃ 之间波动", "重启温控器", "研究所实验楼 2 层，220V", 3200,
            created: D(11, 4, 8, 40), claimed: D(11, 4, 8, 55));
        Complete(o7, "温度传感器接头松动，PID 参数漂移", "紧固传感器接头并重新整定 PID 参数；连续运行 2 小时温控稳定在 ±0.5℃", "", RunStatus.Normal, D(11, 5, 18, 30));
        // 8. 宁波某化工企业：已闭环，11月2日离场为主管补录
        var o8 = Order(No(11, 3, "005"), OrderStatus.Closed, wm, li, "宁波某化工企业", "孙工", "13800000008",
            "宁波市鄞州区 [示例地址]", 29.87, 121.55, "高压反应釜 HTR-500", "SN2306004", "2023-06", true,
            "11月1日", "年度保养", "无", "生产车间，380V 三相电", 1500,
            created: D(11, 1, 15, 0), claimed: D(11, 1, 15, 30), problem: ProblemType.Consult, source: OrderSource.CustomerDirect);
        Complete(o8, "年度保养：釜盖密封圈老化，压力表需校准", "更换密封圈、校准压力表、清洁加热套，保压测试合格", "密封圈 ×2", RunStatus.Normal, D(11, 3, 15, 40));
        o8.ClosedAt = D(11, 3, 17, 10);
        // 9. 苏州某药企：返厂，已闭环
        var o9 = Order(No(11, 2, "021"), OrderStatus.Closed, sl, chen, "苏州某药企", "郑工", "13800000009",
            "苏州市吴中区 [示例地址]", 31.30, 120.62, "微型反应釜 HTM-100", "SN2408031", "2024-08", false,
            "11月1日", "搅拌无力，磁力搅拌偶尔脱耦", "更换搅拌子", "研发实验室", 680,
            created: D(11, 2, 9, 30), claimed: D(11, 2, 10, 0), service: ServiceType.Factory, problem: ProblemType.Return,
            source: OrderSource.ReturnLogistics);
        Complete(o9, "磁力耦合器磨损", "返厂更换磁力耦合器并完成出厂测试", "磁力耦合器 ×1", RunStatus.Normal, D(11, 5, 10, 30));
        o9.ClosedAt = D(11, 5, 11, 0);
        // 10. 李工 10 月：无锡某生物科技公司 安装调试，已闭环
        var o10 = Order(No(10, 20, "008"), OrderStatus.Closed, wm, li, "无锡某生物科技公司", "冯工", "13800000010",
            "无锡市新吴区 [示例地址] 研发中心", 31.56, 120.38, "连续流反应装置", "SN2410002", "2026-10", false,
            "10月20日", "新设备安装调试", "无", "研发中心 1 层，380V 三相电", 4600,
            created: D(10, 20, 8, 10), claimed: D(10, 20, 8, 30), problem: ProblemType.Consult);
        Complete(o10, "新设备安装调试", "完成安装调试与操作培训，客户签署 SAT", "", RunStatus.Normal, D(10, 21, 16, 45));
        o10.ClosedAt = D(10, 22, 10, 0);

        var orders = new[] { o1, o2, o3, o4, o5, o6, o7, o8, o9, o10 };
        _db.Orders.AddRange(orders);
        await _db.SaveChangesAsync();

        // 建单照片（江苏 3 张）
        for (var i = 0; i < 3; i++) AddFile(wm, o1, FileCategory.OrderPhoto, D(11, 5, 9, 20));

        // 打卡
        Punch(o1, li, PunchKind.Arrive, D(11, 5, 15, 40), 90);
        Punch(o1, li, PunchKind.Leave, D(11, 5, 18, 5), 88);
        Punch(o1, li, PunchKind.Arrive, D(11, 6, 10, 5), 86);

        Punch(o5, chen, PunchKind.Arrive, D(11, 5, 9, 10), 80);
        Punch(o5, chen, PunchKind.Leave, D(11, 5, 12, 50), 76);
        Punch(o5, chen, PunchKind.Arrive, D(11, 6, 13, 0), 2400);

        Punch(o7, liu, PunchKind.Arrive, D(11, 4, 9, 30), 120);
        Punch(o7, liu, PunchKind.Leave, D(11, 4, 17, 45), 110);
        Punch(o7, liu, PunchKind.Arrive, D(11, 5, 15, 10), 95);

        Punch(o8, li, PunchKind.Arrive, D(11, 2, 9, 5), 60);
        Supplement(o8, li, zhao, D(11, 2, 16, 15), "现场无信号", D(11, 2, 18, 0));
        Punch(o8, li, PunchKind.Arrive, D(11, 3, 9, 20), 58);
        Punch(o8, li, PunchKind.Leave, D(11, 3, 15, 25), 61);

        Punch(o10, li, PunchKind.Arrive, D(10, 20, 9, 0), 45);
        Punch(o10, li, PunchKind.Leave, D(10, 20, 17, 20), 52);
        Punch(o10, li, PunchKind.Arrive, D(10, 21, 9, 10), 40);
        Punch(o10, li, PunchKind.Leave, D(10, 21, 16, 30), 47);

        // 完工资料
        foreach (var (o, eng) in new[] { (o7, liu), (o8, li), (o9, chen), (o10, li) })
        {
            AddFile(eng, o, FileCategory.ProcessPhoto, o.SubmittedAt!.Value);
            AddFile(eng, o, FileCategory.ProcessPhoto, o.SubmittedAt!.Value);
            AddFile(eng, o, FileCategory.Signature, o.SubmittedAt!.Value);
        }

        // 状态记录（按原型）
        Log(o1, wm, "王敏 建单", OrderStatus.Pending, D(11, 5, 9, 20));
        Log(o1, li, "李工 认领 → 待启动", OrderStatus.Accepted, D(11, 5, 9, 48));
        Log(o1, li, "李工 首次到场 → 处理中", OrderStatus.InProgress, D(11, 5, 15, 40));
        Log(o2, wm, "王敏 建单 · 紧急", OrderStatus.Pending, D(11, 6, 10, 42));
        Log(o3, wm, "王敏 建单", OrderStatus.Pending, D(11, 5, 14, 10));
        Log(o3, li, "李工 认领 → 待启动", OrderStatus.Accepted, D(11, 5, 16, 2));
        Log(o4, sl, "孙磊 建单", OrderStatus.Pending, D(11, 5, 11, 30));
        Log(o5, sl, "孙磊 建单", OrderStatus.Pending, D(11, 4, 16, 20));
        Log(o5, chen, "陈工 认领 → 待启动", OrderStatus.Accepted, D(11, 4, 17, 5));
        Log(o5, chen, "陈工 首次到场 → 处理中", OrderStatus.InProgress, D(11, 5, 9, 10));
        Log(o6, sl, "孙磊 建单", OrderStatus.Pending, D(11, 4, 10, 5));
        Log(o7, wm, "王敏 建单", OrderStatus.Pending, D(11, 4, 8, 40));
        Log(o7, liu, "刘工 认领 → 待启动", OrderStatus.Accepted, D(11, 4, 8, 55));
        Log(o7, liu, "刘工 首次到场 → 处理中", OrderStatus.InProgress, D(11, 4, 9, 30));
        Log(o7, liu, "刘工 提交完工 → 待验收", OrderStatus.Submitted, D(11, 5, 18, 30));
        Log(o8, wm, "王敏 建单", OrderStatus.Pending, D(11, 1, 15, 0));
        Log(o8, li, "李工 提交完工 → 待验收", OrderStatus.Submitted, D(11, 3, 15, 40));
        Log(o8, zhao, "赵主管 确认闭环 → 已闭环", OrderStatus.Closed, D(11, 3, 17, 10));
        Log(o9, sl, "孙磊 建单", OrderStatus.Pending, D(11, 2, 9, 30));
        Log(o9, zhao, "赵主管 确认闭环 → 已闭环", OrderStatus.Closed, D(11, 5, 11, 0));
        Log(o10, wm, "王敏 建单", OrderStatus.Pending, D(10, 20, 8, 10));
        Log(o10, li, "李工 认领 → 待启动", OrderStatus.Accepted, D(10, 20, 8, 30));
        Log(o10, li, "李工 首次到场 → 处理中", OrderStatus.InProgress, D(10, 20, 9, 0));
        Log(o10, li, "李工 提交完工 → 待验收", OrderStatus.Submitted, D(10, 21, 16, 45));
        Log(o10, zhao, "赵主管 确认闭环 → 已闭环", OrderStatus.Closed, D(10, 22, 10, 0));

        await _db.SaveChangesAsync();
    }

    private static WorkOrder Order(string no, OrderStatus status, Employee creator, Employee? assignee, string customer,
        string contact, string phone, string address, double lat, double lng, string model, string sn, string sat, bool signed,
        string faultTime, string symptom, string tried, string env, decimal price, DateTime created, DateTime? claimed = null,
        Urgency urgency = Urgency.Normal, ServiceType service = ServiceType.Onsite, ProblemType problem = ProblemType.Fault,
        OrderSource? source = null, string? request = null)
    {
        var (city, region) = AddressParser.Parse(address);
        return new WorkOrder
        {
            OrderNo = no, Status = status, Source = source ?? (creator.Role == Role.Sales ? OrderSource.SalesReferral : OrderSource.CustomerDirect),
            ProblemType = problem, Urgency = urgency, ServiceType = service, CustomerName = customer, ContactName = contact,
            ContactPhone = phone, Address = address, City = city, Region = region, Latitude = lat, Longitude = lng,
            DeviceModel = model, DeviceSn = sn, SatDate = sat, SatSigned = signed, FaultTime = faultTime, Symptom = symptom,
            CustomerTried = tried, SiteEnvironment = env, CustomerRequest = request, Price = price, CreatorId = creator.Id,
            AssigneeId = assignee?.Id, CreatedAt = created, ClaimedAt = assignee == null ? null : claimed,
            StartedAt = status is OrderStatus.InProgress or OrderStatus.Submitted or OrderStatus.Closed ? claimed : null
        };
    }

    private static void Complete(WorkOrder o, string cause, string solution, string parts, RunStatus run, DateTime at)
    {
        o.FaultCause = cause;
        o.Solution = solution;
        o.PartsReplaced = parts;
        o.RunStatus = run;
        o.SubmittedAt = at;
    }

    private StoredFile AddFile(Employee uploader, WorkOrder o, FileCategory cat, DateTime at)
    {
        var rel = $"{at:yyyy}/{at:MM}/seed-{Guid.NewGuid():N}.png";
        var full = Path.Combine(_uploadRoot, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, Png);
        var f = new StoredFile
        {
            Path = rel, OriginalName = "seed.png", Category = cat, MimeType = "image/png", Size = Png.Length,
            UploaderId = uploader.Id, OrderId = o.Id, CreatedAt = at
        };
        _db.Files.Add(f);
        return f;
    }

    private void Punch(WorkOrder o, Employee eng, PunchKind kind, DateTime time, int meters)
    {
        var lat = GeoUtil.OffsetLatitude(o.Latitude!.Value, meters);
        var photo = AddFile(eng, o, FileCategory.PunchPhoto, time);
        _db.Punches.Add(new Punch
        {
            Order = o, OrderId = o.Id, EngineerId = eng.Id, Kind = kind, Time = time, Latitude = Math.Round(lat, 6),
            Longitude = o.Longitude, Address = o.Address, DistanceMeters = meters, OutOfRange = GeoUtil.IsOutOfRange(meters),
            PhotoFile = photo
        });
    }

    private void Supplement(WorkOrder o, Employee eng, Employee by, DateTime time, string reason, DateTime at) =>
        _db.Punches.Add(new Punch
        {
            OrderId = o.Id, EngineerId = eng.Id, Kind = PunchKind.Leave, Time = time, Address = "",
            IsSupplement = true, SupplementById = by.Id, SupplementReason = reason, SupplementAt = at
        });

    private void Log(WorkOrder o, Employee actor, string text, OrderStatus to, DateTime time) =>
        _db.StatusLogs.Add(new StatusLog { OrderId = o.Id, ActorId = actor.Id, Text = text, ToStatus = to, Time = time });
}
