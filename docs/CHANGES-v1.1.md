# v1.1 变更契约（后端 / 小程序 / PC 后台 三方并行实现的唯一依据）

本文件是 v1.1 的**完整契约**。三位实现者（后端、小程序、PC 后台）只按本文件与 `docs/API.md`（已合并 v1.1）实现，互不沟通也能对上。
凡本文件写「必须 / 固定为」的地方不得自行变通；文案（错误信息、状态记录、按钮文字）按原文逐字实现。

四项变更：
- **(3) 价格**：建单不再填价格；只有主管定价；销售可看、可提价格建议；工程师永远看不到价格。
- **(2) 暂停搁置**：必须选原因；主管可直接暂停（待启动 / 处理中）；负责工程师可申请，主管同意或驳回；进度条显式展示暂停。
- **(4) 多人协作**：1 名负责人 + N 名协助人；协助人可看单、可打卡，工时按人计算。
- **(1) 返厂流程**：返厂单内增加 5 节点物流时间线。

通用约定不变：响应包装 `{code,message,data}`；camelCase；枚举传字符串；时间北京时间 `yyyy-MM-ddTHH:mm:ss`；
错误码 `40000` 参数、`40300` 权限、`40400` 不存在、`40900` 状态冲突；所有工单动作成功后返回最新 **OrderDetail**。

---

## 1. 数据模型（全部为增量迁移）

一个新迁移：`V11_PriceHoldAssistFactory`（位于 `server/src/Huotong.AfterSales.Api/Data/Migrations/`）。只允许 `AddColumn` 与 `CreateTable`/`CreateIndex`，
不得删除列、改列类型、给已有表加外键约束（会触发 SQLite 重建表）。

### 1.1 已有表新增列

| 表 | 列 | 类型 | 可空 | 默认值 | 说明 |
|---|---|---|---|---|---|
| WorkOrders | `PriceSet` | bool | 否 | **true** | 是否已定价。已有数据全部视为已定价。新建工单写 false |
| WorkOrders | `PriceSetAt` | DateTime | 是 | null | 最近一次主管定价时间（已有数据为 null） |
| WorkOrders | `HoldReasonType` | string(20) | 是 | null | 暂停原因类型（HoldReasonType 字符串） |
| WorkOrders | `HoldFromStatus` | string(20) | 是 | null | 暂停前的状态（`Accepted` / `InProgress`），恢复时回到它 |
| WorkOrders | `HoldAt` | DateTime | 是 | null | 进入暂停的时间 |
| WorkOrders | `HoldById` | int | 是 | null | 执行暂停（直接暂停或同意申请）的主管 id。**普通整数列，不建外键、不建导航属性** |
| StatusLogs | `Kind` | string(20) | 是 | null | 记录类别（LogKind 字符串）；null = `Status`（已有数据） |

- 已有列 `WorkOrders.HoldReason`（string?）保留，v1.1 起含义为**暂停说明（note）**。
- 已有列 `WorkOrders.Price` 保留；`PriceSet=false` 时值为 0 且无意义。
- **`PriceSet` 的迁移必须手工确认**：模型里**不要**写 `HasDefaultValue(true)`（EF 对 bool 默认值为 true 的列会吞掉显式的 false）；
  实体属性写 `public bool PriceSet { get; set; }`，迁移里的 `AddColumn<bool>(name: "PriceSet", nullable: false, defaultValue: true)` 把脚手架生成的 `false` 手工改为 `true`。
  建单代码显式写 `PriceSet = false`；`SeedData` 的所有工单显式写 `PriceSet = true`。
- 暂停相关 4 列在「恢复处理」时全部清空（连同 `HoldReason`）。
- 旧数据兼容：状态为 OnHold 但 `HoldReasonType == null` 的旧工单 → 对外视为 `reasonType = "Other"`、`note = HoldReason`、`fromStatus = "InProgress"`。

### 1.2 新表

**OrderAssistants**（工单协助人，当前有效的；移除 = 删除行，历史靠状态记录与打卡保留）

| 列 | 类型 | 说明 |
|---|---|---|
| Id | int PK | |
| OrderId | int FK → WorkOrders（Cascade） | |
| EngineerId | int FK → Employees（Restrict） | 协助工程师 |
| AddedById | int FK → Employees（Restrict） | 添加人（主管；同意申请时为同意的主管） |
| AddedAt | DateTime | |

索引：唯一 `(OrderId, EngineerId)`；普通 `EngineerId`。`WorkOrder` 增加导航 `List<OrderAssistant> Assistants`。

**OrderRequests**（工程师发起、需主管审批的申请：暂停 / 添加协助人）

| 列 | 类型 | 说明 |
|---|---|---|
| Id | int PK | |
| OrderId | int FK → WorkOrders（Cascade） | |
| Type | string(20) | OrderRequestType |
| Status | string(20) | RequestStatus |
| RequesterId | int FK → Employees（Restrict） | 申请人（申请时的负责人） |
| CreatedAt | DateTime | |
| HoldReasonType | string(20)? | Type=Hold 时必有 |
| Note | string(200)? | 申请说明 |
| TargetEngineerId | int? FK → Employees（Restrict） | Type=AddAssistant 时必有 |
| DeciderId | int? FK → Employees（Restrict） | 审批主管；系统自动撤销时为 null |
| DecidedAt | DateTime? | 同意 / 驳回 / 撤销时间 |
| Reply | string(200)? | 驳回回复；系统自动撤销时写撤销原因 |

索引：`OrderId`；`Status`。

**PriceSuggestions**（销售的价格建议，保留历史，详情只展示最新一条）

| 列 | 类型 | 说明 |
|---|---|---|
| Id | int PK | |
| OrderId | int FK → WorkOrders（Cascade） | |
| SalesId | int FK → Employees（Restrict） | |
| Amount | decimal（同 Price，`HasConversion<double>()`） | ≥ 0，2 位小数 |
| Note | string(200)? | |
| CreatedAt | DateTime | |

索引：`OrderId`。

**FactoryRecords**（返厂节点记录，每单每节点最多 1 行）

| 列 | 类型 | 说明 |
|---|---|---|
| Id | int PK | |
| OrderId | int FK → WorkOrders（Cascade） | |
| Node | string(20) | FactoryNode（只会存 `CustomerShipped` / `FactoryReceived` / `ShippedBack` / `CustomerSigned`） |
| OperatorId | int FK → Employees（Restrict） | 首次记录人 |
| Time | DateTime | 首次记录时间（服务器时间） |
| UpdatedById | int? | 最近更正人（普通整数列，无外键） |
| UpdatedAt | DateTime? | 最近更正时间 |
| Courier | string(50)? | 快递公司 |
| TrackingNo | string(50)? | 快递单号 |
| Note | string(500)? | |

索引：唯一 `(OrderId, Node)`。节点照片不加列：用 `StoredFiles.OrderId + Category` 关联（见 1.3 的 3 个新 FileCategory）。

### 1.3 枚举（C# 名 = JSON 字符串值）

| 枚举 | 值（中文） |
|---|---|
| **HoldReasonType**（新） | `WaitCustomer` 等客户, `WaitParts` 等备件, `WaitThirdParty` 等第三方, `Other` 其他 |
| **OrderRequestType**（新） | `Hold` 暂停申请, `AddAssistant` 协助人申请 |
| **RequestStatus**（新） | `Pending` 待审批, `Approved` 已同意, `Rejected` 已驳回, `Cancelled` 已撤销 |
| **FactoryNode**（新） | `CustomerShipped` 客户寄出, `FactoryReceived` 工厂收货, `Repairing` 检测/维修, `ShippedBack` 寄回客户, `CustomerSigned` 客户签收 |
| **ParticipantRole**（新） | `Lead` 负责人, `Assistant` 协助人, `Former` 已退出 |
| **LogKind**（新） | `Status` 状态流转（默认）, `Price` 价格, `Hold` 暂停, `Assist` 人员, `Factory` 返厂 |
| FileCategory（追加） | `FactoryReceivePhoto` 收货照片, `FactoryShipPhoto` 寄回照片, `FactorySignPhoto` 签收凭证 |
| OrderStatus / Role / ServiceType 等 | 不变 |

`EnumText` 增加：`HoldReason(HoldReasonType)`、`Factory(FactoryNode)`、`Participant(ParticipantRole)`、`RequestStatusName(RequestStatus)`，文字即上表。

**暂停原因文字 `holdText` 的拼法（全系统唯一规则）**：
- `Other`（或旧数据无类型）→ 只用说明：`{note}`（空则 `其他`）
- 其余 → `{类型中文}`，有说明时 `{类型中文}：{note}`，例：`等备件：轴承预计周五到`

**金额文字 `money(x)`（状态记录、待办副标题里用）**：0 → `保内免费`；整数 → `¥1,800`（千分位）；非整数 → `¥1,800.50`。

---

## 2. DTO 变更

### 2.1 OrderListItem（所有列表、首页、打卡返回里的工单项）新增 / 变更字段

