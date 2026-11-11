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

PC 后台（`http://localhost:5080/admin/`）用赵主管 `13900000001` / `Huotong@2026` 登录（`POST /api/auth/password-login`）。

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
| `Seed:SupervisorPassword` | 生产空，Development `Huotong@2026` | 写入演示数据时赵主管的 PC 后台初始密码（为空则不设密码） |
| `Bootstrap:SupervisorPassword` | 空 | 启动时给所有**未设密码**的 Supervisor 设上该密码（已有密码的不覆盖），用于已有数据库的生产环境首次开通 PC 后台；不满足强度规则时只记警告日志、不设置 |

环境变量写法：`Database__Provider=SqlServer`、`WeChat__AppSecret=xxx`、`Bootstrap__SupervisorPassword=xxx`。

> macOS 默认文件系统不区分大小写，`data/aftersales.db` 实际会落在源码目录 `Data/` 下（与 `AppDbContext.cs` 同目录）。这不影响运行，`*.db` 已在 `.gitignore` 中；需要的话可把连接串改成 `Data Source=App_Data/aftersales.db`。

## PC 主管后台（API.md §9）

- 静态页面放在 `src/Huotong.AfterSales.Api/wwwroot/admin/`，由后端托管在 `/admin/`（`UseDefaultFiles` + `UseStaticFiles`，`/admin` 302 到 `/admin/`，`.html` 返回 `Cache-Control: no-cache`）。静态中间件位于认证之前，页面本身匿名可访问；页面里调用 `/api/*` 时带 `Authorization`。与 API 同源，无需 CORS。`/uploads` 映射不受影响。
- 只有 `Supervisor` 能密码登录。`Employee.PasswordHash` 用 ASP.NET Core `PasswordHasher<Employee>`（PBKDF2）；为空 = 未设置密码，不能在 PC 登录。
- 密码规则：8–64 位，同时含字母和数字。`PUT /api/me/password` 改自己的密码（已设置过时需原密码）；主管可用 `POST /api/admin/employees/{id}/password` 为任何员工设置/重置密码（同时解除该手机号的登录锁定）。改密/重置**不会**使已签发的 token 失效。
- 登录失败计数在内存里（单实例，重启清零）：同一手机号连续 5 次失败（账号不存在/未设密码/密码错误）锁定 10 分钟，第 5 次失败即返回「尝试次数过多，请 10 分钟后再试」；登录成功清零。
- **生产环境首次开通**：已有数据库不会再跑种子数据，启动时设置环境变量 `Bootstrap__SupervisorPassword=<初始密码>`，所有未设密码的主管会被设上该密码（日志里有「已按 Bootstrap:SupervisorPassword 为 N 个未设密码的主管设置初始密码」）。登录后请立即在后台修改密码，然后去掉该环境变量。
- `GET /api/admin/punches`、`GET /api/admin/hours` 复用 `PunchCalculator.Pair` / `HoursService.Month`，缺卡判定与工单 `MissingPunch` 一致（早于今天、没有配对离场的到场卡）。

## 切换 SQL Server

```json
"Database": { "Provider": "SqlServer" },
"ConnectionStrings": { "Default": "Server=.;Database=AfterSales;User Id=sa;Password=...;TrustServerCertificate=True" }
```

现有迁移（`Data/Migrations/*_Initial`、`*_AddEmployeeTokenVersion`、`*_AddEmployeePassword`）是按 SQLite 生成的。SqlServer 模式下启动时用 `EnsureCreated()` 直接按模型建表（不走迁移）。如果要在 SQL Server 上长期用迁移，建议单独建一个迁移项目，用 `dotnet ef migrations add ... -- --Database:Provider=SqlServer` 生成。

所有时间都以 `DateTime`（北京时间、无时区）存储，金额在库里存 double（SQLite 不能对 decimal 排序/聚合）。

## 微信配置

