# v1.3 变更契约：设备资料（多台设备）+ 附件分类与随时可传 + 转交（后端 / 小程序 / PC 后台 三方并行实现的唯一依据）

本文件是 v1.3 的**完整契约**。三位实现者（后端、小程序、PC 后台）只按本文件与 `docs/API.md`（已合并 v1.3）实现，互不沟通也能对上。
凡本文件写「必须 / 固定为」的地方不得自行变通；文案（错误信息、状态记录、按钮文字、提示语）按原文逐字实现。两份文档冲突时以本文件为准。

三项变更：
- **(1) 设备资料**：建单表单的「设备资料」区改为「*项目编号 → 设备（可多台）→ *是否已完成 SAT → 时间（选填）」；一张工单可以带多台设备；SAT 是否完成与对应时间建单后可以改。
- **(2) 附件分类 + 随时可传**：附件分三类（只分类、不做任何强制检查）；闭环之后建单人 / 负责人 / 协助人 / 主管也能继续上传；每单上限 20 → 50。
- **(3) 转交**：负责工程师把工单转给另一位工程师，对方「确认接手 / 拒绝」，不需要主管批准；原负责人变成「已退出」（只读）。

通用约定不变：响应包装 `{code,message,data}`；camelCase；枚举传字符串；时间北京时间 `yyyy-MM-ddTHH:mm:ss`；
错误码 `40000` 参数、`40300` 权限、`40400` 不存在、`40900` 状态冲突；工单动作成功后返回最新 **OrderDetail**（唯一例外见 §5.4「拒绝接手」）。

名词：
- **参与人** = 负责人 + 协助人（同 v1.1）。**负责人** = `WorkOrder.AssigneeId`。
- **已退出**（本文专指有「退出记录」的人）= 曾经是本单负责人、后来被转交或被主管改派换掉的工程师。他对这张单**只读**（§6）。
  v1.1 里「在本单有打卡但已不是负责人 / 协助人」的人在参与人列表里也显示为「已退出」，那只是展示，不带任何权限（被移除的协助人仍然看不了工单，行为不变）。
- **待确认的转交** = `OrderTransfers` 里 `Status = Pending` 的那一行；每张工单同一时间最多一行。**接手人** = 它的 `ToEngineerId`。
- **附件** = 挂在工单上、`Category` 为 `OrderPhoto` 或 `Attachment` 且未删除的文件（同 v1.2）。**附件类别** = v1.3 新增的三分类（§1.3），与 `FileCategory`、文件来源 `source`、文件类型 `kind` 都不是一回事。

---

## 1. 数据模型（全部为增量迁移）

一个新迁移：`V13_DevicesTransfers`（`server/src/Huotong.AfterSales.Api/Data/Migrations/`，在 `server/src/Huotong.AfterSales.Api` 下执行 `dotnet ef migrations add V13_DevicesTransfers -o Data/Migrations`）。
对已有表只允许 `AddColumn` 与 `CreateIndex`，不得删除列、改列类型、给已有表加外键（会触发 SQLite 重建表）。新表可以带外键。
线上是带真实数据的 SQLite：`Up` 的末尾必须包含 §1.5 的回填 SQL；必须在「当前 HEAD 构建生成并带数据的库」上启动新构建验证（§11.4）。

### 1.1 已有表新增列

| 表 | 列 | 类型 | 可空 | 默认值 | 说明 |
|---|---|---|---|---|---|
| `WorkOrders` | `DeviceText` | string(250) | 否 | `""` | 设备汇总文字（§3.2），建单时写入，之后不变。列表、通知、导出等一律读它，**不需要 Include 设备表** |
| `WorkOrders` | `PendingTransferToId` | int | 是 | null | 待确认转交的接手人 id；null = 没有待确认的转交。**普通整数列，不建外键、不建导航属性**。与 `OrderTransfers` 的一致性见 §5.1 |
| `StoredFiles` | `AttachmentType` | string(20) | 否 | **`'Other'`** | 附件类别。只对附件有意义；其他类别的文件恒为 `Other` |

新索引：`IX_WorkOrders_PendingTransferToId`。

已有列**全部保留并继续写入**（旧客户端、旧代码路径依赖它们）：
- `WorkOrders.DeviceModel` = 第一台设备的 `Title`（§3.2），没有设备为 `""`；`WorkOrders.DeviceSn` = 第一台设备的编号，没有为 `""`。它们从 v1.3 起是「兼容列」，新代码不要再用它们做展示或搜索。
- `WorkOrders.SatSigned`（bool）沿用，v1.3 的含义是「是否已完成 SAT」；`WorkOrders.SatDate`（string）沿用，v1.3 的含义是「SAT 完成时间（SatSigned = true）/ 发货时间（SatSigned = false）」，取值为 `""`、`yyyy-MM-dd`，或旧数据的 `yyyy-MM`。**不改列名、不改 JSON 字段名**。
- `WorkOrders.ProjectNo`（string?）沿用；旧单保持 null。

**EF 配置注意**：三个新列都**不要**配置 `HasDefaultValue`（否则 EF 会吞掉与 CLR 默认值相同的显式值），列默认值只写在迁移里。
EF 为非空字符串生成的 `AddColumn` 默认值是 `""`——`AttachmentType` 那一处必须**手工改成 `defaultValue: "Other"`**。
所有新枚举列（`StoredFiles.AttachmentType`、`OrderTransfers.Status`、`OrderExits.Via`）与现有枚举列一样配置 `HasConversion<string>().HasMaxLength(20)`（库里存枚举名）。
`DeviceText` 配 `HasMaxLength(250)`；`OrderDevices` 的三个字段各 `HasMaxLength(100)`；`OrderTransfers.Reason` / `Reply` 各 `HasMaxLength(200)`。

迁移的时间戳必须晚于 `20261111031917_V12_Attachments`（`dotnet ef` 用当前 UTC 时间生成，正常生成即可，不要手工改小）。
迁移只在 SQLite 上执行（`Program.cs` 对非 SQLite 走 `EnsureCreated`），回填 SQL 用 SQLite 语法即可。

### 1.2 新表

**`OrderDevices`**（工单的设备清单；建单时写入，本期没有修改入口）

| 列 | 类型 | 说明 |
|---|---|---|
| `Id` | int PK | |
| `OrderId` | int | FK → `WorkOrders`，级联删除。导航 `WorkOrder.Devices` |
| `Seq` | int | 从 1 开始的序号（= 表单里的顺序） |
| `Name` | string(100)，非空 | 设备名称，可为 `""` |
| `Sn` | string(100)，非空 | 设备编号，可为 `""` |
| `Model` | string(100)，非空 | 设备型号，可为 `""` |

索引：`IX_OrderDevices_OrderId`。同一行的三项至少一项非空（由服务层保证）。

**`OrderTransfers`**（转交记录，保留全部历史）

| 列 | 类型 | 说明 |
|---|---|---|
| `Id` | int PK | |
| `OrderId` | int | FK → `WorkOrders`，级联删除（`WithMany()`，不在 `WorkOrder` 上加导航） |
| `FromEngineerId` | int | 发起人（当时的负责人），FK → `Employees`，Restrict。导航 `From` |
| `ToEngineerId` | int | 接手人，FK → `Employees`，Restrict。导航 `To` |
| `Reason` | string(200)，非空 | 转交原因 |
| `Status` | string(20) | `TransferStatus` |
| `CreatedAt` | DateTime | 发起时间 |
| `DecidedAt` | DateTime? | 结束时间（接手 / 拒绝 / 撤回 / 失效） |
| `DecidedById` | int? | 结束它的人：接手、拒绝 = 接手人；撤回 = 发起人或主管；系统失效 = null。FK → `Employees`，Restrict。导航 `DecidedBy` |
| `Reply` | string(200)? | 拒绝理由，或系统失效的原因 |

索引：`IX_OrderTransfers_OrderId`、`IX_OrderTransfers_ToEngineerId_Status`。

**`OrderExits`**（退出记录：每张工单每个「已退出」的人最多一行）

| 列 | 类型 | 说明 |
|---|---|---|
| `Id` | int PK | |
| `OrderId` | int | FK → `WorkOrders`，级联删除。导航 `WorkOrder.Exits` |
| `EngineerId` | int | FK → `Employees`，Restrict。导航 `Engineer` |
| `Via` | string(20) | `ExitVia`：怎么退出的 |
| `ExitedAt` | DateTime | 退出时间 |

索引：`IX_OrderExits_OrderId_EngineerId`（唯一）、`IX_OrderExits_EngineerId`。

`AppDbContext` 新增 `DbSet`：`OrderDevices`、`OrderTransfers`、`OrderExits`。
实体与导航的名字固定为：`OrderDevice`（`WorkOrder.Devices : List<OrderDevice>`）、`OrderTransfer`（导航 `From` / `To` / `DecidedBy`，`WorkOrder` 上没有集合导航）、
`OrderExit`（`WorkOrder.Exits : List<OrderExit>`，导航 `Engineer`）。三张新表都没有全局查询过滤器（`StoredFile` 的软删除过滤器与它们无关）。

### 1.3 枚举（C# 名 = JSON 字符串值）

| 枚举 | 值（中文） |
|---|---|
| `AttachmentType`（新，落库） | **声明顺序必须是 `Other, Sat, RepairForm`**（`Other` 是 CLR 默认值，保证任何地方 `new StoredFile()` 默认就是「其他」）。**展示顺序固定为 `Sat → RepairForm → Other`**（`AttachmentRules.TypeOrder`） |
| `TransferStatus`（新，落库） | `Pending` 待确认、`Accepted` 已接手、`Rejected` 已拒绝、`Cancelled` 已撤回、`Expired` 已失效 |
| `ExitVia`（新，落库） | `Transfer` 已转交、`Reassign` 已改派 |
| `LogKind` | 新增 `Info`（资料修改，所有角色可见）。完整：`Status, Price, Hold, Assist, Factory, Attachment, AttachmentPrivate, Info` |
| `ParticipantRole` | 值不变；`Former` 的来源多了「有退出记录」（§6） |

附件类别的文字（**全系统唯一**；后端在 `EnumText`，两个前端各在自己的 `enums.js` 里写同一张表，逐字一致）：

| `attachmentType` | 完整标题 `label`（分组标题、上传时的选项） | 短标题 `short`（标签、表格列、状态记录） |
|---|---|---|
| `Sat` | `SAT 验收单（签字版）` | `SAT 验收单` |
| `RepairForm` | `现场维修登记表/返厂维修登记表（签字版）` | `维修登记表` |
| `Other` | `设备照片、沟通记录及其他附件` | `其他附件` |

`OrderAction`（状态机输入）不变：转交不改变工单状态。

### 1.4 两条不变量（后端所有写路径都必须维持，§11.2 有测试）

- **I1（待确认转交）**：`WorkOrders.PendingTransferToId != null` ⇔ 本单恰有一行 `OrderTransfers.Status = Pending`，且该行 `ToEngineerId = PendingTransferToId`、`FromEngineerId = 工单当前 AssigneeId`、工单状态 ∈ {Accepted, InProgress, OnHold}。
  两处只通过 `TransferService.cs` 里的方法一起改：创建（`StartTransfer`）与结束（`EndPendingTransfer`，它与 `AdminService` 共用静态的 `TransferRules.End`，§5.1）。任何地方都不要直接给 `PendingTransferToId` 或 `OrderTransfer.Status` 赋值。
  「状态 ∈ {Accepted, InProgress, OnHold}」靠三件事保证：提交完工被拦截、作废时失效、其余离开这三个状态的动作（退回、闭环）只发生在不可能有待确认转交的状态上。
- **I2（退出记录）**：`OrderExits` 里的人一定**不是**该单当前的负责人或协助人。任何让某人成为负责人 / 协助人的写操作都要删除他在本单的退出记录（`RemoveExit`）。

### 1.5 迁移里的旧数据回填（写在 `Up` 的最后，`migrationBuilder.Sql`）

```sql
-- 旧单的「型号 + 序列号」变成一台设备（名称为空）
INSERT INTO OrderDevices (OrderId, Seq, Name, Sn, Model)
SELECT Id, 1, '', trim(DeviceSn), trim(DeviceModel) FROM WorkOrders
WHERE trim(DeviceModel) <> '' OR trim(DeviceSn) <> '';

-- 设备汇总文字：旧单只有一台设备 → 就是它的型号（没有型号用编号）
UPDATE WorkOrders SET DeviceText = CASE
  WHEN trim(DeviceModel) <> '' THEN trim(DeviceModel)
  WHEN trim(DeviceSn) <> '' THEN trim(DeviceSn)
  ELSE '未填设备' END;
```

- 旧附件（含 `OrderPhoto` 建单照片）靠列默认值全部落在 `Other`，不需要额外语句。
- 旧单的 `ProjectNo` 保持原样（null 的仍为 null，界面展示「—」）；`SatDate` 保持 `yyyy-MM`；`SatSigned` 原值即「是否已完成 SAT」。
- **不回填退出记录**（旧数据里被改派走的原负责人没有可靠依据）；他们在参与人列表里仍按 v1.1 规则由打卡记录推导显示「已退出」，但没有查看权。
- `Down`：删三张新表、删索引、删三个新列（开发库回滚用；线上不执行）。

### 1.6 种子数据（`SeedData`，仅空库时写入）

- 每张种子工单写 1 行 `OrderDevices`（`Name=""`、`Model` = 原 `model` 参数、`Sn` = 原 `sn` 参数）并设置 `DeviceText`（= 原 `model`）。`DeviceModel` / `DeviceSn` 的值**不变**（现有测试断言它们）。
- o5「南京某检测中心」改成多设备示例：再加 2 行设备（`搅拌器 / SN2405010 / HTS-80`、`冷凝器 / SN2405011 / HTC-20`），`DeviceText = "高压反应釜 HTR-250 等 3 台"`，`ProjectNo = "HT-2024-031"`。其余种子工单的 `ProjectNo` 保持现状。
- 不写种子转交、不写种子退出记录；种子附件的 `AttachmentType` 用默认值 `Other`。

---

## 2. DTO 变更

### 2.1 DeviceDto（新）

```json
{ "seq": 1, "name": "高压反应釜", "sn": "SN2408017", "model": "HTR-500", "title": "高压反应釜 HTR-500" }
```
`title` 见 §3.2。三个字段没填的为 `""`。

### 2.2 TransferDto（新）

```json
{
  "id": 5, "orderId": 3,
  "status": "Pending", "statusText": "待确认",
  "from": { "UserBrief": "" }, "to": { "UserBrief": "" },
  "reason": "电气部分我搞不定，需要更熟的同事",
  "createdAt": "2026-11-11T14:00:00",
  "decidedAt": null, "decidedBy": null,           // 结束时间 / 结束它的人（系统失效时 decidedBy 为 null）
  "reply": null,                                   // 拒绝理由 / 失效原因
  "text": "李工 转交给 王工，待 王工 确认"           // 可直接展示的一句话
}
```
`text` 固定规则（`{from}`、`{to}`、`{by}` 为姓名）：

| status | text |
|---|---|
| `Pending` | `{from} 转交给 {to}，待 {to} 确认` |
| `Accepted` | `{from} 转交给 {to}，{to} 已接手` |
| `Rejected` | `{from} 转交给 {to}，{to} 已拒绝`，有理由时追加 `：{reply}` |
| `Cancelled` | 发起人撤回（`DecidedById == FromEngineerId`）：`{from} 转交给 {to}，已撤回`；主管撤销（其余情况）：`{from} 转交给 {to}，已由 {by} 撤销` |
| `Expired` | `{from} 转交给 {to}，已失效：{reply}` |

- `statusText` = `TransferStatus` 的中文（§1.3）；主管撤销的那一条 `status` 也是 `Cancelled`、`statusText` 也是 `已撤回`，区别只在 `text` 与 `decidedBy`。
- 映射统一放在 `OrderViewBuilder.TransferToDto(OrderTransfer t)`（详情的 `transfers`、列表的 `pendingTransfer` 共用）；调用方负责把 `From` / `To` / `DecidedBy`（各带 `AvatarFile`）Include 进来。
- TransferDto 里**没有** `can*` 字段：能不能接手 / 拒绝 / 撤回只看 `OrderDetail.actions`（§2.9）。

### 2.3 OrderListItem（所有列表、首页、打卡返回里的工单项）新增 / 变更字段

```json
{
  "projectNo": "HT-2024-031" | null,     // 从 OrderDetail 上移到这里；旧单为 null（界面展示「—」）
  "deviceText": "高压反应釜 HTR-250 等 3 台",   // 设备汇总文字，永不为空（没有设备 = "未填设备"）。列表、标题一律显示它
  "deviceModel": "高压反应釜 HTR-250",    // 保留（旧客户端用）：第一台设备的 title；v1.3 客户端不再读
  "deviceSn": "SN2405009",               // 保留（旧客户端用）：第一台设备的编号；v1.3 客户端不再读
  "myRole": "Lead" | "Assistant" | "Former" | null,   // 新增取值 Former：当前用户是本单「已退出」的人（有退出记录）
  "myExitText": "已转交" | "已改派" | null,           // 仅 myRole = Former 时非 null
  "pendingTransfer": null | { "TransferDto": "" }     // 本单待确认的转交；任何能看到这张单的人都能看到
}
```
- `deviceText` 取 `WorkOrders.DeviceText`；该列为空白时（防御）退回 `DeviceModel`，仍为空则 `未填设备`。
- `myMinutes` / `myOnSiteSince` 在 `myRole = Former` 时同样计算（他自己的工时仍然显示）。
- `myRole` 的计算：`RoleIn(o, userId)`（**不改**，仍只返回 Lead / Assistant / null）为 null 且 `OrderViewBuilder.IsFormer(o, user)`（§6.1，含「当前角色是 Engineer」这个条件）为 true → `Former`。
  **`OrderViewBuilder.RoleIn` 绝对不能返回 `Former`**：打卡权限（`PunchService.EnsureParticipant`）、`PunchContextDto.myRole`、详情里的 `IsAssistant` 都靠它判断。
  `myExitText` = 本人那条退出记录的 `Via` 的中文（`Transfer` → `已转交`，`Reassign` → `已改派`；后端 `EnumText.ExitText`）。