```json
{
  "price": 1800.00 | null,            // 变更：可为 null。Engineer 永远为 null；PriceSet=false 时为 null
  "priceSet": true | false | null,    // 新增。Engineer 永远为 null
  "assistants": [ { UserBrief } ],    // 新增。当前协助人，按添加时间；无则 []
  "myRole": "Lead" | "Assistant" | null,   // 新增。当前登录用户在本单的角色（非参与人为 null）
  "myMinutes": 120 | null,            // 新增。myRole 非 null 时 = 本人在本单已配对工时；否则 null
  "myOnSiteSince": "2026-11-06T10:05:00" | null,   // 新增。本人今天在本单未离场的到场时间
  "progressStep": 3,                  // 新增。5 段进度：Pending=1 Accepted=2 InProgress=3 Submitted=4 Closed=5 Voided=0；OnHold = 暂停前状态对应的段（2 或 3）
  "holdReasonType": null | "WaitParts",    // 新增。仅 status=OnHold 时非 null
  "holdText": null | "等备件：轴承预计周五到", // 新增。仅 OnHold 时非 null（见 1.3 拼法）
  "holdFromStatus": null | "InProgress",   // 新增。仅 OnHold 时非 null
  "pendingRequestCount": 0,           // 新增。本单待审批申请数（Hold + AddAssistant）
  "factoryText": null | "检测/维修中"    // 新增。仅 serviceType=Factory 时非 null，见下
}
```
其余字段不变。已有字段语义说明：
- `totalMinutes` = 本单**所有人**已配对工时合计（人·分钟）；`onSiteSince` = 任一参与人今天未离场的最近到场时间；`lastArriveTime`、`issues` 覆盖本单所有打卡人。
- `assignee` = 负责人。
- `factoryText` 固定取值：未记录任何节点 `待客户寄出`；已记客户寄出 `客户已寄出`；已记工厂收货 `检测/维修中`；已记寄回客户 `已寄回客户`；已记客户签收 `客户已签收`（取最靠后的已记录节点）。

**价格可见性（硬规则）**：当前用户 `Role == Engineer` 时，任何接口返回的任何工单对象里 `price = null`、`priceSet = null`、`priceSuggestion = null`、
`logs` 不含 `kind == "Price"` 的记录、`actions` 不含价格动作。实现方式：`OrderViewBuilder.BuildList / BuildOne / FillList / BuildDetail` 一律接收 `CurrentUser`，在映射层统一抹掉，不依赖各调用点自觉。

### 2.2 OrderDetail 新增 / 变更字段（= OrderListItem 全部字段 +）

```json
{
  "priceSetAt": "2026-11-06T09:00:00" | null,     // Engineer 为 null
  "priceSuggestion": null | {                      // 最新一条价格建议；Engineer 永远为 null
    "id": 3, "amount": 1500.00, "note": "老客户，建议优惠" | null,
    "by": { UserBrief }, "createdAt": "2026-11-06T09:30:00",
    "pending": true                                // true = 主管尚未在此建议之后定价（priceSetAt 为 null 或早于 createdAt）
  },
  "holdReason": null | "等备件：轴承预计周五到",     // 保留（旧客户端用）：= holdText
  "hold": null | {                                 // 仅 status=OnHold 时非 null
    "reasonType": "WaitParts", "reasonText": "等备件", "note": "轴承预计周五到" | null,
    "text": "等备件：轴承预计周五到",
    "fromStatus": "InProgress", "at": "2026-11-06T15:00:00" | null, "by": { UserBrief } | null
  },
  "requests": [ { RequestDto } ],                  // 本单全部申请（含已处理），按创建时间正序
  "participants": [ { ParticipantDto } ],          // 见 2.4
  "factory": null | { FactoryDto },                // 仅 serviceType=Factory 时非 null，见 2.5
  "closeBlockedReason": null | "请先补录缺卡",       // 仅 Supervisor 且 status=Submitted 时可能非 null；非 null ⇔ actions 不含 close
  "closeHints": [ "客户签收尚未记录" ],              // 仅 Supervisor 且 status=Submitted；不阻止闭环的提醒，无则 []
  "logs": [ { StatusLogDto + "kind": "Status" } ],
  "issueDetails": [ { 原字段 + "engineer": { UserBrief } } ],
  "files": { 原 4 组 + "factoryPhotos": [FileDto] }   // 3 类返厂照片合并，按时间正序
}
```
- `closeBlockedReason` 取值（按优先级取第一条）：有缺卡 → `请先补录缺卡`；未定价 → `请先设置服务价格（保内填 0）`。
- `closeHints`：返厂单未记录客户签收 → `客户签收尚未记录`。
- `issueDetails[].text`：缺卡文字在最前面加打卡人姓名：`{姓名} 11月5日 15:10 到场后未打离场卡，当天未计工时。补录后系统重新计算。`；距离异常同样加：`{姓名} 11月6日 13:00 到场打卡距客户 2.4 km，超过 1 km。系统仅标记不拦截，请查看照片后判断。`
- **StatusLogDto** 增加 `"kind": "Status" | "Price" | "Hold" | "Assist" | "Factory"`。

### 2.3 RequestDto

```json
{
  "id": 12, "orderId": 3, "type": "Hold" | "AddAssistant", "status": "Pending",
  "requester": { UserBrief }, "createdAt": "2026-11-06T14:00:00",
  "reasonType": "WaitParts" | null,      // Hold
  "reasonText": "等备件" | null,           // Hold
  "note": "轴承预计周五到" | null,
  "text": "等备件：轴承预计周五到" | "添加协助人 王工",   // 可直接展示的一句话；AddAssistant 有说明时为「添加协助人 王工：需要两人抬设备」
  "engineer": { UserBrief } | null,       // AddAssistant 的目标工程师
  "decider": { UserBrief } | null, "decidedAt": null, "reply": null,
  "canDecide": true,                      // 当前用户可同意/驳回：Supervisor 且 status=Pending
  "canCancel": false                      // 当前用户可撤回：本人发起且 status=Pending
}
```

### 2.4 ParticipantDto

```json
{ "user": { UserBrief }, "role": "Lead" | "Assistant" | "Former",
  "totalMinutes": 362,                    // 此人在本单已配对工时
  "onSiteSince": "2026-11-06T10:05:00" | null,   // 此人今天在本单未离场的到场时间
  "missingPunch": false }                 // 此人在本单有缺卡
```
排序：负责人 → 协助人（按添加时间）→ `Former`（在本单有打卡但已不是负责人/协助人，按首次打卡时间）。Pending 无负责人时为 `[]`。

### 2.5 FactoryDto

```json
{
  "nodes": [
    { "node": "CustomerShipped", "label": "客户寄出",
      "state": "done" | "current" | "todo" | "skipped",
      "recorded": true,
      "time": "2026-11-02T10:00:00" | null, "operator": { UserBrief } | null,
      "courier": "顺丰" | null, "trackingNo": "SF1234567890" | null, "note": null,
      "photos": [ FileDto ],
      "canRecord": false,      // 当前用户现在可以记录此节点（未记录过）
      "canEdit": true },       // 当前用户现在可以更正此节点（已记录过）
    { "node": "FactoryReceived", ... }, { "node": "Repairing", ... }, { "node": "ShippedBack", ... }, { "node": "CustomerSigned", ... }
  ]
}
```
固定 5 个元素、固定顺序。`Repairing` 为隐含节点：`recorded=false`、`canRecord=false`、`canEdit=false`、`time` = 工厂收货时间、其余为 null/[]；
其 `state`：已记寄回客户 → `done`；已记工厂收货未寄回 → `current`；否则 `todo`。
其余 4 个节点的 `state`：已记录 → `done`；`CustomerShipped` 未记录但 `FactoryReceived` 已记录 → `skipped`；
未记录节点中按顺序第一个（跳过 Repairing、skipped）→ `current`（工单为 Closed / Voided 时没有 current，全为 `todo`）；其余 `todo`。

### 2.6 其他 DTO 增量

| DTO | 新增字段 |
|---|---|
| PunchContextDto | `"myRole": "Lead" \| "Assistant"` |
| TodayEntryDto / MonthEntryDto | `"role": "Lead" \| "Assistant" \| "Former"`（此工程师**当前**在该工单的角色） |
| EngineerHomeDto | `"onHold": [OrderListItem]`（本人参与的暂停搁置工单，按 HoldAt 倒序） |
| TodoDto | `"requestId": 12 \| null`；`type` 新增 `HoldRequest` / `AssistantRequest` / `PriceSuggestion` |
| EngineerLoadDto | `"assistCount": 1`（作为协助人参与的 Accepted/InProgress/OnHold 工单数） |
| OverviewDto | `"onHold": 1`、`"pendingRequests": 2`、`"unpriced": 3`（未定价且未作废的工单数）、`"priceSuggestions": 1`（有 pending 价格建议的工单数） |
| OrderFilesDto | `"factoryPhotos": [FileDto]` |

### 2.7 actions（OrderDetail.actions，服务端计算，UI 不得自行推导规则）

返回数组按下列**固定顺序**过滤得到。

**Engineer**：`claim`, `appoint`, `checkin`, `start`, `factory`, `complete`, `requestHold`, `requestAssistant`

