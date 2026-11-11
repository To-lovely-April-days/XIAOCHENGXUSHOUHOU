# 接口契约（前后端唯一依据）

Base URL：`{host}/api`。JSON 使用 **camelCase**；枚举一律以**字符串**传输（如 `"Pending"`）。

> 当前版本 **v1.3**（在 v1.1 的价格 / 暂停 / 多人协作 / 返厂流程、v1.2 的工单附件与文件中心之上，新增：多台设备的「设备资料」、附件分类与闭环后可传、工程师之间的「转交」）。本文件即现行契约；
> 逐条校验文案、状态记录文字、通知、测试清单与各端改动清单见 `docs/CHANGES-v1.1.md`、`docs/CHANGES-v1.2.md`、`docs/CHANGES-v1.3.md`（与本文件冲突时以 CHANGES 文档为准；同一内容以版本号最新的那份为准）。

## 0. 通用约定

### 响应包装
所有接口（除文件下载 `GET /api/files/{id}/download` 与导出）返回：
```json
{ "code": 0, "message": "ok", "data": { } }
```
| code | HTTP | 含义 |
|---|---|---|
| 0 | 200 | 成功 |
| 40000 | 400 | 参数校验失败（message 为可直接展示的中文） |
| 40100 | 401 | 未登录 / token 失效（含账号已停用、已解绑、角色或手机号已变更，服务端每次请求复核）→ 前端清 token 重新登录 |
| 40300 | 403 | 无权限 |
| 40400 | 404 | 不存在 |
| 40401 | 404 | 绑定时未找到员工账号（「未找到账号，请联系售后主管在后台添加」） |
| 40900 | 409 | 状态冲突（如「请先离场打卡」「工单已被他人认领」） |
| 50000 | 500 | 服务器错误 |

### 认证
`Authorization: Bearer <token>`（JWT，有效期 30 天）。除 `/api/auth/*` 外都需要。

### 时间
- 所有时间为**北京时间**，格式 `yyyy-MM-ddTHH:mm:ss`（**不带时区后缀**），如 `"2026-11-06T10:05:00"`。前端按本地时间解析（iOS 必须用带 `T` 的格式）。
- 日期 `yyyy-MM-dd`。
- 时长一律用**分钟整数** `xxxMinutes`；前端格式化为 `H:mm`。

### 枚举
| 枚举 | 值（中文） |
|---|---|
| Role | `Engineer` 售后工程师, `Sales` 销售, `Supervisor` 售后主管 |
| OrderStatus | `Pending` 待认领, `Accepted` 待启动, `InProgress` 处理中, `OnHold` 暂停搁置, `Submitted` 待验收, `Closed` 已闭环, `Voided` 已作废 |
| OrderSource | `SalesReferral` 销售转报, `CustomerDirect` 客户直联, `Inspection` 现场巡检, `ReturnLogistics` 退货物流 |
| ProblemType | `Fault` 设备故障, `Complaint` 客诉投诉, `Return` 退回返修, `Consult` 咨询指导 |
| Urgency | `Urgent` 紧急, `Normal` 一般, `Consult` 咨询 |
| ServiceType | `Onsite` 现场, `Remote` 远程, `Factory` 返厂 |
| PunchKind | `Arrive` 到场, `Leave` 离场 |
| RunStatus | `Normal` 正常, `Observe` 待观察, `NotRecovered` 未恢复 |
| IssueType | `MissingPunch` 缺卡, `DistanceAbnormal` 距离异常 |
| FileCategory | `OrderPhoto` 建单照片, `PunchPhoto` 打卡照片, `ProcessPhoto` 处理照片, `Signature` 客户签字件, `Avatar` 头像, `FactoryReceivePhoto` 收货照片, `FactoryShipPhoto` 寄回照片, `FactorySignPhoto` 签收凭证, `Attachment` 附件（v1.2） |
| FileKind | `Image` 图片, `Video` 视频, `Document` 文档（由 mimeType 推导：`image/*`、`video/*`、其余） |
| HoldReasonType | `WaitCustomer` 等客户, `WaitParts` 等备件, `WaitThirdParty` 等第三方, `Other` 其他 |
| OrderRequestType | `Hold` 暂停申请, `AddAssistant` 协助人申请 |
| RequestStatus | `Pending` 待审批, `Approved` 已同意, `Rejected` 已驳回, `Cancelled` 已撤销 |
| FactoryNode | `CustomerShipped` 客户寄出, `FactoryReceived` 工厂收货, `Repairing` 检测/维修（隐含节点）, `ShippedBack` 寄回客户, `CustomerSigned` 客户签收 |
| ParticipantRole | `Lead` 负责人, `Assistant` 协助人, `Former` 已退出（v1.3：有退出记录的原负责人——被转交或被改派换掉，对本单只读；或在本单有打卡但已不是负责人/协助人的人，仅展示） |
| LogKind | `Status` 状态流转, `Price` 价格（工程师不可见）, `Hold` 暂停, `Assist` 人员（含转交）, `Factory` 返厂, `Attachment` 附件, `AttachmentPrivate` 受限附件（工程师不可见）, `Info` 资料修改（v1.3，如 SAT 信息） |
| AttachmentType（v1.3 附件类别） | `Sat` SAT 验收单（签字版）, `RepairForm` 现场维修登记表/返厂维修登记表（签字版）, `Other` 设备照片、沟通记录及其他附件。短标题：SAT 验收单 / 维修登记表 / 其他附件。展示顺序固定为 Sat → RepairForm → Other |
| TransferStatus（v1.3） | `Pending` 待确认, `Accepted` 已接手, `Rejected` 已拒绝, `Cancelled` 已撤回, `Expired` 已失效 |
| ExitVia（v1.3） | `Transfer` 已转交, `Reassign` 已改派 |

**文件来源 source**（字符串，由 FileCategory 推导）：`Attachment` 附件、`OrderPhoto` 建单照片、`ProcessPhoto` 完工照片、`Signature` 签字件、`PunchPhoto` 打卡照片、`Factory` 返厂照片（收货 / 寄回 / 签收三类）。

**暂停原因文字 holdText**：`Other` → 只用说明；其余 → `{类型中文}`，有说明时 `{类型中文}：{说明}`（如 `等备件：轴承预计周五到`）。

### 公共 DTO

**UserBrief**
```json
{ "id": 1, "name": "李工", "surname": "李", "avatarUrl": "https://.../uploads/a.jpg 或 null", "role": "Engineer" }
```

**User**（UserBrief 全部字段 +）
```json
{ "roleName": "售后工程师", "department": "售后部", "phoneMasked": "139****0000", "phone": "13900000000" }
```
（`phone` 仅返回给本人，用于展示；其他场景只给 UserBrief）

**FileDto**
```json
{ "id": 12, "url": "https://host/uploads/2026/11/xxx.jpg", "category": "PunchPhoto", "mimeType": "image/jpeg", "createdAt": "2026-11-06T10:05:00",
  "name": "打卡照片_20261106-100500.jpg", "size": 345678, "kind": "Image", "ext": "jpg" }
```
`url` 为绝对地址（后端用 `PublicBaseUrl` 配置或请求 Host 拼接），静态访问、无需认证；图片 / 视频的预览用它。
`name`：展示名与下载名——上传时客户端给了原始文件名（`fileName`）就用它，否则服务端生成 `{类别前缀}_{yyyyMMdd-HHmmss}{扩展名}`。`size` 字节数；`kind` 为 FileKind；`ext` 小写不带点。

**AttachmentDto**（v1.2；= FileDto 全部字段 +）
```json
{ "source": "Attachment" | "OrderPhoto", "sourceLabel": "附件",
  "restricted": false,                 // 仅销售和主管可见
  "uploader": { UserBrief } | null,
  "canDelete": true,                   // 当前用户现在可以删除
  "canToggleRestricted": false,        // 当前用户可以切换「仅销售和主管可见」（仅 Supervisor）
  "attachmentType": "Sat" | "RepairForm" | "Other" | null,   // v1.3 附件类别；文件中心里非附件来源的行为 null
  "attachmentTypeLabel": "SAT 验收单（签字版）",                // 完整标题（非附件行为 ""）
  "attachmentTypeShort": "SAT 验收单",                         // 短标题（非附件行为 ""）
  "canChangeType": true }              // v1.3：当前用户现在可以改这个附件的类别
```
「附件」= 挂在工单上、category 为 `OrderPhoto`（建单照片，旧数据）或 `Attachment` 且未删除的文件；每单最多 **50** 个（v1.3，原 20）。旧附件一律属于 `Other`。

**附件可见性（硬规则，与价格同等严格）**：当前用户为 `Engineer` 时，任何接口都不返回 `restricted = true` 的附件（详情的 `attachments`、`files.orderPhotos`、下载、文件中心），
`logs` 不含 `kind = "AttachmentPrivate"` 的记录。

