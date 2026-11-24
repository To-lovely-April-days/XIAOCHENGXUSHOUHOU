using Huotong.AfterSales.Api.Domain;
using Huotong.AfterSales.Api.Dtos;
using Huotong.AfterSales.Api.Infrastructure;
using Huotong.AfterSales.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Huotong.AfterSales.Tests;

/// <summary>v1.5 小程序注册申请、账号密码登录、主管审批</summary>
public sealed class V15AccountTests : V11TestBase
{
    private const string Pwd = "Abcd1234";
    private const string NewPhone = "13711112222";

    private sealed class FakeWx : IWeChatService
    {
        public bool Fail;
        public Task<string> Code2Session(string code) =>
            Fail ? throw new InvalidOperationException("微信不可用") : Task.FromResult("openid-" + code);
        public Task<string> GetPhoneNumber(string phoneCode) => Task.FromResult(phoneCode);
    }

    private readonly FakeWx _wx = new();
    private readonly AccountService _acc;
    private readonly PasswordService _pwd;

    public V15AccountTests()
    {
        var tokens = new TokenService(Options.Create(new JwtOptions { Key = "test-key-0123456789-0123456789-0123456789" }));
        var throttle = new LoginThrottle(Clock);
        _acc = new AccountService(Db, _wx, tokens, View, throttle, new RegisterThrottle(Clock), Clock, NullLogger<AccountService>.Instance);
        _pwd = new PasswordService(Db, tokens, View, throttle);
    }

    private static RegisterRequest Reg(string phone = NewPhone, string name = "新同事", string pwd = Pwd, string code = "wxA") =>
        new() { Name = name, Phone = phone, Password = pwd, Code = code };

    private static AccountLoginRequest Login(string phone = NewPhone, string pwd = Pwd, string? code = "wxA") =>
        new() { Phone = phone, Password = pwd, Code = code };

    private int RegId(string phone = NewPhone) => Db.Registrations.AsNoTracking().Single(r => r.Phone == phone).Id;

    // ---------- 注册 ----------

    [Fact]
    public async Task RegisterCreatesPendingAndNoEmployee()
    {
        var r = await _acc.Register(Reg());
        Assert.Equal(RegistrationStatus.Pending, r.Status);
        Assert.Equal("已提交，等待管理员审批", r.Message);
        var row = Db.Registrations.AsNoTracking().Single();
        Assert.Equal("新同事", row.Name);
        Assert.Equal("openid-wxA", row.OpenId);
        Assert.NotEqual(Pwd, row.PasswordHash);
        Assert.False(Db.Employees.Any(e => e.Phone == NewPhone));
        Assert.Equal(1, (await Home.Supervisor(Sup)).PendingRegistrations);
        Assert.Equal(1, (await Admin.Overview()).PendingRegistrations);
    }

    [Theory]
    [InlineData("", NewPhone, Pwd, "wx", "请填写姓名")]
    [InlineData("张三", "1371111", Pwd, "wx", "请填写 11 位手机号")]
    [InlineData("张三", NewPhone, "12345678", "wx", "密码需同时包含字母和数字")]
    [InlineData("张三", NewPhone, "a1", "wx", "密码至少 8 位，且需同时包含字母和数字")]
    [InlineData("张三", NewPhone, Pwd, "", "请在微信小程序内注册")]
    public async Task RegisterValidates(string name, string phone, string pwd, string code, string message) =>
        await Fails(40000, message, () => _acc.Register(Reg(phone, name, pwd, code)));

    [Fact]
    public async Task RegisterRejectsExistingEmployeeAndDuplicatePending()
    {
        await Fails(40900, "该手机号已有账号，请直接登录", () => _acc.Register(Reg(Emp("王工").Phone)));
        await _acc.Register(Reg());
        await Fails(40900, "该手机号已提交注册，正在等待审批", () => _acc.Register(Reg(code: "wxB")));
    }

    [Fact]
    public async Task RegisterLimitedToThreePerWeChatPerDay()
    {
        for (var i = 0; i < 3; i++) await _acc.Register(Reg("1371111000" + i));
        await Fails(40000, "今天提交注册的次数过多，请明天再试", () => _acc.Register(Reg("13711110009")));
        await _acc.Register(Reg("13711110008", code: "wxOther")); // 别的微信不受影响
        Clock.Now = Clock.Now.AddDays(1);
        await _acc.Register(Reg("13711110009"));
    }

