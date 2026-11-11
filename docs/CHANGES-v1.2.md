# v1.2 变更契约：工单附件 + 工单文件中心（后端 / 小程序 / PC 后台 三方并行实现的唯一依据）

本文件是 v1.2（「方案 A」）的**完整契约**。三位实现者（后端、小程序、PC 后台）只按本文件与 `docs/API.md`（已合并 v1.2）实现，互不沟通也能对上。
凡本文件写「必须 / 固定为」的地方不得自行变通；文案（错误信息、状态记录、按钮文字、提示语）按原文逐字实现。两份文档冲突时以本文件为准。

三项变更：
- **(1) 工单附件**：工单除图片、视频外可以带文档（Word / Excel / PPT / PDF / TXT / CSV）；建单时与建单后都能加；可删除；可设「仅销售和主管可见」。
- **(2) 工单文件中心**：PC 后台新增「文件」页，汇总所有工单名下的文件（附件、建单照片、完工照片、签字件、打卡照片、返厂照片）。小程序本期没有全局文件中心。
- **(3) 运维**：单文件上限提到 100 MB，相应的请求体上限、超时、nginx 配置、小程序隐私声明。

「方案 B」（独立的公司资料库）**本期不做**；§10 说明本期设计如何为它留口子。

通用约定不变：响应包装 `{code,message,data}`；camelCase；枚举传字符串；时间北京时间 `yyyy-MM-ddTHH:mm:ss`；
错误码 `40000` 参数、`40300` 权限、`40400` 不存在、`40900` 状态冲突；所有工单动作成功后返回最新 **OrderDetail**。

名词：
- **附件** = 挂在工单上、`Category` 为 `OrderPhoto`（建单照片，旧数据与 v1.1 客户端上传）或 `Attachment`（v1.2 新增）且未删除的文件。两者在界面上是同一个「附件」列表。
- **受限附件** = `Restricted = true` 的附件，界面文字固定为「仅销售和主管可见」。
- **参与人** = 负责人 + 协助人（同 v1.1）。

---

## 1. 数据模型（全部为增量迁移）

一个新迁移：`V12_Attachments`（`server/src/Huotong.AfterSales.Api/Data/Migrations/`）。只允许 `AddColumn` 与 `CreateIndex`，不得删除列、改列类型、加外键（会触发 SQLite 重建表）。
**没有新表。** 必须在「当前 HEAD 构建生成并带数据的库」上启动新构建验证迁移可用（§9.4）。

### 1.1 `StoredFiles` 新增列

| 列 | 类型 | 可空 | 默认值 | 说明 |
|---|---|---|---|---|
| `DisplayName` | string(200) | 是 | null | 客户端明确给出的原始文件名（已清洗，§3.3）。null = 没有可用的原始名，对外名称由服务端生成（§2.1） |
| `Restricted` | bool | 否 | **false** | 仅销售和主管可见。只对附件有意义；其他类别恒为 false |
| `DeletedAt` | DateTime | 是 | null | 软删除时间；非 null = 已删除（磁盘文件保留） |
| `DeletedById` | int | 是 | null | 删除人 id。**普通整数列，不建外键、不建导航属性** |

新索引：`IX_StoredFiles_CreatedAt`（`CreatedAt`）。已有列与索引不动；`OriginalName` 继续按原样保存 multipart 的文件名（不再用于展示）。

- `Category` 列长度 20 不变，新值 `Attachment`（10 个字符）放得下。
- 已有数据：`DisplayName=null`、`Restricted=false`、`DeletedAt=null`，即全部是「未受限、未删除、名称由服务端生成」。
- **软删除的全局规则**：`DeletedAt != null` 的文件对所有读取路径都视为不存在——工单详情（`files.*`、`attachments`、返厂节点照片）、文件中心、下载接口、`FileService.Attach`、附件数量统计。实现方式自选（逐处过滤或 EF 全局查询过滤器），但必须有测试覆盖（§9.2）。

### 1.2 枚举（C# 名 = JSON 字符串值）

| 枚举 | 变更 |
|---|---|
| `FileCategory` | 新增 `Attachment`（中文「附件」）。完整：`OrderPhoto, PunchPhoto, ProcessPhoto, Signature, Avatar, FactoryReceivePhoto, FactoryShipPhoto, FactorySignPhoto, Attachment` |
| `LogKind` | 新增 `Attachment`（附件，所有角色可见）、`AttachmentPrivate`（受限附件的记录，**Engineer 不可见**）。库列长度 20，`AttachmentPrivate` 17 个字符 |
| `FileKind`（新，不落库） | `Image` 图片、`Video` 视频、`Document` 文档。由 `MimeType` 推导：`image/*` → Image，`video/*` → Video，其余 → Document |

**文件来源 `source`**（字符串，不是 C# 枚举也不落库，由 `Category` 推导；文件中心的筛选值与行字段共用）：

| source | 中文 `sourceLabel` | 对应 Category |
|---|---|---|
| `Attachment` | 附件 | `Attachment` |
| `OrderPhoto` | 建单照片 | `OrderPhoto` |
| `ProcessPhoto` | 完工照片 | `ProcessPhoto` |
| `Signature` | 签字件 | `Signature` |
| `PunchPhoto` | 打卡照片 | `PunchPhoto` |
| `Factory` | 返厂照片 | `FactoryReceivePhoto` / `FactoryShipPhoto` / `FactorySignPhoto`（行上的 `sourceLabel` 分别为「返厂·收货照片」「返厂·寄回照片」「返厂·签收凭证」） |

`Avatar` 与未挂到工单的文件（`OrderId == null`）不属于任何来源，不进文件中心。

---

## 2. DTO 变更

### 2.1 FileDto（所有出现 FileDto 的地方）新增 4 个字段

```json
{ "id": 12, "url": "https://host/uploads/2026/11/xxx.pdf", "category": "Attachment", "mimeType": "application/pdf", "createdAt": "2026-11-11T10:05:00",
  "name": "报价单.pdf", "size": 345678, "kind": "Document", "ext": "pdf" }
```

| 字段 | 类型 | 说明 |
|---|---|---|
| `name` | string | 展示名与下载名。`DisplayName` 非空 → 用它；否则服务端生成：`{前缀}_{yyyyMMdd-HHmmss}{扩展名}`，时间取 `CreatedAt`，扩展名取存储路径的扩展名（含点） |
| `size` | number | 字节数 |
| `kind` | `"Image"` \| `"Video"` \| `"Document"` | FileKind |
| `ext` | string | 存储路径的扩展名，小写、不带点，如 `"pdf"`、`"jpg"`；没有则 `""` |

生成名的前缀：`Attachment` → 图片 / 视频 / 文件（按 kind）；`OrderPhoto` → 建单照片（kind=Video 时「建单视频」）；`PunchPhoto` → 打卡照片；`ProcessPhoto` → 完工照片；`Signature` → 签字件；
`FactoryReceivePhoto` → 收货照片；`FactoryShipPhoto` → 寄回照片；`FactorySignPhoto` → 签收凭证；`Avatar` → 头像。例：`打卡照片_20261106-100500.jpg`。

`url` 仍是 `/uploads/...` 的绝对静态地址（无需认证，文件名为随机 GUID，不可猜）。图片、视频的预览与缩略图用 `url`；文档的打开 / 下载一律走 §3.5 的下载接口。

### 2.2 AttachmentDto（新；= FileDto 全部字段 +）

```json
{ "...FileDto": "",
  "source": "Attachment",            // "Attachment" | "OrderPhoto"（文件中心里还有 §1.2 的其他值）
  "sourceLabel": "附件",
  "restricted": false,
  "uploader": { "UserBrief": "" },   // 上传人；员工不存在时为 null
  "canDelete": true,                 // 当前用户现在可以删除
  "canToggleRestricted": false       // 当前用户现在可以切换「仅销售和主管可见」（仅 Supervisor）
}
```

### 2.3 OrderDetail 新增字段