| 动作 | 条件 |
|---|---|
| `claim` | status=Pending |
| `appoint` | 本人是负责人，Accepted / InProgress |
| `checkin` | 本人是负责人**或协助人**，serviceType=Onsite，Accepted / InProgress |
| `start` | 本人是负责人，serviceType=**Remote**，Accepted（返厂单不再有 start） |
| `factory` | serviceType=Factory 且 `factory.nodes` 中存在 `canRecord=true` 的节点 |
| `complete` | 本人是负责人，InProgress；返厂单还须已记录 `ShippedBack` |
| `requestHold` | 本人是负责人，Accepted / InProgress，本单没有 Pending 的 Hold 申请 |
| `requestAssistant` | 本人是负责人，Accepted / InProgress |

**Supervisor**：`assign`, `reassign`, `assistants`, `void`, `hold`, `resume`, `reject`, `close`, `supplement`, `setPrice`, `factory`

| 动作 | 条件 |
|---|---|
| `assign` | Pending |
| `reassign` | Accepted / InProgress / OnHold |
| `assistants` | Accepted / InProgress / OnHold |
| `void` | Pending / Accepted |
| `hold` | **Accepted** / InProgress |
| `resume` | OnHold |
| `reject` | Submitted |
| `close` | Submitted 且无缺卡 且 已定价 |
| `supplement` | 存在缺卡 |
| `setPrice` | status ≠ Voided |
| `factory` | 同上 |

**Sales**：`suggestPrice`, `factory`

| 动作 | 条件 |
|---|---|
| `suggestPrice` | status 不是 Closed / Voided |
| `factory` | 同上（销售只有作为建单人时才可能有可记录节点） |

审批类操作不进 actions：看 `requests[].canDecide / canCancel`。节点更正看 `factory.nodes[].canEdit`。

示例（供测试断言）：
- Supervisor：Pending → `["assign","void","setPrice"]`；Accepted(现场) → `["reassign","assistants","void","hold","setPrice"]`；InProgress → `["reassign","assistants","hold","setPrice"]`；
  OnHold → `["reassign","assistants","resume","setPrice"]`；Submitted 无缺卡已定价 → `["reject","close","setPrice"]`；Submitted 有缺卡 → `["reject","supplement","setPrice"]`；Closed → `["setPrice"]`；Voided → `[]`。
- Engineer 负责人：Accepted 现场 → `["appoint","checkin","requestHold","requestAssistant"]`；Accepted 远程 → `["appoint","start","requestHold","requestAssistant"]`；
  InProgress 现场 → `["appoint","checkin","complete","requestHold","requestAssistant"]`；Accepted 返厂（已记客户寄出）→ `["appoint","factory","requestHold","requestAssistant"]`；
  InProgress 返厂未寄回 → `["appoint","factory","requestHold","requestAssistant"]`；InProgress 返厂已寄回 → `["appoint","factory","complete","requestHold","requestAssistant"]`（factory = 可记客户签收）；OnHold → `[]`；Submitted → `[]`（返厂已寄回未签收时为 `["factory"]`）。
- Engineer 协助人：Accepted/InProgress 现场 → `["checkin"]`；其余 `[]`。
- Sales：Pending 现场 → `["suggestPrice"]`；Closed → `[]`。

---

## 3. 接口

### 3.1 价格

#### POST /api/orders（变更）
请求体去掉 `price`：服务端**忽略**传入的 `price`（旧客户端仍会传，不报错、不保存）。新单 `Price = 0`、`PriceSet = false`。
校验删除「请填写服务价格」三条，其余不变。任何角色建单都没有价格字段。

#### POST /api/orders/{id}/price（Supervisor，新增）
`{ "price": 1800 }`（0 = 保内免费）
- 校验：`price` 为 null → 40000「请填写服务价格（保内填 0）」；< 0 → 40000「服务价格不能小于 0」；> 100000000 → 40000「服务价格过大」；四舍五入到 2 位。
- 状态：Voided → 40900「工单已作废，不能定价」；其余任何状态都可定价 / 改价（含 Closed）。
- 效果：`Price = 值`，`PriceSet = true`，`PriceSetAt = now`。
- 状态记录（kind=`Price`，toStatus=当前状态）：首次定价 `{主管} 定价 {money(新)}`；已定价且金额变化 `{主管} 改价 {money(旧)} → {money(新)}`；已定价且金额不变 → 不写记录（仍更新 PriceSetAt）。
- 通知：无。→ `{ OrderDetail }`

#### POST /api/orders/{id}/price-suggestions（Sales，新增；任何销售都可以，不限建单人）
`{ "amount": 1500, "note": "老客户，建议优惠" }`
- 校验：`amount` 为 null → 40000「请填写建议价格」；< 0 → 40000「建议价格不能小于 0」；> 100000000 → 40000「建议价格过大」；`note` > 200 字 → 40000「备注不能超过 200 个字」。
- 状态：Closed / Voided → 40900「工单当前为「{状态}」，不能提交价格建议」。
- 状态记录（kind=`Price`）：`{销售} 建议价格 {money}`，有备注时追加 `（{note}）`。
- 通知（@所有启用主管）：标题「价格建议」，正文 `{销售} 对 **{客户}**（{工单号}）提交了价格建议，请在工单详情中查看并定价。`**正文不得出现金额**（群里有工程师）。
- → `{ OrderDetail }`

导出见 §5。

### 3.2 暂停搁置

状态机：
- 主管直接暂停：`Accepted → OnHold`、`InProgress → OnHold`，记 `HoldFromStatus`。
- 恢复：`OnHold → HoldFromStatus`（旧数据为 null 时回 `InProgress`）。
- 工程师申请 → 主管同意（等同直接暂停）或驳回（状态不变）。
- `OnHold` 下 `reassign`、`assistants`、`setPrice` 仍可用；`void` 不可用（先恢复）。
- `OrderStateMachine` 增加 `(Accepted, Hold) → OnHold`；`Resume` 的目标状态由调用方按 `HoldFromStatus` 决定（增加 `(OnHold, Resume)` 可到 `Accepted` 或 `InProgress` 的重载）；`ProgressStep(status, holdFrom)`。

#### POST /api/orders/{id}/hold（Supervisor，变更）
`{ "reasonType": "WaitParts", "note": "轴承预计周五到" }`
- 兼容旧客户端：请求体只有 `{ "reason": "等待备件" }`（无 `reasonType`）时，按 `reasonType = Other`、`note = reason` 处理。
- 校验：`reasonType` 缺失（且无旧字段 `reason`）或非法 → 40000「请选择暂停原因」；`Other` 且 note 为空 → 40000「请填写暂停说明」；note > 200 字 → 40000「暂停说明不能超过 200 个字」。
- 状态：非 Accepted / InProgress → 40900「工单当前为「{状态}」，不能执行此操作」（状态机原文）。
- 现场计时：负责人今天在本单未离场 → 40900「工程师正在现场计时，请先离场打卡后再搁置」（原文不变）；协助人未离场 → 40900「协助人{姓名}正在现场计时，请先离场打卡后再搁置」。
- 效果：status=OnHold；写 HoldReasonType / HoldReason(note) / HoldFromStatus / HoldAt / HoldById；本单 Pending 的 Hold 申请置为 `Cancelled`（Reply=「主管已直接暂停」，DecidedAt=now）。
- 状态记录（kind=`Hold`，toStatus=OnHold）：`{主管} 标为暂停搁置 → 暂停搁置（{holdText}）`
- 通知（@负责人与协助人）：标题「工单已暂停搁置」，正文 `**{客户}**（{工单号}）已暂停搁置：{holdText}`
- → `{ OrderDetail }`

#### POST /api/orders/{id}/resume（Supervisor，变更）
- 效果：status = HoldFromStatus（null → InProgress）；清空 5 个暂停字段。
- 状态记录（kind=`Hold`）：`{主管} 恢复处理 → {目标状态中文}`
- 通知（@负责人与协助人）：标题「工单已恢复处理」，正文 `**{客户}**（{工单号}）已恢复为「{目标状态中文}」`
- → `{ OrderDetail }`

#### POST /api/orders/{id}/hold-requests（Engineer，新增）
`{ "reasonType": "WaitParts", "note": "轴承预计周五到" }`
- 权限：不是本单负责人 → 40300「只有负责工程师可以申请暂停」。
- 校验同 hold（三条 40000 文案相同）。
- 状态：非 Accepted / InProgress → 40900「工单当前为「{状态}」，不能申请暂停」。
- 唯一：本单已有 Pending 的 Hold 申请 → 40900「已有待审批的暂停申请」。
- 申请时不检查是否在现场（同意时检查）。
- 状态记录（kind=`Hold`，toStatus=当前状态）：`{工程师} 申请暂停搁置（{holdText}）`
- 通知（@所有启用主管）：标题「暂停搁置申请」，正文 `{工程师} 申请暂停 **{客户}**（{工单号}）：{holdText}，请审批。`
- → `{ OrderDetail }`

