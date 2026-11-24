using Huotong.AfterSales.Api.Domain;
using Huotong.AfterSales.Api.Dtos;
using Huotong.AfterSales.Api.Infrastructure;
using Huotong.AfterSales.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Huotong.AfterSales.Tests;

/// <summary>v1.8 小程序注册审批：审批权限、不能授予管理员、新申请提醒</summary>
public sealed class V18ApprovalTests : V11TestBase
{
    private sealed class FakeWx : IWeChatService
    {
        public Task<string> Code2Session(string code) => Task.FromResult("openid-" + code);
        public Task<string> GetPhoneNumber(string phoneCode) => Task.FromResult(phoneCode);
    }

    private readonly AccountService _acc;

    public V18ApprovalTests()
    {
        WxOptions.Subscribe.Register.TemplateId = "tmpl-register";
        WxOptions.Subscribe.Register.Fields = new() { ["thing1"] = "Customer", ["phone_number2"] = "OrderNo", ["time3"] = "Time", ["thing4"] = "Content" };
        var tokens = new TokenService(Options.Create(new JwtOptions { Key = "test-key-0123456789-0123456789-0123456789" }));
        _acc = new AccountService(Db, new FakeWx(), tokens, View, new LoginThrottle(Clock), new RegisterThrottle(Clock), Clock,
            NullLogger<AccountService>.Instance, Subscribe);
    }

    private static RegisterRequest Reg(string phone = "13755550001", string name = "新同事") =>
        new() { Name = name, Phone = phone, Password = "Abcd1234", Code = "applicant" };

    private async Task<Employee> Grant(string name, bool canApprove = true, string? openId = "wx-approver")
    {
        var e = Db.Employees.Single(x => x.Name == name);
        e.CanApprove = canApprove;
        e.OpenId = openId;
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
        return e;
    }

    [Fact]
    public async Task NewRegistrationNotifiesOnlyPeopleWithApprovalSwitch()
    {
        await Grant("王工");                              // 开了注册审批权限、已绑定微信
        await Grant("赵主管", canApprove: false, "wx-admin"); // 只是管理员：能审批，但不提醒
        await Grant("李工", canApprove: true, openId: null);  // 开了但没绑定微信：发不了
        Subscribe.ClearMockSent();

        await _acc.Register(Reg());
        var sent = Assert.Single(Subscribe.MockSent);
        Assert.Equal(("wx-approver", "tmpl-register", "pages/approvals/index"), (sent.OpenId, sent.TemplateId, sent.Page));
        Assert.Equal("新同事", sent.Data["thing1"]);
        Assert.Equal("13755550001", sent.Data["phone_number2"]);
        Assert.Equal("新同事 申请注册账号，请审批", sent.Data["thing4"]);
    }

    [Fact]
    public async Task NoTemplateConfiguredMeansNoReminderButRegistrationStillWorks()
    {
        WxOptions.Subscribe.Register.TemplateId = "";
        await Grant("王工");
        Subscribe.ClearMockSent();
        Assert.Equal(RegistrationStatus.Pending, (await _acc.Register(Reg())).Status);
        Assert.Empty(Subscribe.MockSent);
    }

    [Fact]
    public async Task RegisterTemplateOnlyOfferedToPeopleWithApprovalSwitch()
    {
        await Grant("王工");
        Assert.Equal(new[] { "tmpl-register" }, await _acc.RegisterTemplateIdsFor(U("王工")));
        Assert.Empty(await _acc.RegisterTemplateIdsFor(U("赵主管"))); // 管理员没单独开：不申请这个模板
        Assert.Empty(await _acc.RegisterTemplateIdsFor(U("李工")));
    }

    [Fact]
    public async Task MiniProgramApprovalCannotGrantAdmin()
    {
        await _acc.Register(Reg());
        var id = Db.Registrations.AsNoTracking().Single().Id;
        var approver = U("王工");
        await Fails(40000, "管理员权限请到电脑后台设置", () => _acc.Approve(id,
            new RegistrationApproveRequest { Position = Position.Admin, Department = "总经办" }, approver, allowAdmin: false));
        await Fails(40000, "管理员权限请到电脑后台设置", () => _acc.Approve(id,
            new RegistrationApproveRequest { Position = Position.Supervisor, Department = "售后部门", IsAdmin = true }, approver, allowAdmin: false));

        var dto = await _acc.Approve(id, new RegistrationApproveRequest { Position = Position.SalesManager, Department = "销售部门" }, approver, allowAdmin: false);
        Assert.Equal((RegistrationStatus.Approved, "王工"), (dto.Status, dto.ReviewedByName));
        var emp = Db.Employees.AsNoTracking().Single(e => e.Phone == "13755550001");
        Assert.Equal((Position.SalesManager, Role.Sales, false, false), (emp.Position, emp.Role, emp.IsAdmin, emp.CanApprove));
    }

    [Fact]
    public async Task UserDtoAndMeExposeApprovalAbility()
    {
        await _acc.Register(Reg());
        await Grant("王工");
        var wang = await Home.Me(U("王工"));
        Assert.True(wang.User.CanApprove);
        Assert.Equal(1, wang.PendingRegistrations);
        var admin = await Home.Me(Sup); // 管理员自带审批能力
        Assert.True(admin.User.CanApprove);
        Assert.Equal(1, admin.PendingRegistrations);
        var li = await Home.Me(U("李工"));
        Assert.False(li.User.CanApprove);
        Assert.Null(li.PendingRegistrations);
    }

    [Fact]
    public async Task AdminCanToggleApprovalSwitchAndOldClientsKeepIt()
    {
        var wang = Emp("王工");
        var on = await Admin.UpdateEmployee(wang.Id, new EmployeeUpdateRequest
            { Name = wang.Name, Phone = wang.Phone, Position = Position.Engineer, Department = wang.Department, CanApprove = true }, Sup);
        Assert.True(on.CanApprove);
        var kept = await Admin.UpdateEmployee(wang.Id, new EmployeeUpdateRequest
            { Name = wang.Name, Phone = wang.Phone, Position = Position.Engineer, Department = wang.Department }, Sup);
        Assert.True(kept.CanApprove);
        var off = await Admin.UpdateEmployee(wang.Id, new EmployeeUpdateRequest
            { Name = wang.Name, Phone = wang.Phone, Position = Position.Engineer, Department = wang.Department, CanApprove = false }, Sup);
        Assert.False(off.CanApprove);
    }
}