```json
{
  "attachments": [ { "AttachmentDto": "" } ],   // 当前用户可见的附件，按 createdAt 正序、id 正序
  "canAddAttachment": true,                    // 当前用户现在可以给本单添加附件（§4.1）
  "canRestrictAttachment": false,              // 当前用户上传时可以勾选「仅销售和主管可见」（Sales / Supervisor 为 true）
  "attachmentLimit": 20,
  "attachmentRemaining": 17                    // 还能再加几个（20 − 本单未删除的附件总数，含当前用户看不到的受限附件；最小 0）
}
```

- `files.orderPhotos`（旧字段，**保留给 v1.1 小程序**）改为：当前用户可见的附件里 `kind` 为 Image / Video 的那些（两种 Category 都算），顺序同 `attachments`。v1.2 客户端不再读它，只读 `attachments`。
- `files.punchPhotos / processPhotos / signatures / factoryPhotos`、`factory.nodes[].photos` 不变（只多了 FileDto 的 4 个新字段）。
- `actions` 数组**不新增**动作；附件相关权限只看 `canAddAttachment`、`canRestrictAttachment`、`attachments[].canDelete / canToggleRestricted`，**UI 不得自行推导规则**。
- `OrderListItem` 不变（列表不带附件信息）。

### 2.4 StatusLogDto

`kind` 多两个值：`Attachment`、`AttachmentPrivate`。`AttachmentPrivate` 与 `Price` 一样不返回给 Engineer。旧客户端遇到未知 kind 按整句文字展示（现有实现已如此）。

### 2.5 FileCenterItem（新；文件中心的统一行 = AttachmentDto 全部字段 +）

```json
{ "...AttachmentDto": "",
  "orderId": 3, "orderNo": "SH-20261105-003", "customerName": "江苏某化工研究院", "orderStatus": "InProgress" }
```
非附件来源的行：`restricted=false`、`canDelete=false`、`canToggleRestricted=false`。

---

## 3. 上传与下载

### 3.1 POST /api/files（变更；任何已登录角色）

multipart 字段：

| 字段 | 必填 | 说明 |
|---|---|---|
| `file` | 是 | 文件本体 |
| `category` | 是 | FileCategory 字符串。附件（建单时与建单后）一律传 `Attachment` |
| `fileName` | 否 | 原始文件名。**客户端手里有真实文件名时必须传**（小程序 `wx.chooseMessageFile` 的 `name`；PC `<input type=file>` 的 `file.name`）；小程序相册 / 相机选的图片视频没有真实文件名，不传 |

→ `{ FileDto }`（含 §2.1 新字段）。上传只落盘建记录，不挂工单；挂工单见 §4。

**校验顺序与文案（全部 40000，除注明外）：**
1. `file` 为空或长度 0 → `请选择要上传的文件`
2. `category` 非法 → `文件类别不正确`
3. 取扩展名 `ext`：`fileName`（有则优先）或 multipart 文件名的扩展名，转小写。
4. 类型判定（§3.2），失败文案：
   - `category = Attachment`：类型不在白名单 → `不支持的文件类型，仅支持图片、视频、Word、Excel、PPT、PDF、TXT、CSV`；扩展名在白名单但内容不符 → `文件内容与扩展名不符，请确认文件未损坏`
   - 其他 category：只接受图片 / 视频，其余一律 → `只能上传图片或视频`（文案不变）
5. 大小：
   - `category ≠ Attachment`：> 20 MB → `文件不能超过 20MB`（不变）
   - `category = Attachment`：图片 > 20 MB → `图片不能超过 20MB`；视频、文档 > 100 MB → `文件不能超过 100MB`
6. 请求体超过接口上限（§8.1）→ HTTP 400、code 40000、`文件不能超过 100MB`（`ExceptionMiddleware` 把 413 的文案从「20MB」改为「100MB」）。

落盘：`uploads/yyyy/MM/{guid:N}{ext}`（规则不变，`ext` 用 §3.2 判定后的规范扩展名）。`MimeType` 存 §3.2 表中的值（**不再信任客户端的 Content-Type**）。
`DisplayName` = 清洗后的 `fileName`（§3.3；未传或清洗后为空 → null）。`OriginalName` 照旧。常量：`FileService.MaxBytes = 20 MB`（保留）、新增 `FileService.MaxAttachmentBytes = 100 MB`。

### 3.2 允许的类型（扩展名 + 内容嗅探，两者都要过）

判定流程：`ext` 属于「文档」扩展名 → 走文档规则（仅 `category=Attachment` 允许）；否则走图片 / 视频规则（**以内容为准**，扩展名缺失或不对时用规范扩展名，兼容小程序没有扩展名的临时文件）。

**图片 / 视频（所有 category）**——读文件头，识别不出即拒绝：

| kind | 内容特征（文件头） | 存储 MIME | 允许保留的扩展名（否则用第一个） |
|---|---|---|---|
| Image | `FF D8 FF` | `image/jpeg` | `.jpg` `.jpeg` |
| Image | `89 50 4E 47` | `image/png` | `.png` |
| Image | `47 49 46 38` | `image/gif` | `.gif` |
| Image | `42 4D` | `image/bmp` | `.bmp` |
| Image | `RIFF` + 偏移 8 `WEBP` | `image/webp` | `.webp` |
| Image | 偏移 4 `ftyp` + 品牌 `heic` / `heix` / `mif1` / `hevc` / `heim` / `heis` / `msf1` | `image/heic` | `.heic` `.heif` |
| Video | 偏移 4 为 `ftyp`（非上述 HEIC 品牌）/ `moov` / `mdat` / `wide` / `free` / `skip` | 按扩展名：`.mp4` `video/mp4`、`.m4v` `video/x-m4v`、`.mov` `video/quicktime`、`.3gp` `video/3gpp` | `.mp4` `.m4v` `.mov` `.3gp`；扩展名不在其中时：品牌 `qt  ` 或非 `ftyp` 开头 → `.mov`，其余 → `.mp4` |

（现有 `FileService.SniffMime` 扩充上述 HEIC 品牌与 QuickTime atom；SVG、TIFF、AVIF、HTML 等识别不出，一律拒绝。）

**文档（仅 `category = Attachment`）：**

| 扩展名 | 存储 MIME | 内容校验 |
|---|---|---|
| `.pdf` | `application/pdf` | 前 1024 字节内出现 `%PDF-` |
| `.doc` | `application/msword` | Office 规则 |
| `.docx` | `application/vnd.openxmlformats-officedocument.wordprocessingml.document` | Office 规则 |
| `.xls` | `application/vnd.ms-excel` | Office 规则 |
| `.xlsx` | `application/vnd.openxmlformats-officedocument.spreadsheetml.sheet` | Office 规则 |
| `.ppt` | `application/vnd.ms-powerpoint` | Office 规则 |
| `.pptx` | `application/vnd.openxmlformats-officedocument.presentationml.presentation` | Office 规则 |
| `.txt` | `text/plain` | 文本规则 |
| `.csv` | `text/csv` | 文本规则 |

- **Office 规则**（六种扩展名共用）：文件头是 OLE 复合文档 `D0 CF 11 E0 A1 B1 1A E1`（老格式，以及加密的新格式），**或** 文件头是 `50 4B 03 04` 且能作为 ZIP 打开并含条目 `[Content_Types].xml`（用 `System.IO.Compression.ZipArchive` 只读中央目录）。
- **文本规则**：文件头不是任何已知二进制（上表图片 / 视频、`%PDF-`、`PK\x03\x04`、OLE 头、`MZ`、`\x7FELF`、`1F 8B`、`Rar!`、`37 7A BC AF`），且前 8192 字节不含 `0x00`（以 UTF-16 BOM `FF FE` / `FE FF` 开头的除外）。
- 其余任何扩展名（`.exe .bat .cmd .sh .js .html .htm .svg .zip .rar .7z .apk .dll .msi .php .xml …`）一律拒绝。只看**最后一个**扩展名（`a.pdf.exe` 拒绝）。
- 扩展名是文档类但内容不符 → 「文件内容与扩展名不符，请确认文件未损坏」（不会退回去按图片处理）。

客户端在上传前按同一张扩展名表和大小上限做预检（文案同上），服务端校验是最终依据。

