using System.Text.RegularExpressions;
using Huotong.AfterSales.Api.Data;
using Huotong.AfterSales.Api.Infrastructure;
using Huotong.AfterSales.Api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// ---------- 配置 ----------
builder.Services.Configure<JwtOptions>(config.GetSection("Jwt"));
builder.Services.Configure<WeChatOptions>(config.GetSection("WeChat"));
builder.Services.Configure<DingTalkOptions>(config.GetSection("DingTalk"));
builder.Services.Configure<AppSettings>(config);
var jwt = config.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();
if (string.IsNullOrWhiteSpace(jwt.Key) || jwt.Key.Length < 32)
    throw new InvalidOperationException("Jwt:Key 未配置或长度不足 32 位");
if (!builder.Environment.IsDevelopment() && jwt.Key.StartsWith("CHANGE-ME"))
    throw new InvalidOperationException("正式环境必须设置自己的 Jwt:Key（环境变量 Jwt__Key，≥32 位随机字符串）");
var wxOpt = config.GetSection("WeChat").Get<WeChatOptions>() ?? new WeChatOptions();
if (!wxOpt.MockMode && (string.IsNullOrWhiteSpace(wxOpt.AppId) || string.IsNullOrWhiteSpace(wxOpt.AppSecret)))
    Console.Error.WriteLine("警告：WeChat:AppId / WeChat:AppSecret 未配置，小程序微信登录与手机号验证将失败。" +
        "本地开发：dotnet user-secrets set \"WeChat:AppSecret\" \"<密钥>\"；服务器：环境变量 WeChat__AppSecret。");

// ---------- 数据库 ----------
var provider = config["Database:Provider"] ?? "Sqlite";
var connStr = config.GetConnectionString("Default") ?? "Data Source=data/aftersales.db";
var isSqlite = !provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase);
if (isSqlite)
{
    // 相对路径按 ContentRoot 解析，并自动建目录
    var csb = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connStr);
    if (!Path.IsPathRooted(csb.DataSource) && csb.DataSource != ":memory:")
        csb.DataSource = Path.Combine(builder.Environment.ContentRootPath, csb.DataSource);
    var dbDir = Path.GetDirectoryName(csb.DataSource);
    if (!string.IsNullOrEmpty(dbDir)) Directory.CreateDirectory(dbDir);
    connStr = csb.ToString();
}
builder.Services.AddDbContext<AppDbContext>(o =>
{
    if (isSqlite) o.UseSqlite(connStr);
    else o.UseSqlServer(connStr);
});

// ---------- 服务 ----------
builder.Services.AddSingleton<IClock, BeijingClock>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<IWeChatService, WeChatService>();
builder.Services.AddSingleton<INotifier, DingTalkRobotNotifier>();
builder.Services.AddSingleton<TokenService>();
builder.Services.AddScoped<FileService>();
builder.Services.AddScoped<OrderViewBuilder>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<OrderService>();
builder.Services.AddScoped<PunchService>();
builder.Services.AddScoped<HoursService>();
builder.Services.AddScoped<HomeService>();
builder.Services.AddScoped<AdminService>();
builder.Services.AddScoped<AdminReportService>();
builder.Services.AddScoped<PasswordService>();
builder.Services.AddSingleton<LoginThrottle>();

builder.Services.AddControllers()
    .AddJsonOptions(o => JsonSetup.Configure(o.JsonSerializerOptions))
    .ConfigureApiBehaviorOptions(o =>
    {
        // 模型校验失败 → 40000 + 第一条中文错误
        o.InvalidModelStateResponseFactory = ctx =>
        {
            var first = ctx.ModelState.Where(kv => kv.Value?.Errors.Count > 0)
                .Select(kv => (Key: kv.Key, Msg: kv.Value!.Errors[0].ErrorMessage)).FirstOrDefault();
            var msg = first.Msg;
            if (string.IsNullOrEmpty(msg) || !Regex.IsMatch(msg, @"[一-龥]"))
            {
                var field = (first.Key ?? "").TrimStart('$', '.');
                msg = string.IsNullOrEmpty(field) ? "请求参数格式错误" : $"参数 {field} 格式不正确";
            }
            return new BadRequestObjectResult(ApiResponse.Fail(40000, msg));
        };
    });