**OrderListItem**
```json
{
  "id": 3,
  "orderNo": "SH-20261105-003",
  "status": "InProgress",
  "urgency": "Normal",
  "serviceType": "Onsite",
  "problemType": "Fault",
  "customerName": "江苏某化工研究院",
  "city": "苏州",              // 地址解析出的城市短名（去掉“市”），解析不到给省份短名或 ""
  "region": "苏州市工业园区",     // 市+区县，解析不到给 ""
  "projectNo": "HT-2024-031" | null,   // v1.3：项目编号（新建单必填；旧单可为 null，界面显示「—」）
  "deviceText": "高压反应釜 HTR-500 等 3 台",   // v1.3：设备汇总文字，永不为空：0 台「未填设备」/ 1 台 = 该设备标题 / n 台「{第 1 台标题} 等 n 台」。列表与标题一律显示它
  "deviceModel": "高压反应釜 HTR-500",  // 旧客户端兼容：第一台设备的标题（v1.3 客户端不再读）
  "deviceSn": "SN2408017",             // 旧客户端兼容：第一台设备的编号（v1.3 客户端不再读）
  "symptom": "升温到 180℃ 左右就不再加热，面板报 E03",
  "price": 1800.00 | null,      // 0 = 保内免费；未定价为 null；**Engineer 永远为 null**
  "priceSet": true | false | null,   // 是否已定价；**Engineer 永远为 null**
  "assignee": { UserBrief } | null,   // 负责人
  "assistants": [ { UserBrief } ],    // 协助人（按添加时间），无则 []
  "myRole": "Lead" | "Assistant" | "Former" | null,   // 当前用户在本单的角色；Former（v1.3）= 本人是本单已退出的原负责人（只读）
  "myExitText": "已转交" | "已改派" | null,           // v1.3：仅 myRole = Former
  "pendingTransfer": null | { TransferDto },          // v1.3：本单待确认的转交（每单最多一个）
  "myMinutes": 120 | null,            // myRole 非 null 时：本人在本单已配对工时
  "myOnSiteSince": "2026-11-06T10:05:00" | null,  // 本人今天在本单未离场的到场时间
  "progressStep": 3,                  // 5 段进度：Pending=1 Accepted=2 InProgress=3 Submitted=4 Closed=5 Voided=0；OnHold=暂停前状态对应的段
  "holdReasonType": null | "WaitParts",           // 仅 OnHold
  "holdText": null | "等备件：轴承预计周五到",       // 仅 OnHold
  "holdFromStatus": null | "InProgress",          // 仅 OnHold
  "pendingRequestCount": 0,           // 待审批申请数
  "factoryText": null | "检测/维修中",  // 仅返厂单：待客户寄出 / 客户已寄出 / 检测/维修中 / 已寄回客户 / 客户已签收
  "creator": { UserBrief },     // 建单人（销售或工程师）
  "createdAt": "2026-11-05T09:20:00",
  "claimedAt": "2026-11-05T09:48:00" | null,
  "appointmentTime": "2026-11-09T10:00:00" | null,
  "submittedAt": null,
  "closedAt": null,
  "totalMinutes": 362,          // 本单所有人累计已配对现场工时（不含正在计时）
  "onSiteSince": "2026-11-06T10:05:00" | null,   // 任一参与人今天有未离场到场时为（最近的）到场时间
  "lastArriveTime": "2026-11-06T10:05:00" | null,
  "issues": ["DistanceAbnormal"]   // 覆盖本单所有打卡人
}
```
前端自行把 `price` 格式化为 `¥1,800` / `保内免费` / `未定价`（`priceSet === false`），把状态等枚举映射成中文与配色。

**价格可见性（硬规则）**：当前用户为 `Engineer` 时，任何接口返回的任何工单对象里 `price`、`priceSet`、`priceSetAt`、`priceSuggestion` 都是 `null`，
`logs` 不含 `kind = "Price"` 的记录，`actions` 不含价格动作。工程师端界面任何位置不展示价格。

**RequestDto**（工程师发起、主管审批的申请）
```json
{
  "id": 12, "orderId": 3, "type": "Hold" | "AddAssistant", "status": "Pending",
  "requester": { UserBrief }, "createdAt": "2026-11-06T14:00:00",
  "reasonType": "WaitParts" | null, "reasonText": "等备件" | null,   // Hold
  "note": "轴承预计周五到" | null,
  "text": "等备件：轴承预计周五到" | "添加协助人 王工",                 // 可直接展示
  "engineer": { UserBrief } | null,                                 // AddAssistant 的目标工程师
  "decider": { UserBrief } | null, "decidedAt": null, "reply": null,
  "canDecide": true,     // 当前用户可同意/驳回（Supervisor 且 Pending）
  "canCancel": false     // 当前用户可撤回（本人发起且 Pending）
}
```

**ParticipantDto**
```json
{ "user": { UserBrief }, "role": "Lead" | "Assistant" | "Former",
  "totalMinutes": 362, "onSiteSince": "2026-11-06T10:05:00" | null, "missingPunch": false,
  "exitText": "已转交" | "已改派" | null, "exitedAt": "2026-11-11T14:20:00" | null }   // v1.3：仅有退出记录的 Former
```
排序：负责人 → 协助人 → 已退出。有退出记录的人即使没有任何打卡也会出现。

**DeviceDto**（v1.3）
```json
{ "seq": 1, "name": "高压反应釜", "sn": "SN2408017", "model": "HTR-500", "title": "高压反应釜 HTR-500" }
```
三项都可以为 `""`（至少一项非空）。`title`：名称与型号都有 → `{名称} {型号}`；只有其一 → 那一个；都没有 → 编号。

**TransferDto**（v1.3，转交）
```json
{
  "id": 5, "orderId": 3, "status": "Pending", "statusText": "待确认",
  "from": { UserBrief }, "to": { UserBrief },
  "reason": "电气部分我搞不定，需要更熟的同事", "createdAt": "2026-11-11T14:00:00",
  "decidedAt": null, "decidedBy": { UserBrief } | null,   // 结束它的人；系统失效时为 null
  "reply": null,                                           // 拒绝理由 / 失效原因
  "text": "李工 转交给 王工，待 王工 确认"                    // 可直接展示（各状态的文字见 CHANGES-v1.3 §2.2）
}
```
能否确认接手 / 拒绝 / 撤回看 `OrderDetail.actions`（`acceptTransfer` / `rejectTransfer` / `cancelTransfer`）。

**FactoryDto**（仅返厂单；`nodes` 固定 5 个、固定顺序）
```json
{ "nodes": [ {
    "node": "CustomerShipped", "label": "客户寄出",
    "state": "done" | "current" | "todo" | "skipped", "recorded": true,
    "time": "2026-11-02T10:00:00" | null, "operator": { UserBrief } | null,
    "courier": "顺丰" | null, "trackingNo": "SF1234567890" | null, "note": null,
    "photos": [ FileDto ],
    "canRecord": false,    // 当前用户现在可记录（未记录过）
    "canEdit": true        // 当前用户现在可更正（已记录过）
  } ] }
```
`Repairing` 为隐含节点（`recorded=false`，不可记录）：已记工厂收货未寄回 → `current`，已寄回 → `done`，否则 `todo`。

**PunchDto**
```json
{
  "id": 7, "orderId": 3, "engineer": { UserBrief },
  "kind": "Arrive", "time": "2026-11-06T10:05:00",
  "latitude": 31.30, "longitude": 120.72, "address": "江苏省苏州市工业园区星湖街...",
  "distanceMeters": 86, "outOfRange": false,
  "photo": { FileDto } | null,
  "isSupplement": false, "supplementBy": { UserBrief } | null, "supplementReason": null, "supplementAt": null,
  "pairMinutes": null           // 仅 Leave 有值：与配对到场的时长
}
```

**StatusLogDto**
```json
{ "id": 1, "text": "李工 认领 → 待启动", "time": "2026-11-05T09:48:00", "actor": { UserBrief }, "toStatus": "Accepted", "kind": "Status" }
```
`kind` 为 LogKind；`Price`、`AttachmentPrivate` 类记录不返回给 Engineer。转交相关记录为 `Assist`，SAT 信息修改为 `Info`。

---

## 1. 认证 `/api/auth`

### POST /api/auth/login
`{ "code": "wx.login 返回的 code" }`
→ 已绑定：`{ "token": "...", "user": { User }, "needBind": false }`
→ 未绑定：`{ "needBind": true, "bindTicket": "短期票据(10 分钟)" , "token": null, "user": null }`

### POST /api/auth/bind
`{ "bindTicket": "...", "phoneCode": "getPhoneNumber 回调里的 code" }`
后端用 phoneCode 调微信 `getuserphonenumber` 取手机号，匹配已登记且启用的员工，写入 openid。
→ `{ "token": "...", "user": { User }, "needBind": false }`
错误：40401 未找到账号；40900 该员工已绑定其他微信（「该账号已绑定其他微信，请联系主管解绑」）。