### 3.3 文件名清洗（`fileName` → `DisplayName`）

取最后一个 `/` 或 `\` 之后的部分 → 去掉控制字符与 `< > : " / \ | ? *` → 连续空白压成 1 个空格 → 去掉首尾空格和点 → 主名为空时主名用 `未命名` →
总长超过 100 个字符时截短主名、保留扩展名。结果为空 → null。

### 3.4 静态访问 `/uploads/...`

保持不变（无需认证），现有功能继续用。新增：`/uploads` 的响应统一加响应头 `X-Content-Type-Options: nosniff`（`Program.cs` 的 `StaticFileOptions.OnPrepareResponse`）。
受限附件的保护方式是「Engineer 从任何接口都拿不到它的记录和 `url`」；`url` 本身不带鉴权（见文末「超出需求的决定」）。

### 3.5 GET /api/files/{id}/download（新增；任何已登录角色；`Authorization: Bearer` 头鉴权，不支持 query token）

返回文件原始内容（不是 JSON 包装体）：
- `Content-Type` = 该文件的 `MimeType`；支持 Range（`enableRangeProcessing: true`）。
- `Content-Disposition: attachment; filename="<ASCII 兜底名>"; filename*=UTF-8''<百分号编码的 name>`——直接用 ASP.NET Core 的 `PhysicalFile(path, mime, fileDownloadName: name)`，`name` 为 §2.1 的 `name`。CORS 已暴露 `Content-Disposition`。

权限（按顺序；失败返回 JSON 包装体 + 对应 HTTP 状态）：
1. 文件不存在、已软删除、或 `Category = Avatar` → 40400 `文件不存在`
2. 未挂工单（`OrderId == null`）：只有上传人本人可下载，否则 40400 `文件不存在`
3. 已挂工单：当前用户必须能查看该工单（`OrderViewBuilder.EnsureCanView`，Engineer 不满足 → 40300 `无权查看该工单`）
4. `Restricted = true` 且当前用户是 Engineer → 40400 `文件不存在`（不暴露存在性）
5. 磁盘上找不到文件 → 40400 `文件已丢失，请联系管理员`

调用方式：
- **PC 后台**：现有 `api.download('/api/files/{id}/download', 兜底文件名)`（fetch 带 Authorization → blob → `a[download]`，文件名取 `Content-Disposition` 的 `filename*`）。
- **小程序**：`wx.downloadFile({ url: baseUrl + '/api/files/{id}/download', header: { Authorization: 'Bearer ' + token }, filePath?, timeout: 600000 })`；`statusCode !== 200` 视为失败（404 →「文件不存在或已删除」，403 →「无权查看该文件」，其他 →「下载失败（{状态码}）」）。

---

## 4. 附件接口

### 4.1 权限规则（服务端唯一来源：新增静态类 `AttachmentRules`，接口校验与 DTO 里的 `can*` 共用）

| 规则 | 条件 |
|---|---|
| **可添加** `canAddAttachment` | 工单不是 Voided，且：当前用户是 Supervisor；**或** 工单不是 Closed 且当前用户是建单人 / 负责人 / 协助人 |
| **可设受限** `canRestrictAttachment` | 当前用户角色是 Sales 或 Supervisor（上传时勾选）。Engineer 永远不能 |
| **可删除** `canDelete` | 当前用户是 Supervisor（任何状态）；**或** 当前用户是该附件的上传人，且工单不是 Closed / Voided |
| **可切换受限** `canToggleRestricted` | 当前用户是 Supervisor（任何状态） |
| **可见** | Sales / Supervisor：全部；Engineer：`Restricted = false` 的（且能查看该工单） |

说明：不是建单人的销售不能添加附件；已退出的工程师（Former）看不了工单，自然不能操作；工程师上传的附件被主管设为受限后，工程师本人也看不到了。

### 4.2 Engineer 可见性（硬规则，与价格同等严格）

当前用户为 `Engineer` 时：
- `OrderDetail.attachments`、`files.orderPhotos` 不含受限附件；
- `logs` 不含 `kind = "AttachmentPrivate"` 的记录（与 `Price` 同处过滤）；
- 下载、删除、切换接口对受限附件一律 40400；
- 文件中心查询（即使将来开放给工程师）不返回受限附件；
- 唯一包含受限附件的量是 `attachmentRemaining`（剩余名额），不含名称等任何信息。

### 4.3 POST /api/orders（变更）

请求体新增（其余字段不变）：
```json
{ "attachments": [ { "fileId": 31, "restricted": false }, { "fileId": 32, "restricted": true } ] }
```
- `attachments` 省略 / null / `[]` = 没有附件。每项 `fileId` 必填，`restricted` 省略 = false。文件须先用 `POST /api/files`（`category=Attachment`）上传。
- 旧字段 `photoFileIds` **继续可用**（v1.1 小程序）：行为不变——挂为 `OrderPhoto`，仍校验 `现场照片/视频最多 9 个`。同一个 fileId 同时出现在两处时按 `attachments` 处理。
- 新校验（40000，除注明外）：
  - `attachments` 中有项缺 `fileId` → `附件不存在，请重新上传`
  - 去重后 `attachments` + `photoFileIds` 总数 > 20 → `附件最多 20 个`
  - 任一项 `restricted = true` 且当前用户是 Engineer → **40300** `只有销售和主管可以设置「仅销售和主管可见」`
  - 文件校验同 §4.4 第 8 步（文案相同）。
- 建单时的附件**不另写状态记录**（已有「建单」记录）。挂载在建单事务内完成：`Category = Attachment`、`OrderId = 新单 id`、`Restricted` 按请求。

### 4.4 POST /api/orders/{id}/attachments（新增；任何角色，服务端按 §4.1 判权）

```json
{ "attachments": [ { "fileId": 31, "restricted": false } ] }
```
→ `{ OrderDetail }`

校验顺序：
1. `attachments` 为空 / null，或有项缺 `fileId` → 40000 `请选择要添加的附件`（同一 fileId 重复出现只算一次，取第一次的 `restricted`）
2. 工单不存在 → 40400 `工单不存在`
3. 当前用户不能查看该工单 → 40300 `无权查看该工单`
4. 工单 Voided → 40900 `工单已作废，不能添加附件`
5. 工单 Closed 且当前用户不是 Supervisor → 40900 `工单已闭环，只有主管可以添加附件`
6. 当前用户不是 Supervisor / 建单人 / 负责人 / 协助人 → 40300 `只有建单人、负责工程师、协助人或主管可以添加附件`
7. 任一项 `restricted = true` 且当前用户是 Engineer → 40300 `只有销售和主管可以设置「仅销售和主管可见」`
8. 逐个文件：不存在或已删除 → 40000 `附件不存在，请重新上传`；不是本人上传 → 40000 `附件不是本人上传，请重新上传`；已挂到任何工单（含本单）→ 40000 `附件已被使用，请重新上传`
9. 本单未删除附件数（两种 Category 合计）+ 本次个数 > 20 → 40000 `附件最多 20 个，本单还可添加 {N} 个`（N = 20 − 现有数；N ≤ 0 时为 `附件最多 20 个，本单已达上限`）

第 2–9 步在 `WriteLock()` 事务内执行（并发添加不会突破 20 个）。通过后：`OrderId = id`、`Category = Attachment`、`Restricted` 按请求。全部成功或全部失败。不发通知。

状态记录（`ToStatus` = 工单当前状态；`{名单}` = 按请求顺序取前 3 个文件的 `name`，用 `、` 连接；单个 `name` 超过 40 个字符时截为前 39 个字符 + `…`）：
- 非受限文件（`kind = Attachment`）：≤ 3 个 `{姓名} 上传附件 {名单}`；> 3 个 `{姓名} 上传附件 {名单} 等 {n} 个`
- 受限文件（`kind = AttachmentPrivate`）：同上，末尾加 `（仅销售和主管可见）`
- 一次请求里两类都有 → 写两条（先非受限、后受限）。

例：`王敏 上传附件 现场铭牌.jpg、故障视频.mp4`、`王敏 上传附件 报价单.pdf（仅销售和主管可见）`。

