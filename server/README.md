# 霍桐售后工单 · 后端（.NET 8）

ASP.NET Core 8 Web API + EF Core 8，默认 SQLite（零配置可跑），可切 SQL Server。
接口契约见 `../docs/API.md`，业务规则见 `../docs/DESIGN.md`。

```
server/
├── Huotong.AfterSales.sln
├── .config/dotnet-tools.json        本地工具（dotnet-ef 8.0.11）
├── src/Huotong.AfterSales.Api/
│   ├── Domain/          实体、枚举、BizException
│   ├── Data/            AppDbContext、Migrations、SeedData（演示数据）
│   ├── Services/        业务服务；纯逻辑：PunchCalculator / OrderStateMachine / CompletionValidator / GeoUtil / AddressParser / OrderNoGenerator
│   ├── Controllers/     API 控制器
│   ├── Dtos/            请求/响应 DTO（字段与 API.md 一致）
│   └── Infrastructure/  统一响应、异常中间件、JSON 转换器、配置项
├── tests/Huotong.AfterSales.Tests/  xUnit 单测 + SQLite 内存库服务测试
└── scripts/smoke.sh     curl + python3 冒烟脚本
```

## 运行

```bash
export PATH="$HOME/.dotnet:$PATH"
cd server
dotnet build
dotnet test
cd src/Huotong.AfterSales.Api && dotnet run      # Development：监听 http://0.0.0.0:5080
```

- 监听 `0.0.0.0:5080`，小程序真机调试用电脑局域网 IP 访问：`http://192.168.x.x:5080/api`（开发者工具需勾选「不校验合法域名」）。
- Swagger（仅 Development）：`http://localhost:5080/swagger`。
- 启动时自动执行 EF 迁移；Development 下数据库为空时自动写入演示数据。
- 冒烟测试（服务启动后）：`./scripts/smoke.sh`，可用 `BASE=http://host:5080` 指定地址。建议在新库上运行（删掉数据库文件重启即可）；重复运行时「补录合肥缺卡」一步会自动跳过。

### 演示账号（Development，MockMode）

用 `POST /api/auth/dev-login {"phone": "..."}` 直接登录：

| 姓名 | 角色 | 手机号 |
|---|---|---|
| 赵主管 | Supervisor | 13900000001 |
| 李工 | Engineer | 13900000000 |
| 陈工 / 刘工 / 王工 | Engineer | 13900000002 / 03 / 04 |
| 王敏 / 孙磊 | Sales | 13900000010 / 11 |

演示工单按原型 16 页的 9 张单 + 李工 10 月一张已闭环单构造。原型的「今天」是 2026-11-06，种子数据按「北京时间今天 − 2026-11-06」整体平移日期（包括工单号日期部分），所以任何一天启动，演示数据都「是今天」。数据只在**数据库为空**时写入；想重置就删掉数据库文件和 `uploads/` 再启动。

## 配置项（appsettings.json / 环境变量）

| 键 | 默认 | 说明 |
|---|---|---|
| `Urls` | `http://0.0.0.0:5080` | 监听地址 |
| `Database:Provider` | `Sqlite` | `Sqlite` 或 `SqlServer` |
| `ConnectionStrings:Default` | `Data Source=data/aftersales.db` | 相对路径按项目目录（ContentRoot）解析，目录不存在会自动创建 |
| `Jwt:Key` | 示例值 | **生产必须修改**，≥ 32 字符随机串 |
| `Jwt:Issuer` / `Jwt:ExpireDays` | `huotong-aftersales` / `30` | token 有效期 30 天；每次请求复核账号（不存在/停用/角色变化/`Employee.TokenVersion` 变化即 40100），解绑、停用、改角色、改手机号会递增 TokenVersion 使旧 token 立即失效 |
| `WeChat:AppId` / `WeChat:AppSecret` | 空 | 小程序 AppID / AppSecret |
| `WeChat:MockMode` | 生产 `false`，Development `true` | 见下文 |
| `DingTalk:Webhook` / `DingTalk:Secret` | 空 | 钉钉群机器人；为空时通知只写日志。Secret 为「加签」密钥（可选） |
| `PublicBaseUrl` | 空 | 文件绝对地址前缀，如 `https://api.example.com`；为空用请求的 scheme+host |
| `Cors:Origins` | `[]` | 允许的来源；为空允许任意来源（供后续 PC 后台使用） |
| `Seed:Enabled` | 生产 `false`，Development `true` | 空库时写入演示数据 |