### POST /api/auth/dev-login（仅 `WeChat:MockMode=true` 时可用，否则 404）
`{ "phone": "13900000000" }` → 同 login 成功返回。用于开发者工具里无法获取真实手机号时调试。
MockMode 下 `login` 的 code 直接当作 openid 使用（`mock-` 前缀），`bind` 的 phoneCode 直接当作手机号。

---

## 2. 我的 `/api/me`

### GET /api/me
→ `{ "user": { User }, "monthMinutes": 1157, "activeOrderCount": 2, "monthTripDays": 4 }`
（`activeOrderCount`：Engineer=本人参与（负责人或协助人）的 Accepted+InProgress+OnHold 数量；Sales=本人建单且未闭环/作废数量；Supervisor=全部 Pending+Submitted 数量）

### PUT /api/me/avatar
`{ "fileId": 12 }`（先用 /api/files 上传，category=Avatar）→ `{ User }`

---

## 3. 文件 `/api/files`

### POST /api/files（multipart/form-data）
字段：`file`（必填）、`category`（FileCategory 字符串）、`fileName`（选填：原始文件名；客户端有真实文件名时必须传——小程序聊天文件、PC 文件选择）。
→ `{ FileDto }`（只上传，不挂工单）
（小程序用 `wx.uploadFile`，返回的 `res.data` 为 JSON 字符串，需要 `JSON.parse`）

| category | 允许的类型 | 大小上限 |
|---|---|---|
| `Attachment`（工单附件） | 图片、视频、Word（.doc/.docx）、Excel（.xls/.xlsx）、PPT（.ppt/.pptx）、PDF、.txt、.csv | 图片 20 MB；视频、文档 100 MB |
| 其他 | 图片、视频 | 20 MB |

类型按**扩展名 + 文件内容**双重校验（服务端不信任客户端声明的 Content-Type）：图片 jpg/png/gif/bmp/webp/heic、视频 mp4/m4v/mov/3gp 以文件头识别；PDF 须含 `%PDF-`；
Office 六种扩展名须为 OLE 复合文档或含 `[Content_Types].xml` 的 ZIP；txt/csv 不得是二进制。可执行文件、脚本、HTML、SVG、压缩包等一律拒绝。完整表见 CHANGES-v1.2 §3.2。
错误（40000）：「请选择要上传的文件」「文件类别不正确」「不支持的文件类型，仅支持图片、视频、Word、Excel、PPT、PDF、TXT、CSV」「文件内容与扩展名不符，请确认文件未损坏」
「只能上传图片或视频」（非 Attachment 类别）「文件不能超过 20MB」「图片不能超过 20MB」「文件不能超过 100MB」。

### GET /api/files/{id}/download（v1.2；任何已登录角色）
用 `Authorization` 头鉴权，返回文件原始内容：`Content-Type` 为文件的 mimeType，
`Content-Disposition: attachment; filename="…"; filename*=UTF-8''<百分号编码的 FileDto.name>`，支持 Range。
权限：文件须已挂工单且当前用户能查看该工单（否则 40300「无权查看该工单」）；受限附件对 Engineer 返回 40400「文件不存在」；已删除 / 头像 → 40400「文件不存在」；
未挂工单的文件只有上传人可下载；磁盘文件缺失 → 40400「文件已丢失，请联系管理员」。失败时返回 JSON 包装体。
PC 后台用 fetch → blob 下载；小程序用 `wx.downloadFile`（带 `header.Authorization`）后 `wx.openDocument({ filePath, fileType, showMenu: true })`。

静态访问：`GET /uploads/...`（无需认证，文件名为随机串；响应带 `X-Content-Type-Options: nosniff`）。

---

## 4. 工单 `/api/orders`

### GET /api/orders/hall?filter=all|urgent|onsite|remote
工单大厅：所有 `Pending` 工单，按紧急优先、创建时间倒序。
→ `{ "items": [OrderListItem], "counts": { "all": 3, "urgent": 1, "onsite": 2, "remote": 1 } }`
（`remote` 计数包括 Remote 和 Factory）

### GET /api/orders/mine?group=doing|check|closed|former|all
工程师本人**负责或协助**的工单（每项带 `myRole`）。doing = Accepted+InProgress+OnHold；check = Submitted；closed = Closed。按状态优先级（InProgress, Accepted, OnHold, Submitted, Closed）再按时间倒序。
v1.3：`former` = 本人**已退出**（被转交 / 被改派换掉）的工单，按退出时间倒序，每项 `myRole = "Former"`、带 `myExitText`；它们不计入 doing / check / closed，`all` 把它们排在最后。
→ `{ "items": [OrderListItem], "counts": { "doing": 2, "check": 0, "closed": 2, "former": 1, "all": 5 } }`

### GET /api/orders/created?group=all|wait|doing|check|closed
本人创建的工单（销售首页「我提交的工单」）。wait = Pending；doing = Accepted+InProgress+OnHold；check = Submitted；closed = Closed。不含 Voided（all 也不含）。
→ `{ "items": [OrderListItem], "counts": { "all": 5, "wait": 1, "doing": 2, "check": 1, "closed": 1 } }`

### GET /api/orders?status=&keyword=&issue=&assigneeId=&serviceType=&priceSet=&pendingRequest=&from=&to=&page=1&pageSize=20
全部工单（Sales、Supervisor 可用；Engineer 403）。`status` 为 OrderStatus 字符串或空=全部（全部不含 Voided，`status=Voided` 可单独查）；`keyword` 模糊匹配工单号 / 客户 / 项目编号 / 任意一台设备的名称、编号、型号 / 设备汇总文字 `deviceText`（v1.3；照着列表上显示的「名称 型号」整串也能搜到）；`issue` 为 IssueType；`from`/`to` 为创建日期 yyyy-MM-dd；`assigneeId` 按负责人过滤；
`priceSet=true|false` 按是否已定价过滤；`pendingRequest=true` 只返回有待审批申请的工单。
→ `{ "items": [OrderListItem], "total": 9, "page": 1, "pageSize": 20, "counts": { "all": 9, "Pending": 3, "Accepted": 1, "InProgress": 2, "OnHold": 0, "Submitted": 1, "Closed": 2 } }`
（counts 只受 keyword 影响，不受 status 影响）

### GET /api/orders/recent-customers
本人最近建过单的客户（去重，最多 5 个），用于新建工单快速填充。
→ `[ { "customerName": "", "contactName": "", "contactPhone": "", "address": "", "latitude": 0, "longitude": 0, "deviceModel": "", "deviceSn": "", "satDate": "2024-08", "satSigned": true, "projectNo": null,
       "devices": [ { "name": "", "sn": "", "model": "" } ] } ]`（`devices` 为 v1.3 新增：该客户最近一张工单的设备清单；`deviceModel` / `deviceSn` 为旧客户端保留）