### 4.5 POST /api/orders/{id}/attachments/{fileId}/delete（新增；任何角色，服务端判权）

无请求体。→ `{ OrderDetail }`
1. 工单不存在 → 40400 `工单不存在`
2. 不能查看该工单 → 40300 `无权查看该工单`
3. 附件不存在（`fileId` 不是本单未删除的附件，或对当前用户不可见）→ 40400 `附件不存在`
4. 当前用户不是 Supervisor 时：不是上传人 → 40300 `只能删除自己上传的附件`；工单 Closed → 40900 `工单已闭环，只有主管可以删除附件`；工单 Voided → 40900 `工单已作废，不能删除附件`

软删除：`DeletedAt = now`、`DeletedById = 当前用户`（`OrderId`、磁盘文件保留）。删除后名额释放。
状态记录：`{姓名} 删除附件 {name}`；被删的是受限附件 → `kind = AttachmentPrivate`，否则 `kind = Attachment`。

### 4.6 POST /api/orders/{id}/attachments/{fileId}/restricted（新增；Supervisor）

`{ "restricted": true }` → `{ OrderDetail }`
1. 角色不是 Supervisor → 40300 `只有主管可以修改附件可见范围`（控制器 `[Authorize(Roles = "Supervisor")]` 先拦，文案为通用的「无权限」；服务层再用 `EnsureRole` 兜底，测试断言服务层文案）
2. `restricted` 缺失 → 40000 `请选择可见范围`
3. 工单不存在 → 40400 `工单不存在`；附件不存在 → 40400 `附件不存在`
4. 值未变化 → 直接返回详情，不写记录。

任何工单状态都可切换。状态记录：
- 设为受限：`{姓名} 将附件 {name} 设为仅销售和主管可见`（`kind = AttachmentPrivate`）
- 取消受限：`{姓名} 将附件 {name} 设为所有人可见`（`kind = Attachment`）

### 4.7 其他挂载路径的加固（`FileService.Attach`）

- 已软删除的文件 → `{label}不存在，请重新上传`。
- `category` 不是 `Attachment` 时，文件的 kind 必须是 Image / Video，否则 40000 `{label}只能是图片或视频`（防止把文档当完工照片 / 打卡照片 / 返厂照片提交）。
- 文件已挂在本单、但类别与目标类别不同（且不是「完工照片 ↔ 签字件」互换）→ 40000 `{label}已用于本单其他资料，请重新上传`（把 `FactoryService` 里已有的保护推广到所有路径；附件不能被挪作完工照片，反之亦然）。

---

## 5. 文件中心接口

### GET /api/admin/files（新增；Supervisor）

查询参数（全部可选）：

| 参数 | 说明 |
|---|---|
| `keyword` | 模糊匹配 `DisplayName` / `OriginalName` / 工单号 / 客户单位（任一包含即命中） |
| `type` | `Image` \| `Video` \| `Document`；非法 → 40000 `文件类型不正确` |
| `source` | §1.2 的 6 个值之一；非法 → 40000 `文件来源不正确` |
| `uploaderId` | 上传人员工 id |
| `orderId` | 只看某一张工单 |
| `restricted` | `true` \| `false`；非法 → 40000 `可见范围筛选不正确` |
| `from` / `to` | 上传日期 yyyy-MM-dd（含两端）；格式错 → `开始日期格式应为 yyyy-MM-dd` / `结束日期格式应为 yyyy-MM-dd`；`from > to` → `开始日期不能晚于结束日期`。**默认不限日期** |
| `page` / `pageSize` | 默认 1 / 30；`pageSize` 限制在 1–200 |

范围：`OrderId != null` 且 `DeletedAt == null` 且 `Category != Avatar` 的全部文件，**包含已作废工单的文件**。排序固定：`createdAt` 倒序、`id` 倒序。

→
```json
{ "items": [ { "FileCenterItem": "" } ],
  "total": 128, "totalSize": 734003200,          // 符合全部筛选条件的文件数与字节总数（不是当前页）
  "page": 1, "pageSize": 30,
  "counts": { "all": 128, "Image": 100, "Video": 8, "Document": 20 } }   // 受除 type 以外的全部筛选影响
```

各来源如何从现有数据推导（不改动任何现有数据）：所有工单文件在挂载时都已写入 `StoredFiles.OrderId`（建单照片、完工照片、签字件、打卡照片、返厂照片都经过 `FileService.Attach`），
所以文件中心就是 `StoredFiles` 与 `WorkOrders` 按 `OrderId` 关联的一个查询，`source` 由 `Category` 映射（§1.2），`uploader` 按 `UploaderId` 查员工。
完工重提 / 返厂更正时被替换下来的旧照片 `OrderId` 已被置 null，自然不出现。

实现为 `FileCenterService.Query(FileCenterQuery q, CurrentUser user)`。本期控制器只对 Supervisor 开放，但查询**必须**带上用户并正确处理可见性，以便以后直接开放：
- Supervisor / Sales：全部；
- Engineer：只含其能查看的工单（待认领、本人负责 / 协助 / 建单）的文件，且不含受限附件。

行上的 `canDelete` / `canToggleRestricted` 只对附件来源按 §4.1 计算，其余为 false。文件中心里的删除、切换受限直接调用 §4.5 / §4.6（行里有 `orderId`）。

---

## 6. 小程序改动清单（`miniprogram/`）

不改 `config.js`、`project.config.json`。不新增 tab、不新增页面。

### 6.1 `utils/request.js`
- `uploadFile(filePath, category, opts)`：`opts` 新增 `fileName`（有值时加入 `formData.fileName`）、`timeout`（默认仍 60000）、`onProgress(percent)`（用返回的 `UploadTask.onProgressUpdate`）。HTTP 413 且响应不是 JSON → 错误文案 `文件不能超过 100MB`。
- `download(url, query, opts)`：`opts` 新增 `filePath`、`timeout`（默认不变）、`onProgress(percent)`、`silent`；非 200 的文案按 §3.5。
- 新增 `del` 不需要（附件接口全是 POST）。

### 6.2 `api/index.js`
- `files.upload(filePath, category, opts)` 透传新 opts；注释里的 category 列表加 `Attachment`。
- `files.downloadPath(id)` → `'/api/files/' + id + '/download'`。
- `orders.addAttachments(id, attachments, opts)` → `POST /api/orders/{id}/attachments`，body `{ attachments }`。
- `orders.deleteAttachment(id, fileId, opts)` → `POST …/attachments/{fileId}/delete`。
- `orders.setAttachmentRestricted(id, fileId, restricted, opts)` → `POST …/attachments/{fileId}/restricted`。
- typedef 补 `AttachmentDto` 与 OrderDetail 新字段。