- **实现路径固定**（详情与列表必须走同一处，避免一边有一边没有）：
  - `OrderViewBuilder.ListExtras` 增加第三项 `Dictionary<int, OrderTransfer> PendingTransfers`；`Extras()` 里一次查出这些工单的 Pending 转交（Include `From` / `To` 及各自的 `AvatarFile`）。
  - `FillList` 增加可选参数 `OrderTransfer? pendingTransfer = null`，在里面统一写 `ProjectNo`、`DeviceText`、`MyRole`（含 Former）、`MyExitText`、`PendingTransfer`；`BuildList` / `BuildOne` 从 `Extras` 取出传入。
  - `BuildDetail` 自己查本单全部转交（给 `transfers` 用），把其中 Pending 的那一条传给 `FillList`。
  - `FillList` 读 `o.Exits`，所以传进来的工单必须来自 `OrderViewBuilder.Orders()`（现有调用方都是，见 §6.1）。
- `projectNo` 从 `OrderDetail` 类上移到 `OrderListItem` 类（删掉 `OrderDetail` 里的同名属性，避免隐藏基类成员）。

### 2.4 OrderDetail 新增 / 变更字段

```json
{
  "devices": [ { "DeviceDto": "" } ],          // 按 seq 正序；没有设备为 []
  "satSigned": true,                            // 含义改为「是否已完成 SAT」（字段名不变）
  "satDate": "2026-03-15" | "2024-08" | "",     // SAT 完成时间 / 发货时间；可为空；旧数据是 yyyy-MM
  "satText": "已完成" | "未完成",                 // satSigned 的文字
  "satDateLabel": "SAT 完成时间" | "发货时间",     // satSigned ? "SAT 完成时间" : "发货时间"
  "productStage": "老产品" | "新产品",            // 不变：satSigned ? 老产品 : 新产品
  "canEditSat": true,                           // 当前用户现在可以修改 SAT 信息（§3.5）
  "transfers": [ { "TransferDto": "" } ],       // 本单全部转交记录（含已结束的），按 createdAt、id 正序
  "readOnlyText": null | "你已退出本单（已转交），只能查看；你的打卡和工时记录已保留。",   // 仅当前用户是「已退出」的工程师时非 null
  "attachmentLimit": 50                         // 20 → 50
}
```
- `readOnlyText` 固定为 `你已退出本单（{myExitText}），只能查看；你的打卡和工时记录已保留。`；仅 `IsFormer(o, user)` 时非 null（主管 / 销售、待确认的接手人都是 null）。
- `devices`：`BuildDetail` 单独查一次 `OrderDevices`（`OrderId == id`，按 `Seq`）；**`OrderViewBuilder.Orders()` 不要 Include `Devices`**（列表只用 `DeviceText`）。
- `participants[]` 每项新增 `"exitText": "已转交" | "已改派" | null`、`"exitedAt": "…" | null`（只有带退出记录的 `Former` 有值；由打卡推导出来的 `Former` 两项都是 null）。
  `Former` 的集合 = 「本单有退出记录的人」∪「在本单有打卡的人」，去掉当前负责人与协助人；同一个人只出现一次（有退出记录的以退出记录为准）。
  排序：负责人 → 协助人（按添加时间）→ `Former`（有退出记录的按 `ExitedAt`、没有的按首次打卡时间，混在一起正序，时间相同按员工 id）。
  **有退出记录的人即使一次卡都没打过也要出现**（远程 / 返厂单），此时 `totalMinutes = 0`、`onSiteSince = null`、`missingPunch = false`。
  （取退出人的姓名、头像要用 `o.Exits[].Engineer`，Include 见 §6.1。）
- `attachmentLimit`：DTO 类里的默认值 `OrderDetail.AttachmentLimit { get; set; } = 20` 也要改成 50（§4.7）。
- `logs[].kind` 多一个值 `Info`。
- `attachments[]` 见 §2.5。`files.*` 不变。

### 2.5 AttachmentDto / FileCenterItem 新增字段

```json
{ "attachmentType": "Sat" | "RepairForm" | "Other" | null,
  "attachmentTypeLabel": "SAT 验收单（签字版）",    // 完整标题；非附件行为 ""
  "attachmentTypeShort": "SAT 验收单",             // 短标题；非附件行为 ""
  "canChangeType": true }                          // 当前用户现在可以改这个附件的类别（§4.5）
```
文件中心里非附件来源的行（打卡照片、完工照片…）：`attachmentType = null`、两个文字为 `""`、`canChangeType = false`。

### 2.6 首页与其他 DTO

| DTO | 变更 |
|---|---|
| `EngineerHomeDto` | 新增 `"transfersToMe": [OrderListItem]`：待我确认的转交（`PendingTransferToId = 我` 的工单，每项带 `pendingTransfer`），按转交发起时间正序 |
| `RecentCustomerDto` | 新增 `"devices": [ { "name": "", "sn": "", "model": "" } ]`（该客户最近一张工单的设备清单）；`deviceModel` / `deviceSn` / `satDate` / `satSigned` / `projectNo` 保留 |
| `ListResult.counts`（`/api/orders/mine`） | 新增键 `"former"` |
| `StatusLogDto` | `kind` 新增 `Info` |

`MeDto`、`SupervisorHomeDto`、`OverviewDto`、`TodoDto`、`PunchContextDto`、工时相关 DTO **不变**。

### 2.7 请求体

```csharp
public class DeviceItem { public string? Name { get; set; } public string? Sn { get; set; } public string? Model { get; set; } }
// CreateOrderRequest 新增：
public List<DeviceItem>? Devices { get; set; }
// AttachmentItem 新增（字符串接收，非法值由服务层报「附件类别不正确」）：
public string? Type { get; set; }
public class SatRequest { public bool? SatSigned { get; set; } public string? SatDate { get; set; } }
public class AttachmentTypeRequest { public string? Type { get; set; } }
public class TransferRequest { public int? EngineerId { get; set; } public string? Reason { get; set; } }
// 拒绝接手沿用 ReplyRequest { Reply }
// FileCenterQuery 新增（[FromQuery] 模型绑定，查询参数名 attachmentType）：
public string? AttachmentType { get; set; }
```
- `devices`：JSON 里**没有这个键**或值为 `null` → 属性为 null（= 旧客户端）；`[]` → 空列表（= 新客户端明确表示没有设备）。两者含义不同（§3.1）。
- 附件类别的解析全系统只有一处：`AttachmentRules.TryParseType(string? s, out AttachmentType? type)`。
  空白 / null → 返回 true、`type = null`（由调用方决定含义：添加 = `Other`，改类别 = 报 `请选择附件类别`，文件中心 = 不筛选）；
  `Trim()` 后大小写不敏感地等于枚举名 `Sat` / `RepairForm` / `Other` → 返回 true 与对应值；其他任何值（含以数字或 `-` 开头的，如 `"1"`）→ 返回 false。
  添加附件、改类别、文件中心筛选三处都用它，不要各写一份（`OrderService.ParseEnum` 会接受数字，不能用）。
- 新增的控制器动作与现有的写法一致：请求体参数写成可空并 `req ?? new()`（请求体为空也返回中文业务错误，而不是模型绑定错误）。

### 2.8 Engineer 可见性硬规则（v1.1 价格、v1.2 受限附件）在新路径上的要求

- 接手人、已退出的人都是 `Engineer`：他们拿到的 OrderDetail / OrderListItem 走同一个 `FillList` / `BuildDetail`，`price`、`priceSet`、`priceSetAt`、`priceSuggestion` 为 null，`logs` 不含 `Price` / `AttachmentPrivate`，`attachments` 不含受限附件。**不得为新查看路径另写一套映射**。
- 转交相关的状态记录一律 `kind = Assist`，正文只有姓名与用户填写的原因 / 理由；通知正文不出现金额、不出现附件名。
- TransferDto、`transfersToMe` 里没有任何价格字段（`transfersToMe` 的项就是普通 OrderListItem）。
- 文件下载（`GET /api/files/{id}/download`）、文件中心对接手人 / 已退出的人：能查看该工单 → 可以拿到非受限文件；受限附件仍然 40400。
- `/uploads` 静态地址**不改**：图片 / 视频本来就是匿名可访问（不按工单判权），受限或已删除的文档由现有的 `FileService.StaticBlocked` 拦截——与「谁能看工单」无关，转交 / 退出不影响它。
- 转交接口本身不泄露工单内容：无关工程师调用四个转交接口只会得到 40300 / 40400 的固定文案；接手人被拒绝 / 撤回 / 失效后立即回到「无权查看该工单」。
- 发起转交不能成为越权通道：只有当前负责人能发起，目标只能是启用中的工程师（销售、主管不能成为接手人），接手人在确认前只有与协助人相同的**只读**范围（不含价格、受限附件）。

### 2.9 actions（`OrderDetail.actions`，服务端计算，UI 不得自行推导规则）

`OrderStateMachine.ActionContext` 末尾新增 3 个带默认值的参数（现有调用与测试不用改就能编译）：
`bool IsFormer = false, bool HasPendingTransfer = false, bool PendingTransferToMe = false`。（SAT 修改不走 actions，见下，所以不需要「是否建单人」。）
最终签名：`ActionContext(Role, UserId, Status, AssigneeId, ServiceType, HasMissingPunch, IsAssistant, PriceSet, HasPendingHoldRequest, FactoryCanRecord, FactoryShippedBack, IsFormer, HasPendingTransfer, PendingTransferToMe)`。

`BuildDetail` 里三个新事实的取值：`IsFormer = OrderViewBuilder.IsFormer(o, user)`；`HasPendingTransfer = o.PendingTransferToId != null`；
`PendingTransferToMe = user.Role == Role.Engineer && o.PendingTransferToId == user.Id`。

返回数组按下列**固定顺序**过滤得到。

**Engineer**：`claim`, `appoint`, `checkin`, `start`, `factory`, `complete`, `requestHold`, `requestAssistant`, **`transfer`**, **`cancelTransfer`**, **`acceptTransfer`**, **`rejectTransfer`**

| 动作 | 条件 |
|---|---|
| （前置）`IsFormer = true` | **只**可能有 `acceptTransfer`、`rejectTransfer`（条件同下）；其余一律没有——直接返回 |
| `claim` … `factory`、`requestHold`、`requestAssistant` | 同 v1.1，不变 |
| `complete` | v1.1 的条件 **且 `!HasPendingTransfer`** |
| `transfer` | 本人是负责人，状态 ∈ {Accepted, InProgress, OnHold}，`!HasPendingTransfer` |
| `cancelTransfer` | 本人是负责人，`HasPendingTransfer` |
| `acceptTransfer` | `PendingTransferToMe` |
| `rejectTransfer` | `PendingTransferToMe` |

**Supervisor**：`assign`, `reassign`, `assistants`, `void`, `hold`, `resume`, `reject`, `close`, `supplement`, `setPrice`, `factory`, **`cancelTransfer`**

| 动作 | 条件 |
|---|---|
| 前 11 个 | 同 v1.1，不变 |
| `cancelTransfer` | `HasPendingTransfer` |

**Sales**：不变（`suggestPrice`, `factory`）。

不进 actions 的：SAT 修改看 `canEditSat`；附件看 `canAddAttachment`、`canRestrictAttachment`、`attachments[].canDelete / canToggleRestricted / canChangeType`。

示例（供测试断言）：
- Engineer 负责人、无待确认转交：Accepted 现场 → `["appoint","checkin","requestHold","requestAssistant","transfer"]`；Accepted 远程 → `["appoint","start","requestHold","requestAssistant","transfer"]`；
  InProgress 现场 → `["appoint","checkin","complete","requestHold","requestAssistant","transfer"]`；OnHold → `["transfer"]`；Submitted / Closed → 同 v1.1（没有 `transfer`）。
- Engineer 负责人、有待确认转交：InProgress 现场 → `["appoint","checkin","requestHold","requestAssistant","cancelTransfer"]`（没有 `complete`、没有 `transfer`）；OnHold → `["cancelTransfer"]`。
- 接手人（不是本单参与人）：`["acceptTransfer","rejectTransfer"]`；接手人同时是本单协助人、InProgress 现场：`["checkin","acceptTransfer","rejectTransfer"]`。
- 已退出的工程师：`[]`；已退出且又被转交回来待确认：`["acceptTransfer","rejectTransfer"]`。
- Supervisor、InProgress、有待确认转交：`["reassign","assistants","hold","setPrice","cancelTransfer"]`。

---

## 3. 设备资料

### 3.1 POST /api/orders（变更）

请求体里「设备资料」相关字段（其余字段不变）：
```json
{
  "projectNo": "HT-2024-031",                                    // 必填
  "devices": [ { "name": "高压反应釜", "sn": "SN2405009", "model": "HTR-250" },
               { "name": "搅拌器", "sn": "", "model": "HTS-80" } ],   // 选填，可为 []
  "satSigned": true,                                             // 必填：是否已完成 SAT
  "satDate": "2026-03-15"                                        // 选填："" / null / yyyy-MM-dd（也接受旧格式 yyyy-MM）
}
```

**设备清单的归一化**（`DeviceRules.Normalize`）：
1. 请求里**有** `devices` 字段（数组，含 `[]`）→ 用它，忽略旧字段 `deviceModel` / `deviceSn`。
2. 请求里**没有** `devices`（省略或 null）→ 旧客户端：`deviceModel`、`deviceSn` 任一非空白时合成一台设备 `{ name: "", sn: deviceSn, model: deviceModel }`；都空白 → 没有设备。
3. 每台设备三项各自 `Trim()`；三项全空的行**直接丢弃**（不报错）；`null` 元素丢弃。剩下的按原顺序编号 `Seq = 1..n`。

**校验**（替换原来「设备型号 / 设备编号 / 购买 SAT 时间 / SAT 是否已签字」四条；位置不变——在经纬度校验之后、「发生时间」之前；全部 40000）：
1. `projectNo` 空白 → `请填写项目编号`；超过 50 个字 → `项目编号不能超过 50 个字`
2. 归一化后设备超过 20 台 → `设备最多 20 台`
3. 任一台的某项超过 100 个字 → `设备名称不能超过 100 个字` / `设备编号不能超过 100 个字` / `设备型号不能超过 100 个字`
4. `satSigned` 缺失 → `请选择是否已完成 SAT`
5. `satDate` 非空白且既不是合法日期 `yyyy-MM-dd` 也不是 `yyyy-MM` → `SAT 完成时间格式应为 yyyy-MM-dd`（`satSigned = true`）/ `发货时间格式应为 yyyy-MM-dd`（`satSigned = false`）。不限制早晚（可以是将来的日期）。

已删除的旧文案（不再出现）：`请填写设备型号`、`请填写设备编号`、`请填写购买/SAT 时间`、`购买/SAT 时间格式应为 yyyy-MM`、`请选择 SAT 是否已签字`。

落库（都在建单事务内）：`ProjectNo`；`OrderDevices` n 行；`DeviceText = DeviceRules.Summary(devices)`；`DeviceModel` = 第一台的 `Title`（没有设备 `""`）；`DeviceSn` = 第一台的 `Sn`（没有 `""`）；`SatSigned`；`SatDate`（空白存 `""`）。
设备不单独写状态记录。

### 3.2 设备文字（全系统唯一规则：`Services/DeviceRules.cs`，静态类）

- `DeviceRules.Max = 20`、`FieldMax = 100`、`None = "未填设备"`。
- **`Title(device)`**：名称与型号都非空 → `{名称} {型号}`（一个半角空格）；只有其一 → 那一个；都空 → 编号。例：`高压反应釜 HTR-250`、`HTR-500`、`SN2405011`。
- **`Summary(devices)`**（= `deviceText`）：0 台 → `未填设备`；1 台 → `Title(第 1 台)`；n ≥ 2 台 → `{Title(第 1 台)} 等 {n} 台`。例：`高压反应釜 HTR-250 等 3 台`。
- 两个前端**不得自己拼**汇总文字：列表、标题、选人弹层副标题一律显示服务端的 `deviceText`；详情里逐台展示用 `devices[]` 的三个原始字段。

### 3.3 用到设备信息的其他地方

| 位置 | v1.3 行为 |
|---|---|
| 工单搜索 `GET /api/orders?keyword=`（`OrderService.ApplyKeyword`） | 匹配：工单号 / 客户单位 / **项目编号** / **任意一台设备的名称、编号、型号**（`o.Devices.Any(d => d.Name.Contains(k) \|\| d.Sn.Contains(k) \|\| d.Model.Contains(k))`，子查询，不需要 Include）/ **设备汇总文字 `DeviceText`**（名称与型号分开存，照着列表上显示的「高压反应釜 HTR-250」整串搜也要能搜到）。不再直接匹配 `DeviceModel` / `DeviceSn` 列（旧数据已回填进设备表）。匹配是子串、区分大小写（SQLite 的既有行为，工单号 / 客户同样如此） |
| 文件中心 `keyword` | 在原有（文件名 / 工单号 / 客户）之外增加**项目编号** |
| 钉钉通知里的 `{o.DeviceModel} · {o.Symptom}`（紧急单、指派、协助提醒 3 处） | 改为 `{o.DeviceText} · {o.Symptom}` |
| `GET /api/orders/recent-customers` | 查询加 `Include(o => o.Devices)`，每项返回 `devices`（按 `Seq`）；旧字段照旧 |
| 导出 `GET /api/admin/export/orders` | 见 §3.4 |
| 文件中心行 | 不含设备字段，不变 |