    // ---------- 登录 ----------

    [Fact]
    public async Task LoginBeforeApprovalSaysPendingOnlyWithRightPassword()
    {
        await _acc.Register(Reg());
        await Fails(40310, "账号正在审核中，请联系管理员", () => _acc.Login(Login()));
        await Fails(40000, "手机号或密码错误", () => _acc.Login(Login(pwd: "Wrong1234")));
        await Fails(40000, "手机号或密码错误", () => _acc.Login(Login("13799990000")));
    }

    [Fact]
    public async Task ApproveCreatesEmployeeWhoCanLogInAndGetsBound()
    {
        await _acc.Register(Reg());
        var dto = await _acc.Approve(RegId(), new RegistrationApproveRequest { Role = Role.Engineer, Name = "李新", Department = "售后部门" }, Sup);
        Assert.Equal(RegistrationStatus.Approved, dto.Status);
        Assert.Equal("赵主管", dto.ReviewedByName);
        var emp = Db.Employees.AsNoTracking().Single(e => e.Phone == NewPhone);
        Assert.Equal((Role.Engineer, "李新", "售后部门", true), (emp.Role, emp.Name, emp.Department, emp.Enabled));
        Assert.Null(emp.OpenId);
        Assert.Null(Db.Registrations.AsNoTracking().Single().PasswordHash);
        Assert.Equal(0, (await Admin.Overview()).PendingRegistrations);

        var r = await _acc.Login(Login(code: "phone1"));
        Assert.False(string.IsNullOrEmpty(r.Token));
        Assert.Equal("李新", r.User!.Name);
        Assert.Equal("openid-phone1", Db.Employees.AsNoTracking().Single(e => e.Id == emp.Id).OpenId);

        await Fails(40900, "该申请已处理过，请刷新", () => _acc.Approve(RegId(), new RegistrationApproveRequest { Role = Role.Sales, Department = "销售部门" }, Sup));
    }

    [Fact]
    public async Task ApproveRequiresRole()
    {
        await _acc.Register(Reg());
        await Fails(40000, "请选择角色", () => _acc.Approve(RegId(), new RegistrationApproveRequest(), Sup));
        await Fails(40400, "注册申请不存在", () => _acc.Approve(9999, new RegistrationApproveRequest { Role = Role.Sales, Department = "销售部门" }, Sup));
    }

    [Fact]
    public async Task RejectShowsReasonAndAllowsResubmit()
    {
        await _acc.Register(Reg());
        await Fails(40000, "请填写驳回原因", () => _acc.Reject(RegId(), new RegistrationRejectRequest { Reason = " " }, Sup));
        await _acc.Reject(RegId(), new RegistrationRejectRequest { Reason = "姓名请填真实姓名" }, Sup);
        await Fails(40311, "注册申请未通过：姓名请填真实姓名。可修改后重新提交", () => _acc.Login(Login()));

        await _acc.Register(Reg(name: "李新", pwd: "Newpass99", code: "wxB"));
        var row = Db.Registrations.AsNoTracking().Single();
        Assert.Equal((RegistrationStatus.Pending, "李新", (string?)null), (row.Status, row.Name, row.RejectReason));
        await Fails(40310, "账号正在审核中，请联系管理员", () => _acc.Login(Login(pwd: "Newpass99")));
        await Fails(40000, "手机号或密码错误", () => _acc.Login(Login()));
    }

    [Fact]
    public async Task LoginMovesWeChatBindingAndSurvivesWeChatFailure()
    {
        var wang = Emp("王工");
        var li = Db.Employees.AsNoTracking().First(e => e.Role == Role.Engineer && e.Id != wang.Id);
        await _pwd.SetFor(wang.Id, new SetPasswordRequest { Password = Pwd });
        await _pwd.SetFor(li.Id, new SetPasswordRequest { Password = Pwd });

        await _acc.Login(Login(wang.Phone, code: "same"));
        var before = Db.Employees.AsNoTracking().Single(e => e.Id == wang.Id);
        await _acc.Login(Login(li.Phone, code: "same")); // 同一个微信换登另一个账号
        var wangAfter = Db.Employees.AsNoTracking().Single(e => e.Id == wang.Id);
        Assert.Null(wangAfter.OpenId);
        Assert.Equal(before.TokenVersion, wangAfter.TokenVersion);
        Assert.Equal("openid-same", Db.Employees.AsNoTracking().Single(e => e.Id == li.Id).OpenId);

        _wx.Fail = true;
        Assert.NotNull((await _acc.Login(Login(wang.Phone, code: "x"))).Token); // 微信不可用也能登录
        Assert.NotNull((await _acc.Login(Login(wang.Phone, code: null))).Token);
        Assert.Null(Db.Employees.AsNoTracking().Single(e => e.Id == wang.Id).OpenId);
    }