### 6.3 新增 `utils/attachments.js`（建单页与详情页共用）
导出：
- 常量：`MAX_COUNT = 20`、`MAX_IMAGE = 20 MB`、`MAX_FILE = 100 MB`、`DOC_EXTS = ['doc','docx','xls','xlsx','ppt','pptx','pdf','txt','csv']`、`OPEN_DOC_EXTS = ['doc','docx','xls','xlsx','ppt','pptx','pdf']`。
- `pickMedia(left)`：`wx.chooseMedia({ count: Math.min(9, left), mediaType: ['mix'], sourceType: ['album','camera'], sizeType: ['original'], maxDuration: 60 })` → 统一条目。**原图，不压缩。**
- `pickChatFiles(left)`：`wx.chooseMessageFile({ count: Math.min(9, left), type: 'file', extension: DOC_EXTS })` → 统一条目（带 `name`、`size`）。
- 统一条目：`{ key, path, name, size, kind: 'Image'|'Video'|'Document', ext, thumb, status: 'uploading'|'done'|'fail', fatal, error, percent, fileId, restricted }`。
- 预检（不通过的条目直接 `status='fail'、fatal=true`，不上传）：文档扩展名不在 `DOC_EXTS` → `不支持的文件类型`；图片 > 20 MB → `图片不能超过 20MB`；其他 > 100 MB → `文件不能超过 100MB`。
- `upload(item, onChange)`：`api.files.upload(item.path, 'Attachment', { silent: true, timeout: 600000, fileName: 文档才传 item.name, onProgress })`，**逐个排队上传**（同一时间只传 1 个）。失败把服务端 `message` 记到 `item.error`，非 fatal 的可点击重试。
- `fmtSize(bytes)`：`< 1024` → `N B`；`< 1 MB` → `N KB`（取整）；`< 1 GB` → `N.N MB`（1 位小数）；否则 `N.NN GB`。
- `badge(att)`：类型角标的文字与颜色——`doc/docx` → `W` `#2B579A`；`xls/xlsx/csv` → `X` `#217346`；`ppt/pptx` → `P` `#D24726`；`pdf` → `PDF` `#C81E2B`；`txt` → `TXT` `#636366`；Image → `图` `#636366`；Video → `视频` `#636366`。
- `open(att, list)`：
  - Image / Video：`wx.previewMedia({ sources, current })`，`sources` 为 `list` 中全部 Image / Video 的 `{ url, type }`（原文件地址，不压缩）；没有 `previewMedia` 时图片退回 `wx.previewImage`。
  - Document：先下载再打开。下载到 `wx.env.USER_DATA_PATH + '/att/' + att.id + '/' + att.name`（下载前清空 `att/` 目录后重建，保证不占满 200 MB 用户目录；写入失败则退回默认临时路径），`timeout: 600000`，`wx.showLoading({ title: '下载中 35%' })` 随进度更新；同一页面生命周期内按 `id` 缓存已下载路径。
    - `ext` 在 `OPEN_DOC_EXTS` 中：`wx.openDocument({ filePath, fileType: att.ext, showMenu: true })`（右上角菜单可转发 / 保存）；失败 toast `无法打开该文件`。
    - `txt` / `csv`（`openDocument` 不支持）：`wx.showModal({ title: att.name, content: '该类型不支持在小程序内预览，可转发到微信聊天后查看。', confirmText: '转发' })` → `wx.shareFileMessage({ filePath, fileName: att.name })`。

### 6.4 `pages/order-new`（index.js / .wxml / .wxss）
- 「现场照片 / 视频」区块改为 **`附件（照片 / 视频 / 文件）`**，副文字 `选填，最多 20 个`。`MAX_MEDIA` 9 → 20，去掉本地的 20 MB 常量，改用 `utils/attachments.js`。
- 点「+」→ `wx.showActionSheet({ itemList: ['拍照或从相册选择', '从微信聊天选择文件'] })` → `pickMedia` / `pickChatFiles`，选完立即排队上传。
- 网格下方固定提示（`.hint`）：`文件需从微信聊天中选择：先把文件发到「文件传输助手」，再点「+」选「从微信聊天选择文件」。支持 Word / Excel / PPT / PDF / TXT / CSV，单个不超过 100MB；照片按原图上传。`
- 格子：图片显示缩略图（本地临时路径）；视频显示 ▶ 与时长（现状）；文档显示类型角标 + 一行文件名。上传中显示 `{percent}%`；失败显示 `点击重试`（fatal 的显示错误文字，只能删除）。受限的格子左上角显示锁形角标。
- 点已上传的格子 → 预览（图片视频）；**当前用户不是 Engineer 时**（建单页没有工单，可按本地角色判断）长按格子 → `wx.showActionSheet`：`设为仅销售和主管可见` / `取消仅销售和主管可见`，切换 `item.restricted`。在提示语后追加一句：`长按附件可设为「仅销售和主管可见」（工程师看不到）。`（仅非 Engineer 显示）
- 校验文案：`附件还在上传，请稍候` / `有附件上传失败，请点击重试或删除`。
- 提交：`attachments: media.filter(m => m.fileId).map(m => ({ fileId: m.fileId, restricted: !!m.restricted }))`，**不再传 `photoFileIds`**。

### 6.5 `pages/order-detail`（index.js / .wxml / .wxss）
- tab 文字「资料」→ **`附件资料`**（key 仍为 `files`，三套 tabs 都改）。
- 该 tab 顶部新增「附件」卡片（在现有资料行卡片之上）：
  - 标题行：`附件` + 数量 `（{attachments.length}）`；`canAddAttachment` 为 true 时右侧 `od-link` 样式的 `添加`。
  - 每行：类型角标（不加载缩略图，避免拉取原图）、名称（单行省略）、副行 `{大小} · {上传人} · {M月d日 HH:mm}`、`restricted` 时显示角标 `仅销售和主管可见`；右侧 `…` 仅当 `canDelete || canToggleRestricted`。
  - 点行 → `attachments.open(att, list)`。
  - 点 `…` → `wx.showActionSheet`：`canToggleRestricted` → `设为仅销售和主管可见` / `设为所有人可见`；`canDelete` → `删除`。删除前 `this.confirm('删除附件', '确定删除「{name}」？删除后不可恢复。', '删除')`；成功 toast `已删除` / `已更新`；都走 `this.run(...)`（用返回的 OrderDetail 刷新）。
  - 没有附件：`暂无附件`。
- 现有资料行里**去掉「建单照片」一行**（已并入附件）；其余行（打卡照片 / 处理照片 / 客户签字件 / 返厂照片）不变，卡片上方加小标题 `流程资料`。
- 添加附件弹层（沿用页面内 sheet 的写法，`attachSheet`）：
  - 标题 `添加附件`，右上 `取消`；`attachmentRemaining === 0` 时点「添加」直接 toast `附件最多 20 个，本单已达上限`。
  - `canRestrictAttachment` 为 true 时显示开关行 `仅销售和主管可见`，副文字 `打开后本次添加的附件工程师看不到`（对本次整批生效）。
  - 两个按钮：`拍照或从相册选择`、`从微信聊天选择文件`（数量上限 = `min(9, attachmentRemaining − 已选数)`）。
  - 提示语同 §6.4（不含长按那句）。
  - 已选列表：角标、名称、大小、状态（`上传中 35%` / `已上传` / 红色错误文字 + `重试` / `移除`）。
  - 主按钮 `确定添加（{n}）`：有上传中 → 禁用并显示 `上传中…`；有失败项 → toast `有附件上传失败，请重试或移除`；否则 `api.orders.addAttachments(id, done.map(x => ({ fileId: x.fileId, restricted: sheet.restricted })))`，成功后关闭弹层、toast `已添加`、用返回的详情刷新。40900 时按现有逻辑重新拉取详情。
  - 关闭弹层时已上传未提交的文件直接丢弃（服务端留作未挂载文件，无需清理）。
- `pages/privacy/index.wxml`：「三、相机与照片」末尾加一句 `建单和处理工单时，你可以从微信聊天中选择文件（如报告、表格）作为工单附件上传。`

### 6.6 其他页面
`complete`、`factory-node`、`checkin`、`utils/avatar.js` 不改（仍是压缩图、20 MB、原 category）。`miniprogram/README.md` 补 v1.2 说明与隐私声明项「选中的文件」。

---

## 7. PC 后台改动清单（`wwwroot/admin/`）

### 7.1 `js/api.js`
- `files.upload(file, category, opts)`：`opts = { fileName?, onProgress? }`。传了 `onProgress` 时改用 `XMLHttpRequest`（`xhr.upload.onprogress` → `onProgress(percent 0–100)`，`xhr.timeout = 900000`），结果与错误处理同 `request`（解包、40100 跳登录、`ApiError`）；HTTP 413 且非 JSON → `ApiError('文件不能超过 100MB', 40000, 413)`；超时 → `上传超时，请检查网络后重试`。`category === 'Attachment'` 时始终附带 `fileName = file.name`。
- `files.download(id, name)` → `download('/api/files/' + id + '/download', name)`。
- `orders.addAttachments(id, attachments)`、`orders.deleteAttachment(id, fileId)`、`orders.setAttachmentRestricted(id, fileId, restricted)`（路径见 §4）。
- `orders.create` 的 body 用 `attachments`。
- `admin.files(params)` → `GET /admin/files`。