#### POST /api/orders/{id}/requests/{requestId}/approve（Supervisor，新增；Hold 与 AddAssistant 共用）
无请求体。
- 申请不存在或不属于该工单 → 40400「申请不存在」；status ≠ Pending → 40900「该申请已处理」。
- **Hold**：执行与直接暂停完全相同的状态与现场计时检查（文案相同），暂停原因取申请内容；申请置 `Approved`。
  状态记录（kind=`Hold`，toStatus=OnHold）：`{主管} 同意暂停申请 → 暂停搁置（{holdText}）`
  通知（@申请人与协助人）：标题「暂停申请已同意」，正文 `**{客户}**（{工单号}）已暂停搁置：{holdText}`
- **AddAssistant**：见 3.3。
- → `{ OrderDetail }`

#### POST /api/orders/{id}/requests/{requestId}/reject（Supervisor，新增）
`{ "reply": "备件明天到，先别停" }`（选填，≤ 200 字，超出 → 40000「回复不能超过 200 个字」）
- 404 / 409 同 approve。申请置 `Rejected`，工单状态不变。
- 状态记录：Hold（kind=`Hold`）`{主管} 驳回暂停申请`，有回复追加 `（{reply}）`；AddAssistant（kind=`Assist`）`{主管} 驳回添加协助人 {目标姓名}`，有回复追加 `（{reply}）`。
- 通知（@申请人）：标题「申请被驳回」，正文 `**{客户}**（{工单号}）的{暂停申请|协助人申请}被驳回`，有回复追加 `：{reply}`。
- → `{ OrderDetail }`

#### POST /api/orders/{id}/requests/{requestId}/cancel（Engineer，新增）
申请人本人撤回。非本人 → 40300「只能撤回本人的申请」；非 Pending → 40900「该申请已处理」。申请置 `Cancelled`（DeciderId=null）。
状态记录：Hold（kind=`Hold`）`{工程师} 撤回暂停申请`；AddAssistant（kind=`Assist`）`{工程师} 撤回协助人申请（{目标姓名}）`。无通知。→ `{ OrderDetail }`

#### 申请的系统自动撤销（不写状态记录、不通知；Status=Cancelled，DeciderId=null，DecidedAt=now，Reply 写原因）
在每个会改变工单状态 / 负责人 / 协助人的动作里统一调用一个方法处理：
- 工单状态离开 {Accepted, InProgress}（完工、作废、被暂停）→ 撤销 Pending 的 Hold 申请，Reply=「工单状态已变化」（主管直接暂停时 Reply=「主管已直接暂停」）。
- 工单状态变为 Submitted / Closed / Voided → 撤销 Pending 的 AddAssistant 申请，Reply=「工单状态已变化」。（OnHold 时 AddAssistant 申请保留，主管仍可审批。）
- 改派（负责人变更）→ 撤销原负责人发起的全部 Pending 申请，Reply=「负责人已变更」。
- 目标工程师通过其他途径成为本单协助人或负责人 → 撤销针对他的 Pending AddAssistant 申请，Reply=「已是本单参与人」。

#### 暂停与其他动作的互斥（OnHold 时）
| 动作 | 结果 |
|---|---|
| 打卡（context / POST） | `blockedReason` / 40900「工单已暂停搁置，恢复后才能打卡」。**例外保留**：该工程师今天在本单有未离场的到场卡时允许补打离场卡（旧数据兜底，原规则不变） |
| start | 40900「工单已暂停搁置，恢复后才能开始处理」 |
| complete | 40900「工单已暂停搁置，恢复后才能提交完工」 |
| appointment | 40900「工单当前为「暂停搁置」，不能预约」（原文） |
| 返厂节点 | 见 3.4 |

### 3.3 多人协作（负责人 + 协助人）

规则：
- 负责人 = `AssigneeId`（不变）。协助人 = `OrderAssistants` 行。协助人必须是启用的 `Engineer`，不能是负责人，每单最多 **5** 人。
- 只有负责人能 appoint / start / complete / 申请暂停 / 申请协助人 / 记录工程师侧的返厂节点。协助人只能看详情、打卡（现场单）。
- 工程师查看详情的权限（`EnsureCanView`）：Pending、本人是负责人、本人是协助人、本人是建单人。
- 协助人可以对工单打卡的条件与负责人相同（Onsite 且 Accepted / InProgress）。任何参与人的**首次到场**都会使 Accepted → InProgress，状态记录 `{姓名} 首次到场 → 处理中`。
- 所有打卡规则（自动判断到场/离场、同一工程师同一时间只能有一个未离场到场、距离标记、缺卡、主管补录）按**工程师本人**计算——`PunchCalculator` 已按（工程师, 工单）配对，不需要改算法。

#### POST /api/orders/{id}/assign（Supervisor，变更）
`{ "engineerId": 4, "assistantIds": [5, 6] }`
- `assistantIds` **省略或 null** = 不调整协助人（旧客户端行为）；**传数组（含空数组）** = 协助人最终全集。
- 原有校验与文案不变。新增（针对 assistantIds，去重后）：含负责人（engineerId）→ 40000「协助人不能是负责人」；有不存在 / 已停用 / 非工程师 → 40000「所选协助人不存在或已停用」；> 5 人 → 40000「协助人最多 5 人」；
  被移除的协助人今天在本单未离场 → 40900「{姓名}正在现场计时，请先离场打卡后再移除」。
- 改派时：新负责人若当前是协助人 → 从协助人中移除并成为负责人（不报错）；原负责人不会自动变成协助人。原负责人在现场计时的拦截不变。
- 状态记录：原有「指派给 / 改派给」记录不变（kind=`Status`）；协助人有增减时**另写**（kind=`Assist`，toStatus=当前状态）：`{主管} 添加协助人 {姓名、姓名}`、`{主管} 移除协助人 {姓名、姓名}`（先写添加后写移除；新负责人从协助人转正不算移除，不写）。
- 通知：原「工单指派提醒」不变；新增协助人另发（@新增的协助人）标题「工单协助提醒」，正文 `{主管} 安排你协助 **{客户}**（{工单号}），负责人 {负责人姓名}。\n\n{设备型号} · {故障现象}`
- → `{ OrderDetail }`

#### POST /api/orders/{id}/assistants（Supervisor，新增）
`{ "engineerIds": [5, 6] }`（协助人最终全集；`[]` = 清空；缺失 → 40000「请选择协助人」）
- 状态：非 Accepted / InProgress / OnHold → 40900「工单当前为「{状态}」，不能调整协助人」。
- 校验、状态记录、通知同上（assign 的 assistantIds 部分）。无变化时不写记录。→ `{ OrderDetail }`

#### POST /api/orders/{id}/assistant-requests（Engineer，新增）
`{ "engineerId": 5, "note": "需要两人抬设备" }`
- 权限：不是负责人 → 40300「只有负责工程师可以申请协助人」。
- 校验：`engineerId` 缺失 → 40000「请选择协助工程师」；是自己 → 40000「协助人不能是负责人」；不存在 / 停用 / 非工程师 → 40000「所选协助人不存在或已停用」；note > 200 字 → 40000「说明不能超过 200 个字」。
- 状态：非 Accepted / InProgress → 40900「工单当前为「{状态}」，不能申请协助人」。
- 冲突：目标已是协助人 → 40900「{姓名}已是本单协助人」；已有针对同一人的 Pending 申请 → 40900「已有针对{姓名}的待审批申请」；当前协助人已满 5 人 → 40900「协助人最多 5 人」。
- 状态记录（kind=`Assist`）：`{工程师} 申请添加协助人 {目标姓名}`，有说明追加 `（{note}）`。
- 通知（@所有启用主管）：标题「协助人申请」，正文 `{工程师} 申请为 **{客户}**（{工单号}）添加协助人 {目标姓名}`，有说明追加 `：{note}`，末尾 `，请审批。`
- → `{ OrderDetail }`

#### approve 的 AddAssistant 分支
- 状态：工单非 Accepted / InProgress / OnHold → 40900「工单当前为「{状态}」，不能调整协助人」；目标已停用 / 改角色 → 40000「所选协助人不存在或已停用」；已满 5 人 → 40900「协助人最多 5 人」；目标已是负责人或协助人 → 直接把申请置 Approved，不重复添加。
- 效果：新增 OrderAssistants 行（AddedById=主管）；申请置 `Approved`。
- 状态记录（kind=`Assist`）：`{主管} 同意添加协助人 {目标姓名}`
- 通知（@申请人与目标）：标题「工单协助提醒」，正文 `{主管} 同意 {目标姓名} 协助 **{客户}**（{工单号}），负责人 {负责人姓名}。`

#### 与完工 / 闭环 / 异常的交互
- **完工**（POST complete）：负责人本人今天在本单未离场 → 40900「请先离场打卡」（原文不变，由 CompletionValidator 抛）；
  其他任何人（协助人或已退出的人）今天在本单未离场 → 40900「{姓名}还在现场计时，请其先离场打卡后再提交完工」（多人时取到场最早的一位）。先判本人，再判他人。