### 3.4 导出（ClosedXML，`AdminService.ExportOrders`）

查询加 `Include(o => o.Devices)`。**现有 27 列的位置与表头都不变**（第 17、18 列的数字格式按列号写死，不能在中间插列），只改两列的内容，并在**最后**追加 3 列：

| 列 | 内容 |
|---|---|
| 第 1 列「项目编号」 | 不变（null → 空） |
| 第 4 列「设备型号」 | 每台设备的 `Title` 用 `；` 连接；没有设备 → 空 |
| 第 5 列「设备编号」 | 每台设备的编号用 `；` 连接，与第 4 列一一对应；某台没填编号且总数 ≥ 2 时用 `—` 占位；没有设备 → 空 |
| 第 28 列「设备台数」（新） | 整数 |
| 第 29 列「SAT 完成时间」（新） | `SatSigned = true` 时的 `SatDate`，否则空 |
| 第 30 列「发货时间」（新） | `SatSigned = false` 时的 `SatDate`，否则空 |

「产品阶段」列不变（`SatSigned ? 老产品 : 新产品`）。工时导出不变。

### 3.5 POST /api/orders/{id}/sat（新增；任何角色，服务端判权）

修改「是否已完成 SAT」与对应时间（两项一起提交，整体覆盖）。

```json
{ "satSigned": true, "satDate": "2026-03-15" }      // satDate 可为 "" / null（= 清空）
```
→ `{ OrderDetail }`

**谁可以改**（`canEditSat` 与接口校验的第 5、6 步共用同一个静态方法 `DeviceRules.SatEditDenied(o, user)`，返回 null = 可以）：
工单不是 Voided，**并且**当前用户是下列之一：Supervisor、建单人、当前负责人。
已退出的人不可以（即使他是建单人）；协助人、不是建单人的销售、待确认转交的接手人不可以。已闭环的工单**可以**改。

校验顺序：
1. `satSigned` 缺失 → 40000 `请选择是否已完成 SAT`
2. `satDate` 格式（规则同 §3.1 第 5 条）→ 40000 `SAT 完成时间格式应为 yyyy-MM-dd` / `发货时间格式应为 yyyy-MM-dd`
3. 工单不存在 → 40400 `工单不存在`
4. 不能查看该工单 → 40300 `无权查看该工单`
5. 工单 Voided → 40900 `工单已作废，不能修改 SAT 信息`
6. 不是 Supervisor 时：已退出 → 40300 `你已退出本单，只能查看`；既不是建单人也不是负责人 → 40300 `只有建单人、负责工程师或主管可以修改 SAT 信息`
7. 两项都没变化 → 直接返回详情，不写记录。

状态记录（`kind = Info`，`ToStatus` = 工单当前状态）：`{姓名} 修改 SAT 信息：{已完成|未完成} SAT，{SAT 完成时间|发货时间} {satDate}`；时间为空时结尾为 `{SAT 完成时间|发货时间}未填`。
例：`王敏 修改 SAT 信息：已完成 SAT，SAT 完成时间 2026-03-15`、`李工 修改 SAT 信息：未完成 SAT，发货时间未填`。不发通知。
实现：`OrderService.SetSat`，用 `Load(id)`（带 `Exits`）；`satDate` 存 `Trim()` 后的值（空白存 `""`）；不需要写锁。`productStage`（老产品 / 新产品）随 `SatSigned` 自动变化，不另写记录。

项目编号、设备清单**建单后不能修改**（§12）。

---

## 4. 附件分类 + 随时可传

### 4.1 权限规则（`AttachmentRules`，接口校验与 DTO 里的 `can*` 共用；`Limit` 改为 **50**）

| 规则 | v1.3 条件（**粗体**为变化） |
|---|---|
| **可添加** `canAddAttachment` | 工单不是 Voided，且：Supervisor；或当前用户是建单人 / 负责人 / 协助人 **且不是「已退出」**。**不再区分是否已闭环**（闭环后这四类人都能继续添加） |
| 可设受限 `canRestrictAttachment` | 不变：Sales、Supervisor |
| **可删除** `canDelete` | Supervisor（任何状态）；或上传人本人、工单不是 Closed / Voided、**且不是「已退出」**、**且（上传人是工程师时）仍是本单的建单人 / 负责人 / 协助人**。闭环后只能新增不能删除（主管除外）——规则不变 |
| 可切换受限 `canToggleRestricted` | 不变：Supervisor |
| **可改类别** `canChangeType`（新） | Supervisor（任何状态）；或上传人本人、工单不是 Voided、且不是「已退出」、且（上传人是工程师时）仍是本单的建单人 / 负责人 / 协助人（**已闭环也可以改**） |
| 可见 | 不变：Sales / Supervisor 全部；Engineer 只有未受限的（且能查看该工单） |

「仍是本单的建单人 / 负责人 / 协助人」这一条（`AttachmentRules.ReadOnlyViewer`）针对的是**待确认转交的接手人**：他以前在本单当过协助人、传过附件、后来被移除，现在因为转交又能（只读）看到这张单——确认接手之前不能靠「上传人本人」删除或改类别自己的旧附件（§2.8、§12：接手人确认前没有任何写权限）。销售 / 主管不受这一条影响；接手人本来就是建单人时权限照旧；确认接手后恢复。

**不做任何强制检查**：缺哪一类附件都不拦提交完工、不拦闭环、不出提示。

### 4.2 POST /api/orders（变更）与 POST /api/orders/{id}/attachments（变更）

附件项新增 `type`：
```json
{ "attachments": [ { "fileId": 31, "restricted": false, "type": "Sat" } ] }
```
- `type` 省略 / null / 空白 = `Other`（v1.2 客户端不传）。只认枚举名 `Sat` / `RepairForm` / `Other`（大小写不敏感；数字或其他值非法）——用 §2.7 的 `AttachmentRules.TryParseType`。
- 同一个 `fileId` 在请求里出现多次：取**第一次**的 `restricted` 与 `type`（去重规则不变，只是多带一个字段）。
- 旧字段 `photoFileIds`（v1.1 客户端）挂载的建单照片一律 `Other`。
- 文件在挂载时写入 `AttachmentType`（`Mount`）。`FileService.Attach`（打卡 / 完工 / 返厂照片、`photoFileIds`）不写这个字段，保持默认值 `Other`。
- 实现：`NormalizeAttachments` 的元组多带一个 `AttachmentType Type`；遇到非法 `type` 直接抛 40000 `附件类别不正确`（「缺 `fileId`」仍然返回 null，由调用方报各自原来的文案；同一项两者都有问题时先报缺 `fileId`）。

新增 / 变更的校验文案：
- 任一项 `type` 非法 → 40000 `附件类别不正确`（建单：与「附件不存在，请重新上传」同一位置；添加接口：第 1 步，在 `请选择要添加的附件` 之后）
- 建单：去重后 `attachments` + `photoFileIds` 总数 > 50 → 40000 **`附件最多 50 个`**
- 添加：超过上限 → 40000 **`附件最多 50 个，本单还可添加 {N} 个`** / **`附件最多 50 个，本单已达上限`**

`POST /api/orders/{id}/attachments` 的第 4–6 步（`AttachmentRules.AddDenied`）改为：
4. 工单 Voided → 40900 `工单已作废，不能添加附件`
5. （Supervisor 到此通过）当前用户是本单「已退出」的人 → 40300 `你已退出本单，只能查看`
6. 当前用户不是建单人 / 负责人 / 协助人 → 40300 `只有建单人、负责工程师、协助人或主管可以添加附件`

**已删除的文案**：`工单已闭环，只有主管可以添加附件`。其余步骤（查看权限、受限权限、逐个文件校验、写锁事务）不变。

状态记录**不变**（`{姓名} 上传附件 {名单}` …，不带类别）。不发通知。

### 4.3 POST /api/orders/{id}/attachments/{fileId}/delete（变更）

`AttachmentRules.DeleteDenied` 在 Supervisor 放行之后、「只能删除自己上传的附件」之前加一步：当前用户是本单「已退出」的人 → 40300 `你已退出本单，只能查看`；
在「只能删除自己上传的附件」之后加一步：当前用户是工程师且不是本单的建单人 / 负责人 / 协助人（即只读的待确认接手人）→ 40300 `只有建单人、负责工程师、协助人或主管可以删除附件`。
其余不变（闭环后非主管 → 40900 `工单已闭环，只有主管可以删除附件`）。

### 4.4 POST /api/orders/{id}/attachments/{fileId}/restricted

不变。

### 4.5 POST /api/orders/{id}/attachments/{fileId}/type（新增；任何角色，服务端判权）

```json
{ "type": "RepairForm" }
```
→ `{ OrderDetail }`

校验顺序（第 2 步起在 `WriteLock()` 事务内）：
1. `type` 缺失或非法 → 40000 `请选择附件类别`
2. 工单不存在 → 40400 `工单不存在`
3. 不能查看该工单 → 40300 `无权查看该工单`
4. 附件不存在（不是本单未删除的附件，或对当前用户不可见）→ 40400 `附件不存在`
5. 当前用户不是 Supervisor 时，按顺序：已退出 → 40300 `你已退出本单，只能查看`；不是上传人 → 40300 `只能修改自己上传的附件`；是工程师且不是本单的建单人 / 负责人 / 协助人（只读的待确认接手人）→ 40300 `只有建单人、负责工程师、协助人或主管可以修改附件类别`；工单 Voided → 40900 `工单已作废，不能修改附件类别`
   （这一步写成 `AttachmentRules.ChangeTypeDenied(f, o, user)`，`canChangeType` = `IsAttachment(f.Category) && Visible(f, user) && ChangeTypeDenied(...) == null`，与 `CanDelete` 的写法一致）
6. 值未变化 → 直接返回详情，不写记录。

实现放在 `AttachmentService.cs`（`OrderService.SetAttachmentType`），附件用现有的 `LoadAttachment(orderId, fileId, user)` 取（它已经处理「不是本单的 / 已删除 / 对当前用户不可见 → 40400」）。

状态记录：`{姓名} 将附件 {name} 归为「{短标题}」`（例：`王敏 将附件 验收单.pdf 归为「SAT 验收单」`）；该附件是受限附件 → `kind = AttachmentPrivate`，否则 `kind = Attachment`。不发通知。
（`name` 用 `FileService.NameOf`，不截短——与「设为受限时收起旧记录」的按名称匹配逻辑保持兼容。）

### 4.6 文件中心 `GET /api/admin/files`（变更）

- 新增查询参数 `attachmentType`：`Sat` | `RepairForm` | `Other`；非法 → 40000 `附件类别不正确`。给了这个参数时只返回**附件**（`Category` 为 `OrderPhoto` / `Attachment`）且 `AttachmentType` 等于该值的行；`counts` 同样受它影响。
  与 `source` 同时给时取交集（如 `source=PunchPhoto&attachmentType=Sat` → 空结果，不报错）。
- `keyword` 增加匹配项目编号（§3.3）。
- 每行多 §2.5 的 4 个字段（在 `OrderViewBuilder.FillAttachment` 里统一填，详情与文件中心共用；`canChangeType` = `AttachmentRules.CanChangeType(f, o, user)`）。
- 以 Engineer 身份调用 `FileCenterService.Query` 时的工单范围，跟随 §6.1 的查看权限（多了「已退出」「待确认转交的接手人」两种），仍不含受限附件。
- 加载当页工单的查询加 `.Include(o => o.Exits)`（`canDelete` / `canChangeType` 要判断「已退出」）。

### 4.7 上限 20 → 50：所有写死「20」的地方（逐条改，改完全仓库搜 `20 个` 复核）

| 位置 | 改什么 |
|---|---|
| 后端 `Services/AttachmentRules.cs` | `Limit = 50` |
| 后端 `Services/OrderService.cs`（`Create`） | `附件最多 20 个` → `附件最多 50 个` |
| 后端 `Services/AttachmentService.cs`（`AddAttachments`） | 两条 `附件最多 20 个，本单…` → 50 |
| 后端 `Dtos/CommonDtos.cs` | `OrderDetail.AttachmentLimit` 的默认值 `= 20` → `= 50` |
| 后端 `server/README.md` v1.2 小节 | `每单最多 20 个` → 50（并注明 v1.3 调整） |
| 后端测试 `V12AttachmentTests.cs`、`V12FileCenterTests.cs` | `AttachmentLimit`、全部 `AttachmentRemaining`、三条文案、填满上限的用例（§11.1） |
| 小程序 `utils/attachments.js` | `MAX_COUNT = 50`（`pages/order-new` 的 `MAX_MEDIA` 取自它，toast `附件最多 50 个` 随之变化） |
| 小程序 `pages/order-detail/index.js` | `attachLimitText` 的两条文案 → 50 |
| 小程序 `pages/order-new/index.js` 文件头注释、`api/index.js` typedef 里的 `attachmentLimit:number(20)`、`README.md` v1.2 小节的 `最多 20 个` / `MAX_COUNT=20` | → 50 |
| PC `js/views/attachments-upload.js` | `MAX_COUNT = 50`；模板里写死的 `附件最多 20 个，已达上限` → `附件最多 50 个，已达上限`（建议改成用 `MAX_COUNT` 插值） |
| PC `js/views/orders-drawer.js` | toast `附件最多 20 个，本单已达上限` → 50 |
| PC `js/views/orders-create.js` 文件头注释、`README.md` 的 `每单最多 20 个` | → 50 |

后端的三条文案建议改为用 `AttachmentRules.Limit` 插值，避免以后再漏。**注意区分**：`图片不能超过 20MB`、`设备最多 20 台`、`姓名不能超过 20 个字` 里的 20 与本项无关，不要动。

---

## 5. 转交

### 5.1 规则总览

- 负责工程师在 Accepted / InProgress / OnHold 的工单上发起，选一位**启用中的工程师**（不能是自己；可以是本单协助人，也可以是本单已退出的人），**原因必填**。不需要主管批准。
- 对方「确认接手」前：工单仍在原负责人手上，一切照常（打卡、预约、申请暂停 / 协助、返厂节点都可以）；**唯一的限制是不能提交完工、不能再发起第二个转交**。原负责人可以「撤回转交」。
- 同一张工单同一时间只能有一个待确认的转交（不变量 I1）。
- 对方确认后：对方成为负责人，原负责人写入退出记录（`Via = Transfer`），成为「已退出」（§6）。接手人原本是协助人 → 从协助人中移除（不算移除，不写「移除协助人」记录）。原负责人**不会**自动变成协助人。工单状态不变。
- 原负责人的状态记录、打卡、工时原样保留，工时仍在他名下（工时按打卡人归属，本来如此，不需要改 `HoursService`）。
- 主管不审批，但每一步都通知到主管，并且可以「撤销」一个待确认的转交。主管自己换人仍用已有的「改派」。
- 并发：四个转交接口、以及 `Assign`（指派 / 改派）、`Void`（作废）、`Complete`（提交完工）都在 `WriteLock()` 事务内执行「加载 → 校验 → 写」
  （`Assign`、`Void`、`Complete` 是本期新加锁：取锁之后再 `Load`，`SaveChanges` 后 `CommitAsync`，通知与 `BuildDetail` 放在提交之后；只看请求体、不读数据库的校验——如 `请选择工程师`——留在取锁之前，现有的校验顺序与文案不变）。
  这样「确认接手 / 撤回 / 拒绝 / 改派 / 作废 / 提交完工 / 再次发起」任意两个同时到达都会排队，后到的看到的是前一个提交后的状态（重复点击「确认接手」第二次得到 `该转交已处理或已失效`）。
  审查与验收时又补了三处同样的写锁（校验顺序与文案都不变）：`SetAssistants`（主管调整协助人——否则把接手人加为协助人与对方确认接手同时到达时，同一个人会既是负责人又是协助人）、
  `Appointment`（预约）、`Start`（开始处理）（否则原负责人的预约 / 开始处理与对方确认接手同时到达时，会在他退出之后落下一条他的记录、甚至把工单改成「处理中」）。
- `PunchService.Create`（打卡）也包进一个数据库事务：在加载工单之前 `await using var tx = await _db.Database.BeginTransactionAsync();`，`SaveChanges` 之后 `CommitAsync`，返回结果的查询放在提交之后。
  目的：原负责人「到场打卡」与对方「确认接手」/ 主管「改派」同时到达时也排队——要么打卡先成功、接手被「正在现场计时」拦住，要么接手先成功、打卡得到 `只有本单的负责人或协助人可以打卡`。
  不这样做的话，极端情况下已退出的人会留下一张自己打不了离场卡的到场卡（当天拦住新负责人提交完工，次日变成缺卡由主管补录）。
- `WriteLock()` 不能嵌套（同一个 `DbContext` 上已有事务时再开会抛异常）：内部方法（`StartTransfer`、`EndPendingTransfer`、`AddExit`、`AutoCancelRequests`…）一律不自己开事务，由最外层的接口方法开。

**现场计时的裁决**（保证缺卡 / 补卡逻辑不被破坏）：
- 发起时，本人今天在本单有未离场的到场卡 → 拦截（先打离场卡）。
- 确认接手时，原负责人今天在本单有未离场的到场卡（发起之后又到场了）→ 拦截（请他先离场）。
- 主管改派的同类拦截（v1.1 已有：`原负责工程师正在现场计时，请先离场打卡后再改派`）不变。
- 因此「已退出」的人在退出那一刻一定没有正在计时的到场卡，不存在「想打离场卡却没权限」的情况。往日的缺卡（到场后没打离场、已过当天）不拦截转交，仍由主管用 `POST /api/admin/punches/supplement` 补录（该接口本来就不要求被补录人还是参与人，不用改）。

