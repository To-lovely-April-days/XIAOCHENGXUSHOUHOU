using Huotong.AfterSales.Api.Data;
using Huotong.AfterSales.Api.Domain;
using Huotong.AfterSales.Api.Dtos;
using Huotong.AfterSales.Api.Infrastructure;
using Huotong.AfterSales.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

namespace Huotong.AfterSales.Tests;

/// <summary>PC 主管后台（API.md §9）：密码登录、改密、打卡记录、工时统计。SQLite 内存库 + 种子数据。</summary>
public sealed class AdminPcTests : IDisposable
{
    private const string SupPwd = "Huotong@2026";
    private const string ZhaoPhone = "13900000001";

    private readonly SqliteConnection _conn;
    private readonly AppDbContext _db;
    private readonly FixedClock _clock = new(new DateTime(2026, 11, 6, 13, 42, 10));
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aftersales-pc-test-" + Guid.NewGuid().ToString("N"));
    private readonly OrderViewBuilder _view;
    private readonly PasswordService _pwd;
    private readonly AdminService _admin;
    private readonly AdminReportService _reports;

    private class Env : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "test";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Test";
    }

    public AdminPcTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_conn).Options);
        _db.Database.Migrate();
        SeedData.RunAsync(_db, Path.Combine(_root, "uploads"), _clock.Now, SupPwd).GetAwaiter().GetResult();
        _db.ChangeTracker.Clear();

        var files = new FileService(_db, _clock, new Env { ContentRootPath = _root }, new HttpContextAccessor(),
            Options.Create(new AppSettings { PublicBaseUrl = "http://test" }));
        _view = new OrderViewBuilder(_db, _clock, files);
        var tokens = new TokenService(Options.Create(new JwtOptions { Key = "test-key-0123456789-0123456789-0123456789" }));
        _pwd = new PasswordService(_db, tokens, _view, new LoginThrottle(_clock));
        _admin = new AdminService(_db, _clock, _view, files);
        _reports = new AdminReportService(_db, _clock, _view, new HoursService(_db, _clock, _view));
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
        try { Directory.Delete(_root, true); } catch { }
    }

    private Employee Emp(string name) => _db.Employees.Single(x => x.Name == name);
    private CurrentUser U(string name) { var e = Emp(name); return new CurrentUser(e.Id, e.Role); }

    private static async Task<BizException> Biz(Func<Task> f) => await Assert.ThrowsAsync<BizException>(f);

    // ---------- 密码登录 ----------

    [Fact]
    public async Task PasswordLoginSucceedsForSeededSupervisor()
    {
        var r = await _pwd.Login(new PasswordLoginRequest { Phone = ZhaoPhone, Password = SupPwd });
        Assert.False(r.NeedBind);
        Assert.False(string.IsNullOrEmpty(r.Token));
        Assert.Equal("赵主管", r.User!.Name);
        Assert.Equal(Role.Supervisor, r.User.Role);
        Assert.Equal(ZhaoPhone, r.User.Phone);
    }

    [Fact]
    public async Task PasswordLoginFailuresShareOneMessage()
    {
        // 密码错误 / 账号不存在 / 未设置密码（李工）→ 同一文案
        foreach (var (phone, pwd) in new[] { (ZhaoPhone, "Wrong1234"), ("13999999999", SupPwd), ("13900000000", SupPwd) })
        {
            var ex = await Biz(() => _pwd.Login(new PasswordLoginRequest { Phone = phone, Password = pwd }));
            Assert.Equal(40000, ex.Code);
            Assert.Equal("手机号或密码错误", ex.Message);
        }
    }

    [Fact]
    public async Task PasswordLoginLocksAfterFiveFailures()
    {
        for (var i = 1; i <= 4; i++)
            Assert.Equal("手机号或密码错误", (await Biz(() => _pwd.Login(new PasswordLoginRequest { Phone = ZhaoPhone, Password = "Wrong1234" }))).Message);
        var fifth = await Biz(() => _pwd.Login(new PasswordLoginRequest { Phone = ZhaoPhone, Password = "Wrong1234" }));
        Assert.Equal(40000, fifth.Code);
        Assert.Equal("尝试次数过多，请 10 分钟后再试", fifth.Message);

        // 锁定期内正确密码也不行
        _clock.Now = _clock.Now.AddMinutes(9);
        Assert.Equal("尝试次数过多，请 10 分钟后再试",
            (await Biz(() => _pwd.Login(new PasswordLoginRequest { Phone = ZhaoPhone, Password = SupPwd }))).Message);

        // 10 分钟后解锁
        _clock.Now = _clock.Now.AddMinutes(1).AddSeconds(1);
        var ok = await _pwd.Login(new PasswordLoginRequest { Phone = ZhaoPhone, Password = SupPwd });
        Assert.NotNull(ok.Token);

        // 成功后计数清零：再错 4 次仍是普通错误
        for (var i = 1; i <= 4; i++)
            Assert.Equal("手机号或密码错误", (await Biz(() => _pwd.Login(new PasswordLoginRequest { Phone = ZhaoPhone, Password = "Wrong1234" }))).Message);
    }

    [Fact]
    public async Task PasswordLoginRejectsNonSupervisorAndDisabled()
    {
        var li = Emp("李工");
        await _pwd.SetFor(li.Id, new SetPasswordRequest { Password = "Engineer2026" });
        var ex = await Biz(() => _pwd.Login(new PasswordLoginRequest { Phone = li.Phone, Password = "Engineer2026" }));
        Assert.Equal(40300, ex.Code);
        Assert.Equal("仅售后主管可登录电脑后台", ex.Message);

        var zhao = _db.Employees.Single(e => e.Phone == ZhaoPhone);
        zhao.Enabled = false;
        await _db.SaveChangesAsync();
        ex = await Biz(() => _pwd.Login(new PasswordLoginRequest { Phone = ZhaoPhone, Password = SupPwd }));
        Assert.Equal(40300, ex.Code);
        Assert.Equal("账号已停用", ex.Message);
    }

    // ---------- 改密 / 重置 ----------

    [Theory]
    [InlineData("abc123")]      // 太短
    [InlineData("abcdefgh")]    // 无数字
    [InlineData("12345678")]    // 无字母
    [InlineData("")]
    public async Task ChangePasswordEnforcesStrength(string weak)
    {
        var ex = await Biz(() => _pwd.ChangeMine(U("赵主管"), new ChangePasswordRequest { OldPassword = SupPwd, NewPassword = weak }));
        Assert.Equal(40000, ex.Code);
        ex = await Biz(() => _pwd.SetFor(Emp("李工").Id, new SetPasswordRequest { Password = weak }));
        Assert.Equal(40000, ex.Code);
    }

    [Fact]
    public async Task ChangePasswordChecksOldPasswordAndTakesEffect()
    {
        var ex = await Biz(() => _pwd.ChangeMine(U("赵主管"), new ChangePasswordRequest { OldPassword = "Wrong1234", NewPassword = "NewPass2027" }));
        Assert.Equal("原密码错误", ex.Message);
        ex = await Biz(() => _pwd.ChangeMine(U("赵主管"), new ChangePasswordRequest { OldPassword = null, NewPassword = "NewPass2027" }));
        Assert.Equal(40000, ex.Code);

        var user = await _pwd.ChangeMine(U("赵主管"), new ChangePasswordRequest { OldPassword = SupPwd, NewPassword = "NewPass2027" });
        Assert.Equal("赵主管", user.Name);
        await Biz(() => _pwd.Login(new PasswordLoginRequest { Phone = ZhaoPhone, Password = SupPwd }));
        Assert.NotNull((await _pwd.Login(new PasswordLoginRequest { Phone = ZhaoPhone, Password = "NewPass2027" })).Token);

        // 未设置过密码时 oldPassword 可为空
        await _pwd.ChangeMine(U("李工"), new ChangePasswordRequest { OldPassword = "", NewPassword = "First2026" });
        Assert.True((await _admin.Employees()).Single(e => e.Name == "李工").HasPassword);
    }

    [Fact]
    public async Task EmployeesListShowsHasPassword()
    {
        var list = await _admin.Employees();
        Assert.True(list.Single(e => e.Name == "赵主管").HasPassword);
        Assert.False(list.Single(e => e.Name == "李工").HasPassword);
    }

    [Fact]
    public async Task BootstrapSetsOnlyMissingSupervisorPasswords()
    {
        var zhao = _db.Employees.Single(e => e.Phone == ZhaoPhone);
        zhao.PasswordHash = null;
        await _db.SaveChangesAsync();

        Assert.NotNull((await PasswordService.BootstrapSupervisors(_db, "weak")).Error);
        Assert.Equal((0, (string?)null), await PasswordService.BootstrapSupervisors(_db, null));
        Assert.Equal(1, (await PasswordService.BootstrapSupervisors(_db, "Boot2026x")).Count);
        Assert.Equal(0, (await PasswordService.BootstrapSupervisors(_db, "Other2026x")).Count); // 已有密码不覆盖
        Assert.NotNull((await _pwd.Login(new PasswordLoginRequest { Phone = ZhaoPhone, Password = "Boot2026x" })).Token);
    }

    [Fact]
    public async Task BootstrapFirstSupervisorOnlyWhenNoneExists()
    {
        var now = new DateTime(2026, 11, 10, 9, 0, 0);
        // 已有主管（赵主管）→ 不新建
        Assert.Equal((false, (string?)null), await PasswordService.BootstrapFirstSupervisor(_db, "张辉", "13811112222", "Boot2026x", now));

        foreach (var s in _db.Employees.Where(e => e.Role == Role.Supervisor)) s.Role = Role.Engineer;
        await _db.SaveChangesAsync();

        Assert.Equal((false, (string?)null), await PasswordService.BootstrapFirstSupervisor(_db, "", "13811112222", "Boot2026x", now));
        Assert.NotNull((await PasswordService.BootstrapFirstSupervisor(_db, "张辉", "13811112222", "weak", now)).Error);
        Assert.NotNull((await PasswordService.BootstrapFirstSupervisor(_db, "张辉", "123", "Boot2026x", now)).Error);
        Assert.NotNull((await PasswordService.BootstrapFirstSupervisor(_db, "张辉", ZhaoPhone, "Boot2026x", now)).Error); // 号码已占用
        Assert.True((await PasswordService.BootstrapFirstSupervisor(_db, "张辉", "+86 138-1111-2222", "Boot2026x", now)).Created);
        Assert.Equal((false, (string?)null), await PasswordService.BootstrapFirstSupervisor(_db, "李四", "13833334444", "Boot2026x", now)); // 只建一次

        var created = _db.Employees.Single(e => e.Phone == "13811112222");
        Assert.Equal(Role.Supervisor, created.Role);
        Assert.NotNull((await _pwd.Login(new PasswordLoginRequest { Phone = "13811112222", Password = "Boot2026x" })).Token);
    }

    // ---------- 打卡记录 ----------

    [Fact]
    public async Task AdminPunchesCountsAndAbnormalFilter()
    {
        // 默认最近 7 天（10-31 ~ 11-06）：江苏 3 + 南京 3 + 合肥 3 + 宁波 4 = 13；10 月无锡不在范围内
        var all = await _reports.Punches(new AdminPunchQuery());
        Assert.Equal(13, all.Total);
        Assert.Equal(new Dictionary<string, int> { ["all"] = 13, ["distance"] = 1, ["supplement"] = 1, ["missing"] = 1 }, all.Counts);
        Assert.Equal(all.Items.OrderByDescending(i => i.Time).Select(i => i.Id), all.Items.Select(i => i.Id)); // 时间倒序
        Assert.Equal("南京某检测中心", all.Items[0].CustomerName);
        var liLeave = all.Items.Single(i => i.CustomerName == "江苏某化工研究院" && i.Kind == PunchKind.Leave);
        Assert.Equal(145, liLeave.PairMinutes);
        Assert.StartsWith("SH-", liLeave.OrderNo);

        var missing = await _reports.Punches(new AdminPunchQuery { Abnormal = "missing" });
        var m = Assert.Single(missing.Items);
        Assert.True(m.Missing);
        Assert.Equal(PunchKind.Arrive, m.Kind);
        Assert.Equal("合肥某研究所", m.CustomerName);
        Assert.Equal("刘工", m.Engineer.Name);
        Assert.Equal(all.Counts, missing.Counts); // counts 不受 abnormal 影响

        var dist = Assert.Single((await _reports.Punches(new AdminPunchQuery { Abnormal = "distance" })).Items);
        Assert.True(dist.OutOfRange);
        Assert.Equal(2400, dist.DistanceMeters);

        var sup = Assert.Single((await _reports.Punches(new AdminPunchQuery { Abnormal = "supplement" })).Items);
        Assert.True(sup.IsSupplement);
        Assert.Equal("赵主管", sup.SupplementBy!.Name);
        Assert.Equal(430, sup.PairMinutes);

        // 今天未离场的到场不算缺卡
        Assert.False(all.Items.Single(i => i.CustomerName == "江苏某化工研究院" && i.Time.Date == _clock.Now.Date).Missing);
    }

    [Fact]
    public async Task AdminPunchesFiltersByEngineerDateAndPages()
    {
        var liu = await _reports.Punches(new AdminPunchQuery { EngineerId = Emp("刘工").Id });
        Assert.Equal(3, liu.Total);
        Assert.Equal(1, liu.Counts["missing"]);
        Assert.Equal(0, liu.Counts["distance"]);

        var oct = await _reports.Punches(new AdminPunchQuery { From = "2026-10-01", To = "2026-10-31" });
        Assert.Equal(4, oct.Total);
        Assert.All(oct.Items, i => Assert.Equal("无锡某生物科技公司", i.CustomerName));

        var p2 = await _reports.Punches(new AdminPunchQuery { Page = 2, PageSize = 5 });
        Assert.Equal(13, p2.Total);
        Assert.Equal(5, p2.Items.Count);
        Assert.Equal(2, p2.Page);
        Assert.Equal(5, p2.PageSize);

        Assert.Equal(40000, (await Biz(() => _reports.Punches(new AdminPunchQuery { Abnormal = "foo" }))).Code);
        Assert.Equal(40000, (await Biz(() => _reports.Punches(new AdminPunchQuery { From = "2026-11-06", To = "2026-11-01" }))).Code);
    }

    // ---------- 工时统计 ----------

    [Fact]
    public async Task AdminHoursSummarizesEveryEngineer()
    {
        var h = await _reports.Hours(2026, 11);
        Assert.Equal(new[] { "李工", "刘工", "陈工", "王工" }, h.Engineers.Select(e => e.Engineer.Name));
        Assert.Equal(940 + 495 + 220, h.TotalMinutes);

        var li = h.Engineers[0];
        Assert.Equal(940, li.TotalMinutes); // 430 + 365 + 145，今天计时中不计入
        Assert.Equal(4, li.TripDays);
        Assert.Equal(2, li.OrderCount);
        Assert.Equal(1, li.SupplementCount);
        Assert.Equal(0, li.MissingCount);
        Assert.True(li.RunningNow);
        Assert.Equal(new[] { ("2026-11-02", 430), ("2026-11-03", 365), ("2026-11-05", 145), ("2026-11-06", 0) },
            li.Days.Select(d => (d.Date.ToString("yyyy-MM-dd"), d.Minutes)));

        var liu = h.Engineers[1];
        Assert.Equal(495, liu.TotalMinutes);
        Assert.Equal(1, liu.MissingCount);
        Assert.False(liu.RunningNow);

        var chen = h.Engineers[2];
        Assert.Equal(220, chen.TotalMinutes);
        Assert.True(chen.RunningNow);

        var wang = h.Engineers[3];
        Assert.Equal(0, wang.TotalMinutes);
        Assert.Empty(wang.Days);

        // 与已有 /api/hours/month 一致
        var hours = new HoursService(_db, _clock, _view);
        foreach (var e in h.Engineers)
            Assert.Equal((await hours.Month(e.Engineer.Id, 2026, 11)).TotalMinutes, e.TotalMinutes);

        var oct = await _reports.Hours(2026, 10);
        Assert.Equal(940, oct.TotalMinutes);
        Assert.Equal("李工", oct.Engineers[0].Engineer.Name);
        Assert.False(oct.Engineers[0].RunningNow);
    }
}