- **闭环**：缺卡判断覆盖本单所有人的打卡（含协助人、已退出的人），文案不变「请先补录缺卡」。
- **issues / issueDetails / 主管待办的缺卡与距离异常**：覆盖本单所有打卡人；`issueDetails[].engineer` 标明是谁。
- **主管补录**：接口不变（按 `arrivePunchId` 定位工程师）。状态记录：被补录人是当前负责人 → `{主管} 补录离场 18:20`（原文不变）；否则 → `{主管} 补录离场 18:20（{被补录人姓名}）`。
- **改派 / 暂停 / 移除协助人** 的现场计时拦截见各接口。

#### 列表 / 首页 / 打卡 / 工时 的口径
- `GET /api/orders/mine`：本人是负责人**或协助人**的工单（分组规则不变），每项带 `myRole`。
- `GET /api/home/engineer`：`current / next / upcoming / recentDone / onHold` 都按「本人参与（负责人或协助人）」取；`state` 规则不变（有参与的 InProgress → working；否则有参与的 Accepted → next；否则 idle）。
- `GET /api/me`：Engineer 的 `activeOrderCount` = 本人参与的 Accepted+InProgress+OnHold。
- `GET /api/punches/context`：带 orderId 时本人须是负责人或协助人，否则 40300「只有本单的负责人或协助人可以打卡」（POST 同）；不带 orderId 的自动选单范围 = 本人参与的现场单（优先级规则不变）。
- `GET /api/engineers`：角色放开为 **Supervisor + Engineer**（负责人申请协助人时要选人）。`loadText` 在「待验收 N 单」之后追加「协助 N 单」（assistCount > 0 时）；`HomeService.LoadText` 增加可选参数 `assisting = 0`，原 4 参数调用结果不变。
- 工时（today / month / admin hours / 导出）本来就按打卡人归属：协助人的工时算协助人自己的，无需改算法；只增加 `role` 字段与导出列（§5）。

### 3.4 返厂流程（serviceType = Factory）

5 个节点在现有状态内推进，不新增工单状态：

| # | node | 中文 | 谁可以记录 | 必填 | 选填 | 允许的工单状态 | 前置 |
|---|---|---|---|---|---|---|---|
| 1 | `CustomerShipped` | 客户寄出 | 建单人、负责人、主管 | courier、trackingNo | note | Pending / Accepted / InProgress / OnHold | 工厂收货尚未记录 |
| 2 | `FactoryReceived` | 工厂收货 | 负责人、主管 | photoIds ≥ 1 | note | Accepted / InProgress | 已记客户寄出（**主管可跳过**） |
| 3 | `Repairing` | 检测/维修 | —（隐含，不可提交） | | | | |
| 4 | `ShippedBack` | 寄回客户 | 负责人、主管 | courier、trackingNo、photoIds ≥ 1 | note | InProgress | 已记工厂收货 |
| 5 | `CustomerSigned` | 客户签收 | 建单人、负责人、主管 | — | photoIds、note | InProgress / OnHold / Submitted | 已记寄回客户 |

- 「建单人」不限角色（销售建单即销售；工程师建的客户直联单即该工程师）。协助人不能记录节点。
- 记录 `FactoryReceived` 时若工单为 Accepted → 自动变为 InProgress（`StartedAt ??= now`），取代返厂单的「开始处理」。
- `POST /api/orders/{id}/start` 对返厂单 → 40900「返厂工单记录「工厂收货」后自动开始处理」。
- 完工（complete）对返厂单要求已记录 `ShippedBack`，否则 40900「返厂工单请先记录「寄回客户」再提交完工」（在 CompletionValidator 之前判断，排在暂停判断之后）。
- `CustomerSigned` 在寄回之后、闭环之前任何时候都可记录；闭环不要求它，未记录时 `closeHints` 给出「客户签收尚未记录」。
- 返厂单永不打卡（原规则不变）。
- **更正**：已记录的节点再次提交 = 更正（覆盖字段、`UpdatedById/UpdatedAt`），可更正的人同上表「谁可以记录」，工单不是 Closed / Voided 即可；更正不检查前置、不改变工单状态。
- 旧数据：v1.1 之前已在处理中的返厂单没有任何节点，由主管记录「工厂收货」（可跳过客户寄出）后，负责人继续走寄回 → 完工。

#### POST /api/orders/{id}/factory-nodes（任何角色，新增；服务端按上表判权）
```json
{ "node": "ShippedBack", "courier": "顺丰", "trackingNo": "SF1234567890", "note": "含电源线 1 根", "photoIds": [41, 42] }
```
判断顺序与文案：
1. `node` 缺失 / 非法 → 40000「请选择返厂节点」；`Repairing` → 40000「检测/维修无需记录」。
2. 工单不是返厂单 → 40900「只有返厂工单需要记录返厂流程」。
3. 权限：节点 1、5 → 40300「只有建单人、负责工程师或主管可以记录此节点」；节点 2、4 → 40300「只有负责工程师或主管可以记录此节点」。
4. 该节点已记录（= 更正）：工单 Closed / Voided → 40900「工单当前为「{状态}」，不能更正返厂记录」；否则跳到第 7 步。
5. 状态（首次记录）：OnHold 且节点为 2 或 4 → 40900「工单已暂停搁置，恢复后才能继续返厂流程」；Pending 且节点为 2 → 40900「请先认领或指派工程师」；其余不在允许状态 → 40900「工单当前为「{状态}」，不能记录「{节点中文}」」。
6. 前置（首次记录）：节点 1 而工厂收货已记录 → 40900「工厂已收货，无需再记录客户寄出」；节点 2 无客户寄出且操作人不是主管 → 40900「请先记录「客户寄出」」；节点 4 无工厂收货 → 40900「请先记录「工厂收货」」；节点 5 无寄回客户 → 40900「请先记录「寄回客户」」。
7. 字段：节点 1、4：`courier` 空 → 40000「请填写快递公司」，> 50 字 → 40000「快递公司不能超过 50 个字」；`trackingNo` 空 → 40000「请填写快递单号」，> 50 字 → 40000「快递单号不能超过 50 个字」（两者去首尾空格，单号去掉全部空格）。
   节点 2：photoIds 去重后 < 1 → 40000「请至少上传 1 张收货照片（外观/配件）」。节点 4：< 1 → 40000「请至少上传 1 张寄回照片」。任何节点 photoIds > 9 → 40000「照片最多 9 张」。`note` > 500 字 → 40000「备注不能超过 500 个字」。
   节点 1 不收照片（忽略 photoIds）；节点 2、5 忽略 courier / trackingNo。
8. 照片：`photoIds` 为该节点照片的**最终全集**（与完工照片同语义）：本单该类别下已挂载但不在列表里的照片解绑（OrderId=null）；用 `FileService.Attach` 挂载并把类别修正为：节点 2 → `FactoryReceivePhoto`，节点 4 → `FactoryShipPhoto`，节点 5 → `FactorySignPhoto`（已挂在本单同类别的照片不校验上传人，便于他人更正）。上传时前端用这 3 个 category 调 `POST /api/files`。

状态记录（kind=`Factory`，toStatus=操作后的状态）：

| 节点 | 首次记录 | 更正 |
|---|---|---|
| 1 | `{姓名} 记录客户寄出（{快递公司} {单号}）` | `{姓名} 更正客户寄出（{快递公司} {单号}）` |
| 2 | Accepted 时 `{姓名} 工厂收货 → 处理中`；已是处理中时 `{姓名} 工厂收货` | `{姓名} 更正工厂收货` |
| 4 | `{姓名} 寄回客户（{快递公司} {单号}）` | `{姓名} 更正寄回客户（{快递公司} {单号}）` |
| 5 | `{姓名} 记录客户签收` | `{姓名} 更正客户签收` |

通知（仅首次记录）：节点 1 → @负责人（有负责人时）标题「返厂设备已寄出」，正文 `**{客户}**（{工单号}）客户已寄出：{快递公司} {单号}`；
节点 2 → @建单人 标题「工厂已收货」，正文 `**{客户}**（{工单号}）返厂设备已到厂，开始检测/维修`；
节点 4 → @建单人 标题「返厂设备已寄回」，正文 `**{客户}**（{工单号}）已寄回客户：{快递公司} {单号}`；节点 5 无通知。
→ `{ OrderDetail }`

`factory.nodes[].canRecord`（节点未记录）= 第 3、5、6 步对当前用户全部通过；`canEdit`（节点已记录）= 第 3、4 步通过。

### 3.5 查询类接口的参数增量