### 7.2 `js/enums.js`、`js/format.js`、`js/ui.js`、`css/admin.css`
- `FILE_CATEGORY` 加 `Attachment: { label: '附件' }`；新增 `FILE_KIND = { Image:{label:'图片'}, Video:{label:'视频'}, Document:{label:'文档'} }`、`FILE_SOURCE`（§1.2 六项：附件 / 建单照片 / 完工照片 / 签字件 / 打卡照片 / 返厂照片）。
- `format.js` 新增 `fmtSize(bytes)`（规则同 §6.3）与 `fileBadge(item)`（角标文字与颜色同 §6.3）。
- `ui.js`：`preview(items, index)` 兼容 `{ url, type: 'image'|'video' }`，lightbox 对 video 渲染 `<video controls autoplay>`（传字符串或没有 type 时按图片，现有调用不受影响）；`ICONS` 加 `folder`、`file`、`video`、`lock`、`trash`、`paperclip`。
- `admin.css`：新增样式一律以 `files-` / `att-` 为前缀，不改已有类。

### 7.3 新增公共组件 `js/views/attachments-upload.js`（新建工单与抽屉共用）
props：`limit`（还能加几个）、`canRestrict`、`modelValue`（条目数组）；条目 `{ key, file, name, size, kind, ext, previewUrl, status: 'uploading'|'done'|'error', fatal, error, percent, id, restricted }`。
- `<input type="file" multiple hidden accept="image/*,video/*,.doc,.docx,.xls,.xlsx,.ppt,.pptx,.pdf,.txt,.csv">`，另支持把文件拖到区域内。
- 预检同 §6.3 的文案；超出 `limit` 的部分忽略并提示 `附件最多 20 个，已忽略多余的 {k} 个`。
- 每项一行：角标 / 图片缩略图（本地 objectURL）、文件名、大小、进度条与百分比、错误文字 + `重试`、`移除`；`canRestrict` 时每项有复选框 `仅销售和主管可见`。
- 同时最多上传 2 个。暴露 `uploading`、`hasError`、`done`（`[{ fileId, restricted }]`）。

### 7.4 `js/views/orders-create.js`
- 「现场照片」区块改为 **`附件`**，字段标题 `附件（照片 / 视频 / 文件，选填，最多 20 个）`，用 §7.3 组件（`limit=20`、`canRestrict=true`）。
- 提示：`支持图片、视频、Word、Excel、PPT、PDF、TXT、CSV；图片单个不超过 20MB，其他不超过 100MB。服务价格不在建单时填写，建单后由主管在工单详情中定价。`
- 提交 body 用 `attachments`（不再传 `photoFileIds`）；校验文案 `有附件未上传成功，请重试或移除`、`附件仍在上传，请稍候`。

### 7.5 `js/views/orders-drawer.js`
在「返厂流程」之后、「打卡记录」之前新增 **`附件`** 区：
- 标题 `附件`（数量）+ 右侧链接 `添加附件`（`d.canAddAttachment`）与 `全部文件`（跳 `#/files?orderId={id}`，可看本单的完工照片、签字件等）。
- 列表行：角标（不加载缩略图）、名称（点击：图片视频 → `preview`，同单全部图片视频可左右切换；文档 → `api.files.download`）、`{大小} · {上传人} · {M-d HH:mm}`、来源为 `OrderPhoto` 时显示灰色标签 `建单照片`、`restricted` 显示标签 `仅销售和主管可见`；行尾按钮：`下载`、`设为仅销售和主管可见 / 设为所有人可见`（`canToggleRestricted`）、`删除`（`canDelete`，先 `confirm('删除附件', '确定删除「{name}」？删除后不可恢复。', { danger: true, okText: '删除' })`）。
- 空状态 `暂无附件`。
- 点「添加附件」在区内展开 §7.3 组件（`limit = d.attachmentRemaining`、`canRestrict = d.canRestrictAttachment`）+ `确定添加` / `取消`；成功后用返回的详情刷新、toast `已添加 {n} 个附件`、`$emit('changed')`。
- 所有操作成功后都用返回的 OrderDetail 更新 `detail`；错误用 `toastError`。

### 7.6 新增 `js/views/files.js`（`#/files`）与 `js/main.js`
- `main.js`：`ROUTES` 在 `/hours` 之后加 `'/files': { title: '文件', icon: 'folder', component: FilesView }`。页面标题 `工单文件中心`。
- 查询参数全部放在 hash 上：`?keyword=&type=&source=&uploaderId=&orderId=&from=&to=&page=&id=`（`id` = 打开的工单抽屉）。
- 顶部：类型 tab `全部 / 图片 / 视频 / 文档`（带 `counts`）；筛选条：搜索框（占位 `搜索文件名 / 工单号 / 客户`）、来源下拉（`全部来源` + 六项）、上传人下拉（`GET /api/admin/employees`，`全部上传人`）、日期范围（两个 `type=date`，默认空 = 不限）、`重置`。带 `orderId` 进入时显示可关闭的筛选标签 `工单 {orderNo}`。
- 汇总行：`共 {total} 个文件 · {fmtSize(totalSize)}`。
- 表格（`data-table`）列：
  1. 预览：图片且 `size ≤ 2 MB` → 32px 缩略图（`loading="lazy"`，原图地址）；其余 → 类型角标。点击：图片 / 视频打开 lightbox（当前页全部图片视频可切换），文档直接下载。
  2. 文件名（`name`，悬停显示全名）+ `restricted` 时标签 `仅销售和主管可见`
  3. 来源（`sourceLabel`）
  4. 工单：`{orderNo}` 换行 `{customerName}`，点击打开工单抽屉（页面内嵌 `OrdersDrawer`，`updateQuery({ id: orderId })`；抽屉 `changed` 后重新加载列表）
  5. 上传人
  6. 上传时间（`fmtDateTime`）
  7. 大小（右对齐，`fmtSize`）
  8. 操作：`下载`；附件行另有 `设为仅销售和主管可见 / 设为所有人可见`（`canToggleRestricted`）、`删除`（`canDelete`，确认框同 §7.5）。不做批量下载。
- 分页用现有 `Pagination`（每页 30）；空状态 `没有符合条件的文件`；加载 / 错误状态沿用 `punches.js` 的写法。
- `README.md`（admin 目录）补「文件」页、路由表、v1.2 小节。

---

## 8. 运维

### 8.1 请求体上限与超时
- `FilesController.Upload`：`[RequestSizeLimit]` 与 `[RequestFormLimits(MultipartBodyLengthLimit)]` 改为 `FileService.MaxAttachmentBytes + 1 MB`（105,906,176 字节）。特性会覆盖 Kestrel 全局上限。
- `appsettings.json` 的 `Kestrel:Limits:MaxRequestBodySize`（31457280）**不改**：其他接口都是小 JSON，保持 30 MB 兜底。
- `ExceptionMiddleware`：413 的文案改为 `文件不能超过 100MB`。
- 小程序：附件上传与文档下载 `timeout: 600000`（10 分钟）；PC：XHR `timeout = 900000`。
- **nginx（手工步骤）**：站点配置 `/www/server/panel/vhost/nginx/extension/shouhou.sh-htlab.com/huotong-proxy.conf` 里 `client_max_body_size 30m;` 必须改为 **`client_max_body_size 110m;`**，并把 `proxy_read_timeout 120s;` 改为 `300s`、新增 `proxy_send_timeout 300s;`，然后 `nginx -t && nginx -s reload`。不改的话超过 30 MB 的上传会被 nginx 直接返回 413（客户端显示「文件不能超过 100MB」）。
  后端实现者把这段写进 `deploy/README.md` 第 3 步的配置示例，并加一条「从 v1.1 升级到 v1.2 必须手工改这一行」的说明（`deploy/` 下只允许改这个文件的这部分）。
- 磁盘：附件按原图 / 原文件保存，`uploads/` 增长会明显变快；`deploy/README.md` 的备份提示处补一句「v1.2 起请关注磁盘空间」。