**内部方法**（`Services/TransferService.cs`，`partial class OrderService`）：
- `StartTransfer(o, from, to, reason, now)`：新增 `OrderTransfers` 行（Pending），`o.PendingTransferToId = to.Id`。
- `EndPendingTransfer(o, status, decidedBy, now, reply)`：把本单 Pending 的那一行置为 `status`，写 `DecidedAt = now`、`DecidedById = decidedBy?.Id`、`Reply = reply`，`o.PendingTransferToId = null`；没有 Pending 行时什么都不做；返回被结束的那一行（或 null）。
- `ExpirePendingTransfer(o, actor, now, why)`：= `EndPendingTransfer(o, Expired, null, now, why)`，并在确实结束了一行时写状态记录（§5.6）。
- `AddExit(o, engineerId, via, now)`：该人在本单已有退出记录 → 更新 `Via`、`ExitedAt`；否则新增。
- `RemoveExit(o, engineerId)`：有就删。
- 「结束一行待确认转交 + 清空工单上的 `PendingTransferToId`」与「失效记录的文字」这两小段逻辑，`AdminService`（停用接手人，§5.6）也要用：
  把它们放在同一个文件里的 `public static class TransferRules`（`End(order, transfer, status, decidedById, now, reply)`、`ExpiredLogText(fromName, toName, why)`）里，两边共用，不要各写一份。

`OrderService.Load(id)` 增加 `.Include(o => o.Exits)`。

### 5.2 POST /api/orders/{id}/transfers（新增；Engineer）—— 发起转交

```json
{ "engineerId": 5, "reason": "电气部分我搞不定，需要更熟的同事" }
```
→ `{ OrderDetail }`

校验顺序（第 3 步起在 `WriteLock()` 事务内）：
1. `engineerId` 缺失 → 40000 `请选择接手工程师`
2. `reason` 空白 → 40000 `请填写转交原因`；超过 200 个字 → 40000 `转交原因不能超过 200 个字`
3. 工单不存在 → 40400 `工单不存在`
4. 当前用户不是负责人 → 40300 `只有负责工程师可以转交工单`
5. 工单状态不是 Accepted / InProgress / OnHold → 40900 `工单当前为「{状态}」，不能转交`
6. `engineerId` 是自己 → 40000 `不能转交给自己`
7. 接手人不存在 / 已停用 / 不是工程师 → 40000 `所选工程师不存在或已停用`
8. 本单已有待确认的转交 → 40900 `已有待确认的转交，请先撤回`
9. 本人今天在本单有未离场的到场卡 → 40900 `你正在现场计时，请先离场打卡后再转交`

通过后：`StartTransfer`。
状态记录（`kind = Assist`）：`{发起人} 发起转交给 {接手人}：{原因}`。例：`李工 发起转交给 王工：电气部分我搞不定，需要更熟的同事`。
通知（提交之后）：标题 `工单转交`，正文
`{发起人} 想把 **{客户}**（{工单号}）转交给 {接手人}，原因：{原因}。\n\n请 {接手人} 在小程序里确认接手或拒绝；确认前工单仍由 {发起人} 负责。`
@：接手人手机号 + 全部启用的主管手机号（接手人在前）。

### 5.3 POST /api/orders/{id}/transfers/{transferId}/accept（新增；Engineer）—— 确认接手

无请求体。→ `{ OrderDetail }`（此时当前用户已是负责人）

校验顺序（全部在 `WriteLock()` 事务内）：
1. 工单不存在 → 40400 `工单不存在`；转交不存在（`transferId` 不属于本单）→ 40400 `转交不存在`
2. 当前用户不是该转交的接手人 → 40300 `只有接手人可以确认接手`
3. 该转交不是 Pending → 40900 `该转交已处理或已失效`
4. 防御：工单状态不是 Accepted / InProgress / OnHold，或工单当前负责人不是该转交的发起人 → 40900 `该转交已处理或已失效`
5. 发起人今天在本单有未离场的到场卡 → 40900 `{发起人}正在现场计时，请其先离场打卡后再确认接手`

第 5 步用现有的 `RunningPairs(o.Id, now)` 判断（只看**今天**未离场的到场卡；往日缺卡不拦）。`{发起人}` 与文案之间没有空格（与现有的 `{name}还在现场计时…` 一致）。

通过后（同一次 `SaveChanges`，顺序固定）：
- `o.AssigneeId = 接手人`、`o.Assignee = 接手人实体`、`o.ClaimedAt = now`（与改派一致）；状态、预约时间、暂停信息不变。
- 接手人若是本单协助人 → 删除其 `OrderAssistant` 行（不写记录）。
- `AddExit(o, 发起人, Transfer, now)`；`RemoveExit(o, 接手人)`。
- `EndPendingTransfer(o, Accepted, 接手人, now, null)`。
- `AutoCancelRequests(o, now, oldLeadId: 发起人)`（v1.1 已有：原负责人发起的待审批申请自动撤销，回复「负责人已变更」；目标已是参与人的协助人申请自动撤销）。

状态记录（`kind = Assist`）：`{接手人} 确认接手（原负责人 {发起人}）`。
通知：标题 `工单已转交`，正文 `{接手人} 已接手 **{客户}**（{工单号}），原负责人 {发起人} 退出本单。\n\n{DeviceText} · {Symptom}`；@：发起人 + 全部主管（发起人在前）。

### 5.4 POST /api/orders/{id}/transfers/{transferId}/reject（新增；Engineer）—— 拒绝接手

```json
{ "reply": "这周都在外地，排不开" }      // 选填，≤ 200 字
```
→ **`{ OrderDetail }` 或 `null`**：拒绝后当前用户若仍能查看这张工单（他是协助人 / 建单人 / 已退出的人）→ 返回最新详情；否则 `data` 为 **`null`**（他已经没有查看权，客户端收到 null 要离开详情页）。这是全系统唯一一个可能返回 null 的工单动作。

校验顺序：
1. `reply` 超过 200 个字 → 40000 `拒绝理由不能超过 200 个字`
2. 工单不存在 → 40400 `工单不存在`；转交不存在 → 40400 `转交不存在`
3. 当前用户不是该转交的接手人 → 40300 `只有接手人可以拒绝`
4. 该转交不是 Pending → 40900 `该转交已处理或已失效`

通过后：`EndPendingTransfer(o, Rejected, 接手人, now, reply)`（`reply` 为 `Trim()` 后的值，空白存 null）。工单其他内容不变。
返回值的实现：提交之后用 `OrderViewBuilder.CanView(o, user)`（§6.1）判断——true → `BuildDetail`；false → 返回 null。**不要**用「调用 `BuildDetail` 再捕获 40300」的写法。
控制器返回类型写 `ApiResponse<OrderDetail?>`，成功时 `code = 0`、`data = null`、HTTP 200。
状态记录（`kind = Assist`）：`{接手人} 拒绝接手`，有理由时 `{接手人} 拒绝接手：{理由}`。
通知：标题 `转交被拒绝`，正文 `{接手人} 拒绝接手 **{客户}**（{工单号}）`，有理由时追加 `：{理由}`，最后固定追加 `。工单仍由 {发起人} 负责。`；@：发起人 + 全部主管（发起人在前）。

### 5.5 POST /api/orders/{id}/transfers/{transferId}/cancel（新增；Engineer、Supervisor）—— 撤回 / 撤销

无请求体。→ `{ OrderDetail }`

校验顺序：
1. 工单不存在 → 40400 `工单不存在`；转交不存在 → 40400 `转交不存在`
2. 当前用户既不是该转交的发起人、也不是 Supervisor → 40300 `只有发起人或主管可以撤回转交`
3. 该转交不是 Pending → 40900 `该转交已处理或已失效`

通过后：`EndPendingTransfer(o, Cancelled, 当前用户, now, null)`。

| 操作人 | 状态记录（`kind = Assist`） | 通知 |
|---|---|---|
| 发起人 | `{发起人} 撤回转交（{接手人}）` | 标题 `转交已撤回`，正文 `{发起人} 撤回了 **{客户}**（{工单号}）转交给 {接手人} 的请求。`；@：接手人 + 全部主管（接手人在前） |
| 主管 | `{主管} 撤销转交（{发起人} → {接手人}）` | 标题 `转交已撤销`，正文 `{主管} 撤销了 **{客户}**（{工单号}）由 {发起人} 转交给 {接手人} 的请求，工单仍由 {发起人} 负责。`；@：发起人、接手人（按此顺序） |

### 5.6 待确认期间发生其他事情时的裁决

| 发生的事 | 裁决 |
|---|---|
| **提交完工** | **直接拦截**：`Complete` 在「只有负责工程师可以操作」之后、暂停检查之前加一步：本单有待确认的转交 → 40900 `本单有待确认的转交，请先撤回转交后再提交完工`。同时 `actions` 不给 `complete`（§2.9）。因此「待验收 / 已闭环」的工单上不可能有待确认的转交 |
| **主管改派**（`Assign` 的改派分支，负责人发生变化） | 转交自动失效：`ExpirePendingTransfer(o, 主管, now, "主管已改派")`。改派给转交的接手人也一样（他通过改派成为负责人） |
| **主管作废**（`Void`） | 转交自动失效：原因 `工单已作废` |
| **主管暂停 / 恢复、同意暂停申请** | 不影响（OnHold 也是可转交状态），转交保持待确认 |
| **主管调整协助人、同意协助人申请** | 不影响。接手人被加为协助人后仍可确认接手（接手时从协助人中移除） |
| **接手人被停用 / 改为非工程师角色**（`AdminService.UpdateEmployee`） | 以该员工为接手人的所有待确认转交自动失效：原因 `接手人账号已停用`（停用）/ `接手人已不是工程师`（改角色；两者同时发生取前者）。在同一次保存里把对应工单的 `PendingTransferToId` 清空并写状态记录（操作人 = 执行修改的主管）。用数据库事务包住。细则见表后 |
| **发起人（现负责人）被停用** | 不处理：转交保持待确认，接手人仍可确认接手（正好把单子接过去）；主管也可以直接改派 |
| **主管重新指派（Pending → Accepted）、闭环、退回** | 不会遇到待确认的转交（这些状态下不可能存在），不需要处理 |

系统失效的状态记录（`kind = Assist`，操作人 = 触发这件事的人，**写在触发动作自身的状态记录之前**）：
`转交已失效（{发起人} → {接手人}）：{原因}`。例：`转交已失效（李工 → 王工）：主管已改派`。系统失效**不单独发通知**（改派、作废各有自己的通知或记录）。
失效记录的 `ToStatus` = 触发动作**执行前**的工单状态（作废时是 `Accepted`，不是 `Voided`）；`Time` 与触发动作的记录相同（同一个 `now`），靠先 `Add` 保证排在前面。

`AdminService.UpdateEmployee` 的细则：
- 触发条件（按修改前后的值判断）：修改前是「启用中的工程师」，修改后「已停用」或「角色不是 Engineer」。只改姓名 / 手机号 / 部门不触发。
- 做法：在 `SaveChanges` 之前查出 `ToEngineerId == 该员工 && Status == Pending` 的转交（Include `From`），连同各自的工单一起改：`TransferRules.End(order, transfer, Expired, null, now, 原因)`，
  并各写一条状态记录（`ActorId` = 执行修改的主管、`ToStatus` = 工单当前状态、`Kind = Assist`、文字用 `TransferRules.ExpiredLogText`，其中接手人姓名取**修改后**的姓名）。
- 整个 `UpdateEmployee`（校验之后的读写）包在 `_db.Database.BeginTransactionAsync()` 里；没有待确认转交时行为与现在完全一样。
- 没有别的入口会让员工停用 / 改角色（没有删除员工的接口；`Unbind`、改密码不影响转交）。

### 5.7 主管改派与「已退出」（`Assign` 的改派分支，变更）

在原有逻辑上增加（顺序固定）：
1. 取写锁后加载；校验不变。
2. 负责人确实发生变化时：`ExpirePendingTransfer(o, me, now, "主管已改派")`（先写失效记录）。
3. 写改派记录、换负责人（原逻辑）。
4. `AddExit(o, 原负责人, Reassign, now)`（原负责人非空且 ≠ 新负责人）；`RemoveExit(o, 新负责人)`。
5. `ApplyAssistants`：每个**新增**的协助人 `RemoveExit`（原负责人如果在这次改派里被加为协助人，就不留退出记录）。
6. `AutoCancelRequests`（原逻辑）。

其他让人成为协助人的路径同样要 `RemoveExit`：`SetAssistants`（经 `ApplyAssistants`）、`ApproveRequest` 的 AddAssistant 分支。
被**移除的协助人不写退出记录**（v1.1 行为不变：看不了工单；有打卡的在参与人列表里由打卡推导显示「已退出」）。
由此的一个边界（按此实现，不要另加规则）：已退出的原负责人后来被加为协助人（退出记录被删）、之后又被移除 → 他与其他被移除的协助人一样，不再有查看权。
首次指派（Pending → Accepted）没有原负责人，不涉及退出记录；认领（`Claim`）同理。
`ApproveRequest` 的 AddAssistant 分支里「目标已是负责人或协助人 → 直接置为已同意」那条路径不新增协助人，不需要 `RemoveExit`（I2 保证他没有退出记录）。

主管改派换掉原负责人后，原负责人的待审批申请照旧由 `AutoCancelRequests(o, now, oldLeadId)` 自动撤销（v1.1 已有），所以已退出的人手里不会留着可撤回的申请。

---

## 6. 「已退出」与查看权限

### 6.1 谁能看工单详情（`OrderViewBuilder.EnsureCanView`，静态方法，签名不变）

Supervisor / Sales：任意。Engineer：满足任一即可——
1. 工单是 Pending；2. 本人是负责人；3. 本人是建单人；4. 本人是协助人；
5. **本人是本单「已退出」的人**（`o.Exits.Any(x => x.EngineerId == user.Id)`）；
6. **本人是待确认转交的接手人**（`o.PendingTransferToId == user.Id`）。

否则 40300 `无权查看该工单`。第 6 条在转交被拒绝 / 撤回 / 失效的那一刻自动消失。

实现：把判断抽成 `public static bool CanView(WorkOrder o, CurrentUser user)`，`EnsureCanView` = `if (!CanView(o, user)) throw …`（拒绝接手的返回值也用 `CanView`，§5.4）。

新增静态方法 `OrderViewBuilder.IsFormer(WorkOrder o, CurrentUser user)` = `user.Role == Role.Engineer && o.Exits.Any(x => x.EngineerId == user.Id)`。
全系统判断「当前用户是否已退出」**只用这个方法**（`myRole`、`readOnlyText`、`actions`、附件 / SAT / 返厂节点的拦截）；销售、主管永远不是「已退出」（曾是工程师、后来改了角色的人按新角色对待）。
参与人列表里的 `Former` 则直接看 `o.Exits`，与那个人现在的角色无关。

**`o.Exits` 必须已加载**——下面每一处都要加 Include（漏了的后果是「已退出的人被当成无关的人」，权限判断会错）：
- `OrderViewBuilder.Orders()`（详情、所有列表）：`.Include(o => o.Exits).ThenInclude(x => x.Engineer).ThenInclude(e => e!.AvatarFile)`（参与人列表要显示退出人的姓名与头像）
- `OrderService.Load()`（所有工单动作、附件接口、返厂节点）：`.Include(o => o.Exits)`
- `FileService.ResolveDownload` 里加载工单的查询：`.Include(x => x.Exits)`
- `FileCenterService.Query` 里加载当页工单的查询：`.Include(o => o.Exits)`；它给 Engineer 的范围条件同步加上第 5、6 两条（`x.Order.Exits.Any(e => e.EngineerId == uid) || x.Order.PendingTransferToId == uid`）
- `PunchService.OrdersWithAssistants()` **不需要**（打卡只认负责人 / 协助人）
- `AdminService.ExportOrders` / `ExportHours`、`HoursService` **不需要**（工时按打卡人归属，角色列用 `RoleIn(...) ?? Former`，本来就对）

一个查询里同时 Include `Assistants` 与 `Exits` 两个集合，EF 会记一条「多个集合 Include」的警告；两者都很小（协助人 ≤ 5、退出记录通常 0–2 条），保持单条查询即可，不要改成 `AsSplitQuery`。

**已退出的人 / 接手人的每一条写路径都被拦住**（核对用，§11.2 第 18、26 条逐个测）：

| 接口 | 已退出的人 | 待确认的接手人（不是本单其他身份） |
|---|---|---|
| 预约 / 开始处理 / 提交完工 | 40300 `只有负责工程师可以操作`（现有） | 同左 |
| 申请暂停 / 申请协助人 | 40300 `只有负责工程师可以申请暂停` / `…申请协助人`（现有） | 同左 |
| 撤回本人的申请 | 他的申请在退出时已被自动撤销 → 40900 `该申请已处理`（现有） | 40300 `只能撤回本人的申请`（现有） |
| 发起转交 | 40300 `只有负责工程师可以转交工单` | 同左 |
| 撤回转交 | 他发起的转交在他退出时一定已结束 → 40900 `该转交已处理或已失效`；别人发起的 → 40300 | 40300 `只有发起人或主管可以撤回转交` |
| 打卡、打卡上下文（带 `orderId`） | 40300 `只有本单的负责人或协助人可以打卡`（现有） | 同左 |
| 添加 / 删除附件、改附件类别 | 40300 `你已退出本单，只能查看`（新） | 添加：40300 `只有建单人、负责工程师、协助人或主管可以添加附件`；删除 / 改类别：不是上传人 → 40300 `只能删除自己上传的附件` / `只能修改自己上传的附件`；是上传人（以前当协助人时传的）→ 40300 `只有建单人、负责工程师、协助人或主管可以删除附件` / `…可以修改附件类别`，详情里该附件的 `canDelete` / `canChangeType` 为 false |
| 修改 SAT 信息 | 40300 `你已退出本单，只能查看`（新） | 40300 `只有建单人、负责工程师或主管可以修改 SAT 信息` |
| 返厂节点 | 40300 `你已退出本单，只能查看`（新，`FactoryRules.Permission`） | 40300（现有的节点权限文案） |
| 认领 | 工单不可能回到 Pending → 40900（现有） | 同左 |