builder.Services.ConfigureHttpJsonOptions(o => JsonSetup.Configure(o.SerializerOptions));
builder.Services.Configure<Microsoft.AspNetCore.Mvc.MvcOptions>(o => o.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true);

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.MapInboundClaims = false;
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidIssuer = jwt.Issuer,
        ValidAudience = "huotong-app",
        IssuerSigningKey = TokenService.SigningKey(jwt.Key),
        NameClaimType = ClaimsExtensions.UserIdClaim,
        RoleClaimType = ClaimsExtensions.RoleClaim,
        ClockSkew = TimeSpan.FromMinutes(1)
    };
    o.Events = new JwtBearerEvents
    {
        OnChallenge = async ctx =>
        {
            ctx.HandleResponse();
            await ExceptionMiddleware.Write(ctx.HttpContext, 401, 40100, "登录已失效，请重新登录");
        },
        OnForbidden = ctx => ExceptionMiddleware.Write(ctx.HttpContext, 403, 40300, "无权限"),
        // 每次请求复核账号：不存在 / 已停用 / 角色已变 / 令牌版本已变（解绑等）→ 认证失败 → OnChallenge 返回 40100
        OnTokenValidated = async ctx =>
        {
            var p = ctx.Principal;
            var idText = p?.FindFirst(ClaimsExtensions.UserIdClaim)?.Value;
            var roleText = p?.FindFirst(ClaimsExtensions.RoleClaim)?.Value;
            var tvText = p?.FindFirst(ClaimsExtensions.TokenVersionClaim)?.Value ?? "0";
            if (!int.TryParse(idText, out var uid)) { ctx.Fail("account invalid"); return; }
            var db = ctx.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
            var emp = await db.Employees.AsNoTracking().Where(e => e.Id == uid)
                .Select(e => new { e.Enabled, e.Role, e.TokenVersion }).FirstOrDefaultAsync();
            if (emp == null || !emp.Enabled || emp.Role.ToString() != roleText || emp.TokenVersion.ToString() != tvText)
                ctx.Fail("account invalid");
        }
    };
});
builder.Services.AddAuthorization();

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
{
    var origins = config.GetSection("Cors:Origins").Get<string[]>() ?? Array.Empty<string>();
    if (origins.Length == 0) p.AllowAnyOrigin(); else p.WithOrigins(origins);
    p.AllowAnyHeader().AllowAnyMethod().WithExposedHeaders("Content-Disposition");
}));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "霍桐售后工单 API", Version = "v1" });
    c.MapType<DateTime>(() => new OpenApiSchema { Type = "string", Example = new Microsoft.OpenApi.Any.OpenApiString("2026-11-06T10:05:00") });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT", In = ParameterLocation.Header, Name = "Authorization"
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        { new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }, Array.Empty<string>() }
    });
    c.CustomSchemaIds(t => t.FullName);
});

var app = builder.Build();

// ---------- 初始化数据库 ----------
var uploadRoot = Path.Combine(app.Environment.ContentRootPath, "uploads");
Directory.CreateDirectory(uploadRoot);
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    if (db.Database.IsSqlite())
    {
        db.Database.Migrate();
    }
    else
    {
        // 迁移基于 SQLite 生成，SQL Server 直接按模型建表
        db.Database.EnsureCreated();
    }
    if (config.GetValue<bool>("Seed:Enabled"))
    {
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();
        await SeedData.RunAsync(db, uploadRoot, clock.Now, config["Seed:SupervisorPassword"]);
    }
    var startupLog = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    // 正式环境首次启动：没有任何主管时，按 Bootstrap:SupervisorName/SupervisorPhone 建第一个主管
    var first = await PasswordService.BootstrapFirstSupervisor(db, config["Bootstrap:SupervisorName"],
        config["Bootstrap:SupervisorPhone"], config["Bootstrap:SupervisorPassword"],
        scope.ServiceProvider.GetRequiredService<IClock>().Now);
    if (first.Error != null) startupLog.LogWarning("初始主管未创建：{Error}", first.Error);
    else if (first.Created) startupLog.LogInformation("已创建初始主管 {Name}", config["Bootstrap:SupervisorName"]);
    // 已有数据库首次开通 PC 后台：给未设密码的主管设上 Bootstrap:SupervisorPassword
    var bootstrap = await PasswordService.BootstrapSupervisors(db, config["Bootstrap:SupervisorPassword"]);
    if (bootstrap.Error != null)
        startupLog.LogWarning("Bootstrap:SupervisorPassword 未生效：{Error}", bootstrap.Error);
    else if (bootstrap.Count > 0)
        startupLog.LogInformation("已按 Bootstrap:SupervisorPassword 为 {Count} 个未设密码的主管设置初始密码", bootstrap.Count);
}

app.UseMiddleware<ExceptionMiddleware>();
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
app.UseCors();
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(uploadRoot),
    RequestPath = "/uploads"
});

// PC 主管后台静态页面：wwwroot/admin/ → /admin/（匿名访问，位于认证中间件之前；页面内的 /api 调用自带 token）
var adminRoot = Path.Combine(app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot"), "admin");
Directory.CreateDirectory(adminRoot);
var adminFiles = new PhysicalFileProvider(adminRoot);
app.Use(async (ctx, next) =>
{
    // /admin → /admin/（保留查询串），保证页面里的相对路径正确
    if (ctx.Request.Path.Value == "/admin" && (HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method)))
    {
        ctx.Response.Redirect("/admin/" + ctx.Request.QueryString);
        return;
    }
    await next();
});
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = adminFiles, RequestPath = "/admin" });
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = adminFiles,
    RequestPath = "/admin",
    OnPrepareResponse = c =>
    {
        // 页面入口与脚本/样式每次向服务器校验（ETag，未变化返回 304），发布新版本后立即生效。
        // 后台是无构建的 ES 模块：若浏览器把新旧模块混用（旧 enums.js + 新视图），整页会白屏，所以 js/css 也不能走启发式缓存。
        var name = c.File.Name;
        if (name.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".css", StringComparison.OrdinalIgnoreCase))
            c.Context.Response.Headers.CacheControl = "no-cache";
    }
});
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapGet("/", () => Results.Ok(ApiResponse.Ok(new { name = "霍桐售后工单 API", status = "ok" }))).ExcludeFromDescription();

// 未匹配的 /api 路由也返回包装体
app.MapFallback("/api/{**path}", (HttpContext ctx) => ExceptionMiddleware.Write(ctx, 404, 40400, "接口不存在"));

app.Run();

public partial class Program { }