### 8.2 小程序后台（手工步骤，写进 `miniprogram/README.md` 与 `docs/DESIGN.md`）
- 「设置 → 服务内容声明 → 用户隐私保护指引」新增一项：**选中的文件**（用途：`用于将聊天中的文件作为工单附件上传`）。不声明的话 `wx.chooseMessageFile` 在正式版会被拒绝。
- request / uploadFile / downloadFile 合法域名已有 `https://shouhou.sh-htlab.com`，无需改动。
- 小程序需重新提审发布；后端与 PC 后台先上线不影响 v1.1 小程序（§8.3）。

### 8.3 与 v1.1 小程序的兼容
- 旧建单请求（`photoFileIds`、`category=OrderPhoto`、≤ 9 个、≤ 20 MB）行为不变。
- 旧详情页读 `files.orderPhotos`：现在包含全部可见的图片 / 视频附件（含 PC 新上传的），文档在旧版小程序里看不到。
- 旧客户端忽略新字段与新的 `kind` 值。

---

## 9. 测试

### 9.1 必须修改的现有测试
- `FileSniffTests`：保留现有用例，补充 HEIC 新品牌、`moov` / `mdat` / `wide` 开头的 QuickTime、PDF、OLE、ZIP 头的用例。
- 断言 `files.orderPhotos` 的测试语义不变（纯图片的建单照片仍在其中），无需改值；若种子数据新增了附件（§9.5）导致数量断言变化，按新数量改。
- 其余 171 个测试必须原样通过。

### 9.2 必须新增的测试（xUnit，服务级，沿用 `V11TestBase` 的 SQLite 内存库 + 种子数据写法；新文件 `V12AttachmentTests.cs`、`V12FileCenterTests.cs`、`V12UploadTests.cs`）

上传校验（用内存 `FormFile` 构造真实文件头；docx 用 `ZipArchive` 现场生成含 `[Content_Types].xml` 的包）：
1. Attachment：pdf / docx / xlsx / pptx / doc（OLE 头）/ txt / csv / jpg / mp4 各自成功，`mimeType`、`kind`、`ext`、`size`、`name` 正确
2. `fileName` 传 `..\\..\\报价 单?.PDF` → `name = "报价 单.PDF"`、存储扩展名 `.pdf`；不传 `fileName` 的图片 → `name` 形如 `图片_yyyyMMdd-HHmmss.jpg`
3. `.exe` / `.html` / `.svg` / `.zip` / `.js` → `不支持的文件类型，仅支持图片、视频、Word、Excel、PPT、PDF、TXT、CSV`
4. `.pdf` 内容为 `MZ…`、`.docx` 内容为普通 ZIP（无 `[Content_Types].xml`）、`.txt` 内容含 NUL → `文件内容与扩展名不符，请确认文件未损坏`
5. `category=OrderPhoto` 上传 pdf → `只能上传图片或视频`；`category=OrderPhoto` 声明 `image/svg+xml` 的 SVG → `只能上传图片或视频`
6. 大小：Attachment 图片 20 MB + 1 → `图片不能超过 20MB`；Attachment 视频 100 MB + 1 → `文件不能超过 100MB`；OrderPhoto 视频 20 MB + 1 → `文件不能超过 20MB`（用声明长度的假流，不真的分配内存）
7. 加密 docx（OLE 头 + `.docx`）成功

附件规则：
8. 建单带 `attachments`（含 1 个受限）：销售建单成功，`attachments` 数量与 `restricted` 正确，文件 `Category=Attachment`、`OrderId` 已写；不写附件状态记录
9. 旧请求体 `photoFileIds` 仍成功，文件为 `OrderPhoto`，同时出现在 `attachments`（`source=OrderPhoto`）与 `files.orderPhotos`
10. 建单 `attachments` + `photoFileIds` 共 21 个 → `附件最多 20 个`；工程师建单带 `restricted=true` → 40300
11. 添加权限矩阵：建单人 / 负责人 / 协助人 / 主管成功；非建单销售、无关工程师 → 40300 对应文案；Voided → 40900；Closed 非主管 → 40900，主管成功
12. 添加校验：空数组、他人上传、已挂载文件、已删除文件、超过 20（文案里的 N 正确、N=0 的文案）
13. 状态记录：≤ 3 个、> 3 个、受限批次、混合批次写两条；`kind` 正确
14. 删除：上传人成功（未闭环）；非上传人 40300；闭环后上传人 40900、主管成功；删除后不在 `attachments`、`files.orderPhotos`、文件中心，名额恢复；磁盘文件仍在；记录文字与 kind
15. 切换受限：主管成功且写记录；重复设置不写记录；销售 / 工程师 40300
16. **工程师可见性**：有受限附件的工单，工程师（负责人）拿到的 `attachments`、`files.orderPhotos`、`logs` 都不含它；序列化后的整个 OrderDetail JSON 不含受限文件的 `name` 与 `url`；工程师删除 / 下载受限附件 → 40400；销售、主管能看到
17. 工程师上传的附件被主管设为受限后，该工程师看不到、删不了（40400）
18. `canAddAttachment` / `canRestrictAttachment` / `canDelete` / `canToggleRestricted` / `attachmentRemaining` 在各角色、各状态下与规则一致
19. `FileService.Attach` 加固：文档不能作为完工照片（`处理照片只能是图片或视频`）；本单附件不能被挪作完工照片（`处理照片已用于本单其他资料，请重新上传`）

下载（服务级：新增 `FileService.ResolveDownload(id, user)` 返回路径 / MIME / 名称，控制器只做输出）：
20. 主管 / 销售 / 可见工单的工程师成功；无关工程师 40300；受限 + 工程师 40400；已删除 40400；头像 40400；未挂载文件仅上传人可取；磁盘文件缺失 40400 `文件已丢失，请联系管理员`

文件中心：
21. 种子库查询：包含 6 种来源、不含头像 / 未挂载 / 已删除；`total`、`totalSize`、`counts` 正确；排序为时间倒序
22. 各筛选：`keyword`（文件名 / 工单号 / 客户）、`type`、`source=Factory`（三种返厂类别）、`uploaderId`、`orderId`、`restricted`、`from/to`、分页；非法参数的文案
23. 以工程师身份调用 `FileCenterService.Query`：只含其可见工单的文件，且不含受限附件；以销售身份：含受限附件
24. 行上的 `canDelete` / `canToggleRestricted`：附件行为 true（主管），非附件行为 false

迁移：
25. `V12_Attachments` 在只应用到 `V11_PriceHoldAssistFactory` 的库（带种子数据）上执行成功，旧文件行 `Restricted=false`、`DeletedAt=null`、`DisplayName=null`，详情与文件中心正常

### 9.3 `server/scripts/smoke.sh` 新增检查（接在现有检查之后；测试文件用 python3 在 `$TMP` 生成）
1. 上传 `category=Attachment` 的 PDF（带 `fileName=报价单.pdf`）→ 200，`kind=Document`、`name=报价单.pdf`
2. 上传 docx（python `zipfile` 生成）、txt、PNG（Attachment）→ 200
3. 上传 `.exe` → 400 + 「不支持的文件类型…」；`.pdf` 假内容 → 400 + 「文件内容与扩展名不符…」；PDF 以 `category=OrderPhoto` → 400 + 「只能上传图片或视频」
4. 21 MB 的 mp4（`ftyp` 头 + 填充）以 Attachment 上传 → 200；同一文件以 OrderPhoto 上传 → 400 「文件不能超过 20MB」；101 MB 以 Attachment 上传 → 400 「文件不能超过 100MB」
5. 销售建单带 `attachments`（1 公开 PDF + 1 受限 PDF）→ 详情 `attachments` 2 个、`attachmentRemaining=18`、`canRestrictAttachment=True`
6. 工程师认领后看详情：`attachments` 1 个，响应全文不含受限文件名；`canAddAttachment=True`、`canRestrictAttachment=False`
7. 工程师添加 1 个附件 → 成功，logs 含「上传附件」；工程师带 `restricted:true` → 403；无关工程师添加 → 403
8. 工程师下载公开附件 → 200，`Content-Disposition` 含 `filename*=UTF-8''`，字节数一致；工程师下载受限附件 → 404；未带 token 下载 → 401；销售下载受限附件 → 200
9. 主管把工程师的附件设为受限 → 工程师详情里消失；再设回公开
10. 工程师删除自己的附件 → 成功；删除销售的附件 → 403；主管删除 → 成功；再次删除 → 404
11. 旧请求体 `photoFileIds` 建单 → `files.orderPhotos` 1 个且 `attachments` 1 个（`source=OrderPhoto`）
12. `GET /api/admin/files`：主管 200 且有 `total`、`totalSize`、`counts`；`?type=Document`、`?source=PunchPhoto`、`?orderId=`、`?keyword=报价` 结果符合预期；`?type=Nope` → 400 「文件类型不正确」；工程师 / 销售 → 403
13. `GET /uploads/...` 响应头含 `X-Content-Type-Options: nosniff`
14. `GET /admin/js/views/files.js` → 200（PC 文件页已随后端发布）