### GET /api/orders/{id}
权限：Supervisor/Sales 任意；Engineer：Pending 工单，或本人是负责人 / 协助人 / 建单人的工单，
或（v1.3）本人是本单**已退出**的原负责人（只读），或本人是本单**待确认转交的接手人**（只读，可确认接手 / 拒绝）。否则 40300「无权查看该工单」。
→ **OrderDetail** = OrderListItem 全部字段 +
```json
{
  "source": "SalesReferral",
  "contactName": "张工", "contactPhone": "13800000000",
  "address": "苏州市工业园区星湖街 [示例地址] 3 号楼 2 层实验室",
  "latitude": 31.30, "longitude": 120.72,
  "devices": [ DeviceDto ],          // v1.3：设备清单（按 seq）；没有设备为 []
  "satSigned": true,                 // v1.3 含义：是否已完成 SAT
  "satDate": "2026-03-15" | "2024-08" | "",   // v1.3：SAT 完成时间（satSigned=true）/ 发货时间（false）；可为空；旧数据为 yyyy-MM
  "satText": "已完成" | "未完成",      // v1.3
  "satDateLabel": "SAT 完成时间" | "发货时间",   // v1.3
  "canEditSat": true,                // v1.3：当前用户现在可以修改 SAT 信息（建单人 / 负责人 / 主管，工单未作废）
  "productStage": "老产品",          // satSigned ? 老产品 : 新产品
  "transfers": [ TransferDto ],      // v1.3：本单全部转交记录，时间正序
  "readOnlyText": null | "你已退出本单（已转交），只能查看；你的打卡和工时记录已保留。",   // v1.3：仅当前用户是已退出的工程师
  "faultTime": "11月4日 下午",
  "customerTried": "断电重启、检查电源线",
  "siteEnvironment": "通风橱内，220V 单相电",
  "customerRequest": null,
  "priceSetAt": "2026-11-06T09:00:00" | null,      // Engineer 为 null
  "priceSuggestion": null | {                       // 销售最新一条价格建议；Engineer 永远为 null
    "id": 3, "amount": 1500.00, "note": "老客户，建议优惠" | null, "by": { UserBrief },
    "createdAt": "2026-11-06T09:30:00", "pending": true    // true = 主管尚未在此建议之后定价
  },
  "holdReason": null | "等备件：轴承预计周五到",      // = holdText（旧客户端兼容）
  "hold": null | {                                  // 仅 OnHold
    "reasonType": "WaitParts", "reasonText": "等备件", "note": "轴承预计周五到" | null, "text": "等备件：轴承预计周五到",
    "fromStatus": "InProgress", "at": "2026-11-06T15:00:00" | null, "by": { UserBrief } | null
  },
  "requests": [ RequestDto ],                       // 本单全部申请（含已处理），时间正序
  "participants": [ ParticipantDto ],               // 负责人 → 协助人 → 已退出；Pending 为 []
  "factory": null | { FactoryDto },                 // 仅返厂单
  "closeBlockedReason": null | "请先补录缺卡" | "请先设置服务价格（保内填 0）",   // 仅 Supervisor 且 Submitted；非 null ⇔ actions 无 close
  "closeHints": [ "客户签收尚未记录" ],               // 仅 Supervisor 且 Submitted；不阻止闭环
  "completion": null | {
    "faultCause": "", "solution": "", "partsReplaced": "", "runStatus": "Normal",
    "submittedAt": "2026-11-06T13:50:00"
  },
  "attachments": [ AttachmentDto ],   // v1.2：当前用户可见的附件（建单照片 + 附件），时间正序
  "canAddAttachment": true,           // v1.2：当前用户现在可以添加附件
  "canRestrictAttachment": false,     // v1.2：上传时可勾选「仅销售和主管可见」（Sales / Supervisor）
  "attachmentLimit": 50,              // v1.3：20 → 50
  "attachmentRemaining": 47,          // 还能再加几个
  "files": {
    "orderPhotos":   [FileDto],      // 旧客户端兼容：可见附件中的图片 / 视频；v1.2 客户端读 attachments
    "punchPhotos":   [FileDto],
    "processPhotos": [FileDto],
    "signatures":    [FileDto],
    "factoryPhotos": [FileDto]       // 返厂 3 类照片合并
  },
  "punches": [PunchDto],             // 时间正序（含所有参与人）
  "logs": [StatusLogDto],            // 时间正序
  "issueDetails": [ { "type": "MissingPunch", "text": "刘工 11月5日 15:10 到场后未打离场卡，当天未计工时。补录后系统重新计算。", "punchId": 9, "date": "2026-11-05", "engineer": { UserBrief } } ],
  "actions": ["claim", "appoint", "checkin", "start", "factory", "complete", "requestHold", "requestAssistant", "transfer", "cancelTransfer", "acceptTransfer", "rejectTransfer"]
}
```
（`projectNo`、`deviceText`、`pendingTransfer`、`myExitText` 在 OrderListItem 里，详情同样带有。）
`actions` 为**当前用户**在当前状态下可执行的动作（服务端计算，前端据此显示按钮，**不得自行推导规则**）。数组按下列固定顺序过滤：
- Engineer：`claim`（Pending）；`appoint`（负责人，Accepted/InProgress）；`checkin`（负责人或协助人，Onsite 且 Accepted/InProgress）；`start`（负责人，**Remote** 且 Accepted）；
  `factory`（返厂单且存在 `canRecord` 的节点）；`complete`（负责人，InProgress；返厂单还须已记录寄回客户；**v1.3：且本单没有待确认的转交**）；`requestHold`（负责人，Accepted/InProgress，且无待审批的暂停申请）；`requestAssistant`（负责人，Accepted/InProgress）；
  v1.3：`transfer`（负责人，Accepted/InProgress/OnHold，且没有待确认的转交）；`cancelTransfer`（负责人，有待确认的转交）；`acceptTransfer`、`rejectTransfer`（本人是待确认转交的接手人）。
  **已退出的工程师**只可能有 `acceptTransfer` / `rejectTransfer`（又被转交回来时），其余一律没有。
- Supervisor：`assign`（Pending）、`reassign`（Accepted/InProgress/OnHold）、`assistants`（Accepted/InProgress/OnHold）、`void`（Pending/Accepted）、`hold`（Accepted/InProgress）、`resume`（OnHold）、`reject`（Submitted）、
  `close`（Submitted 且无 MissingPunch 且已定价）、`supplement`（存在 MissingPunch）、`setPrice`（非 Voided）、`factory`、v1.3：`cancelTransfer`（有待确认的转交）
- Sales：`suggestPrice`（非 Closed/Voided）、`factory`
- 审批看 `requests[].canDecide / canCancel`；返厂节点更正看 `factory.nodes[].canEdit`。
- 附件不进 `actions`：看 `canAddAttachment`、`canRestrictAttachment`、`attachments[].canDelete / canToggleRestricted / canChangeType`。SAT 修改看 `canEditSat`。

### POST /api/orders
任何角色可建单。
```json
{
  "source": "SalesReferral", "problemType": "Fault", "urgency": "Normal", "serviceType": "Onsite",
  "customerName": "江苏某化工研究院", "contactName": "张工", "contactPhone": "13800000000",
  "address": "苏州市工业园区星湖街 [示例地址]", "latitude": 31.30, "longitude": 120.72,
  "projectNo": "HT-2024-031",
  "devices": [ { "name": "高压反应釜", "sn": "SN2408017", "model": "HTR-500" }, { "name": "搅拌器", "sn": "", "model": "HTS-80" } ],
  "satSigned": true, "satDate": "2026-03-15",
  "faultTime": "11月4日 下午",
  "symptom": "升温到 180℃ 左右就不再加热，面板报 E03",
  "customerTried": "断电重启、检查电源线",
  "siteEnvironment": "通风橱内，220V 单相电",
  "customerRequest": null,
  "attachments": [ { "fileId": 12, "restricted": false, "type": "Sat" }, { "fileId": 13, "restricted": true, "type": "Other" } ]
}
```
**设备资料（v1.3）**：`projectNo` 必填（40000「请填写项目编号」，≤ 50 字）；`devices` 选填、可为 `[]`、最多 20 台（「设备最多 20 台」），每台 `name` / `sn` / `model` 各 ≤ 100 字、三项全空的行被忽略；
`satSigned` 必填 = 是否已完成 SAT（「请选择是否已完成 SAT」）；`satDate` 选填，`yyyy-MM-dd`（也接受旧格式 `yyyy-MM`），含义随 `satSigned`：已完成 → SAT 完成时间，未完成 → 发货时间（格式错误：「SAT 完成时间格式应为 yyyy-MM-dd」/「发货时间格式应为 yyyy-MM-dd」）。
旧客户端不传 `devices` 而传 `deviceModel` / `deviceSn` 时，服务端合成一台设备（不再要求这两项必填）。建单后项目编号与设备清单不能修改；SAT 信息可用 `POST /api/orders/{id}/sat` 修改。
**没有价格字段**：新单一律「未定价」（`priceSet=false`），由主管在详情里定价。旧客户端若仍传 `price`，服务端忽略、不报错。
`attachments`（v1.2）：先用 `POST /api/files`（`category=Attachment`）上传；`restricted` 省略 = false，Engineer 传 true → 40300「只有销售和主管可以设置「仅销售和主管可见」」；
`type`（v1.3）为附件类别，省略 / null / 空白 = `Other`；只认枚举名 `Sat` / `RepairForm` / `Other`（大小写不敏感），数字等其他任何值 → 40000「附件类别不正确」；同一 `fileId` 重复出现时取第一次的 `restricted` 与 `type`。
旧字段 `photoFileIds`（v1.1 客户端，≤ 9，挂为建单照片，类别 `Other`）继续可用；两者合计 ≤ 50，否则 40000「附件最多 50 个」。建单时的附件不单独写状态记录。
校验：类型四项、客户四项、项目编号、是否已完成 SAT、发生时间、故障现象、客户已尝试、现场环境必填；Onsite 必须有经纬度；contactPhone 去空格后校验 7–20 位数字/横杠。
→ `{ OrderDetail }`

### POST /api/orders/{id}/sat（v1.3；建单人 / 当前负责人 / 主管）
`{ "satSigned": true, "satDate": "2026-03-15" }`（`satDate` 可为 `""` / null = 清空）。修改「是否已完成 SAT」与对应时间，整体覆盖；工单已作废不可改（40900「工单已作废，不能修改 SAT 信息」），已闭环可以改。
错误：40000「请选择是否已完成 SAT」/ 时间格式；40300「只有建单人、负责工程师或主管可以修改 SAT 信息」「你已退出本单，只能查看」。
状态记录（kind=Info）：`王敏 修改 SAT 信息：已完成 SAT，SAT 完成时间 2026-03-15`；值未变化不写记录。→ `{ OrderDetail }`