- `GET /api/orders`：新增 `priceSet=true|false`（按是否已定价过滤；非法值 → 40000「定价筛选不正确」）、`pendingRequest=true`（只返回有待审批申请的工单）。counts 规则不变。Sales 可用这两个参数。
- `GET /api/home/supervisor` 的 `todos` 顺序固定为：`HoldRequest` → `AssistantRequest` → `Unassigned` → `Submitted` → `PriceSuggestion` → `MissingPunch` → `DistanceAbnormal`。新增三类：
  ```json
  { "type": "HoldRequest", "orderId": 3, "requestId": 12, "title": "江苏某化工研究院", "urgent": false, "subtitle": "李工 申请暂停 · 等备件：轴承预计周五到" },
  { "type": "AssistantRequest", "orderId": 3, "requestId": 13, "title": "江苏某化工研究院", "urgent": false, "subtitle": "李工 申请添加协助人 王工" },
  { "type": "PriceSuggestion", "orderId": 2, "requestId": null, "title": "无锡某制药企业", "urgent": true, "subtitle": "王敏 建议价格 ¥1,500" }
  ```
  HoldRequest / AssistantRequest：全部 Pending 申请，按创建时间正序（AssistantRequest 有说明时副标题追加 `：{note}`）。PriceSuggestion：最新建议 `pending=true` 且工单未闭环未作废，按建议时间正序（有备注追加 `（{note}）`）。
  `Submitted` 类待办：工单未定价时副标题末尾追加 `，尚未定价`。原有 4 类其余不变（`requestId` 为 null）。
- `GET /api/admin/overview`：增加 `onHold`、`pendingRequests`、`unpriced`、`priceSuggestions`。

---

## 4. 进度条（5 段）与暂停展示

5 段固定为：待认领 / 待启动 / 处理中 / 待验收 / 已闭环。UI 一律用服务端的 `progressStep`，不再用状态→段的本地映射。
- 第 1 … `progressStep` 段点亮。
- `status == "OnHold"` 时：第 `progressStep` 段显示为**暂停**样式（灰色 `#AEAEB2` 底 + 暂停图标「⏸」或斜纹，不用红/黑），其之前的段正常点亮；进度条下方（或右侧）显示灰色文字 `暂停 · {holdText}`。
- 状态标签：OnHold 显示 `暂停搁置`；列表行的说明文字显示 `暂停 · {holdText}`（负责人姓名在前，如 `李工 · 暂停 · 等备件：轴承预计周五到`）。
- 返厂单在列表说明里追加 `factoryText`（如 `陈工 · 检测/维修中`）。

---

## 5. 按人工时、统计与导出

- **详情**：`participants[]` 每人一行（角色、累计工时 `totalMinutes`、是否在现场、是否缺卡）。`totalMinutes`（工单级）= 各人之和。
- **工时统计**（`GET /api/admin/hours`、`/api/hours/month`、`/api/hours/today`）：按打卡人归属，协助人的工时计入协助人本人；明细项新增 `role`。合计口径不变。
- **导出工时** `GET /api/admin/export/hours`：在最后追加第 10 列 `本单角色`（负责人 / 协助人 / 已退出）。前 9 列不变。
- **导出工单台账** `GET /api/admin/export/orders`（仅主管）：
  - 第 16 列 `是否收费`：未定价 → `未定价`；价格 > 0 → `是`；0 → `否（保内）`。
  - 第 17 列 `服务价格`：未定价 → 文本 `未定价`；否则数字。
  - 第 12 列 `处理人` = 负责人（不变）；第 18 列 `现场工时(小时)` = 所有人合计（不变）。
  - 在最后追加 5 列：`协助人`（姓名用「、」连接）、`人员工时`（每个在本单有工时的人 `{姓名} {小时1位小数}h`，用「；」连接，负责人在前，如 `李工 6.0h；王工 3.5h`）、
    `暂停原因`（仅 OnHold：holdText）、`返厂进度`（返厂单：factoryText）、`寄回单号`（返厂单已寄回：`{快递公司} {单号}`）。

---

## 6. 小程序改动清单（miniprogram/）

通用：
- `utils/enums.js`：新增 `HOLD_REASON`（4 项，含 `HOLD_REASON_OPTIONS`）、`REQUEST_TYPE`、`REQUEST_STATUS`、`FACTORY_NODE`（5 项 label + `action` 文案：客户寄出→「记录客户寄出」、工厂收货→「确认工厂收货」、寄回客户→「寄回客户」、客户签收→「记录客户签收」）、`PARTICIPANT_ROLE`；
  `FILE_CATEGORY` 加 3 类；`ACTION` 加 `factory` `requestHold`「申请暂停」`requestAssistant`「申请协助人」`assistants`「调整协助人」`setPrice`「定价」`suggestPrice`「价格建议」；`TODO_TYPE` 加 3 类；
  `progressSegments(order)` 改为接收工单对象，返回 `[{on, paused}]`（用 `order.progressStep`、`order.status`）；删除 `STATUS[*].step` 的使用。
- `utils/format.js`：`formatPrice(price, short, priceSet)`：`priceSet === false` 或 `price === null` 且 `priceSet !== true` 时返回 `未定价`；其余不变。
- `api/index.js`：新增 `orders.setPrice(id, price)`、`orders.suggestPrice(id, amount, note)`、`orders.hold(id, reasonType, note)`（改签名）、`orders.requestHold(id, reasonType, note)`、
  `orders.approveRequest(id, requestId)`、`orders.rejectRequest(id, requestId, reply)`、`orders.cancelRequest(id, requestId)`、`orders.setAssistants(id, engineerIds)`、
  `orders.requestAssistant(id, engineerId, note)`、`orders.factoryNode(id, body)`；`orders.assign(id, engineerId, assistantIds)`；更新顶部 typedef。
- 新组件 `components/progress-bar/`（属性 `order`；5 段 + 暂停样式 + 暂停文字，§4）。
- 新页面 `pages/factory-node/index`（query：`id`、`node`；在 `app.json` 注册）：返厂节点记录 / 更正表单，照片上传逻辑复用 `pages/complete` 的做法（相机或相册，最多 9 张，上传 category 按节点）。

| 文件 | 角色 | 改动 |
|---|---|---|
| `pages/order-new/*` | 全部 | 删除「服务价格」字段、`price` 校验与提交字段、`price-tip`；分组标题「价格与照片」改为「现场照片」。提交体不带 price |
| `components/home-sales/*` | 销售 | 「新建工单」副标题改为「填写客户、设备与故障情况」；列表价格用 `formatPrice(o.price, false, o.priceSet)`（未定价显示灰色「未定价」）；进度条改用 `<progress-bar>`；`noteOf`：OnHold → `暂停 · {holdText}`；返厂单追加 `factoryText` |
| `pages/orders/*` | 工程师（大厅） | 卡片删除价格 |
| `pages/orders/*` | 销售 / 主管 | 价格支持「未定价」；状态行 OnHold 显示 `暂停 · {holdText}`；有协助人时显示 `+N 协助`；主管：`pendingRequestCount > 0` 显示琥珀色「待审批」小标 |
| `pages/my-orders/*` | 工程师 | 删除价格；`myRole === 'Assistant'` 显示「协助」标签；工时列用 `myMinutes` / `myOnSiteSince`；OnHold 说明 `暂停 · {holdText}`；返厂单说明用 `factoryText`；页头说明改为「本人负责或协助的工单」 |
| `components/home-engineer/*` | 工程师 | 删除所有价格；当前单 / 下一单 / 列表项在 `myRole === 'Assistant'` 时显示「协助」标签且**不显示**「提交完工」「去开始处理」按钮（只保留打卡 / 查看详情）；返厂单按钮改为「查看返厂流程」→ 详情；新增「暂停搁置」小节展示 `onHold` 列表（客户名 + `暂停 · {holdText}`）；非现场提示文字改为「远程工单在详情页点「开始处理」；返厂工单在详情页记录返厂节点」 |
| `components/home-supervisor/*` | 主管 | `buildTodo` 增加：`HoldRequest`（按钮「审批」，suffix「暂停申请」）、`AssistantRequest`（按钮「审批」，suffix「协助人申请」）、`PriceSuggestion`（按钮「定价」，suffix「价格建议」），都跳工单详情 |
| `components/assign-sheet/*` | 主管 / 负责人 | 新增属性 `mode`：`assign`（默认；单选负责人 + 下方「协助人（可多选）」区，提交 `assign(id, engineerId, assistantIds)`，改派时预选当前协助人）、`assistants`（只多选协助人，负责人置灰标「负责人」，提交 `setAssistants`）、`request`（负责人用：单选 1 名工程师 + 说明输入框，提交 `requestAssistant`；已是协助人/负责人的置灰）。最多 5 名协助人，超出 toast「协助人最多 5 人」 |
| `components/status-tag/*` | 全部 | 不变（OnHold 仍显示「暂停搁置」） |
| `pages/order-detail/*` | 全部 | 见下 |
| `pages/checkin/*` | 工程师 | 无逻辑改动（协助人由服务端放行）；页头显示 `myRole === 'Assistant'` 时的「协助」标签 |
| `pages/complete/*` | 工程师 | 40900 且文案不是「请先离场打卡」时，用弹窗原样展示服务端 message（他人未离场 / 返厂未寄回 / 已暂停） |
| `pages/hours/*` | 工程师 | 明细项 `role === 'Assistant'` 显示「协助」标签 |

