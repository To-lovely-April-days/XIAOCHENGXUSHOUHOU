using Huotong.AfterSales.Api.Domain;
using Huotong.AfterSales.Api.Dtos;
using Huotong.AfterSales.Api.Infrastructure;
using Huotong.AfterSales.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Huotong.AfterSales.Tests;

/// <summary>v1.7 岗位（销售主管 / 管理员）、固定部门、管理员权限</summary>
public sealed class V17PositionTests : V11TestBase
{
    private const string Pwd = "Abcd1234";
    private readonly PasswordService _pwd;
    private readonly TokenService _tokens = new(Options.Create(new JwtOptions { Key = "test-key-0123456789-0123456789-0123456789" }));

    public V17PositionTests() => _pwd = new PasswordService(Db, _tokens, View, new LoginThrottle(Clock));

    private static EmployeeCreateRequest New(string name, string phone, Position position, string dept, bool? admin = null) =>
        new() { Name = name, Phone = phone, Position = position, Department = dept, IsAdmin = admin, Password = Pwd };

    private Employee ById(int id) => Db.Employees.AsNoTracking().Single(e => e.Id == id);

    [Fact]
    public async Task PositionsMapToPermissionRoles()
    {
        var sm = await Admin.CreateEmployee(New("钱主管", "13722220001", Position.SalesManager, "销售部门"));
        var ad = await Admin.CreateEmployee(New("周总", "13722220002", Position.Admin, "总经办"));
        var sup = await Admin.CreateEmployee(New("吴主管", "13722220003", Position.Supervisor, "售后部门"));
        Assert.Equal((Role.Sales, "销售主管", false), (sm.Role, sm.RoleName, sm.IsAdmin));
        Assert.Equal((Role.Supervisor, "管理员", true), (ad.Role, ad.RoleName, ad.IsAdmin)); // 管理员岗位自带管理员权限
        Assert.Equal((Role.Supervisor, "售后主管", false), (sup.Role, sup.RoleName, sup.IsAdmin));
        Assert.True(ById(sm.Id).IsSalesManager);
    }

    [Fact]
    public async Task DepartmentMustBeOneOfTheFixedList()
    {
        await Fails(40000, "请选择部门", () => Admin.CreateEmployee(New("甲", "13722220011", Position.Engineer, "")));
        await Fails(40000, "部门不在可选范围内", () => Admin.CreateEmployee(New("甲", "13722220011", Position.Engineer, "技术部")));
        foreach (var (d, i) in Departments.All.Select((d, i) => (d, i)))
            Assert.Equal(d, (await Admin.CreateEmployee(New("同事" + i, "1372222002" + i, Position.Engineer, d))).Department);
    }

    [Fact]
    public async Task EditingKeepsLegacyDepartmentWhenUnchanged()
    {
        var wang = Emp("王工"); // 种子数据里是旧写法「售后部」
        var dto = await Admin.UpdateEmployee(wang.Id, new EmployeeUpdateRequest { Name = "王工", Phone = wang.Phone, Role = Role.Engineer, Department = wang.Department }, Sup);
        Assert.Equal(wang.Department, dto.Department);
        await Fails(40000, "部门不在可选范围内", () => Admin.UpdateEmployee(wang.Id,
            new EmployeeUpdateRequest { Name = "王工", Phone = wang.Phone, Role = Role.Engineer, Department = "研发部" }, Sup));
    }

    [Fact]
    public async Task OnlyAdminsCanLogInToPcBackend()
    {
        var sup = await Admin.CreateEmployee(New("吴主管", "13722220003", Position.Supervisor, "售后部门"));
        await Fails(40300, "该账号没有电脑后台的管理员权限", () => _pwd.Login(new PasswordLoginRequest { Phone = "13722220003", Password = Pwd }));

        var before = ById(sup.Id).TokenVersion;
        var dto = await Admin.UpdateEmployee(sup.Id, new EmployeeUpdateRequest
            { Name = "吴主管", Phone = "13722220003", Position = Position.Supervisor, Department = "售后部门", IsAdmin = true }, Sup);
        Assert.True(dto.IsAdmin);
        Assert.Equal("售后主管", dto.RoleName);
        Assert.Equal(before + 1, ById(sup.Id).TokenVersion); // 权限变了，旧登录失效
        var r = await _pwd.Login(new PasswordLoginRequest { Phone = "13722220003", Password = Pwd });
        Assert.True(r.User!.IsAdmin);

        var engineerAdmin = await Admin.CreateEmployee(New("软件同事", "13722220004", Position.Engineer, "软件部门", admin: true));
        Assert.True(engineerAdmin.IsAdmin);
        Assert.NotNull((await _pwd.Login(new PasswordLoginRequest { Phone = "13722220004", Password = Pwd })).Token);
    }

    [Fact]
    public async Task AdminCannotDropOwnAdminRights()
    {
        var me = Emp("赵主管");
        await Fails(40900, "不能停用自己、修改自己的角色或取消自己的管理员权限", () => Admin.UpdateEmployee(me.Id,
            new EmployeeUpdateRequest { Name = me.Name, Phone = me.Phone, Position = Position.Supervisor, Department = me.Department, IsAdmin = false }, Sup));
        // 旧客户端不传 isAdmin：保持原样
        var dto = await Admin.UpdateEmployee(me.Id, new EmployeeUpdateRequest { Name = me.Name, Phone = me.Phone, Role = Role.Supervisor, Department = me.Department }, Sup);
        Assert.True(dto.IsAdmin);
    }

    [Fact]
    public async Task SalesManagerSeesAllSalesOrdersButSalesSeesOnlyOwn()
    {
        var sm = await Admin.CreateEmployee(New("钱主管", "13722220001", Position.SalesManager, "销售部门"));
        var bySales = await NewPending("销售建的单");                    // 王敏（销售）建
        var bySup = (await Orders.Create(NewOrder("主管建的单"), Sup)).Id; // 主管建
        var manager = new CurrentUser(sm.Id, Role.Sales, SalesManager: true);
        var mine = (await Orders.Create(NewOrder("销售主管自己建的单"), manager)).Id;

        var seen = (await Orders.Created(manager, "all")).Items.Select(x => x.Id).ToList();
        Assert.Contains(bySales, seen);
        Assert.Contains(mine, seen);
        Assert.DoesNotContain(bySup, seen);

        var plainSales = (await Orders.Created(U("王敏"), "all")).Items.Select(x => x.Id).ToList();
        Assert.Contains(bySales, plainSales);
        Assert.DoesNotContain(mine, plainSales);
        Assert.True((await Home.Me(manager)).ActiveOrderCount >= 2);
    }

    [Fact]
    public async Task TokenCarriesAdminAndSalesManagerFlags()
    {
        var sm = await Admin.CreateEmployee(New("钱主管", "13722220001", Position.SalesManager, "销售部门"));
        var handler = new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler();
        var t1 = handler.ReadJsonWebToken(_tokens.CreateUserToken(ById(sm.Id)));
        Assert.Equal(("Sales", "1", "0"), (t1.GetClaim("role").Value, t1.GetClaim("sm").Value, t1.GetClaim("adm").Value));
        var t2 = handler.ReadJsonWebToken(_tokens.CreateUserToken(Emp("赵主管")));
        Assert.Equal(("Supervisor", "0", "1"), (t2.GetClaim("role").Value, t2.GetClaim("sm").Value, t2.GetClaim("adm").Value));
    }
}