### POST /api/orders/{id}/claim（Engineer）
Pending → Accepted，assignee = 自己。已被认领 → 40900「工单已被他人认领」。→ `{ OrderDetail }`

### POST /api/orders/{id}/appointment（负责工程师）
`{ "appointmentTime": "2026-11-09T10:00:00" }` → `{ OrderDetail }`（写状态记录「李工 预约 11月9日 10:00」）

### POST /api/orders/{id}/start（负责工程师，仅远程单，Accepted → InProgress）→ `{ OrderDetail }`
现场单 → 40900「现场工单请到场打卡后自动开始」；返厂单 → 40900「返厂工单记录「工厂收货」后自动开始处理」；OnHold → 40900「工单已暂停搁置，恢复后才能开始处理」。

### POST /api/orders/{id}/complete（负责工程师）
```json
{ "faultCause": "", "solution": "", "partsReplaced": "", "runStatus": "Normal", "processPhotoIds": [21], "signaturePhotoIds": [22] }
```
校验见 DESIGN §5。`processPhotoIds` / `signaturePhotoIds` 为最终全集：本单已挂载但未包含在内的旧处理照片/签字件将被移除；已挂在本单的文件（含退回改派前他人上传的）可直接沿用，其他文件须为本人上传。→ `{ OrderDetail }`
只有负责人可提交。按顺序拦截：v1.3 本单有待确认的转交 → 40900「本单有待确认的转交，请先撤回转交后再提交完工」；OnHold → 40900「工单已暂停搁置，恢复后才能提交完工」；返厂单未记录寄回客户 → 40900「返厂工单请先记录「寄回客户」再提交完工」；
负责人本人今天在本单未离场 → 40900「请先离场打卡」；其他任何人今天在本单未离场 → 40900「{姓名}还在现场计时，请其先离场打卡后再提交完工」。

### POST /api/orders/{id}/assign（Supervisor）
`{ "engineerId": 4, "assistantIds": [5, 6] }`。Pending → Accepted；Accepted/InProgress/OnHold 时为改派（状态不变）；原负责工程师今天在本单未离场 → 40900「原负责工程师正在现场计时，请先离场打卡后再改派」。
`assistantIds` 省略或 null = 不调整协助人；传数组（含 `[]`）= 协助人最终全集（规则同下面的 `/assistants`）。改派给当前协助人 → 该人成为负责人并从协助人中移除。
v1.3：改派时本单待确认的转交自动失效（状态记录 `转交已失效（李工 → 王工）：主管已改派`）；被换掉的原负责人成为「已退出」（`exitText = 已改派`，只读可见），除非这次改派同时把他设为协助人。→ `{ OrderDetail }`

### POST /api/orders/{id}/assistants（Supervisor）
`{ "engineerIds": [5, 6] }` 协助人最终全集（`[]` 清空）。工单须为 Accepted/InProgress/OnHold，否则 40900「工单当前为「…」，不能调整协助人」。
校验：含负责人 → 40000「协助人不能是负责人」；不存在/停用/非工程师 → 40000「所选协助人不存在或已停用」；超过 5 人 → 40000「协助人最多 5 人」；被移除的人今天在本单未离场 → 40900「{姓名}正在现场计时，请先离场打卡后再移除」。
状态记录（kind=Assist）：`赵主管 添加协助人 王工、刘工` / `赵主管 移除协助人 王工`。通知新增的协助人。→ `{ OrderDetail }`

### POST /api/orders/{id}/assistant-requests（负责工程师）
`{ "engineerId": 5, "note": "需要两人抬设备" }`。工单须为 Accepted/InProgress。同一目标只能有一条待审批申请。通知主管，进入主管待办。→ `{ OrderDetail }`

### POST /api/orders/{id}/void（Supervisor）`{ "reason": "" }` → `{ OrderDetail }`

### POST /api/orders/{id}/hold（Supervisor，Accepted / InProgress → OnHold）
`{ "reasonType": "WaitParts", "note": "轴承预计周五到" }`（`Other` 时 note 必填，note ≤ 200 字）。兼容旧请求体 `{ "reason": "等待备件" }` → 按 `Other` + note 处理。
错误：40000「请选择暂停原因」/「请填写暂停说明」；负责人今天在本单未离场 → 40900「工程师正在现场计时，请先离场打卡后再搁置」；协助人未离场 → 40900「协助人{姓名}正在现场计时，请先离场打卡后再搁置」。
记录暂停前状态；状态记录 `赵主管 标为暂停搁置 → 暂停搁置（等备件：轴承预计周五到）`。→ `{ OrderDetail }`

### POST /api/orders/{id}/hold-requests（负责工程师）
请求体同 hold。工单须为 Accepted/InProgress；每单同时只能有一条待审批的暂停申请（40900「已有待审批的暂停申请」）。状态不变，通知主管并进入主管待办。→ `{ OrderDetail }`

### POST /api/orders/{id}/requests/{requestId}/approve（Supervisor）
同意申请（暂停申请 → 工单进入 OnHold，检查与直接暂停相同；协助人申请 → 添加协助人）。已处理 → 40900「该申请已处理」；不存在 → 40400「申请不存在」。→ `{ OrderDetail }`
### POST /api/orders/{id}/requests/{requestId}/reject（Supervisor）`{ "reply": "选填，≤200 字" }` 驳回，工单状态不变，通知申请人。→ `{ OrderDetail }`
### POST /api/orders/{id}/requests/{requestId}/cancel（申请人本人）撤回待审批的申请。→ `{ OrderDetail }`
工单状态 / 负责人变化导致申请失去意义时，系统自动把申请置为 `Cancelled`（规则见 CHANGES-v1.1 §3.2）。

### POST /api/orders/{id}/resume（Supervisor）OnHold → 暂停前的状态（Accepted 或 InProgress；旧数据回 InProgress）→ `{ OrderDetail }`
### POST /api/orders/{id}/reject（Supervisor）`{ "reason": "" }`（Submitted → InProgress，退回处理中）→ `{ OrderDetail }`
### POST /api/orders/{id}/close（Supervisor）Submitted → Closed；有 MissingPunch（任何参与人）→ 40900「请先补录缺卡」；未定价 → 40900「请先设置服务价格（保内填 0）」。返厂单未记录客户签收不拦截（`closeHints` 提示）。→ `{ OrderDetail }`

### POST /api/orders/{id}/price（Supervisor）
`{ "price": 1800 }`（0 = 保内免费）。除 Voided 外任何状态都可定价 / 改价。校验：必填、≥ 0、≤ 100000000，保留 2 位小数。
状态记录（kind=Price，工程师不可见）：`赵主管 定价 ¥1,800` / `赵主管 改价 ¥1,800 → ¥2,000`。→ `{ OrderDetail }`

### POST /api/orders/{id}/price-suggestions（Sales）
`{ "amount": 1500, "note": "老客户，建议优惠" }`（note 选填 ≤ 200 字）。工单不能是 Closed/Voided。销售不能直接定价。
详情的 `priceSuggestion` 返回最新一条；通知主管（通知正文不含金额）并进入主管待办。→ `{ OrderDetail }`

### POST /api/orders/{id}/factory-nodes（返厂流程；按节点判权）
```json
{ "node": "ShippedBack", "courier": "顺丰", "trackingNo": "SF1234567890", "note": "含电源线 1 根", "photoIds": [41, 42] }
```
| # | node | 谁可以记录 | 必填 | 允许的工单状态 | 前置 |
|---|---|---|---|---|---|
| 1 | `CustomerShipped` 客户寄出 | 建单人、负责人、主管 | courier、trackingNo | Pending/Accepted/InProgress/OnHold | 工厂收货尚未记录 |
| 2 | `FactoryReceived` 工厂收货 | 负责人、主管 | photoIds ≥ 1 | Accepted/InProgress | 已记客户寄出（主管可跳过） |
| 3 | `Repairing` 检测/维修 | 隐含，不可提交 | | | |
| 4 | `ShippedBack` 寄回客户 | 负责人、主管 | courier、trackingNo、photoIds ≥ 1 | InProgress | 已记工厂收货 |
| 5 | `CustomerSigned` 客户签收 | 建单人、负责人、主管 | —（photoIds、note 选填） | InProgress/OnHold/Submitted | 已记寄回客户 |

- 记录工厂收货时 Accepted → InProgress（取代返厂单的「开始处理」）。完工要求节点 4；闭环不要求节点 5。返厂单不打卡。
- 已记录的节点再次提交 = 更正（同样的人可更正，工单未闭环/作废即可；不改变状态）。
- 照片先用 `POST /api/files` 上传，category：节点 2 `FactoryReceivePhoto`、节点 4 `FactoryShipPhoto`、节点 5 `FactorySignPhoto`；`photoIds` 为该节点照片的最终全集，每节点最多 9 张。
- 每个节点写状态记录（kind=Factory），如 `王敏 记录客户寄出（顺丰 SF1234567890）`、`陈工 工厂收货 → 处理中`、`陈工 寄回客户（顺丰 SF…）`、`王敏 记录客户签收`。
- 全部错误文案见 CHANGES-v1.1 §3.4。→ `{ OrderDetail }`