### 6.2 权限矩阵

「✓」可以，「—」不可以，「只读」= 能打开详情、能预览 / 下载非受限文件，不能做任何写操作。

| 能力 | 负责人 | 协助人 | 接手人（待确认，且不是本单其他身份） | 已退出 | 建单人（销售 / 工程师） | 主管 |
|---|---|---|---|---|---|---|
| 查看详情、下载非受限文件 | ✓ | ✓ | 只读 | 只读 | ✓ | ✓ |
| 出现在「我的工单」 | ✓ | ✓ | —（在首页「待我确认的转交」里） | ✓（`former` 分组，带「已转交 / 已改派」标记） | 「我提交的工单」 | — |
| 计入「进行中」数量、首页当前 / 下一单 | ✓ | ✓ | — | — | — | — |
| 打卡 | ✓ | ✓ | — | — | — | — |
| 预约、开始处理、提交完工、申请暂停、申请协助人 | ✓（有待确认转交时不能提交完工） | — | — | — | — | — |
| 发起转交 | ✓ | — | — | — | — | —（用改派） |
| 撤回 / 撤销转交 | ✓（自己发起的） | — | — | — | — | ✓ |
| 确认接手 / 拒绝 | — | 是接手人时 ✓ | ✓ | 是接手人时 ✓ | 是接手人时 ✓ | — |
| 添加附件（任何未作废状态，含已闭环） | ✓ | ✓ | — | — | ✓ | ✓ |
| 删除自己上传的附件（未闭环、未作废） | ✓ | ✓ | — | — | ✓ | ✓（任何人的、任何状态） |
| 改自己上传的附件的类别（未作废） | ✓ | ✓ | — | — | ✓ | ✓（任何人的） |
| 修改 SAT 信息（未作废） | ✓ | — | — | — | ✓ | ✓ |
| 返厂节点 | 同 v1.1 | — | — | — | 节点 1、5（同 v1.1） | 同 v1.1 |
| 价格、受限附件及其记录 | 工程师一律看不到（硬规则不变） | | | | 销售可见 | 可见 |

**「已退出」优先于其他身份**：一个人既是本单已退出的人、又是建单人（工程师自己建单、自己认领、后来转交出去）→ 仍然只读。落实到代码是 5 处加判断（都用 `OrderViewBuilder.IsFormer`），文案统一为 40300 **`你已退出本单，只能查看`**：
- `AttachmentRules.AddDenied`、`AttachmentRules.DeleteDenied`、`AttachmentRules.ChangeTypeDenied`（§4）
- `DeviceRules.SatEditDenied`（§3.5）
- `FactoryRules.Permission`：在最前面加一步，已退出 → 上述文案（于是 `canRecord` / `canEdit` 对他都是 false，`actions` 里也不会有 `factory`）

其余只认负责人的接口（预约、开始、完工、申请暂停、申请协助人、发起转交）对已退出的人本来就返回 `只有负责工程师可以…`，不用改；打卡返回 `只有本单的负责人或协助人可以打卡`，不用改。
已退出的人重新成为负责人 / 协助人（被转交回来并确认、被改派回来、被加为协助人）→ 退出记录删除，恢复对应身份的全部权限。

---

## 7. 列表与首页口径

### 7.1 GET /api/orders/mine?group=doing|check|closed|former|all（变更）

- 范围：本人是负责人 / 协助人 / **已退出** 的工单，不含 Voided、Pending。
- `doing` / `check` / `closed`：只含本人是负责人或协助人的（与 v1.2 完全相同，**已退出的不混进来**）。
- **`former`（新）**：本人已退出的工单（任何未作废状态），按 `ExitedAt` 倒序、id 倒序。
- `all`：前三组（原排序）+ `former`（排在最后，按 `ExitedAt` 倒序）。
- `counts`：`doing`、`check`、`closed` 口径不变；新增 `former`；`all` = 四者之和。
- 非法 `group` → 40000 `分组参数不正确`（不变）。
- 查询条件：`(o.AssigneeId == me || o.Assistants.Any(a => a.EngineerId == me) || o.Exits.Any(x => x.EngineerId == me)) && Status 不是 Voided / Pending`；取回后按「本人是否负责人 / 协助人」分成两堆（I2 保证一张单不会同时落在两堆里）。
- 待我确认的转交**不在**这个接口里（它在首页的 `transfersToMe`）；除非本人同时是这张单的协助人 / 已退出的人，那时按那个身份出现。

### 7.2 GET /api/home/engineer（变更）

- 「本人的工单」（current / next / upcoming / onHold / recentDone）口径不变：只含负责人或协助人，**不含已退出的、不含待我确认的转交**。
- 新增 `transfersToMe`（§2.6）：`_view.Orders().Where(o => o.PendingTransferToId == me.Id)`，按该转交的 `CreatedAt` 正序（经 `BuildList` 得到，每项的 `pendingTransfer` 非 null）。
  接手人同时是本单协助人时，这张单既在 `transfersToMe` 里、也照常出现在 current / upcoming 等位置（两处都显示，不去重）。
- `GET /api/me` 的 `activeOrderCount` 口径不变。

### 7.3 其他

- `GET /api/orders/created`、`/hall`、`GET /api/orders`（全部工单）：范围与排序不变；每项多 §2.3 的字段。`GET /api/orders` 的 `keyword` 见 §3.3。**不新增**「待确认转交」筛选参数。
- 列表项的 `pendingTransfer`：在 `OrderViewBuilder.Extras` 里按 `PendingTransferToId != null` 的工单批量查一次（带 `From` / `To` 及头像导航），不要逐单查询。
- 主管首页待办、概况数字**不新增**转交类条目（转交不需要主管处理）。
- `GET /api/engineers` 不变（工程师选接手人用它，v1.1 已对 Engineer 开放）。
- 工时（今日 / 月度 / 导出）不变：按打卡人归属；已退出的人的条目 `role = Former`（v1.1 已如此）。

---

## 8. 旧客户端兼容（体验版很快更新，目标：不崩、数据不丢）

后端与 PC 后台一起发布（PC 是后端托管的静态文件，永远同版本）。需要兼容的只有**尚未更新的 v1.1 / v1.2 小程序**：

| 旧客户端的行为 | v1.3 后端的处理 |
|---|---|
| 建单传 `deviceModel` / `deviceSn`，不传 `devices` | 合成一台设备（§3.1 归一化第 2 条），不报错；两项都没填也能建单（没有设备） |
| 建单传 `satDate: "yyyy-MM"`、`satSigned` | 原样接受并保存；`satSigned` 按「是否已完成 SAT」理解 |
| 建单不填项目编号（旧表单里是选填） | **40000 `请填写项目编号`**。旧表单里有这个输入框，用户按提示补上即可再提交——这是产品决定（新建单一律必填），不是崩溃 |
| 建单传 `photoFileIds` / 不带 `type` 的 `attachments` | 照旧，类别一律 `Other` |
| 详情页读 `deviceModel`、`deviceSn`、`satDate`、`satSigned`、`projectNo` | 字段都还在：前两个是第一台设备的信息；多台设备时旧版只显示第一台。新客户端补填的 `yyyy-MM-dd` 日期旧版按「x年x月」显示 |
| 详情页读 `attachments` / `files.orderPhotos` | 不变；旧版不分组、不能改类别 |
| 「我的工单 → 全部」里出现 `myRole = "Former"` 的工单 | 旧版把它当普通行显示（没有「协助」标记）；点进去 `actions = []`，只能看 |
| 不认识 `transfer` 等新动作、`pendingTransfer`、`Info` 类记录 | 旧版按钮表里没有这些 key，自动忽略；未知 `kind` 的记录按整句文字展示（现有实现如此） |
| 旧版工程师被别人转交 | 旧版看不到入口，无法确认接手——**转交功能要求双方都更新到 v1.3 小程序**；在此之前发起人可以撤回，主管可以撤销或直接改派 |
| 旧版在已闭环工单上添加附件 | 旧版界面按 `canAddAttachment` 显示入口，后端放开后自然可用 |
| 旧版前端自己限制「最多 20 个」 | 旧版小程序的 `MAX_COUNT` 仍是 20：已有 20 个以上附件的工单，旧版会提示已达上限（实际还能加）；更新到 v1.3 后恢复正常。不影响数据 |
| 旧版负责人在有待确认转交的工单上点「提交完工」 | 后端返回 40900 `本单有待确认的转交，请先撤回转交后再提交完工`，旧版把这句话 toast 出来；旧版没有「撤回」入口，由主管撤销或等对方处理 |
| 旧版「已退出」的人进入详情 | `actions = []`，没有底部按钮；`canAddAttachment = false`，没有「添加」入口——只读效果与新版一致，只是没有只读提示条 |

发布顺序：后端 + PC 后台先上线 → 小程序 v1.3 提审发布。小程序 v1.3 依赖 v1.3 后端，**不能先于后端发布**。本期没有 nginx / 运维配置变更，`deploy/README.md` 不用改。

---

## 9. 小程序改动清单（`miniprogram/`）

不新增页面、不新增 tab、不改 `project.config.json`。**工程师端任何位置仍不展示价格。**

### 9.1 `config.js`
`version: '1.3.0'`。

### 9.2 `utils/enums.js`
- 新增 `ATTACHMENT_TYPE`（顺序即展示顺序）与 `ATTACHMENT_TYPE_OPTIONS`：
  ```js
  const ATTACHMENT_TYPE = {
    Sat:        { label: 'SAT 验收单（签字版）', short: 'SAT 验收单' },
    RepairForm: { label: '现场维修登记表/返厂维修登记表（签字版）', short: '维修登记表' },
    Other:      { label: '设备照片、沟通记录及其他附件', short: '其他附件' }
  };
  ```
- 新增 `TRANSFER_STATUS`：`Pending 待确认`、`Accepted 已接手`、`Rejected 已拒绝`、`Cancelled 已撤回`、`Expired 已失效`（展示优先用服务端的 `statusText` / `text`）。
- `ACTION` 增加：`transfer: { label: '转交' }`、`cancelTransfer: { label: '撤回转交' }`、`acceptTransfer: { label: '确认接手' }`、`rejectTransfer: { label: '拒绝' }`。
- `FILE_CATEGORY` 补 `Attachment: { label: '附件' }`（v1.2 漏了，顺手补上）。

### 9.3 `api/index.js`
- `orders.mine(group)`：注释与类型加 `'former'`，`counts` 加 `former`。
- `orders.create(body)`：注释改为 v1.3 字段（`projectNo` 必填、`devices:[{name,sn,model}]`、`satSigned`、`satDate('yyyy-MM-dd'|'')`、`attachments:[{fileId,restricted?,type?}]`；不再传 `deviceModel` / `deviceSn`）。
- 新增：
  - `orders.setSat(id, satSigned, satDate, opts)` → `POST /api/orders/{id}/sat`，body `{ satSigned, satDate }`
  - `orders.setAttachmentType(id, fileId, type, opts)` → `POST …/attachments/{fileId}/type`，body `{ type }`
  - `orders.transfer(id, engineerId, reason, opts)` → `POST /api/orders/{id}/transfers`
  - `orders.acceptTransfer(id, transferId, opts)` → `POST …/transfers/{transferId}/accept`
  - `orders.rejectTransfer(id, transferId, reply, opts)` → `POST …/transfers/{transferId}/reject`，body `{ reply: reply || null }`；**可能 resolve 为 null**
  - `orders.cancelTransfer(id, transferId, opts)` → `POST …/transfers/{transferId}/cancel`
- typedef 补 `DeviceDto`、`TransferDto` 与 OrderListItem / OrderDetail / AttachmentDto 的新字段。

### 9.4 `utils/attachments.js`
- `MAX_COUNT = 50`。
- 统一条目增加字段 `type`（`'Sat' | 'RepairForm' | 'Other'`，默认 `'Other'`）；`pick(left, type)` / `pickMedia` / `pickChatFiles` 多一个可选参数，把 `type` 写进生成的每个条目（不影响上传：类别是挂载时才提交的）。
- 其余（预检、排队上传、角标、打开）不变。`HINT` 文案不变。

### 9.5 `pages/order-new`（index.js / .wxml / .wxss）

**「设备」分组整体替换**，分组标题改为 **`设备资料`**，从上到下固定为：

1. **项目编号**（cell，`id="f-projectNo"`）：标签 `项目编号 *`（带红星）；`<input maxlength="50">`，placeholder `必填`。
2. **设备**（可多台）：每台一个小卡片（`id="f-devices"` 包住整个区域）：
   - 卡片头：左 `设备 {序号}`，右 `删除`（只剩 1 台时点「删除」= 清空这一台的三项，不删卡片）。
   - 三个输入框（都选填，`maxlength="100"`）：`设备名称`（placeholder `如 高压反应釜`）、`设备编号`（placeholder `铭牌上的序列号`，`num-font`）、`设备型号`（placeholder `如 HTR-500`）。
   - 卡片下方按钮 **`+ 添加设备`**；已有 20 台时点击 toast `设备最多 20 台`。
   - 按钮下方提示（`.hint`）：`选填。一个项目多台设备同时维修时，点「+ 添加设备」逐台添加；整行不填的会被忽略。`
   - 进入页面时默认有 1 张空卡片。
3. **是否已完成 SAT**（cell，`id="f-satSigned"`）：标签 `是否已完成 SAT *`；右侧两个单选胶囊（`chip`，`aria-role="radio"`）**`已完成`** / **`未完成`**；初始都不选中（`form.satSigned = null`）。
   在「已完成 / 未完成」之间切换时**清空已选的时间**（两个时间含义不同）；第一次选择（原来没选）不清空——先填了时间再选 SAT 的人不用重填。
4. **时间**（cell，`picker mode="date"`，`fields="day"`，不设 `end`）：标签随上一项变化——未选 → `SAT 完成时间 / 发货时间`；已完成 → `SAT 完成时间`；未完成 → `发货时间`。
   值为空显示占位 `选填`；有值时显示 `yyyy-MM-dd`，并在右侧显示文字按钮 `清除`（`catchtap`，把值清空）。

`form` 去掉 `deviceModel`、`deviceSn`；`satSigned` 初值 `null`；新增 `devices: [{ key, name, sn, model }]`（`key` 仅用于 `wx:key`）。

**校验顺序**（`REQUIRED` 与 `validate`；toast 文案逐字）：客户单位 → 联系人 → 联系电话 → 客户地址 → **`请填写项目编号`**（key `projectNo`）→ **`请选择是否已完成 SAT`**（key `satSigned`，值为 null 时）→ 发生时间 → 故障现象 → 客户已尝试 → 现场环境 → 附件。
去掉 `请填写设备型号`、`请填写设备编号`、`请选择购买 / SAT 时间` 三条。设备、时间不做必填校验。
**注意**：现有 `validate()` 对每一项用 `!trim(values[r.key])` 判空，`satSigned` 是布尔值，选了「未完成」（`false`）也会被判成没填——这一项必须单独判断 `f.satSigned !== true && f.satSigned !== false`，不能走通用分支。
设备字段的长度由输入框的 `maxlength="100"` 保证、台数由「+ 添加设备」按钮保证，前端不再另做校验（服务端仍会校验）。

**提交体**（`buildBody`）：
```js
projectNo: trim(f.projectNo),
devices: f.devices.map(d => ({ name: trim(d.name), sn: trim(d.sn), model: trim(d.model) }))
                  .filter(d => d.name || d.sn || d.model),
satSigned: f.satSigned,            // true / false
satDate: f.satDate || '',
attachments: media.filter(m => m.fileId).map(m => ({ fileId: m.fileId, restricted: !!m.restricted, type: m.type || 'Other' }))
```
不再传 `deviceModel` / `deviceSn`。

**预填**（`applyPrefill`，最近客户）：客户 / 联系人 / 电话 / 地址照旧；`projectNo`；`devices` = `c.devices`（没有该字段或为空时：`c.deviceModel || c.deviceSn` 非空则合成一台 `{ name:'', sn:c.deviceSn, model:c.deviceModel }`，否则 1 张空卡片）；`satSigned = !!c.satSigned`；`satDate`：只有 `yyyy-MM-dd` 格式才预填，其他（旧的 `yyyy-MM`）留空。

**「附件」分组按类别拆成三块**：
- 分组标题行：`附件（照片 / 视频 / 文件）` + 副文字 **`选填，三类合计最多 50 个`**。
- 按 `ATTACHMENT_TYPE` 的顺序渲染三个小节，每个小节：小标题 = 该类的完整标题 `label`；下面是这一类的格子网格（样式同现有 `media-grid`），末尾一个「+」格。空小节只有标题和「+」格。
- 点某一小节的「+」→ 现有的选择流程（`attachments.pick(left, type)`，`left = 50 − media.length`），选中的条目 `type` = 该小节的类别。达到 50 个后三个「+」都不显示，再点 toast `附件最多 50 个`。
- 页面数据仍是一个 `media` 数组（提交、上传状态、重试、删除、长按设受限逻辑都不变）；渲染用 JS 里算好的 `mediaGroups: [{ type, label, items: [{ …条目, index }] }]`（WXML 不做过滤），格子的 `data-index` 用条目在 `media` 里的下标。
- 底部提示（`mediaHint` / `mediaHintRestrict`）不变，放在第三个小节之后。