    [Fact]
    public async Task LoginRejectsDisabledNoPasswordAndLocksAfterFiveFailures()
    {
        var wang = Emp("王工");
        await Fails(40000, "手机号或密码错误", () => _acc.Login(Login(wang.Phone))); // 没设密码
        await _pwd.SetFor(wang.Id, new SetPasswordRequest { Password = Pwd });
        Db.ChangeTracker.Clear();
        var e = Db.Employees.Single(x => x.Id == wang.Id);
        e.Enabled = false;
        await Db.SaveChangesAsync();
        await Fails(40300, "账号已停用，请联系管理员", () => _acc.Login(Login(wang.Phone)));

        for (var i = 0; i < 4; i++) await Fails(40000, "手机号或密码错误", () => _acc.Login(Login(wang.Phone, "Wrong1234")));
        await Fails(40000, "尝试次数过多，请 10 分钟后再试", () => _acc.Login(Login(wang.Phone, "Wrong1234")));
        await Fails(40000, "尝试次数过多，请 10 分钟后再试", () => _acc.Login(Login(wang.Phone)));
    }

    [Fact]
    public async Task LogoutUnbindsWeChat()
    {
        var wang = Emp("王工");
        await _pwd.SetFor(wang.Id, new SetPasswordRequest { Password = Pwd });
        await _acc.Login(Login(wang.Phone, code: "p"));
        await _acc.Logout(U("王工"));
        Assert.Null(Db.Employees.AsNoTracking().Single(e => e.Id == wang.Id).OpenId);
    }

    // ---------- 后台 ----------

    [Fact]
    public async Task ListPutsPendingFirstThenHandled()
    {
        await _acc.Register(Reg("13711110001", "甲"));
        Tick();
        await _acc.Register(Reg("13711110002", "乙", code: "wxB"));
        await _acc.Reject(RegId("13711110001"), new RegistrationRejectRequest { Reason = "不是本公司员工" }, Sup);
        var list = await _acc.List();
        Assert.Equal(1, list.PendingCount);
        Assert.Equal(new[] { "乙", "甲" }, list.Items.Select(x => x.Name));
        Assert.Equal(new[] { "待审批", "已驳回" }, list.Items.Select(x => x.StatusText));
    }

    [Fact]
    public async Task AdminCreateWithInitialPasswordCanLogInAndClearsPendingRegistration()
    {
        await _acc.Register(Reg());
        await Admin.CreateEmployee(new EmployeeCreateRequest { Name = "直接建的", Phone = NewPhone, Role = Role.Sales, Department = "销售部门", Password = "Init1234" });
        Assert.Empty(Db.Registrations.AsNoTracking());
        Assert.Equal("直接建的", (await _acc.Login(Login(pwd: "Init1234"))).User!.Name);
        await Fails(40000, "初始密码需同时包含字母和数字", () =>
            Admin.CreateEmployee(new EmployeeCreateRequest { Name = "某人", Phone = "13711113333", Role = Role.Sales, Department = "销售部门", Password = "12345678" }));
    }

    [Fact]
    public async Task ApproveBlockedWhenPhoneTakenMeanwhile()
    {
        await _acc.Register(Reg());
        var id = RegId();
        Db.Employees.Add(new Employee { Name = "占号", Phone = NewPhone, Role = Role.Sales, Enabled = true, CreatedAt = Clock.Now });
        await Db.SaveChangesAsync();
        await Fails(40900, "该手机号已有账号，无需再审批，可直接驳回这条申请", () =>
            _acc.Approve(id, new RegistrationApproveRequest { Role = Role.Engineer, Department = "售后部门" }, Sup));
        Assert.Equal(RegistrationStatus.Pending, Db.Registrations.AsNoTracking().Single().Status);
    }
}