环境变量写法：`Database__Provider=SqlServer`、`WeChat__AppSecret=xxx`。

> macOS 默认文件系统不区分大小写，`data/aftersales.db` 实际会落在源码目录 `Data/` 下（与 `AppDbContext.cs` 同目录）。这不影响运行，`*.db` 已在 `.gitignore` 中；需要的话可把连接串改成 `Data Source=App_Data/aftersales.db`。

## 切换 SQL Server

```json
"Database": { "Provider": "SqlServer" },
"ConnectionStrings": { "Default": "Server=.;Database=AfterSales;User Id=sa;Password=...;TrustServerCertificate=True" }
```

现有迁移（`Data/Migrations/*_Initial`、`*_AddEmployeeTokenVersion`）是按 SQLite 生成的。SqlServer 模式下启动时用 `EnsureCreated()` 直接按模型建表（不走迁移）。如果要在 SQL Server 上长期用迁移，建议单独建一个迁移项目，用 `dotnet ef migrations add ... -- --Database:Provider=SqlServer` 生成。

所有时间都以 `DateTime`（北京时间、无时区）存储，金额在库里存 double（SQLite 不能对 decimal 排序/聚合）。

## 微信配置

1. 在 `appsettings.Production.json` 或环境变量里填 `WeChat:AppId`、`WeChat:AppSecret`，`WeChat:MockMode=false`。
2. 小程序后台「开发管理 → 服务器域名」把 API 域名加到 request / uploadFile / downloadFile 合法域名（必须 HTTPS）。
3. 登录流程：`wx.login` → `POST /api/auth/login {code}`（服务端调 `jscode2session` 取 openid）→ 未绑定时返回 `bindTicket`（10 分钟有效）→ 手机号快速验证 `getPhoneNumber` 拿到 code → `POST /api/auth/bind {bindTicket, phoneCode}`（服务端调 `wxa/business/getuserphonenumber`，access_token 在内存缓存，失效时自动刷新一次）。
4. MockMode=true 时：`login` 的 code 直接作为 openid（加 `mock-` 前缀），`bind` 的 phoneCode 直接作为手机号，并开放 `/api/auth/dev-login`（MockMode=false 时该接口返回 404）。

## 文件

`POST /api/files`（multipart：`file` + `category`）存到 `<项目目录>/uploads/yyyy/MM/{guid}{ext}`，通过 `/uploads/...` 静态访问（无需认证）。只允许 `image/*`、`video/*`，单个 ≤ 20MB。小程序 `wx.uploadFile` 的 Content-Type 可能是 `application/octet-stream`，这时按扩展名推断类型。

## 导出

`GET /api/admin/export/orders`、`/api/admin/export/hours` 用 ClosedXML 生成 `.xlsx`（`Content-Disposition: attachment`，文件名是 UTF-8 编码的中文名）。

## 实现说明

- 统一响应 `{code, message, data}`；业务异常 `BizException(code, message)` 由中间件映射为 HTTP `code/100`；模型校验失败返回 40000；401/403 由 JwtBearer 事件输出包装体；未匹配的 `/api/*` 返回 40400。
- JSON：camelCase、枚举字符串、时间 `yyyy-MM-ddTHH:mm:ss`、日期 `yyyy-MM-dd`；`counts` 字典的 key 原样输出（如 `Pending`）。
- 业务时间都来自 `IClock`（北京时间，`Asia/Shanghai`，找不到时用 `China Standard Time`），测试里可以替换成 `FixedClock`。
- 工单号 `SH-yyyyMMdd-NNN`：在 Serializable 事务里取当日最大号再 +1，有唯一索引兜底，冲突时重试。
- 认领用条件更新（`WHERE Status='Pending'`），并发认领时只有一个人能成功，其他人收到 409「工单已被他人认领」。