### 9.4 迁移验证（后端实现者必须手工执行一次并在交付说明里写结果）
用当前 HEAD 的构建启动一次空库（`Seed:Enabled=true`）生成带数据的 `<scratch>.db` → 停掉 → 用新构建以同一个库启动（`--urls http://127.0.0.1:509x`，不得占用 5080）→ 启动无异常、旧工单详情与 `GET /api/admin/files` 正常 → 再跑 `smoke.sh`。

### 9.5 种子数据（`SeedData`，仅空库时写入）
给 o1 增加 2 个由销售上传的附件（写入最小合法 PDF `%PDF-1.4\n%%EOF\n`）：`现场情况说明.pdf`（公开）、`报价单.pdf`（受限），`Category=Attachment`、`DisplayName` 如上。现有测试若断言 o1 的文件数量，按 §9.1 调整。

---

## 10. 为「方案 B：公司资料库」预留（本期不做）

- 资料库文件将是 `OrderId == null`、新 Category（如 `Library`）的 `StoredFiles` 行，目录结构用新表表达；本期新增的 `DisplayName / Restricted / DeletedAt / DeletedById` 四列可直接复用。
- §3.1–§3.3 的上传校验（类型表、大小、文件名清洗）与 §3.5 的下载接口按 Category 分支即可扩展，不需要改现有分支。
- `FileDto` 的 `name / size / kind / ext` 与 `FileCenterItem` 的行结构可直接用于资料库列表；PC「文件」页届时加一个顶层切换（工单文件 / 公司资料）。
- 因此本期**不要**把「必须挂工单」写死在 `FileService.Upload` 或下载接口的公共路径里（只在附件接口与文件中心查询里限定）。

---

## 11. 各实现者改动的文件

**后端（`server/`、`deploy/README.md`、`server/README.md`）**
`Domain/Entities.cs`、`Domain/Enums.cs`、`Data/AppDbContext.cs`、`Data/Migrations/*V12_Attachments*`（+ 快照）、`Data/SeedData.cs`、`Dtos/CommonDtos.cs`、`Dtos/RequestDtos.cs`、`Dtos/ViewDtos.cs`、
`Services/FileService.cs`、`Services/AttachmentRules.cs`（新）、`Services/AttachmentService.cs`（新，`partial class OrderService`）、`Services/FileCenterService.cs`（新）、`Services/OrderService.cs`（Create）、`Services/OrderViewBuilder.cs`、
`Controllers/FilesController.cs`、`Controllers/OrdersController.cs`、`Controllers/AdminController.cs`、`Infrastructure/ExceptionMiddleware.cs`、`Program.cs`（DI、`/uploads` 的 nosniff）、
`tests/`（§9.1、§9.2）、`scripts/smoke.sh`（§9.3）、`server/README.md`「文件」一节、`deploy/README.md`（仅 §8.1 所述内容）。

**小程序（`miniprogram/`）**
`utils/request.js`、`utils/attachments.js`（新）、`utils/icons.js`（如需新图标）、`api/index.js`、`pages/order-new/*`、`pages/order-detail/*`、`pages/privacy/index.wxml`、`README.md`。

**PC 后台（`server/src/Huotong.AfterSales.Api/wwwroot/admin/`）**
`js/api.js`、`js/enums.js`、`js/format.js`、`js/ui.js`、`js/main.js`、`js/views/attachments-upload.js`（新）、`js/views/files.js`（新）、`js/views/orders-create.js`、`js/views/orders-drawer.js`、`css/admin.css`、`README.md`。

三方互不修改对方目录下的文件。

---

## 12. 超出需求、需产品确认的决定

1. **受限附件的静态地址不鉴权**：`/uploads/{随机名}` 仍可匿名访问；保护手段是工程师从任何接口都拿不到记录与地址。若要求「即使拿到链接也打不开」，需把附件改为只经下载接口输出（小程序图片预览要先下载，体验变差）。
2. **不是建单人的销售不能添加附件**（按需求字面：建单人、负责人、协助人、主管）。
3. **工程师自己上传的附件被主管设为受限后，本人也看不到**。
4. **建单照片并入附件列表**：旧的建单照片与新附件是同一个列表，共用 20 个上限，同样可删除、可设受限。
5. **图片上限保持 20 MB**，只有视频和文档到 100 MB；**完工照片、打卡照片、返厂照片、头像不变**（压缩图、20 MB）。
6. **不生成缩略图**：详情里的附件只显示类型角标；文件中心只给 ≤ 2 MB 的图片显示缩略图，其余显示角标。
7. **TXT / CSV 在小程序里不能直接预览**（微信 `openDocument` 不支持），改为「转发到聊天」；PC 端直接下载。
8. **所有类别的上传都改为按文件内容识别**：不再信任客户端声明的类型，SVG、TIFF、AVIF 等原先可能混进来的格式现在会被拒绝。
9. **Office 文件的内容校验较宽**：六种扩展名都接受「OLE 头或含 `[Content_Types].xml` 的 ZIP」（为了放行加密文档），不区分 Word / Excel / PPT 之间的错配。
10. **添加附件不发钉钉通知**；建单时的附件不单独写状态记录。
11. **文件中心包含已作废工单的文件**，默认不限日期，每页 30 条；新增了需求之外的 `orderId`、`restricted` 筛选和按类型的 `counts`。
12. **主管可以在已作废的工单上删除附件、切换受限**（需求只写了「作废后不能添加」）。
13. 小程序 tab「资料」改名为「附件资料」；小程序里设置受限：建单页长按单个附件，详情页添加时整批一个开关。
14. nginx 除 `client_max_body_size 110m` 外，建议同时把 `proxy_read_timeout` 提到 300s 并加 `proxy_send_timeout 300s`。

---

## 13. 后端安全审查后的补充（实现与上文不同之处，以本节为准）

- **`/uploads` 静态地址**：图片、视频匿名可访问（预览 / 缩略图 / v1.1 小程序依赖，受限图片也一样，见 CHANGES-v1.2 §12.1）；**受限或已删除的文档**（pdf / Office / txt / csv）静态地址返回 404，只能走 `GET /api/files/{id}/download`；非图片视频的内容一律带 `Content-Disposition: attachment` 输出（不在本站源下渲染）；`.heic` / `.heif` 已补进静态类型表。
- **设为受限会收起旧记录**：把附件设为「仅销售和主管可见」时，本单此前带该文件名的 `Attachment` 状态记录一并改为 `AttachmentPrivate`（工程师不再看到文件名；同一条记录里的其他文件名一起隐藏；取消受限后不恢复）。
- **头像与工单文件互斥**：已挂工单的文件不能设为头像（`头像文件已用于工单，请重新上传`）；`Avatar` 类别的文件不能当附件 / 照片挂到工单（`…不存在，请重新上传`）。
- **类型识别收紧**：BMP 还要求保留字段为 0；ISO-BMFF / QuickTime 要求首字节 ≤ 0x08（避免把「The free…」之类的文本误判成视频）；AVIF（`ftyp avif/avis`）拒绝；`category` 不接受数字；文件名清洗额外去掉不可见的格式字符（零宽字符、RLO 等）。