**`pages/order-detail` 详细改动**
- 头部：加入 `<progress-bar order="{{order}}">`（所有角色）。
- 价格格（头部第 1 格）：
  - 工程师：整格替换为「维修类型」（现场/远程/返厂），页面任何位置不出现价格。
  - 销售：显示价格或「未定价」；`actions` 含 `suggestPrice` 时格内显示链接「提建议」→ 底部弹层（金额 digit 输入 + 备注，提交 `suggestPrice`）；`priceSuggestion` 存在时在头部下方显示一行 `我方建议 ¥1,500 · {note}`（pending 时标「待主管确认」）。
  - 主管：显示价格或红色「未定价」；链接「定价」/「改价」→ 底部弹层（金额输入，0 = 保内免费；`priceSuggestion.pending` 时弹层顶部显示 `{销售} 建议 ¥1,500 · {note}` 并提供「采用建议」一键填入）；`priceSuggestion.pending` 时头部下方显示琥珀色提示条「{销售} 建议价格 ¥1,500」。
- 申请卡片（异常警示区下方，所有角色可见 `requests` 中 `status === 'Pending'` 的项）：琥珀色卡片，标题「暂停申请」/「协助人申请」，正文 `{requester.name}：{text}`、时间；`canDecide` → 按钮「同意」「驳回」（驳回弹输入框，回复选填）；`canCancel` → 按钮「撤回」。
- 暂停提示：`order.hold` 非空时在信息 tab 顶部（工程师）与进度 tab 顶部（销售/主管）显示灰色提示条 `暂停搁置 · {hold.text}`，副行 `{hold.by.name} {时间} 暂停，恢复后回到「{fromStatus 中文}」`。
- 主管「标为暂停搁置」与工程师「申请暂停」共用一个底部弹层：4 个原因胶囊（单选）+ 说明输入框（选「其他」时必填，placeholder「如：客户下周一才能安排停机」）+ 确定。不再用 `wx.showModal` 输入原因。
- 「处理人员」卡片（信息 tab，所有角色；`participants` 非空时）：每人一行：头像、姓名、角色标签（负责人 / 协助 / 已退出）、累计工时 `H:mm`（现场单）、在现场时红色「现场中」、缺卡时红色「缺卡」。`actions` 含 `assistants` → 右上链接「调整协助人」（assign-sheet mode=assistants）；含 `requestAssistant` → 链接「申请协助人」（mode=request）。原「处理人」头部行保留（负责人）。
- 返厂流程卡片（仅返厂单）：5 个节点的竖向时间线（样式复用进度时间线）：标题=节点中文、状态点（done 实心 / current 红 / todo 灰 / skipped 灰并标「已跳过」）、副行 `{operator.name} · {时间}`、快递行 `{courier} {trackingNo}`（**点击复制单号**，`wx.setClipboardData`）、备注、照片缩略图（点击预览）；`canRecord` → 行内主按钮（文案见 FACTORY_NODE.action）；`canEdit` → 行内文字链接「更正」；都跳 `pages/factory-node/index?id=&node=`。
  - 工程师的 tabs：返厂单为 `信息 / 返厂流程 / 资料`（取代「打卡记录」，默认选中「返厂流程」）；非返厂单不变。
  - 销售 / 主管：返厂卡片放在「进度」tab 顶部。
- 资料 tab：返厂单增加一行「返厂照片」（`files.factoryPhotos`）。
- 进度 tab（销售/主管）`buildSteps`：OnHold 当前步标题「暂停搁置」、副行 `hold.text`；返厂单的「待启动」当前步副行改为「等待设备到厂，工厂收货后进入处理中」。
- 打卡记录 tab：多人时每条打卡显示姓名（现有 `multiEng` 逻辑保留）；按天小计为所有人合计。
- 底部按钮：
  - 工程师顺序 `['requestHold', 'appoint', 'checkin', 'start', 'claim', 'factory', 'complete']`；主管顺序 `['void', 'hold', 'reject', 'reassign', 'resume', 'assign', 'factory', 'close']`；销售 `['factory']`。（`setPrice` / `suggestPrice` / `assistants` / `requestAssistant` 是页面内链接，不进底部栏。）
  - `factory` 按钮文案 = 第一个 `canRecord` 节点的 action 文案，点击进入该节点表单。
  - 底部最多显示 3 个按钮：超过时把顺序靠前的多余项收进「更多」（`wx.showActionSheet`）。
  - 主管在 Submitted 且 `closeBlockedReason` 非空：显示禁用的「确认闭环」，点击 toast `closeBlockedReason`（取代写死的「请先补录缺卡」）。点击「确认闭环」时若 `closeHints` 非空，确认框正文追加 `提示：{closeHints 用；连接}`。
  - `progressNote` 里销售的说明改为「销售可查看进度、提价格建议；改派、作废、定价、补录由主管操作。」

---

## 7. PC 后台改动清单（wwwroot/admin/）

| 文件 | 改动 |
|---|---|
| `js/api.js` | `orders.setPrice / hold(id, reasonType, note) / approveRequest / rejectRequest / setAssistants / factoryNode / assign(id, engineerId, assistantIds)`；`orders.list` 参数加 `priceSet`、`pendingRequest` |
| `js/enums.js` | `HOLD_REASON`、`REQUEST_TYPE`、`REQUEST_STATUS`、`FACTORY_NODE`、`PARTICIPANT_ROLE`；`FILE_CATEGORY` 加 3 类；`ORDER_ACTIONS` 加 `assistants`「调整协助人」`setPrice`「定价」`factory`；`QUICK_FILTERS` 加 `{ key: 'requests', label: '待审批申请', query: { pendingRequest: true } }`、`{ key: 'unpriced', label: '未定价', query: { priceSet: false } }` |
| `js/format.js` | `fmtPrice(price, opts)` 支持 `opts.priceSet === false` → `未定价` |
| `js/views/orders-create.js` | 删除服务价格输入、校验与提交字段 |
| `js/views/orders.js` | 价格列：未定价显示红色「未定价」；状态列 OnHold 显示 `暂停搁置 · {holdText}`（过长省略，title 全文）；处理人列显示负责人 + `+N`（title 列出协助人）；`pendingRequestCount > 0` 显示「待审批」标；返厂单在维修类型旁显示 `factoryText`；概况条增加「暂停搁置 N」「待审批申请 N」「未定价 N」「价格建议 N」（来自 overview）；支持新的两个 quick 筛选 |
| `js/views/orders-drawer.js` | 头部加 5 段进度条（§4）；价格行：金额或「未定价」+ 按钮「定价 / 改价」；`priceSuggestion` 行（`{by.name} 建议 ¥x · note · 时间`，pending 时高亮并带「采用」）；`hold` 提示条（`暂停搁置 · {hold.text}`，暂停人与时间）；**申请区**（Pending 的 `requests`：内容 + 「同意」「驳回」）；**处理人员区**（participants 表：姓名、角色、累计工时、现场中/缺卡；按钮「调整协助人」）；**返厂流程区**（5 节点时间线：操作人、时间、快递公司与单号（点击复制）、备注、照片缩略图可预览；`canRecord` → 按钮，`canEdit` → 「更正」）；底部按钮加 `hold`（Accepted 也有）、`factory`；禁用的「确认闭环」提示用 `closeBlockedReason`；闭环确认框展示 `closeHints`；打卡记录每条显示姓名 |
| `js/views/orders-assign.js` | 单选负责人 + 多选协助人（复选，最多 5，负责人不可选为协助人；改派时预选当前协助人）；新增 `mode: 'assistants'`（只调整协助人，调用 `setAssistants`） |
| `js/views/orders-hold.js`（新） | 暂停弹窗：原因下拉（4 项）+ 说明（其他时必填） |
| `js/views/orders-price.js`（新） | 定价弹窗：金额（0 = 保内免费），展示最新价格建议并可一键采用 |
| `js/views/orders-factory.js`（新） | 返厂节点记录 / 更正弹窗：按节点显示快递公司、单号、备注、照片上传（`api.files.upload`，category 按节点，最多 9 张） |
| `js/views/hours.js` | 工程师每日明细中 `role === 'Assistant'` 的条目显示「协助」标签 |
| `js/views/orders-export.js` | 说明文字提示台账新增列（协助人、人员工时、暂停原因、返厂进度、寄回单号）；无逻辑改动 |
| `css/admin.css` | 进度条暂停样式、申请区、人员表、返厂时间线样式 |
| `README.md`（admin） | 同步功能说明 |

PC 后台只有主管能登录，因此只实现主管视角的动作（定价、暂停、审批、协助人、全部返厂节点）。

---

## 8. 后端改动文件

`Domain/Entities.cs`、`Domain/Enums.cs`、`Data/AppDbContext.cs`、`Data/Migrations/*`（新迁移 + 快照）、`Data/SeedData.cs`、`Dtos/CommonDtos.cs`、`Dtos/RequestDtos.cs`、`Dtos/ViewDtos.cs`、
`Services/OrderService.cs`、`Services/OrderStateMachine.cs`、`Services/OrderViewBuilder.cs`、`Services/PunchService.cs`、`Services/HomeService.cs`、`Services/HoursService.cs`、
`Services/AdminService.cs`、`Services/AdminReportService.cs`（仅透传）、`Services/CompletionValidator.cs`（不改签名）、新增 `Services/FactoryService.cs`、`Services/OrderRequestService.cs`（或并入 OrderService，由实现者定）、
`Controllers/OrdersController.cs`、`Controllers/HomeController.cs`（engineers 放开角色）、`Program.cs`（注册新服务）、`scripts/smoke.sh`、`tests/*`。