### 9.6 `pages/order-detail`（index.js / .wxml / .wxss）

**头部**：设备行 `od-device` 改为只显示 `{{order.deviceText}}`。

**只读横幅**（紧跟头部之后、异常警示之前）：`order.readOnlyText` 非空时显示一条灰色提示条，文字就是 `readOnlyText`。

**转交卡片**（`order.pendingTransfer` 非空时显示，位置在「待审批的申请」卡片之前，样式沿用 `od-req`）：
- 标题行：左 `转交待确认`，右 时间（`formatDateTimeShort(pendingTransfer.createdAt)`）。
- 正文第一行：
  - `actions` 含 `acceptTransfer` → `{from.name} 请你接手本单`
  - `actions` 含 `cancelTransfer` 且当前角色是 Engineer → `已发起转交，等待 {to.name} 确认`
  - 其他人（协助人、销售、主管、建单人）→ `{from.name} 已发起转交，等待 {to.name} 确认`
- 正文第二行：`原因：{reason}`
- 按钮行（按 `actions`）：
  - `acceptTransfer` + `rejectTransfer` → 主按钮 **`确认接手`**、次按钮 **`拒绝`**
  - `cancelTransfer`：Engineer → 右侧小按钮 **`撤回转交`**；Supervisor → **`撤销转交`**
  - 都没有 → 不显示按钮行
- 交互：
  - 确认接手：`this.confirm('确认接手', '接手后你将成为本单负责人，' + from.name + ' 退出本单（其打卡和工时记录保留）。', '确认接手')` → `this.run(api.orders.acceptTransfer(id, t.id, { loading: '提交中' }), '已接手', true)`
  - 拒绝：`this.askReason('拒绝接手', '拒绝理由（选填）', '拒绝', true)`（`askReason` 增加第 4 个参数 `optional`：为 true 时允许空内容，resolve `''`；理由超过 200 字 → toast `拒绝理由不能超过 200 个字`）→ `this.run(api.orders.rejectTransfer(id, t.id, reply, { loading: '提交中' }), '已拒绝', true)`；
    **返回 `null` → `wx.navigateBack()`（没有上一页则 `wx.switchTab` 回首页）**；返回详情 → 照常 `applyDetail`。
    注意 `run()` 的现状：成功时 resolve 接口返回值（拒绝后可能是 `null`，`applyDetail(null)` 本来就直接返回）；**失败时 resolve `undefined`**（错误被 `run` 自己吞掉）。所以必须用 `d === null` 判断「拒绝成功且已无查看权」，不能用 `!d`。
  - 失去查看权时的兜底（接手人停在详情页、对方撤回了转交 / 主管改派了）：`load()` **非首次**加载失败且 `err.code === 40300` → toast `无权查看该工单` 后 `wx.navigateBack()`（没有上一页则回首页）。首次加载失败仍按现有的错误态显示。
    于是「确认接手 / 拒绝」遇到 40900 `该转交已处理或已失效` 时：toast 该文案 → `run` 自动重新加载 → 仍能看的（协助人等）刷新出最新状态，不能看的被带离页面。
    **操作失败后的那次自动重新加载**（`run` 里的 `load(true)`）发现 40300 时**不再另弹**「无权查看该工单」（否则会把刚弹出的真实原因盖掉），保留原提示、1.5 秒后离开；
    onShow / 下拉刷新发现 40300 仍弹「无权查看该工单」、0.7 秒后离开。离开前若用户已经从详情页进了别的页面，则不退页（回到详情页时 onShow 会重新走一遍）。
  - 撤回转交（工程师）：`confirm('撤回转交', '确定撤回转交给 ' + to.name + ' 的请求？', '撤回')` → `cancelTransfer`，toast `已撤回`
  - 撤销转交（主管）：`confirm('撤销转交', '确定撤销 ' + from.name + ' 转交给 ' + to.name + ' 的请求？工单仍由 ' + from.name + ' 负责。', '撤销')` → `cancelTransfer`，toast `已撤销转交`
- 工程师视角最近一条转交被拒绝时的提示（沿用 `od-req-rejected` 的样式，放在转交卡片的位置）：当前用户是负责人、工单状态 ∈ {Accepted, InProgress, OnHold}（与「申请被驳回」的提示一致，待验收 / 闭环后不再显示）、本单没有待确认的转交、`transfers` 的最后一条 `status = Rejected` 且 `from.id` 是自己 → 显示该条的 `text`（服务端文字，如 `李工 转交给 王工，王工 已拒绝：这周都在外地，排不开`）。

**底部按钮**：`ENGINEER_ORDER` 改为 `['transfer', 'requestHold', 'appoint', 'checkin', 'start', 'claim', 'factory', 'complete']`（`transfer` 最不优先，按钮多时进「更多」）。
`doAction('transfer')` → `this.setData({ sheetMode: 'transfer', sheetShow: true })`。`acceptTransfer` / `rejectTransfer` / `cancelTransfer` 不进底部按钮（只在转交卡片里）。`SUPERVISOR_ORDER`、`SALES_ORDER` 不变。

**「信息」tab**：
- 在「故障情况」卡片之后（工程师视角在「客户」卡片之后）新增卡片 **`设备资料`**：
  - 标题行右侧：`order.canEditSat` 为 true 时显示链接 **`修改 SAT`**。
  - 行（`od-kv`）：`项目编号`（值为空显示 `—`）；每台设备一组：小标题 `设备 {seq}`，下面只显示填了的项——`名称`、`编号`、`型号`；没有设备时一行 `设备` / `未填设备`；`是否已完成 SAT` / `{satText}`；`{satDateLabel}` / `satDate`（原样显示；为空显示 `未填写`）。
- 「工单信息」卡片（`orderRows`）去掉 `购买/SAT`、`项目编号` 两行（已移到「设备资料」）。
- 「处理人员」卡片（`buildParticipants`）：`Former` 且 `exitText` 非空时，在角色标签后追加一个小字 `{exitText}`（如 `已退出` 标签 + `已转交`）。
- `assigneeSub`：匹配状态记录的正则改为 `/认领|指派|改派|确认接手/`，命中「确认接手」时动词用 `接手`（如 `售后工程师 · 11月11日 14:20 接手`）。
  查找时**跳过以 `转交已失效` 开头的记录**（它的文字里也有人名和「主管已改派」，会被误当成改派记录）。

**「修改 SAT 信息」弹层**（沿用页面内 sheet 的写法，`satSheet`）：
- 标题 `修改 SAT 信息`，右上 `取消`；副文字 `接单时拿不到的时间可以之后补填。`
- `是否已完成 SAT`：两个胶囊 `已完成` / `未完成`，初值 = `order.satSigned`；切换时清空下面的时间。
- 时间行：标签 = 已完成 `SAT 完成时间` / 未完成 `发货时间`；`picker mode="date"`；空值占位 `选填`；有值时显示 `清除`。初值：`order.satDate` 是 `yyyy-MM-dd` 才带入，否则空（旧的 `yyyy-MM` 在行下方用小字提示 `原记录：{satDate}`）。
- 主按钮 **`保存`** → `this.run(api.orders.setSat(id, satSigned, satDate || '', { loading: '保存中' }), '已保存')`，成功后关闭弹层。
  例外：有「原记录」提示（旧的 `yyyy-MM` 没带进日期框）且用户**什么都没改**就点「保存」→ 直接关闭弹层、不调接口（否则只是打开弹层点一下保存就会把原记录的时间清空）；切换了 SAT 或选了日期则照常提交。

**「附件资料」tab**：
- 「附件」卡片改为**按类别分三组**（顺序 `Sat → RepairForm → Other`；`buildAttachments` 返回 `[{ type, label, count, items }]`，按 `attachmentType` 分组，组内保持服务端顺序）：
  - 卡片标题：`附件（{总数}）`（不再带「添加」链接）。
  - 每组一个小标题行：左 `{label}（{count}）`，右 `canAddAttachment` 时显示链接 **`添加`**（`data-att-type` = 该组类别；**不要用 `data-type`**——弹层里「拍照或从相册选择 / 从微信聊天选择文件」两个按钮已经用 `data-type="media|chat"`，`onAttachPick` 读的就是它）。
  - 组内每行与 v1.2 相同（角标、名称、`{大小} · {上传人} · {M月d日 HH:mm}`、受限标签、`…`）。**空组显示一行灰字 `暂无`**。
    点开图片 / 视频预览时，左右滑动的顺序与页面上的分组顺序一致（Sat → RepairForm → Other；建单页同理）。
  - 「…」菜单（`hasMenu = canDelete || canToggleRestricted || canChangeType`）：`canChangeType` 时在最前面加两项——`归为「{另外两类的短标题}」` 各一项（如当前是「其他附件」→ `归为「SAT 验收单」`、`归为「维修登记表」`）→ `this.run(api.orders.setAttachmentType(id, att.id, type, { loading: '提交中' }), '已更新', true)`；后面是原有的 `设为仅销售和主管可见 / 设为所有人可见`、`删除`。
- 「添加附件」弹层：
  - 打开时带上被点击那组的类别（`attachSheet.type`；`emptyAttachSheet()` 里默认 `'Other'`）。标题下新增一行 `类别` + 三个可切换的胶囊（短标题 `SAT 验收单` / `维修登记表` / `其他附件`，`data-att-type`），当前类别高亮；本次添加的附件**整批**归入所选类别（切换胶囊只改 `attachSheet.type`，不影响已选的文件）。
  - 提交：`done.map(x => ({ fileId: x.fileId, restricted, type: sheet.type }))`。
  - `attachLimitText`：`附件最多 50 个，本单还可添加 {n} 个` / `附件最多 50 个，本单已达上限`。
- 已闭环工单上 `canAddAttachment` 为 true 时照常显示「添加」（规则完全由服务端决定，前端不要再按状态隐藏）。
- 「流程资料」卡片不变。

### 9.7 `components/assign-sheet`（index.js / .wxml）

新增 `mode = 'transfer'`（负责工程师转交）：
- 标题 `转交工单`；副标题同其他模式，其中设备用 `order.deviceText`（**所有模式**都把 `o.deviceModel` 换成 `o.deviceText`）。
- 列表：单选。自己（当前负责人）置灰，标签 `负责人`；本单协助人**可选**，标签 `协助人`；其余无标签。
- 列表下方输入框（复用 `request` 模式已有的 `as-note-input`，`transfer` 模式同样显示）：`transfer` 模式的 placeholder 为 **`转交原因（必填），如：电气问题需要更熟悉的同事处理`**，`maxlength="200"`；`request` 模式的 placeholder 不变。
- 底部说明 `NOTES.transfer`：`对方确认接手后成为本单负责人，你将退出本单（只能查看），已有的打卡和工时保留；对方确认前工单仍由你负责，可随时撤回。`
- 确认按钮：未选人 `请选择接手工程师`；已选 `转交给 {姓名}`。
- 提交前校验：原因为空 → toast `请填写转交原因`；> 200 字 → `转交原因不能超过 200 个字`。
- 提交 `api.orders.transfer(order.id, pickId, reason, { loading: '提交中' })`，成功 toast `已发起转交，等待 {姓名} 确认`，触发 `done`（detail = 返回的 OrderDetail）。

### 9.8 `components/home-engineer`（index.js / .wxml / .wxss）

- `deviceLine(o)` 用 `o.deviceText`；「后续安排」行里的 `item.deviceModel` 改为 `item.deviceText`。
- 新增区块 **「待我确认的转交」**：放在 `.main` 的最前面（当前工单 / 下一单卡片之前），`transfersToMe.length > 0` 时显示，要醒目（红色描边或红点的卡片）：
  - 区块标题：`待我确认的转交 {数量}`
  - 每项一张卡片，点击进详情：第一行 客户单位；第二行 `{pendingTransfer.from.name} 请你接手 · {deviceText}`；第三行 `原因：{pendingTransfer.reason}`（最多两行，超出省略）；右侧箭头。
  - 确认 / 拒绝在详情页里做，首页不放按钮。
- 当前工单卡片的「提交完工」按钮：`current.pendingTransfer` 非空且 `current.myRole === 'Lead'` 时点击不跳转完工页，toast `本单有待确认的转交，请先撤回转交后再提交完工` 并进入详情页。
- 首页卡片上的工单（current / next / upcoming / onHold）如果 `pendingTransfer` 非空且本人是负责人（`myRole === 'Lead'`，待确认转交的发起人一定是当前负责人），在客户名旁显示小标签 `转交待确认`。

### 9.9 `pages/my-orders`（index.js / .wxml）

- 页面说明文字改为 `本人负责、协助或已转交的工单`。
- `FILTERS` 改为：`进行中 doing`、`待验收 check`、`已闭环 closed`、**`已退出 former`**、`全部 all`。
- `toRow`：`device` 用 `o.deviceText`；`o.myRole === 'Former'` 时：客户名旁显示小标签 `{o.myExitText || '已退出'}`（灰色，样式同「协助」标签），状态备注固定为 `已退出本单 · 仅可查看`（不走 `noteOf`）；工时仍显示本人工时。
- `o.pendingTransfer` 非空且本人是负责人（`o.myRole === 'Lead'`）时，客户名旁显示小标签 `转交待确认`。

### 9.10 `pages/orders`（工单大厅 / 全部工单）

- 大厅行 `toHallItem.device` 用 `o.deviceText`（不再拼 `deviceSn`）；全部工单行 `toListItem.device` 用 `[o.orderNo, o.deviceText, o.symptom]`。
- 全部工单行：`o.pendingTransfer` 非空时显示小标签 `转交待确认`（与「待审批」标签并排，灰色）。
- 搜索框 placeholder 改为 `搜索客户、设备、项目编号或工单号`。

### 9.11 其他

- `pages/complete/index.js`：现有的 `blockText` 判断（`actions` 不含 `complete` 时）在最前面加一个分支——`o.pendingTransfer` 非空且 `o.myRole === 'Lead'` → `本单有待确认的转交，请先撤回转交后再提交完工`。其余不变。
- `components/home-sales`、`components/home-supervisor`：不显示设备字段，**不用改**（主管首页传给 `assign-sheet` 的是工单详情，自带 `deviceText`；只需把 `home-supervisor/index.js` 第 184 行注释里的 `deviceModel` 改成 `deviceText`，可选）。
- `pages/checkin`、`pages/factory-node`、`pages/hours`、`pages/me`、`pages/privacy`：不改。
- `miniprogram/README.md` 补 v1.3 小节（三项变更、新接口、依赖 v1.3 后端）。本期**不需要**新的隐私声明项。

---

## 10. PC 后台改动清单（`wwwroot/admin/`）

主管在 PC 上**不发起转交**（用已有的改派），只查看与撤销。

### 10.1 `js/api.js`
- `orders.setSat(id, { satSigned, satDate })` → `POST /orders/{id}/sat`
- `orders.setAttachmentType(id, fileId, type)` → `POST /orders/{id}/attachments/{fileId}/type`
- `orders.cancelTransfer(id, transferId)` → `POST /orders/{id}/transfers/{transferId}/cancel`
- `orders.addAttachments(id, attachments)`：条目带 `type`（`[{ fileId, restricted, type }]`）
- `admin.files(params)`：多一个 `attachmentType`

### 10.2 `js/enums.js`
- 新增 `ATTACHMENT_TYPE`（内容与 §9.2 逐字一致：`label` / `short`，顺序 Sat、RepairForm、Other）。
- 新增 `TRANSFER_STATUS`（待确认 / 已接手 / 已拒绝 / 已撤回 / 已失效）。
- `ORDER_ACTIONS` 增加 `cancelTransfer: { label: '撤销转交', kind: 'secondary' }`（只在抽屉的转交提示里用，不进底部按钮布局 `LAYOUT`）。

### 10.3 `js/views/attachments-upload.js`
- `MAX_COUNT = 50`；超出提示改为 `附件最多 50 个，已忽略多余的 {k} 个`（现在已经用 `MAX_COUNT` 插值，改常量即可）；模板里**写死**的 `附件最多 20 个，已达上限` 改为 `附件最多 50 个，已达上限`（§4.7）。
- 新增 prop `type`（`'Sat' | 'RepairForm' | 'Other'`，默认 `'Other'`）：新加入的条目记下当时的 `type`；`attDone(items)` 返回 `[{ fileId, restricted, type }]`。组件本身不提供类别选择（由父组件决定）。

### 10.4 `js/views/orders-create.js`

**「设备」区整体替换**，区标题改为 **`设备资料`**，顺序固定：
1. `项目编号 *`（`span-2`）：`<input maxlength="50">`，placeholder `必填`；为空 → 字段错误 `请填写项目编号`。
2. `设备（选填，可添加多台）`（`span-2`）：一个小表格，每行三个输入框（表头 `设备名称` / `设备编号` / `设备型号`，各 `maxlength="100"`，placeholder 依次为 `如 高压反应釜`、`铭牌上的序列号`、`如 HTR-500`）+ 行尾 `删除` 链接（只剩一行时 = 清空该行）；下方按钮 **`+ 添加设备`**（已有 20 行时禁用，`title="设备最多 20 台"`）。默认 1 个空行。
   字段提示：`一个项目多台设备同时维修时逐台添加；整行不填的会被忽略。`
3. `是否已完成 SAT *`：单选 `已完成（老产品）` / `未完成（新产品）`；未选 → `请选择是否已完成 SAT`。在两者之间切换时清空时间（第一次选择不清空，与小程序一致）。
4. 时间（`<input type="date">`）：标签随上一项变化——未选 `SAT 完成时间 / 发货时间`、已完成 `SAT 完成时间`、未完成 `发货时间`；后缀 `（选填）`。