### 转交（v1.3；规则与全部文案见 CHANGES-v1.3 §5、§6）

负责工程师把工单转给另一位工程师，对方确认接手后成为负责人，**不需要主管批准**；主管会收到通知，并可撤销待确认的转交。
- 可转交的状态：Accepted / InProgress / OnHold。同一张工单同一时间只能有一个待确认的转交（`pendingTransfer`）。
- 确认前：工单仍由原负责人负责，除「提交完工」「再次发起转交」外一切照常；接手人对工单只读。
- 确认后：接手人成为负责人（若原是协助人则从协助人中移除）；原负责人成为「已退出」——在参与人里显示 `Former` + `exitText = 已转交`，仍可查看工单（只读），在 `GET /api/orders/mine?group=former` 里能找到；他的状态记录、打卡、工时原样保留。
- 「已退出」优先于其他身份：已退出的人即使是建单人也不能再添加附件、修改 SAT、记录返厂节点（40300「你已退出本单，只能查看」）；重新成为负责人 / 协助人后恢复。
- 待确认期间：主管改派 / 作废、接手人被停用或改角色 → 转交自动失效（`Expired`，写状态记录）；主管暂停 / 恢复不影响。
- 已退出的原负责人后来被加为协助人 / 被改派或转交回来 → 退出记录删除、恢复对应身份；被加为协助人后又被移除 → 与其他被移除的协助人一样不再能查看。
- 接手人在确认前的可见范围与协助人相同（不含价格、受限附件），可下载非受限文件；转交被拒绝 / 撤回 / 失效后立即恢复为「无权查看该工单」（他是协助人 / 建单人 / 已退出的人时除外）。
- 同一张工单的转交四个接口与改派、调整协助人、作废、预约、开始处理、提交完工、打卡在服务端串行执行：重复点击或双方同时操作，后到的请求按前一个完成后的状态处理（如 40900「该转交已处理或已失效」）。

#### POST /api/orders/{id}/transfers（负责工程师）
`{ "engineerId": 5, "reason": "电气部分我搞不定，需要更熟的同事" }`（原因必填，≤ 200 字）。
错误：40000「请选择接手工程师」「请填写转交原因」「不能转交给自己」「所选工程师不存在或已停用」；40300「只有负责工程师可以转交工单」；
40900「工单当前为「…」，不能转交」「已有待确认的转交，请先撤回」「你正在现场计时，请先离场打卡后再转交」。
状态记录（kind=Assist）：`李工 发起转交给 王工：{原因}`。通知接手人与主管。→ `{ OrderDetail }`

#### POST /api/orders/{id}/transfers/{transferId}/accept（接手人）
确认接手。错误：40400「转交不存在」；40300「只有接手人可以确认接手」；40900「该转交已处理或已失效」「{原负责人}正在现场计时，请其先离场打卡后再确认接手」。
状态记录：`王工 确认接手（原负责人 李工）`。通知原负责人与主管。→ `{ OrderDetail }`

#### POST /api/orders/{id}/transfers/{transferId}/reject（接手人）
`{ "reply": "选填，≤200 字" }`。状态记录：`王工 拒绝接手：{理由}`。通知发起人与主管。
错误：40000「拒绝理由不能超过 200 个字」；40400「转交不存在」；40300「只有接手人可以拒绝」；40900「该转交已处理或已失效」。
→ `{ OrderDetail }`；**拒绝后当前用户已不能查看该工单时 `code = 0`、`data` 为 `null`**（HTTP 200；客户端应离开详情页）。这是唯一可能返回 null 的工单动作。

#### POST /api/orders/{id}/transfers/{transferId}/cancel（发起人本人或 Supervisor）
撤回 / 撤销待确认的转交。错误：40300「只有发起人或主管可以撤回转交」；40900「该转交已处理或已失效」。
状态记录：发起人 `李工 撤回转交（王工）`；主管 `赵主管 撤销转交（李工 → 王工）`。→ `{ OrderDetail }`

### 附件（v1.2；规则与全部文案见 CHANGES-v1.2 §4；v1.3 的分类与放开见 CHANGES-v1.3 §4）

| 规则 | 条件 |
|---|---|
| 可添加 | 工单不是 Voided，且：Supervisor；或当前用户是建单人 / 负责人 / 协助人且不是「已退出」。**v1.3：已闭环的工单同样可以添加** |
| 上传时可设「仅销售和主管可见」 | Sales、Supervisor |
| 可删除（软删除） | Supervisor（任何状态）；或上传人本人、工单不是 Closed / Voided、且不是「已退出」（闭环后只能新增不能删除）。v1.3：上传人是工程师时还须仍是本单的建单人 / 负责人 / 协助人（待确认转交的接手人在确认前只读） |
| 可切换「仅销售和主管可见」 | Supervisor |
| 可改类别（v1.3） | Supervisor（任何状态）；或上传人本人、工单不是 Voided、且不是「已退出」；上传人是工程师时还须仍是本单的建单人 / 负责人 / 协助人 |
| 可见 | Sales / Supervisor 全部；Engineer 只有未受限的 |

附件分三类（`attachmentType`：`Sat` / `RepairForm` / `Other`），上传时选类别、查看时按类别分组；**只分类，不做任何强制检查**（缺哪一类都不拦完工 / 闭环）。

#### POST /api/orders/{id}/attachments
`{ "attachments": [ { "fileId": 31, "restricted": false, "type": "Sat" } ] }`（文件须为本人上传、尚未挂到任何工单；`type` 省略 = `Other`）。每单最多 50 个（40000「附件最多 50 个，本单还可添加 N 个」/「附件最多 50 个，本单已达上限」）。
错误：40900「工单已作废，不能添加附件」；40300「你已退出本单，只能查看」「只有建单人、负责工程师、协助人或主管可以添加附件」「只有销售和主管可以设置「仅销售和主管可见」」；
40000「请选择要添加的附件」「附件类别不正确」「附件不存在，请重新上传」「附件不是本人上传，请重新上传」「附件已被使用，请重新上传」。
状态记录（kind=Attachment；受限的为 AttachmentPrivate 并在末尾加「（仅销售和主管可见）」）：`王敏 上传附件 现场铭牌.jpg、故障视频.mp4`、超过 3 个 `… 等 5 个`。→ `{ OrderDetail }`

#### POST /api/orders/{id}/attachments/{fileId}/delete
软删除。错误：40400「附件不存在」（含对当前用户不可见的受限附件）；40300「你已退出本单，只能查看」「只能删除自己上传的附件」「只有建单人、负责工程师、协助人或主管可以删除附件」（上传人是待确认转交的接手人、且不是本单其他身份）；40900「工单已闭环，只有主管可以删除附件」「工单已作废，不能删除附件」。
状态记录 `王敏 删除附件 报价单.pdf`。→ `{ OrderDetail }`

#### POST /api/orders/{id}/attachments/{fileId}/restricted（Supervisor）
`{ "restricted": true }`。状态记录 `赵主管 将附件 报价单.pdf 设为仅销售和主管可见`（AttachmentPrivate）/ `赵主管 将附件 报价单.pdf 设为所有人可见`（Attachment）；值未变化不写记录。→ `{ OrderDetail }`

#### POST /api/orders/{id}/attachments/{fileId}/type（v1.3；上传人本人或 Supervisor）
`{ "type": "RepairForm" }` 修改附件类别（传错类时不用删了重传）。错误：40000「请选择附件类别」；40400「附件不存在」；40300「你已退出本单，只能查看」「只能修改自己上传的附件」「只有建单人、负责工程师、协助人或主管可以修改附件类别」（同上）；40900「工单已作废，不能修改附件类别」。
状态记录 `王敏 将附件 验收单.pdf 归为「SAT 验收单」`（受限附件为 AttachmentPrivate）；值未变化不写记录。→ `{ OrderDetail }`

---

## 5. 打卡 `/api/punches`（Engineer）