1. 在 `appsettings.Production.json` 或环境变量里填 `WeChat:AppId`、`WeChat:AppSecret`，`WeChat:MockMode=false`。
2. 小程序后台「开发管理 → 服务器域名」把 API 域名加到 request / uploadFile / downloadFile 合法域名（必须 HTTPS）。
3. 登录流程：`wx.login` → `POST /api/auth/login {code}`（服务端调 `jscode2session` 取 openid）→ 未绑定时返回 `bindTicket`（10 分钟有效）→ 手机号快速验证 `getPhoneNumber` 拿到 code → `POST /api/auth/bind {bindTicket, phoneCode}`（服务端调 `wxa/business/getuserphonenumber`，access_token 在内存缓存，失效时自动刷新一次）。
4. MockMode=true 时：`login` 的 code 直接作为 openid（加 `mock-` 前缀），`bind` 的 phoneCode 直接作为手机号，并开放 `/api/auth/dev-login`（MockMode=false 时该接口返回 404）。

## 文件

`POST /api/files`（multipart：`file` + `category` + 可选 `fileName`）存到 `<项目目录>/uploads/yyyy/MM/{guid}{ext}`，通过 `/uploads/...` 静态访问（无需认证，文件名是随机 GUID；响应带 `X-Content-Type-Options: nosniff`）。

- **类型按内容识别**（`FileService`，契约见 `docs/CHANGES-v1.2.md` §3.2），不信任客户端的 Content-Type：所有类别都接受图片 / 视频（JPEG、PNG、GIF、BMP、WebP、HEIC、MP4 / MOV / M4V / 3GP，读文件头，扩展名缺失或不对时用规范扩展名）；只有 `category=Attachment`（v1.2 工单附件）另外接受文档：`.pdf`、`.doc/.docx`、`.xls/.xlsx`、`.ppt/.pptx`、`.txt`、`.csv`（扩展名 + 内容都要过：PDF 头、OLE 头或含 `[Content_Types].xml` 的 ZIP、文本不含 NUL）。可执行文件、脚本、HTML、SVG、压缩包等一律拒绝。
- **大小**：图片与非附件类别 ≤ 20MB（`FileService.MaxBytes`）；附件里的视频、文档 ≤ 100MB（`FileService.MaxAttachmentBytes`）。上传接口用 `[RequestSizeLimit]` 把请求体上限单独提到 101MB，其他接口仍受 `appsettings.json` 里 Kestrel 30MB 的全局上限约束。**nginx 的 `client_max_body_size` 必须 ≥ 110m**（见 `deploy/README.md`），否则大文件在 nginx 就被 413 拒掉。
- **文件名**：`fileName` 清洗后存到 `StoredFiles.DisplayName`，作为展示名 / 下载名；没有时由服务端生成（如 `打卡照片_20261106-100500.jpg`）。`FileDto` 带 `name / size / kind / ext`。
- **鉴权下载**：`GET /api/files/{id}/download`（`Authorization: Bearer`，不支持 query token）返回原文件，`Content-Disposition: attachment` 带 UTF-8 原始文件名，支持 Range。权限：能查看所属工单；受限附件对工程师一律 404；未挂工单的文件只有上传人能取；头像不走这个接口。

### 工单附件与文件中心（v1.2）