`emptyForm()`：去掉 `deviceModel`、`deviceSn`；新增 `devices: [{ name:'', sn:'', model:'' }]`。`validate()` 去掉设备型号 / 设备编号 / 购买 SAT 时间三条，`dirty` 的判断要能处理 `devices` 数组（任一行任一项非空即为已填写）。
提交体同 §9.5（`projectNo` 必填、`devices` 过滤空行、`satSigned`、`satDate`，不传 `deviceModel` / `deviceSn`）。

**「附件」区按类别拆成三个上传区**：区标题 `附件`，字段标题 `附件（照片 / 视频 / 文件，选填，三类合计最多 50 个）`；
按 `ATTACHMENT_TYPE` 顺序放三个 `AttachmentsUpload`，每个上方是该类的完整标题 `label`，`:type` 为对应类别，`:limit` = `50 − 另外两个区已选的数量`；
提交 `attachments` = 三个区 `attDone` 的合并；「仍在上传」「有上传失败」的判断覆盖三个区。底部提示文字不变。

### 10.5 `js/views/orders-drawer.js`

- `deviceLine`：`[d.deviceText, label(SERVICE_TYPE, d.serviceType)]`。
- 在基本信息 `dl.kv` 之后新增 **`设备资料`** 区：
  - 标题行：`设备资料`，右侧 `d.canEditSat` 时显示链接 **`修改 SAT`**。
  - `dl.kv`：`项目编号`（空 → `—`）；`是否已完成 SAT`（`d.satText`）；`{d.satDateLabel}`（`d.satDate`，空 → `未填写`）。
  - 设备表（`span-2`）：列 `#` / `设备名称` / `设备编号` / `设备型号`，空项显示 `—`；没有设备时一行灰字 `未填设备`。
- **修改 SAT 弹窗**：新文件 `js/views/orders-sat.js`（写法参照 `orders-hold.js`）：标题 `修改 SAT 信息`；单选 `已完成` / `未完成`（初值 `d.satSigned`，切换清空时间）；日期输入（标签同 §10.4 第 4 项；初值仅当 `d.satDate` 为 `yyyy-MM-dd`，旧的 `yyyy-MM` 用提示文字 `原记录：{satDate}（不重新选择日期则保留）` 显示）；按钮 `取消` / `保存`；成功后 `applied(res, 'SAT 信息已保存')`。
  旧记录的保留规则：没切换「已完成 / 未完成」、也没重新选日期就保存 → 把原来的 `yyyy-MM` 原样带回（服务端判定无变化，不写记录）；一旦切换，按「切换清空时间」处理，括号里的那句提示随之消失。
- **转交**：
  - `d.pendingTransfer` 非空时，在「待审批申请」提示块之前显示一个提示块（`alert alert-warn`）：标题 `转交待确认` + 标签 `待确认` + 时间；正文 `{d.pendingTransfer.text}`；第二行 `原因：{reason}`；`actions` 含 `cancelTransfer` 时按钮 **`撤销转交`** →
    `confirm('撤销转交', '确定撤销 {from} 转交给 {to} 的请求？工单仍由 {from} 负责。', { okText: '撤销', danger: true })` → `api.orders.cancelTransfer` → `applied(res, '已撤销转交')`。
  - 在「处理人员」之后新增 **`转交记录`** 区（`d.transfers.length > 0` 时显示）：按时间倒序，每条一行——`{text}`、灰字 `原因：{reason}`、时间 `fmtMDHM(createdAt)`，状态用 `Tag`（`statusText`：待确认 = warn，其余 = neutral）。
- 「处理人员」：`Former` 且 `exitText` 非空 → 在角色标签后加灰字 `{exitText}`。
- **「附件」区按类别分三组**（顺序 Sat、RepairForm、Other）：
  - 区标题 `附件（{总数}）` + `全部文件` 链接（不变）；原来标题上的「添加附件」链接去掉。
  - 每组小标题：`{label}（{count}）`，`d.canAddAttachment` 时右侧链接 **`添加`**；空组显示灰字 `暂无`。
  - 点「添加」→ 在该组下方展开上传区（同一时间只展开一组；切到另一组前若已有选好未提交的文件，先 `confirm('放弃已选的附件？', '换到另一类后，已选但未提交的附件不会保存。', { okText: '放弃', danger: true })`，取消则保持原组）：`<AttachmentsUpload :type="该组类别" :limit="attRemaining" :can-restrict="…">`；提交 `api.orders.addAttachments(d.id, attDone(items))`（条目已带 `type`）；成功 toast `已添加 {n} 个附件`。
    `openAttAdd` 里写死的 toast `附件最多 20 个，本单已达上限` 改为 `附件最多 50 个，本单已达上限`（§4.7）。
  - 每行操作在原有 `下载` / 可见范围 / `删除` 之外：`canChangeType` 时显示一个小下拉（`<select class="select att-type-select" aria-label="附件类别">`，三个选项用短标题，当前值选中），改变即调用 `api.orders.setAttachmentType`，成功 toast `已归为「{短标题}」`，失败还原选项并 `toastError`。
- 已闭环工单：`d.canAddAttachment` 为 true 时照常显示「添加」（不要按状态隐藏）。

### 10.6 `js/views/orders.js`（工单列表）

- 行里的设备文字：`[o.projectNo, o.deviceText, o.symptom].filter(Boolean).join(' · ')`。
- `o.pendingTransfer` 非空 → 在该行显示标签 `转交待确认`（`title` = `o.pendingTransfer.text`），与「待审批」标签并排。
- 搜索框 placeholder 改为 `搜索工单号、客户、项目编号、设备`；文件头注释里的 `keyword` 说明同步。

### 10.7 `js/views/files.js`（文件中心）

- 筛选条在「来源」下拉之后新增 **附件类别**下拉：`全部类别` + 三项（短标题）；值放在 hash 的 `attachmentType` 上并传给接口；`重置` 时清空；`hasFilter` 的判断包含它。
  为了在 1280 宽下排成一行，三个筛选下拉加了新类 `att-filter-select`（110px）；搜索框 placeholder 为 `文件名 / 工单号 / 客户 / 项目编号`。
- 表格在「来源」列之后新增一列 **`类别`**：`attachmentTypeShort`，非附件行显示 `—`。
- 附件行的操作列：`canChangeType` 时增加与 §10.5 相同的小下拉。
- 文件头注释的查询参数列表补 `attachmentType`。

### 10.8 `css/admin.css`、`README.md`
- `orders-assign.js`、`orders-hold.js`、`orders-price.js`、`orders-factory.js`、`orders-export.js`、`punches.js`、`hours.js`、`accounts.js`：不改（都不显示设备字段）。
- `admin.css`：新增样式一律以 `dev-`（设备资料）、`tr-`（转交）、`att-`（附件）为前缀，不改已有类。
- `README.md`（admin 目录）补 v1.3 小节、`orders-sat.js`、`#/files` 的新参数、`api.js` 新方法。

---

## 11. 测试

### 11.1 必须修改的现有测试 / 脚本（改的是「预期」，不是放宽断言）

- **夹具**：`V11TestBase.NewOrder()` 与 `ServiceIntegrationTests` 里的建单请求体加 `ProjectNo = "P-TEST"`（否则 `请填写项目编号`）。其余字段保持旧写法（`DeviceModel` / `DeviceSn` / `SatDate = "2024-08"`，不传 `Devices`）——它们正好覆盖「旧客户端请求体」这条路径。
- **actions 断言**（工程师负责人在 Accepted / InProgress / OnHold 多了末尾的 `transfer`）：`OrderStateMachineTests`（第 74–84 行一带的 7 处，以及 OnHold 负责人由 `[]` 变为 `["transfer"]`）、`V11MiscTests`、`V11FactoryTests`（3 处）、其他用 `Assert.Equal(new[] {…}, d.Actions)` 断言工程师负责人动作的地方；`smoke.sh` 第 97、201、260、282 行的 4 处预期字符串末尾加 `,transfer`。
- **附件上限与闭环后添加**：`V12AttachmentTests` / `V12FileCenterTests` 里所有 `AttachmentRemaining`（`20 − n` → `50 − n`）、`AttachmentLimit`（20 → 50）、三条 `附件最多 20 个…` 文案（→ 50）、填满上限的用例（要填到 50）；
  `工单已闭环，只有主管可以添加附件` 的 3 处断言（`V12AttachmentTests.AddOnClosedAndVoided`）改为：建单人 / 负责人 / 协助人在已闭环工单上**添加成功**；
  非参与方的销售（孙磊）由 40900 变为 40300 `只有建单人、负责工程师、协助人或主管可以添加附件`；同一用例里 `Assert.False(… CanAddAttachment)`（闭环后的工程师负责人）变为 `True`。
- **改派后的原负责人**（现在有退出记录：能看、且出现在参与人列表里）：
  - `V11AssistTests.ReassignToAssistantPromotes`：第 91 行的参与人全集由 `[(Lead, 刘工), (Assistant, 陈工)]` 变为 `[(Lead, 刘工), (Assistant, 陈工), (Former, 王工)]`（王工没打过卡也要出现）；
    第 92 行 `Detail(id, 王工)` 由 40300 改为「可以查看，`myRole = Former`、`myExitText = "已改派"`、`actions = []`、`readOnlyText` 非空」。
    同一用例后半段把王工加为协助人 → 断言他的退出记录已删除（`myRole = Assistant`）、刘工成为 `Former`。
  - 其他在**改派之后**断言 `participants` 全集或断言原负责人 40300 的用例，同样处理（改派发生在断言之前才受影响）。
  - 由**移除协助人**得到的「已退出」不变：`V11AssistTests` 第 262 行一带的断言结果不变（被移除的协助人仍然看不了工单）。
- **建单必填文案**：断言 `请填写设备型号` / `请填写设备编号` / `请选择 SAT 是否已签字` 等旧文案的用例，按 §3.1 调整或删除。
- **迁移测试**：`V12FileCenterTests.MigrationUpgradesV11DatabaseWithExistingRows` 第 294 行断言「待应用的迁移只有 `V12_Attachments`」，现在是 `V12_Attachments`、`V13_DevicesTransfers` 两个，按新列表改；
  这个用例和 `V11MiscTests` 的迁移用例随后都会一路迁移到最新，等于顺带验证了 V13 回填在更老的库上也能跑通，保留。
- **`mine` 的 counts**：断言 `counts` 键集合或 `all` 数值的用例，加上 `former`（没有已退出工单时为 0，`all` 不变）。
- **`smoke.sh` 的建单请求体**共 4 个模板、6 次建单，全部要带非空的 `projectNo`：
  第 80 行那个把现有的 `\"projectNo\":null` 改成非空编号；第 189 行（远程单）、`FACTORY_BODY`（用了 2 次）、`ORDER_BODY()` 函数（用了 2 次）这三个**现在根本没有 `projectNo` 字段**，要加上。
  它们的 `deviceModel` / `deviceSn` / `satDate:"yyyy-MM"` 保持旧写法（顺带覆盖旧客户端请求体）。
- **导出**：断言导出总列数为 27 的用例改为 30；按列号断言前 27 列内容的用例不变。
- **状态记录条数 / 最后一条**：在「改派」「作废」之后用 `d.Logs.Last()` 或 `TakeLast(n)` 断言的用例，只有在当时存在待确认转交时才会多出一条失效记录——现有用例都没有转交，结果不变。
- 其余现有测试必须原样通过。新加的写锁（`Assign` / `Void` / `Complete` / `PunchService.Create` / `UpdateEmployee`）不得改变任何现有的校验顺序与文案。

### 11.2 必须新增的测试（xUnit，服务级，沿用 `V11TestBase` 的写法；新文件 `V13DeviceTests.cs`、`V13AttachmentTests.cs`、`V13TransferTests.cs`）

设备资料：
1. 建单带 3 台设备（其中一行全空）：详情 `devices` 2 条、`seq` 连续、`title` 与 `deviceText`（`… 等 2 台`）正确；`DeviceModel` / `DeviceSn` 兼容列 = 第一台。
2. `devices: []` 且 `satDate` 为空：建单成功，`deviceText = "未填设备"`、`devices = []`、`satDate = ""`。
3. 旧请求体（只有 `deviceModel` / `deviceSn` / `satDate:"2024-08"`）：成功，合成一台设备（名称为空）；两项都不传：成功、没有设备。
4. 同时传 `devices` 与 `deviceModel`：以 `devices` 为准。
5. 校验：缺项目编号、项目编号 51 字、21 台设备、设备名称 101 字、缺 `satSigned`、`satDate` 为 `2026-13-01` / `abc`（文案随 `satSigned` 变）。
6. `DeviceRules.Title` / `Summary` 的各种组合（只有名称、只有型号、只有编号、都有、0 / 1 / n 台）。
7. 搜索：按第二台设备的名称 / 编号 / 型号、按项目编号都能搜到；旧种子工单按原型号 / 序列号仍能搜到。
8. `recent-customers` 返回 `devices`。
9. SAT 修改：建单人 / 负责人 / 主管成功并写 `Info` 记录（文案逐字）；协助人、非建单销售、无关工程师 → 40300；Voided → 40900；Closed 可改；值不变不写记录；清空时间；格式错误文案；`canEditSat` 与之一致。
10. 导出：多设备工单第 4、5 列用 `；` 连接，新增的 3 列内容正确，总列数 30；旧工单导出内容与 v1.2 相同（前 27 列）。

附件：
11. 添加时带 `type`：三类各自落库；省略 = `Other`；非法值 → `附件类别不正确`；建单时的 `attachments[].type` 同样生效；`photoFileIds` 为 `Other`。
12. 闭环后：建单人、负责人、协助人、主管添加成功；无关工程师 / 非建单销售 → 40300；闭环后非主管删除 → 40900（不变）；作废后任何人添加 → 40900。
13. 上限 50：第 51 个 → `附件最多 50 个，本单已达上限`；`还可添加 N 个` 的 N 正确。
14. 改类别：上传人成功（未闭环、已闭环都可以）；主管改别人的成功；非上传人 → 40300 `只能修改自己上传的附件`；作废后上传人 → 40900；值不变不写记录；记录文案与 `kind`（受限附件为 `AttachmentPrivate`）；工程师改一个对他不可见的受限附件 → 40400。
15. `attachmentType` / `attachmentTypeLabel` / `attachmentTypeShort` / `canChangeType` 在详情与文件中心一致；文件中心 `attachmentType` 筛选、非法值文案、非附件行为 null。

转交：
16. 发起：成功后 `pendingTransfer` 正确、`OrderTransfers` 一行 Pending、`PendingTransferToId` 已写、状态记录与通知（标题、正文、@ 顺序）逐字正确；发起人的 actions 有 `cancelTransfer`、没有 `transfer` 与 `complete`。
17. 发起的各项校验（§5.2 第 1–9 条逐条）；三种允许状态都能发起，Pending / Submitted / Closed / Voided 不能。
18. 接手人在确认前：能看详情（`actions = ["acceptTransfer","rejectTransfer"]`）、价格字段为 null、看不到受限附件；不能打卡、不能添加附件；出现在自己首页的 `transfersToMe`，不在 `mine` 的任何分组里。
19. 确认接手：负责人变更、`ClaimedAt` 更新、状态不变；原负责人有退出记录（`Via = Transfer`）；转交为 Accepted、`PendingTransferToId` 为 null；记录与通知正确；原负责人待审批的暂停 / 协助人申请被自动撤销。
20. 接手人原本是协助人：接手后不在协助人里，没有「移除协助人」记录。
21. 确认接手被拦：原负责人发起后又到场未离场 → 40900（文案带姓名）；重复确认 → `该转交已处理或已失效`；别人来确认 → 40300。
22. 拒绝：带理由 / 不带理由的记录与通知；接手人随后查看工单 → 40300；接手人不是其他身份时接口返回 null，是协助人时返回详情。
23. 撤回（发起人）与撤销（主管）：记录、通知各自正确；无关工程师撤回 → 40300；已结束的再撤回 → 40900。
24. 同一工单第二次发起 → `已有待确认的转交，请先撤回`；撤回后可以再次发起（历史两条都在 `transfers` 里）。
25. 待确认期间：提交完工 → 40900 `本单有待确认的转交，请先撤回转交后再提交完工`；主管改派 → 转交 Expired（`reply = "主管已改派"`）、失效记录在改派记录之前；主管作废 → Expired（`工单已作废`）；主管暂停 / 恢复 → 仍 Pending 且之后可以确认接手；接手人被停用 → Expired（`接手人账号已停用`）且工单 `PendingTransferToId` 为 null；接手人改为销售 → Expired（`接手人已不是工程师`）。
26. 已退出的原负责人：能看详情（只读、`readOnlyText` 逐字、`myRole = Former`、`myExitText = "已转交"`、`actions = []`）；预约 / 完工 / 申请暂停 / 申请协助 / 再转交 / 打卡 / 添加附件 / 删除自己之前上传的附件 / 改类别 / 改 SAT 全部被拒（各自文案）；他是建单人时也一样被拒（`你已退出本单，只能查看`）；`mine?group=former` 能找到、`doing` 里没有、`counts` 正确；首页不含这张单；`activeOrderCount` 不含。
27. 已退出的人即使没有任何打卡（远程单）也出现在 `participants`（`role = Former`、`exitText = "已转交"`），排序在协助人之后；他转交前的打卡工时仍计入本人月度工时与导出。
28. 主管改派：原负责人得到退出记录（`Via = Reassign`、`myExitText = "已改派"`），可以只读查看；改派同时把原负责人设为协助人 → 没有退出记录；之后把已退出的人加为协助人 / 改派回来 → 退出记录被删除，权限恢复。
29. 来回转交：A → B 接手后，B → A，A 确认 → A 是负责人、没有退出记录，B 有退出记录。
30. 不变量 I1 / I2：在以上每个场景结束时用一个公共断言方法检查（`PendingTransferToId` 与 Pending 行一一对应；退出记录里没有当前负责人 / 协助人）。
31. 工程师可见性：接手人、已退出的人序列化后的整个 OrderDetail JSON 不含价格数字、不含受限附件的 `name` 与 `url`；转交相关通知正文不含金额。
32. `OrderStateMachine.Actions`：§2.9 的全部示例。
33. 文件下载（`Files.ResolveDownload`）：接手人（待确认）与已退出的人都能下载本单非受限文件（附件、打卡照片）；受限附件 → 40400 `文件不存在`；接手人拒绝 / 被撤回之后 → 40300 `无权查看该工单`；无关工程师始终 40300。
34. 列表里的 `pendingTransfer`：`GET /api/orders`（主管）、`mine`（发起人）、首页 `transfersToMe`（接手人）里同一张单的 `pendingTransfer.id` 一致；转交结束后三处都变回 null / 从 `transfersToMe` 消失。
35. 附件类别解析：`type = "1"`、`"sat "`（带空格，合法）、`"SAT"`（大小写，合法）、`"Nope"`；改类别传空 → `请选择附件类别`。
36. 停用 / 改角色的边界：只改接手人的姓名或手机号 → 转交仍 Pending；停用**发起人** → 转交仍 Pending 且接手人可以确认接手；一个工程师同时是两张单的接手人 → 停用后两张单都失效、各写一条记录。
37. 已退出 → 被加为协助人 → 又被移除：退出记录不存在，查看 → 40300（§5.7 的边界）。
38. 拒绝后的状态：转交 `Rejected`、发起人的 actions 重新出现 `transfer`（InProgress 时还有 `complete`）；发起人可以立刻再次转交给同一个人。