### GET /api/punches/context?orderId=3
负责人和协助人都可以打卡，所有规则按工程师本人计算（各打各的卡、各算各的工时）。带 `orderId` 时本人须是该单负责人或协助人，否则 40300「只有本单的负责人或协助人可以打卡」。
`orderId` 可省略（底部「打卡」按钮进入），在本人参与（负责或协助）的现场单中选：优先取今天有未离场到场的工单；否则取 InProgress 的现场单；否则取 Accepted 的现场单中预约时间最早的（无预约排最后，再按认领时间；与首页 next 规则一致）；都没有 → `data: null`。
工单为 Accepted/InProgress 时可打卡；OnHold/Voided 时仅允许为本单今天未离场的到场卡补打离场卡，其余 → blockedReason / POST 40900：OnHold「工单已暂停搁置，恢复后才能打卡」，其他状态「工单当前为「…」，不能打卡」。
→
```json
{
  "orderId": 3, "orderNo": "SH-20261105-003", "customerName": "江苏某化工研究院",
  "address": "...", "latitude": 31.30, "longitude": 120.72,
  "nextKind": "Leave",
  "openArriveTime": "2026-11-06T10:05:00" | null,   // nextKind=Leave 时为配对的到场时间
  "blockedReason": null | "请先在〈南京某检测中心〉离场打卡",  // 不为 null 时前端禁用拍照按钮并展示
  "rangeMeters": 1000,
  "serverTime": "2026-11-06T13:42:10",
  "engineerName": "李工",
  "myRole": "Lead" | "Assistant"
}
```

### POST /api/punches
```json
{ "orderId": 3, "kind": "Leave", "latitude": 31.301, "longitude": 120.721, "address": "江苏省苏州市...", "photoFileId": 30 }
```
`kind` 为前端看到的类型；服务端重新判断，不一致 → 40900「打卡状态已变化，请刷新」。任何参与人的首次到场使 Accepted → InProgress（写状态记录 `{姓名} 首次到场 → 处理中`）。
→ `{ "punch": { PunchDto }, "order": { OrderListItem } }`

---

## 6. 工时 `/api/hours`（Engineer，Supervisor 可带 `engineerId` 查看他人）

### GET /api/hours/today
→
```json
{
  "date": "2026-11-06",
  "closedMinutes": 0,                 // 今日已配对完成的分钟
  "running": true,
  "runningSince": "2026-11-06T10:05:00" | null,
  "serverTime": "2026-11-06T13:42:18",  // 前端用它与本地时间求偏差后每秒刷新
  "firstArriveTime": "2026-11-06T10:05:00" | null,
  "tripDay": 2,                       // 出差第 N 天（今天无到场为 0）
  "entries": [                        // 今日每一对，正在计时的 leave 为 null
    { "orderId": 3, "orderNo": "SH-20261105-003", "customerName": "江苏某化工研究院",
      "arrive": { PunchDto }, "leave": { PunchDto } | null, "minutes": 0 | 145, "isSupplement": false,
      "role": "Lead" | "Assistant" | "Former" }   // 此工程师当前在该工单的角色
  ]
}
```
工时按打卡人归属：协助人的工时计入协助人本人。

### GET /api/hours/month?year=2026&month=11
→
```json
{
  "year": 2026, "month": 11,
  "totalMinutes": 1157, "tripDays": 4,
  "days": [   // 只返回有打卡的天，日期倒序
    { "date": "2026-11-06", "minutes": 217, "running": true, "hasSupplement": false,
      "entries": [ { "orderId": 3, "orderNo": "", "customerName": "江苏某化工研究院",
                     "arriveTime": "2026-11-06T10:05:00", "leaveTime": null, "minutes": 0,
                     "isSupplement": false, "supplementReason": null, "missing": false, "role": "Lead" } ] }
  ]
}
```
（`missing=true` 表示缺卡未补录；`minutes` 为当天已配对合计，running 部分前端实时加）

---

## 7. 首页聚合 `/api/home`

### GET /api/home/engineer
```json
{
  "user": { User },
  "today": { 同 /api/hours/today 的 data },
  "state": "working" | "next" | "idle",
  "current": { OrderListItem } | null,     // working：InProgress 中最近有到场的那张（优先 onSiteSince 不为空）
  "next": { OrderDetail 中的 OrderListItem 字段 + "address","contactName","contactPhone","latitude","longitude" } | null,  // next：Accepted 且预约最早（无预约排最后，再按认领时间）
  "upcoming": [ { OrderListItem } ],      // 除 current/next 外本人 Accepted 的单，按预约时间
  "hall": { "count": 3, "urgentCount": 1, "items": [ OrderListItem 最多 2 条 ] },
  "recentDone": [ { OrderListItem } ],    // 本人 Submitted/Closed 最近 2 条
  "onHold": [ { OrderListItem } ],        // 本人参与的暂停搁置工单（按暂停时间倒序）
  "transfersToMe": [ { OrderListItem } ], // v1.3：待我确认的转交（每项带 pendingTransfer），按发起时间正序
  "monthMinutes": 1157
}
```
「本人」的工单 = 本人是负责人**或协助人**的工单（每项带 `myRole`；协助人不能提交完工 / 开始处理）。所有工单项的 `price`、`priceSet` 为 null。
v1.3：本人已退出的工单、待我确认的转交都**不算**「本人的工单」（不进 current / next / upcoming / onHold / recentDone，也不计入 `activeOrderCount`）；后者单独放在 `transfersToMe`。
state 规则：有 InProgress 单 → working；否则有 Accepted 单 → next；否则 idle。

### GET /api/home/supervisor
```json
{
  "user": { User },
  "now": "2026-11-06T13:42:00",
  "onSiteCount": 2,
  "timeline": [ { "engineer": { UserBrief }, "segments": [ { "start": "2026-11-06T10:05:00", "end": null, "customerName": "江苏某化工研究院", "orderId": 3 } ], "note": "今日无打卡" | "已指派无锡某制药企业 · 待启动" | "" } ],
  "todos": [
    { "type": "Unassigned", "orderId": 2, "title": "无锡某制药企业", "urgent": true, "subtitle": "待认领 · 已发布 3 小时，暂无人认领" },
    { "type": "Submitted", "orderId": 7, "title": "合肥某研究所", "subtitle": "刘工 11月5日提交完工，资料已上传" },
    { "type": "MissingPunch", "orderId": 7, "title": "刘工 · 缺离场卡", "subtitle": "11月5日 合肥某研究所，当天未计工时" },
    { "type": "DistanceAbnormal", "orderId": 5, "title": "陈工 · 打卡距离异常", "subtitle": "南京某检测中心 13:00 到场，距客户 2.4 km" },
    { "type": "HoldRequest", "orderId": 3, "requestId": 12, "title": "江苏某化工研究院", "subtitle": "李工 申请暂停 · 等备件：轴承预计周五到" },
    { "type": "AssistantRequest", "orderId": 3, "requestId": 13, "title": "江苏某化工研究院", "subtitle": "李工 申请添加协助人 王工" },
    { "type": "PriceSuggestion", "orderId": 2, "requestId": null, "title": "无锡某制药企业", "urgent": true, "subtitle": "王敏 建议价格 ¥1,500" }
  ]
}
```
todos 的固定顺序：HoldRequest → AssistantRequest → Unassigned → Submitted → PriceSuggestion → MissingPunch → DistanceAbnormal。每项都有 `requestId`（非申请类为 null）。
Unassigned 只列：紧急的 Pending，或发布超过 2 小时的 Pending。DistanceAbnormal 只列最近 3 天的。HoldRequest / AssistantRequest 列全部待审批申请。
PriceSuggestion 列「最新价格建议尚未被主管处理」且未闭环未作废的工单。Submitted 类在工单未定价时副标题末尾追加「，尚未定价」。缺卡、距离异常覆盖所有参与人。

### GET /api/engineers（Supervisor、Engineer）
指派面板 / 选择协助人用。
→ `[ { "user": { UserBrief }, "doingCount": 1, "acceptedCount": 1, "submittedCount": 0, "assistCount": 1, "onSiteNow": true, "loadText": "处理中 1 单、待启动 1 单、协助 1 单 · 苏州" } ]`
（doing/accepted/submitted 只数作为负责人的工单；`assistCount` = 作为协助人参与的 Accepted/InProgress/OnHold 工单数。）
loadText 规则：无任何进行中 → 「今日空闲」；否则拼接「处理中 N 单」「待启动 N 单」「待验收 N 单」「协助 N 单」，用「、」连接；若今天有到场打卡，追加「 · {最近到场工单城市}」，否则若无进行中则追加「 · 今日未出差」。

---

## 8. 主管后台 `/api/admin/*`（Supervisor）

### GET /api/admin/overview
→ `{ "pending": 3, "urgentPending": 1, "inProgress": 2, "onSiteEngineers": 2, "submitted": 1, "closedThisWeek": 6, "weekMinutes": 5388, "onHold": 1, "pendingRequests": 2, "unpriced": 3, "priceSuggestions": 1, "updatedAt": "..." }`
（`pendingRequests` 待审批申请数；`unpriced` 未定价且未作废的工单数；`priceSuggestions` 有未处理价格建议的工单数）