- 「附件」= 挂在工单上、`Category` 为 `OrderPhoto`（建单照片 / v1.1 客户端）或 `Attachment` 的未删除文件，每单最多 20 个。接口：建单请求体的 `attachments`、`POST /api/orders/{id}/attachments`、`.../attachments/{fileId}/delete`、`.../attachments/{fileId}/restricted`（主管）。权限规则集中在 `Services/AttachmentRules.cs`（接口校验与详情里的 `canAddAttachment / canDelete / canToggleRestricted` 共用），实现在 `Services/AttachmentService.cs`（`partial class OrderService`）。
- **「仅销售和主管可见」（`Restricted`）与价格同等严格**：工程师从详情（`attachments`、`files.orderPhotos`、`logs` 里的 `AttachmentPrivate` 记录）、下载、删除、文件中心都拿不到受限附件；过滤在 `OrderViewBuilder.BuildDetail`、`FileService.ResolveDownload`、`FileCenterService.Query`、`AttachmentService.LoadAttachment` 四处。静态地址 `/uploads/...` 本身不鉴权，保护手段是工程师拿不到记录与地址。
- **软删除**：`DeletedAt / DeletedById`，磁盘文件保留。`AppDbContext` 给 `StoredFile` 配了全局查询过滤器（`DeletedAt == null`），已删除文件对所有读取路径都不存在；要看到它们用 `IgnoreQueryFilters()`。
- **文件中心**：`GET /api/admin/files`（主管）= `StoredFiles` 与 `WorkOrders` 按 `OrderId` 关联的查询（`Services/FileCenterService.cs`），覆盖附件、建单照片、完工照片、签字件、打卡照片、返厂照片，不含头像与未挂工单的文件；支持 keyword / type / source / uploaderId / orderId / restricted / from / to 与分页，返回 `total`、`totalSize`、按类型的 `counts`。查询带当前用户，已按工程师可见性过滤，以后开放给其他角色不用改。
- 迁移 `V12_Attachments` 只加列（`DisplayName / Restricted / DeletedAt / DeletedById`）和索引 `IX_StoredFiles_CreatedAt`，可直接在 v1.1 的库上升级，启动时自动执行。

### v1.2 安全审查后的补充（与 CHANGES-v1.2 的差异）

- **`/uploads` 静态地址**：图片、视频匿名可访问（预览 / 缩略图 / v1.1 小程序依赖，受限图片也一样，见 CHANGES-v1.2 §12.1）；**受限或已删除的文档**（pdf / Office / txt / csv）静态地址返回 404，只能走 `GET /api/files/{id}/download`；非图片视频的内容一律带 `Content-Disposition: attachment` 输出（不在本站源下渲染）；`.heic` / `.heif` 已补进静态类型表。
- **设为受限会收起旧记录**：把附件设为「仅销售和主管可见」时，本单此前带该文件名的 `Attachment` 状态记录一并改为 `AttachmentPrivate`（工程师不再看到文件名；同一条记录里的其他文件名一起隐藏；取消受限后不恢复）。
- **头像与工单文件互斥**：已挂工单的文件不能设为头像（`头像文件已用于工单，请重新上传`）；`Avatar` 类别的文件不能当附件 / 照片挂到工单（`…不存在，请重新上传`）。
- **类型识别收紧**：BMP 还要求保留字段为 0；ISO-BMFF / QuickTime 要求首字节 ≤ 0x08（避免把「The free…」之类的文本误判成视频）；AVIF（`ftyp avif/avis`）拒绝；`category` 不接受数字；文件名清洗额外去掉不可见的格式字符（零宽字符、RLO 等）。

## 导出

`GET /api/admin/export/orders`、`/api/admin/export/hours` 用 ClosedXML 生成 `.xlsx`（`Content-Disposition: attachment`，文件名是 UTF-8 编码的中文名）。

## 实现说明

- 统一响应 `{code, message, data}`；业务异常 `BizException(code, message)` 由中间件映射为 HTTP `code/100`；模型校验失败返回 40000；401/403 由 JwtBearer 事件输出包装体；未匹配的 `/api/*` 返回 40400。
- JSON：camelCase、枚举字符串、时间 `yyyy-MM-ddTHH:mm:ss`、日期 `yyyy-MM-dd`；`counts` 字典的 key 原样输出（如 `Pending`）。
- 业务时间都来自 `IClock`（北京时间，`Asia/Shanghai`，找不到时用 `China Standard Time`），测试里可以替换成 `FixedClock`。
- 工单号 `SH-yyyyMMdd-NNN`：在 Serializable 事务里取当日最大号再 +1，有唯一索引兜底，冲突时重试。
- 认领用条件更新（`WHERE Status='Pending'`），并发认领时只有一个人能成功，其他人收到 409「工单已被他人认领」。