审查 / 验收补充（`V13ReviewTests.cs` 与 `V13TransferTests` 末尾的并发用例）：
- 待确认接手人确认前不能删除 / 改类别自己以前上传的附件，确认后恢复；接手人本来是建单人时权限照旧。
- `SetAssistants`、`Appointment`、`Start` 进写锁后校验顺序与文案不变、失败不留事务；与「确认接手」并发时排队（双连接用例）。
- 按设备汇总文字整串搜索（`高温反应釜 HTX-777`、`… 等 2 台`）能搜到。

迁移：
39. `V13_DevicesTransfers` 在只应用到 `V12_Attachments` 的库（用原始 SQL 插入带数据的旧行：一张有型号 + 序列号的工单、一张两项都为空串的工单、几个旧文件）上执行成功：
    设备行回填正确（第二张没有设备行）、`DeviceText` 正确（`未填设备`）、旧文件 `AttachmentType = Other`、`PendingTransferToId` 为 null、`ProjectNo` 原样；旧工单的详情、列表、搜索、文件中心正常。

### 11.3 `server/scripts/smoke.sh` 新增检查（接在现有检查之后，段落标题 `== v1.3 …`；脚本仍需要 `WeChat__MockMode=true`）

1. 销售建单：`projectNo` + 2 台设备 + `satSigned:false` + 不传 `satDate` → 200，`deviceText` 以 ` 等 2 台` 结尾、`devices` 2 条、`satDateLabel = 发货时间`、`canEditSat = True`
2. 建单不带 `projectNo` → 400 + `请填写项目编号`；旧请求体（`deviceModel` / `deviceSn` / `satDate:"2024-08"`，带 `projectNo`）→ 200 且 `devices` 1 条
3. `GET /api/orders?keyword=<第二台设备编号>` 与 `?keyword=<项目编号>`（主管）都能搜到第 1 步的工单
4. `POST /api/orders/{id}/sat`：销售改为已完成 + 日期 → 200，`satText = 已完成`，logs 含 `修改 SAT 信息`；无关工程师 → 403
5. 建单带三类附件各 1 个 → 详情里三个 `attachmentType` 各 1；`attachmentLimit = 50`
6. 上传人把一个附件改为 `RepairForm` → 200；非上传人（工程师）改 → 403；`type=Nope` → 400 + `请选择附件类别`
7. 把一张由销售建单、王工负责的工单走到已闭环（用远程单最省事：认领 → `start` → 完工 → 主管定价 → 闭环；或直接复用脚本前面已经闭环的单）之后：销售（建单人）、王工（负责人）各添加 1 个附件 → 200，`canAddAttachment = True`；王工删除自己闭环后上传的附件 → 409 + `工单已闭环，只有主管可以删除附件`
8. `GET /api/admin/files?attachmentType=Sat` → 200 且每行 `attachmentType = Sat`；`?attachmentType=Nope` → 400 + `附件类别不正确`
9. 转交：新建一张远程单，王工认领；王工转交给李工（不带原因 → 400 `请填写转交原因`；带原因 → 200，`pendingTransfer.status = Pending`）
10. 李工 `GET /api/home/engineer` 的 `transfersToMe` 含该单；李工看详情 → 200，`actions = acceptTransfer,rejectTransfer`，`price` 为 None
11. 王工此时提交完工（先 `start`）→ 409 + `本单有待确认的转交…`；王工再次发起转交 → 409
12. 李工拒绝（带理由）→ 200 且 `data` 为 null；李工再看详情 → 403
13. 王工再次转交给李工 → 李工确认接手 → 200，`assignee.name = 李工`
14. 王工看详情 → 200，`myRole = Former`、`actions` 为空、`readOnlyText` 非空；王工添加附件 → 403 + `你已退出本单，只能查看`；`GET /api/orders/mine?group=former`（王工）含该单
15. 主管视角：详情 `transfers` 2 条；`participants` 里王工为 `Former`、`exitText = 已转交`
16. 主管撤销：李工转交给王工（待确认）→ 主管 `cancel` → 200，`pendingTransfer` 为 None，logs 含 `撤销转交`
17. `GET /admin/js/views/orders-sat.js` → 200（PC 新文件已随后端发布）

### 11.4 迁移验证（后端实现者必须手工执行一次并在交付说明里写结果）

用当前 HEAD（v1.2）的构建启动一次空库（`Seed:Enabled=true`）生成带数据的 `<scratch>.db` 与上传目录 → 停掉 → 用新构建以同一个库启动（`--urls http://127.0.0.1:<指定端口>`，**不得占用 5080**）→ 启动无异常，
旧工单详情有 1 台设备、`deviceText` 正确、附件全部在「其他附件」、`GET /api/admin/files` 正常 → 再跑 `smoke.sh`。用完杀掉进程、删除临时库。

---

## 12. 本期不做（实现者不要自由发挥）

- **建单后修改项目编号、设备清单**（只有 SAT 是否完成 + 时间可以改）。
- 按设备拆分处理、工时、定价、闭环——都仍按整张工单走。
- 附件缺失的任何强制检查 / 提醒（缺 SAT 验收单不拦完工、不拦闭环、不标红）。
- 转交需要主管批准；主管在 PC 或小程序上「发起转交」（主管用改派）；转交给多人 / 指定多个候选人；转交超时自动处理；接手人确认前的任何写权限。
- 被移除的协助人的只读查看权（维持 v1.1：移除后看不了）。
- 给旧数据回填退出记录。
- 主管首页待办 / 概况里的转交条目；`GET /api/orders` 的「待确认转交」筛选；转交的站内消息 / 订阅消息（只有钉钉群通知）。
- 「已退出」工程师给自己的往日缺卡补卡（仍只有主管能补录）。
- 小程序全局文件中心、对工程师 / 销售开放文件中心接口。
- 文件中心按项目编号 / 设备的专门筛选项（只是并入关键字搜索）。
- nginx / 部署配置变更（本期没有）。

---

## 13. 各实现者改动的文件

**后端（`server/`，含 `server/README.md`）**
`Domain/Entities.cs`（`WorkOrder` 新列与 `Devices` / `Exits` 导航、`StoredFile.AttachmentType`、新实体 `OrderDevice` / `OrderTransfer` / `OrderExit`）、`Domain/Enums.cs`、`Data/AppDbContext.cs`、
`Data/Migrations/*V13_DevicesTransfers*`（+ 快照）、`Data/SeedData.cs`、`Dtos/CommonDtos.cs`、`Dtos/RequestDtos.cs`、`Dtos/ViewDtos.cs`、
`Services/DeviceRules.cs`（新）、`Services/TransferService.cs`（新，`partial class OrderService` + 静态类 `TransferRules`）、`Services/OrderService.cs`（Create、ApplyKeyword、Mine、RecentCustomers、Complete、Assign、Void、ApplyAssistants、Load、SetSat、通知文字）、
`Services/OrderRequestService.cs`（ApproveRequest 的 `RemoveExit`）、`Services/OrderStateMachine.cs`、`Services/OrderViewBuilder.cs`、`Services/AttachmentRules.cs`、`Services/AttachmentService.cs`、
`Services/FactoryRules.cs`、`Services/FileService.cs`（ResolveDownload 的 Include）、`Services/FileCenterService.cs`、`Services/HomeService.cs`、`Services/AdminService.cs`（导出、停用员工时的转交失效）、
`Services/PunchService.cs`（仅 `Create` 包事务，§5.1；其余不动）、
`Controllers/OrdersController.cs`（`sat`、`attachments/{fileId}/type`、`transfers` 四个接口；`mine` 无需改）、`Controllers/AdminController.cs`（`FileCenterQuery` 新参数随模型绑定，无需改代码时不动）、
`tests/`（§11.1、§11.2）、`scripts/smoke.sh`（§11.1、§11.3）。
文档：`server/README.md` 补 v1.3 小节。`deploy/` 不改。`docs/` 下的文件实现者一律不动：`docs/DESIGN.md` 本期不更新，其 §6（新建工单字段）、§7（通知）、§10（多人协作）、§12（附件）与本文件不一致的地方以本文件为准。

**小程序（`miniprogram/`）**
`config.js`、`utils/enums.js`、`utils/attachments.js`、`api/index.js`、`pages/order-new/*`、`pages/order-detail/*`、`pages/my-orders/*`、`pages/orders/*`、`pages/complete/index.js`、
`components/assign-sheet/*`、`components/home-engineer/*`、`README.md`（`components/home-supervisor/index.js` 仅一处注释，可选）。

**PC 后台（`server/src/Huotong.AfterSales.Api/wwwroot/admin/`）**
`js/api.js`、`js/enums.js`、`js/views/attachments-upload.js`、`js/views/orders-create.js`、`js/views/orders-drawer.js`、`js/views/orders-sat.js`（新）、`js/views/orders.js`、`js/views/files.js`、`css/admin.css`、`README.md`。

三方互不修改对方目录下的文件。`docs/CHANGES-v1.3.md` 与 `docs/API.md` 由架构负责人维护，实现者发现矛盾时不要自行改文档，在交付说明里写出来。

---

## 14. 超出需求字面、需产品确认的决定

1. **「已退出」优先于建单人**：工程师自己建单、认领后转交出去，他对这张单也只读（不能再传附件、改 SAT、记返厂节点）。
2. **有待确认的转交时不能提交完工**（选择「拦截」而不是「自动撤销」）：发起人想自己做完，要先点「撤回转交」。
3. **发起转交、确认接手都要求原负责人今天不在「现场计时中」**（要先打离场卡），与主管改派的现有限制一致。
4. **拒绝后接手人立即失去查看权**；拒绝理由选填。
5. **主管撤销**与发起人撤回共用一个接口，状态都记为「已撤回」（文字上区分「已由某主管撤销」）。
6. **接手人被停用 / 改角色时转交自动失效**；发起人被停用时不处理（接手人仍可接过去）。
7. **被主管改派走的原负责人**从 v1.3 起也获得只读查看权（标记「已改派」）；升级前被改派走的人没有（不回填）。被移除的协助人仍然没有。
8. **旧单没有「名称」**：旧的「设备型号」原样作为型号；旧单 SAT 未签字的「购买/SAT 时间」在新界面上显示在「发货时间」名下。
9. **时间精确到日**（`yyyy-MM-dd`）；旧数据的 `yyyy-MM` 原样显示，不强制补全。切换「已完成 / 未完成」时清空已填的时间。
10. **项目编号对旧版小程序同样必填**：旧版表单里它标着「选填」，不填会收到「请填写项目编号」。
11. **建单后不能改项目编号和设备清单**（需求只要求 SAT 与时间可改）；如果实际使用中经常要补设备，建议下一期加「编辑设备资料」。
12. **附件类别不进状态记录的「上传附件」文字**（保持 v1.2 文案）；改类别单独写一条记录。
13. **上传人在工单闭环后仍可改自己上传附件的类别**（闭环后只是不能删）。
14. 导出在末尾追加了「设备台数 / SAT 完成时间 / 发货时间」三列；多台设备在「设备型号」「设备编号」两列里用「；」连接。
15. 小程序「我的工单」新增「已退出」分组；工程师首页新增「待我确认的转交」区块。
16. 转交的状态记录归在「人员」类（`kind = Assist`），SAT 修改新增一类「资料」（`kind = Info`）。
17. **已退出的人被重新加为协助人、之后又被移除**：与其他被移除的协助人一样不再有查看权（退出记录在他成为协助人时已删除，不恢复）。
18. **接手人确认接手后沿用原来的预约时间**（不清空）；原负责人待审批的暂停 / 协助人申请自动撤销（与改派一致），不另行通知。
19. **已退出的人不能改自己以前上传的附件的类别、也不能删**（「已退出不能再做任何操作」优先于「上传者本人可以改类别」）；需要时由主管代改。
20. **待确认的接手人**在确认前能看到工单全部非受限内容（含客户联系人、电话、地址、处理记录），与协助人的可见范围相同。
21. **待确认的接手人确认前不能动自己以前在本单上传的附件**（他曾是协助人、后被移除的情形）：删除 / 改类别都要等确认接手之后。
22. **旧单的 `yyyy-MM` 时间不会因为「打开修改 SAT 再直接保存」而被清空**：小程序不提交、PC 原样带回；要清空旧时间需切换一次「已完成 / 未完成」。
23. **搜索**额外匹配设备汇总文字（列表上显示的那一串）；匹配区分大小写（搜 `htc-20` 搜不到 `HTC-20`，是 SQLite 下的既有行为）。
24. 建单时**第一次**选择 SAT 不清空已填的时间，只有在「已完成 ↔ 未完成」之间切换才清空（两端一致）。

---

## 15. 最容易出错的地方（实现与审查时逐条核对）

1. **两处状态要一起改**：`WorkOrders.PendingTransferToId` 与 `OrderTransfers.Status`（不变量 I1）。所有会换负责人或让工单离开可转交状态的路径（确认接手、拒绝、撤回、主管改派、作废、停用接手人）都必须经过 `EndPendingTransfer` / `TransferRules.End`；`Assign`、`Void`、`Complete` 要进写锁，`PunchService.Create` 要包事务（§5.1）。
2. **退出记录的不变量 I2 与 `Include(o => o.Exits)`**：让人成为负责人 / 协助人的 4 条路径（确认接手、改派的新负责人、`ApplyAssistants` 新增的协助人、`ApproveRequest` 同意协助人申请）都要 `RemoveExit`；§6.1 列出的 4 处查询都要加 Include（`Orders()` 那一处还要带 `Engineer` 与头像）；`RoleIn` 不能返回 `Former`（否则已退出的人能打卡）。
3. **`AttachmentType` 的默认值**：枚举第一个成员必须是 `Other`；迁移里的列默认值要手工改成 `'Other'`；模型上不能配 `HasDefaultValue`。
4. **建单兼容**：`devices` 省略（旧客户端，合成一台）与 `devices: []`（新客户端，没有设备）含义不同；`DeviceText` / `DeviceModel` / `DeviceSn` 三个冗余列都要写；项目编号必填导致现有夹具与 `smoke.sh` 的 4 个请求体模板都要补（其中 3 个原来没有这个字段）。
5. **拒绝接手的返回值可能是 null**（接手人已无查看权）；小程序要离开详情页而不是拿 null 去刷新，并且要用 `d === null` 区分「成功且无权查看」与 `run()` 失败时的 `undefined`。相关地：有待确认转交时 `complete` 既不在 `actions` 里、接口也拦截；现有的工程师 actions 断言末尾都多了 `transfer`。
6. **闭环后添加附件放开、删除不放开**：`AddDenied` 去掉闭环判断，`DeleteDenied` 保留；两个前端都只看 `canAddAttachment` / `canDelete`，不要按状态自己隐藏入口。
7. **工程师硬规则**：接手人与已退出的人都是工程师，新增的任何 DTO、状态记录、通知里都不能带出价格与受限附件。
8. **被改派走的原负责人现在出现在参与人列表里**（即使没打过卡），现有用例 `V11AssistTests.ReassignToAssistantPromotes` 的参与人断言与 40300 断言因此要改（§11.1）——这是预期变化，不是回归。
9. **上限 20 → 50 有 10 多处写死的文案与常量**（§4.7 的表），后端 DTO 默认值、PC 模板里的那一句、PC 抽屉里的 toast 最容易漏。
10. **列表与详情的新字段走同一条路径**（`Extras` → `FillList`，§2.3）；`pendingTransfer` 只在详情里有、列表里没有，是最可能出现的不一致——首页「待我确认的转交」卡片、列表上的「转交待确认」标签都依赖列表项里的它。
11. **附件类别字符串只有一个解析入口**（`AttachmentRules.TryParseType`），`"1"` 这样的数字必须判为非法；小程序里类别用 `data-att-type`，不要与已有的 `data-type="media|chat"` 撞名。