### POST /api/admin/punches/supplement
`{ "orderId": 7, "arrivePunchId": 9, "kind": "Leave", "time": "2026-11-05T18:20:00", "reason": "手机电量耗尽，工程师电话确认约 18:20 离场" }`
校验：reason 必填；Leave 时间必须晚于对应到场且同一天；→ `{ OrderDetail }`（写状态记录「赵主管 补录离场 18:20」；被补录人不是当前负责人时为「赵主管 补录离场 18:20（王工）」）。协助人的缺卡同样用此接口补录。

### GET /api/admin/export/orders?from=&to=&status=
返回 xlsx（`Content-Disposition: attachment`）。列与 SOP 进度台账一致：项目编号、工单号、客户全称、设备型号、设备编号、产品阶段、故障描述、故障发生时间、报修人、报修电话、维修类型、处理人(负责人)、处理时间(首次到场)、解决措施、进度状态、是否收费、服务价格、现场工时(小时,1 位小数，所有人合计)、验收结果(运行状况)、客户签字(有/无)、建单人、建单时间，
v1.1 在最后追加：协助人、人员工时（`李工 6.0h；王工 3.5h`）、暂停原因、返厂进度、寄回单号。未定价的工单「是否收费」「服务价格」两列都写 `未定价`。
v1.3：多台设备时「设备型号」「设备编号」两列各用 `；` 连接（一一对应，缺编号用 `—` 占位）；在最后再追加 3 列：设备台数、SAT 完成时间、发货时间。
### GET /api/admin/export/hours?year=&month=
xlsx：工程师、日期、工单号、客户、到场、离场、工时(小时,1 位小数)、是否补录、补录原因、本单角色（负责人 / 协助人 / 已退出）。每人每对打卡一行，协助人的工时在协助人名下。

### 账号管理
- GET /api/admin/employees → `[ { "id","name","phone","role","roleName","department","enabled","bound": true,"avatarUrl" } ]`
- POST /api/admin/employees `{ "name","phone","role","department" }`
- PUT /api/admin/employees/{id} `{ "name","phone","role","department","enabled" }`
- POST /api/admin/employees/{id}/unbind → 清空 openid，并使该员工旧 token 失效
- 停用、修改角色或手机号同样会使该员工旧 token 失效（下次请求返回 40100）
- v1.3：停用一名工程师或把他改为其他角色时，以他为接手人的待确认转交自动失效（工单仍由原负责人负责）

---

## 9. PC 主管后台补充（网页端 `/admin`）

PC 后台为纯静态网页，由后端托管在 `GET /admin/`（`wwwroot/admin/`），与 API 同源，无需 CORS。只有 `Supervisor` 角色能登录 PC 后台。

### 账号密码
员工表新增 `PasswordHash`（PBKDF2，ASP.NET Core `PasswordHasher<T>`），可为空（未设置密码不能在 PC 登录）。
种子数据中赵主管的初始密码由配置 `Seed:SupervisorPassword` 提供（Development 默认 `Huotong@2026`）。

#### POST /api/auth/password-login
`{ "phone": "13900000001", "password": "..." }`
→ `{ "token": "...", "user": { User }, "needBind": false }`
错误：40000「手机号或密码错误」（账号不存在/密码错误/未设置密码一律同一文案）；40300「仅售后主管可登录电脑后台」；已停用 → 40300「账号已停用」。
同一手机号连续 5 次失败锁定 10 分钟（内存计数即可）→ 40000「尝试次数过多，请 10 分钟后再试」。

#### PUT /api/me/password
`{ "oldPassword": "...", "newPassword": "..." }`（未设置过密码时 oldPassword 可为空）。新密码 ≥ 8 位且同时含字母和数字，否则 40000。→ `{ User }`

#### POST /api/admin/employees/{id}/password（Supervisor）
`{ "password": "..." }` 为该员工设置/重置密码（同样的强度规则）。→ `EmployeeDto`
`GET /api/admin/employees` 的每项新增 `"hasPassword": true`。

### GET /api/admin/punches?from=&to=&engineerId=&abnormal=&page=1&pageSize=50
打卡记录（Supervisor）。`from`/`to` 为日期 yyyy-MM-dd（含两端，默认最近 7 天）；`abnormal` 可选 `distance`（outOfRange）| `supplement`（补录）| `missing`（缺卡：返回没有配对离场且早于今天的到场卡）。时间倒序。
→
```json
{ "items": [ { PunchDto 全部字段, "orderNo": "SH-...", "customerName": "...", "missing": false } ],
  "total": 30, "page": 1, "pageSize": 50,
  "counts": { "all": 30, "distance": 1, "supplement": 2, "missing": 1 } }
```
（counts 受 from/to/engineerId 影响，不受 abnormal 影响）

### GET /api/admin/hours?year=2026&month=11
工时统计（Supervisor），每个 Engineer 一行（含无打卡的工程师，按 totalMinutes 倒序）。
→
```json
{ "year": 2026, "month": 11,
  "totalMinutes": 5388,
  "engineers": [ {
    "engineer": { UserBrief }, "totalMinutes": 940, "tripDays": 4, "orderCount": 2,
    "supplementCount": 1, "missingCount": 0, "runningNow": true,
    "days": [ { "date": "2026-11-06", "minutes": 217 } ]   // 有工时或有打卡的天，日期正序
  } ] }
```
单个工程师的每日明细用已有的 `GET /api/hours/month?year=&month=&engineerId=`。
`running` 的时长不计入 totalMinutes（与导出一致）。

### GET /api/admin/files?keyword=&type=&source=&attachmentType=&uploaderId=&orderId=&restricted=&from=&to=&page=1&pageSize=30（v1.2，Supervisor）
工单文件中心：所有挂在工单上的文件（附件、建单照片、完工照片、签字件、打卡照片、返厂照片；不含头像、未挂工单的文件、已删除的附件；含已作废工单的文件）。上传时间倒序。
`keyword` 模糊匹配文件名 / 工单号 / 客户 / 项目编号（v1.3）；`type` 为 FileKind；`source` 为文件来源；`from`/`to` 为上传日期 yyyy-MM-dd（含两端，默认不限）；`restricted=true|false`；
`attachmentType`（v1.3）= `Sat` | `RepairForm` | `Other`，给了它就只返回该类别的附件（非法 → 40000「附件类别不正确」）；与 `source` 同时给时取交集（如 `source=PunchPhoto` → 空结果），`counts` 同样受它影响。
→
```json
{ "items": [ { AttachmentDto 全部字段（source 可为 6 种来源之一；非附件行 restricted / canDelete / canToggleRestricted 均为 false）,
               "orderId": 3, "orderNo": "SH-20261105-003", "customerName": "江苏某化工研究院", "orderStatus": "InProgress" } ],
  "total": 128, "totalSize": 734003200, "page": 1, "pageSize": 30,
  "counts": { "all": 128, "Image": 100, "Video": 8, "Document": 20 } }
```
`total` / `totalSize` 为符合筛选的全部文件数与字节数；`counts` 受除 `type` 外的筛选影响。错误：40000「文件类型不正确」「文件来源不正确」「可见范围筛选不正确」「开始日期不能晚于结束日期」。
删除 / 切换受限 / 改类别（v1.3）直接用工单附件接口（行里有 `orderId`）；下载用 `GET /api/files/{id}/download`。Engineer、Sales 本期无权访问（403）。

### 已有接口在后台的用法（无需新增）
- 工单列表：`GET /api/orders`（status/keyword/issue/assigneeId/priceSet/pendingRequest/from/to/page/pageSize），`issue=MissingPunch|DistanceAbnormal`；「本周」= from 本周一 to 今天；快捷筛选「待审批申请」= `pendingRequest=true`，「未定价」= `priceSet=false`。
- 概况：`GET /api/admin/overview`；详情抽屉：`GET /api/orders/{id}`；操作：assign（含协助人）/assistants/void/hold/resume/reject/close/price、申请审批 `requests/{requestId}/approve|reject`、返厂节点 `factory-nodes`、`POST /api/admin/punches/supplement`。
- 新建工单：`POST /api/orders`（PC 端没有地图选点，手工填写地址与经纬度，现场单经纬度必填；没有价格字段；附件用普通文件选择，`attachments` 字段；v1.3：项目编号必填、设备可多台、是否已完成 SAT 必填、时间选填，附件按三类分别上传）。
- 附件：抽屉里的「附件」区（v1.3 按类别分三组）用 `attachments` 接口；上传 `POST /api/files`（`category=Attachment`，带 `fileName`）；下载 `GET /api/files/{id}/download`；改类别 `attachments/{fileId}/type`。
- v1.3：抽屉里的「设备资料」可修改 SAT 信息（`POST /api/orders/{id}/sat`）；转交只读展示（`pendingTransfer`、`transfers`），主管可撤销待确认的转交（`transfers/{transferId}/cancel`）；主管换人仍用 `assign`（改派）。
- 工程师列表：`GET /api/engineers`；账号：`/api/admin/employees*`；导出：`/api/admin/export/orders|hours`（需带 Authorization，前端用 fetch → blob 下载）。