SeedData（只在空库运行）：所有工单 `PriceSet = true`；`无锡某制药企业`（紧急待认领）改为 `PriceSet = false, Price = 0`；
返厂已闭环单 `苏州某药企` 补 4 条 FactoryRecords（客户寄出 顺丰 SF1100200300、工厂收货 + 1 张照片、寄回客户 顺丰 SF1100200399 + 1 张照片、客户签收）及对应状态记录。不改动任何打卡数据（现有工时断言不受影响）。

---

## 9. 测试

### 9.1 必须修改的现有测试
| 测试 | 修改 |
|---|---|
| `OrderStateMachineTests.LegalTransitions` | 增加 `(Accepted, Hold) → OnHold`；`(OnHold, Resume)` 改为按来源状态断言（InProgress / Accepted 两种） |
| `OrderStateMachineTests.EngineerActions / SupervisorActions / SalesHasNoActions` | 按 §2.7 的新签名与示例数组重写（Sales 用例改名 `SalesActions`，断言 `["suggestPrice"]`） |
| `OrderStateMachineTests.ProgressSteps` | 新签名；增加 OnHold(from Accepted)=2、OnHold(from InProgress)=3、OnHold(from null)=3 |
| `ServiceIntegrationTests.NewOrder()` | 去掉 `Price = 0`（或保留以验证被忽略） |
| `ServiceIntegrationTests.HoldBlockedWhileEngineerOnSite` | 改用新请求体 `{ ReasonType = WaitParts }`；文案断言不变 |
| `ServiceIntegrationTests.LeaveAllowedOnHeldOrderWithOpenArrive` | 最后一条断言改为「工单已暂停搁置，恢复后才能打卡」 |
| 所有直接调用 `BuildList / BuildOne / FillList` 的测试与服务 | 新增 `CurrentUser` 参数 |
| `scripts/smoke.sh` | 建单不再依赖 price；远程单 actions 断言改为 `appoint,start,requestHold,requestAssistant`；暂停断言改为新请求体，`holdReason` 断言为 `等备件`；返厂单走节点流程 |

### 9.2 必须新增的测试（xUnit，服务级，沿用 SQLite 内存库 + 种子数据的写法）

迁移：
1. `MigrationUpgradeKeepsExistingOrdersPriced`：先 `Migrate("AddEmployeePassword")`，用 SQL 插入 1 名员工 + 1 张工单（Price=1800），再 `Migrate()`；断言该单 `PriceSet == true`、`HoldReasonType == null`，已有 StatusLog 的 `Kind == null`，4 张新表存在且为空。
2. `NewOrderIsUnpriced`：用 EF 新建工单后重新读取，`PriceSet == false`（防 bool 默认值陷阱）。

价格：
3. `CreateIgnoresPrice`：请求带 `Price = 999` → 详情 `priceSet=false`、`price=null`。
4. `SupervisorSetsAndChangesPrice`：定价 0 → 记录「赵主管 定价 保内免费」；改 1800 → 「赵主管 改价 保内免费 → ¥1,800」；负数 / null → 40000；Voided → 40900。
5. `EngineerNeverSeesPrice`：同一张已定价且有价格建议的工单，工程师取 detail / hall / mine / home / 打卡返回的 order：`price == null && priceSet == null && priceSuggestion == null`，logs 无 `Price` 类；序列化后的 JSON 不含金额数字。销售与主管能看到。
6. `SalesSuggestsPrice`：销售提交 → 主管详情 `priceSuggestion.pending == true`；主管定价后 `pending == false`；工程师 / 主管调用 → 403（控制器层角色限制，可用服务层权限断言替代）；通知正文不含金额。
7. `CloseRequiresPrice`：未定价的 Submitted 单 close → 40900「请先设置服务价格（保内填 0）」，`closeBlockedReason` 同文案，actions 无 `close`；定价后可闭环。
8. `ExportShowsUnpriced`：导出台账中未定价单第 16、17 列为「未定价」。

暂停：
9. `SupervisorHoldsFromAcceptedAndResumesBack`：Accepted → OnHold → resume 回 Accepted；InProgress 同理回 InProgress；`hold.text`、`progressStep` 正确。
10. `HoldValidation`：无 reasonType → 「请选择暂停原因」；Other 无说明 → 「请填写暂停说明」；旧请求体 `{reason}` 可用。
11. `HoldRequestApproveRejectCancel`：负责人申请 → 主管待办出现 `HoldRequest`、`pendingRequestCount == 1`、通知 +1；重复申请 → 40900「已有待审批的暂停申请」；非负责人 → 40300；驳回带回复 → 状态不变、申请 Rejected；再申请 → 同意 → OnHold，原因取自申请；撤回只能本人。
12. `HoldBlockedWhileAssistantOnSite`：协助人未离场时直接暂停与同意申请都 → 40900「协助人王工正在现场计时，请先离场打卡后再搁置」。
13. `OnHoldBlocksPunchStartComplete`：三条文案逐一断言。
14. `PendingRequestsAutoCancelled`：申请后主管直接暂停 / 改派 / 完工 → 申请变 Cancelled，Reply 为对应原因。

多人：
15. `AssignWithAssistantsAndReplace`：指派带 2 名协助人 → 两条状态记录；`assistants` 接口替换为 1 人 → 「移除协助人」；负责人作协助人 / 停用工程师 / 超过 5 人 → 对应 40000。
16. `ReassignToAssistantPromotes`：改派给当前协助人 → 成为负责人且不在 assistants 中。
17. `AssistantSeesAndPunches`：协助人能取详情（`myRole == Assistant`）、出现在 mine / home；能到场打卡且使 Accepted → InProgress；非参与工程师打卡 → 40300；协助人在另一单未离场时 → 「请先在〈…〉离场打卡」。
18. `CompleteBlockedWhileAssistantOnSite`：协助人未离场 → 40900「王工还在现场计时，请其先离场打卡后再提交完工」；负责人自己未离场仍是「请先离场打卡」；协助人不能提交完工（403）。
19. `PerPersonHours`：两人各自一对打卡 → `participants` 各自 `totalMinutes`，工单 `totalMinutes` = 和，`myMinutes` 为本人的；`AdminReportService.Hours` 把协助人工时计入协助人；工时导出第 10 列为「协助人」；台账「人员工时」列包含两人。
20. `AssistantMissingPunchBlocksClose`：协助人昨天缺卡 → issues 含 MissingPunch、`issueDetails[0].engineer` 为协助人、close 被拦；主管补录后记录为「赵主管 补录离场 18:20（王工）」，可闭环。
21. `AssistantRequestFlow`：负责人申请 → 待办 `AssistantRequest`；同一人重复申请 → 40900；同意 → 成为协助人并通知；驳回 → 不变；移除在现场的协助人 → 40900。
22. `EngineersListOpenToEngineerAndCountsAssist`：`assistCount` 与 loadText「协助 1 单」。

返厂：
23. `FactoryHappyPath`：销售建返厂单 → 工程师认领 → 销售记客户寄出 → 工程师工厂收货（Accepted → InProgress，记录「… 工厂收货 → 处理中」）→ 寄回客户 → 完工 → 销售记客户签收 → 闭环；每步断言 `factory.nodes[*].state`、`factoryText`、actions。
24. `FactoryOrderingRules`：工程师跳过客户寄出直接收货 → 「请先记录「客户寄出」」；主管可跳过且节点 1 变 `skipped`；无收货就寄回、无寄回就签收 → 对应文案；`Repairing` → 40000；非返厂单 → 40900。
25. `FactoryFieldValidation`：缺快递公司 / 单号 / 照片的各条 40000 文案。
26. `FactoryPermissions`：协助人、无关销售记节点 → 40300；建单销售只能记 1、5。
27. `FactoryCompleteRequiresShippedBack`：未寄回完工 → 40900 文案；返厂单 start → 40900 文案；返厂单打卡仍被拒。
28. `FactoryEditKeepsStatusAndReplacesPhotos`：更正寄回单号 → 记录「更正寄回客户（…）」，状态不变，照片按最终全集替换；Closed 后更正 → 40900。
29. `FactoryHoldInteraction`：OnHold 时记收货 → 40900「工单已暂停搁置，恢复后才能继续返厂流程」，记客户寄出仍可。
30. `CloseHintWhenNotSigned`：寄回未签收的 Submitted 返厂单：`closeHints == ["客户签收尚未记录"]` 且可闭环。

其他：
31. `SupervisorTodosOrderAndNewTypes`：三类新待办的顺序、`requestId`、副标题。
32. `OverviewCountsV11`：`onHold / pendingRequests / unpriced / priceSuggestions`。
33. `OrdersQueryPriceSetAndPendingRequestFilters`。

验收：`cd server && dotnet test -v q` 全绿（原 112 个 + 新增）。
