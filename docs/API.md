# 接口契约（前后端唯一依据）

Base URL：`{host}/api`。JSON 使用 **camelCase**；枚举一律以**字符串**传输（如 `"Pending"`）。

## 0. 通用约定

### 响应包装
所有接口（除文件下载/导出）返回：
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
| FileCategory | `OrderPhoto` 建单照片, `PunchPhoto` 打卡照片, `ProcessPhoto` 处理照片, `Signature` 客户签字件, `Avatar` 头像 |

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
{ "id": 12, "url": "https://host/uploads/2026/11/xxx.jpg", "category": "PunchPhoto", "mimeType": "image/jpeg", "createdAt": "2026-11-06T10:05:00" }
```
`url` 为绝对地址（后端用 `PublicBaseUrl` 配置或请求 Host 拼接）。

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
  "deviceModel": "高压反应釜 HTR-500",
  "deviceSn": "SN2408017",
  "symptom": "升温到 180℃ 左右就不再加热，面板报 E03",
  "price": 1800.00,             // 0 = 保内
  "assignee": { UserBrief } | null,
  "creator": { UserBrief },     // 建单人（销售或工程师）
  "createdAt": "2026-11-05T09:20:00",
  "claimedAt": "2026-11-05T09:48:00" | null,
  "appointmentTime": "2026-11-09T10:00:00" | null,
  "submittedAt": null,
  "closedAt": null,
  "totalMinutes": 362,          // 累计已配对现场工时（不含正在计时）
  "onSiteSince": "2026-11-06T10:05:00" | null,   // 今天有未离场到场时为到场时间
  "lastArriveTime": "2026-11-06T10:05:00" | null,
  "issues": ["DistanceAbnormal"]
}
```
前端自行把 `price` 格式化为 `¥1,800` / `保内免费`，把状态等枚举映射成中文与配色。

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
{ "id": 1, "text": "李工 认领 → 待启动", "time": "2026-11-05T09:48:00", "actor": { UserBrief }, "toStatus": "Accepted" }
```

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
（`activeOrderCount`：Engineer=本人 Accepted+InProgress+OnHold 数量；Sales=本人建单且未闭环/作废数量；Supervisor=全部 Pending+Submitted 数量）

### PUT /api/me/avatar
`{ "fileId": 12 }`（先用 /api/files 上传，category=Avatar）→ `{ User }`

---

## 3. 文件 `/api/files`

### POST /api/files（multipart/form-data）
字段：`file`（必填，图片或视频，≤ 20MB），`category`（FileCategory 字符串）
→ `{ FileDto }`
（小程序用 `wx.uploadFile`，返回的 `res.data` 为 JSON 字符串，需要 `JSON.parse`）

静态访问：`GET /uploads/...`（无需认证）。

---

## 4. 工单 `/api/orders`

### GET /api/orders/hall?filter=all|urgent|onsite|remote
工单大厅：所有 `Pending` 工单，按紧急优先、创建时间倒序。
→ `{ "items": [OrderListItem], "counts": { "all": 3, "urgent": 1, "onsite": 2, "remote": 1 } }`
（`remote` 计数包括 Remote 和 Factory）

### GET /api/orders/mine?group=doing|check|closed|all
工程师本人负责的工单。doing = Accepted+InProgress+OnHold；check = Submitted；closed = Closed。按状态优先级（InProgress, Accepted, OnHold, Submitted, Closed）再按时间倒序。
→ `{ "items": [OrderListItem], "counts": { "doing": 2, "check": 0, "closed": 2, "all": 4 } }`

### GET /api/orders/created?group=all|wait|doing|check|closed
本人创建的工单（销售首页「我提交的工单」）。wait = Pending；doing = Accepted+InProgress+OnHold；check = Submitted；closed = Closed。不含 Voided（all 也不含）。
→ `{ "items": [OrderListItem], "counts": { "all": 5, "wait": 1, "doing": 2, "check": 1, "closed": 1 } }`

### GET /api/orders?status=&keyword=&issue=&assigneeId=&serviceType=&from=&to=&page=1&pageSize=20
全部工单（Sales、Supervisor 可用；Engineer 403）。`status` 为 OrderStatus 字符串或空=全部（全部不含 Voided，`status=Voided` 可单独查）；`keyword` 模糊匹配工单号/客户/设备型号/设备编号；`issue` 为 IssueType；`from`/`to` 为创建日期 yyyy-MM-dd。
→ `{ "items": [OrderListItem], "total": 9, "page": 1, "pageSize": 20, "counts": { "all": 9, "Pending": 3, "Accepted": 1, "InProgress": 2, "OnHold": 0, "Submitted": 1, "Closed": 2 } }`
（counts 只受 keyword 影响，不受 status 影响）

### GET /api/orders/recent-customers
本人最近建过单的客户（去重，最多 5 个），用于新建工单快速填充。
→ `[ { "customerName": "", "contactName": "", "contactPhone": "", "address": "", "latitude": 0, "longitude": 0, "deviceModel": "", "deviceSn": "", "satDate": "2024-08", "satSigned": true, "projectNo": null } ]`

### GET /api/orders/{id}
权限：Supervisor/Sales 任意；Engineer：Pending 工单或本人负责的工单。
→ **OrderDetail** = OrderListItem 全部字段 +
```json
{
  "source": "SalesReferral",
  "contactName": "张工", "contactPhone": "13800000000",
  "address": "苏州市工业园区星湖街 [示例地址] 3 号楼 2 层实验室",
  "latitude": 31.30, "longitude": 120.72,
  "satDate": "2024-08", "satSigned": true,
  "productStage": "老产品",          // satSigned ? 老产品 : 新产品
  "projectNo": null,
  "faultTime": "11月4日 下午",
  "customerTried": "断电重启、检查电源线",
  "siteEnvironment": "通风橱内，220V 单相电",
  "customerRequest": null,
  "holdReason": null,
  "completion": null | {
    "faultCause": "", "solution": "", "partsReplaced": "", "runStatus": "Normal",
    "submittedAt": "2026-11-06T13:50:00"
  },
  "files": {
    "orderPhotos":   [FileDto],
    "punchPhotos":   [FileDto],
    "processPhotos": [FileDto],
    "signatures":    [FileDto]
  },
  "punches": [PunchDto],             // 时间正序
  "logs": [StatusLogDto],            // 时间正序
  "issueDetails": [ { "type": "MissingPunch", "text": "11月5日 15:10 到场后未打离场卡，当天未计工时。补录后系统重新计算。", "punchId": 9, "date": "2026-11-05" } ],
  "actions": ["claim", "appoint", "checkin", "start", "complete", "assign", "reassign", "void", "hold", "resume", "reject", "close", "supplement"]
}
```
`actions` 为**当前用户**在当前状态下可执行的动作（服务端计算，前端据此显示按钮）：
- Engineer：`claim`（Pending）；本人负责时：`appoint`（Accepted/InProgress）、`checkin`（Onsite 且 Accepted/InProgress）、`start`（非 Onsite 且 Accepted）、`complete`（InProgress）
- Supervisor：`assign`（Pending）、`reassign`（Accepted/InProgress/OnHold）、`void`（Pending/Accepted）、`hold`（InProgress）、`resume`（OnHold）、`reject`（Submitted）、`close`（Submitted 且无 MissingPunch）、`supplement`（存在 MissingPunch）
- Sales：`[]`

### POST /api/orders
任何角色可建单。
```json
{
  "source": "SalesReferral", "problemType": "Fault", "urgency": "Normal", "serviceType": "Onsite",
  "customerName": "江苏某化工研究院", "contactName": "张工", "contactPhone": "13800000000",
  "address": "苏州市工业园区星湖街 [示例地址]", "latitude": 31.30, "longitude": 120.72,
  "deviceModel": "高压反应釜 HTR-500", "deviceSn": "SN2408017",
  "satDate": "2024-08", "satSigned": true, "projectNo": null,
  "faultTime": "11月4日 下午",
  "symptom": "升温到 180℃ 左右就不再加热，面板报 E03",
  "customerTried": "断电重启、检查电源线",
  "siteEnvironment": "通风橱内，220V 单相电",
  "customerRequest": null,
  "price": 1800,
  "photoFileIds": [12, 13]
}
```
校验：带 * 字段必填；Onsite 必须有经纬度；price ≥ 0；photoFileIds ≤ 9；contactPhone 去空格后校验 7–20 位数字/横杠。
→ `{ OrderDetail }`

### POST /api/orders/{id}/claim（Engineer）
Pending → Accepted，assignee = 自己。已被认领 → 40900「工单已被他人认领」。→ `{ OrderDetail }`

### POST /api/orders/{id}/appointment（负责工程师）
`{ "appointmentTime": "2026-11-09T10:00:00" }` → `{ OrderDetail }`（写状态记录「李工 预约 11月9日 10:00」）

### POST /api/orders/{id}/start（负责工程师，非 Onsite，Accepted → InProgress）→ `{ OrderDetail }`

### POST /api/orders/{id}/complete（负责工程师）
```json
{ "faultCause": "", "solution": "", "partsReplaced": "", "runStatus": "Normal", "processPhotoIds": [21], "signaturePhotoIds": [22] }
```
校验见 DESIGN §5。`processPhotoIds` / `signaturePhotoIds` 为最终全集：本单已挂载但未包含在内的旧处理照片/签字件将被移除；已挂在本单的文件（含退回改派前他人上传的）可直接沿用，其他文件须为本人上传。→ `{ OrderDetail }`

### POST /api/orders/{id}/assign（Supervisor）
`{ "engineerId": 4 }`。Pending → Accepted；Accepted/InProgress/OnHold 时为改派（状态不变）；原负责工程师今天在本单未离场 → 40900「原负责工程师正在现场计时，请先离场打卡后再改派」。→ `{ OrderDetail }`

### POST /api/orders/{id}/void（Supervisor）`{ "reason": "" }` → `{ OrderDetail }`
### POST /api/orders/{id}/hold（Supervisor）`{ "reason": "等待备件" }`；负责工程师今天在本单未离场 → 40900「工程师正在现场计时，请先离场打卡后再搁置」→ `{ OrderDetail }`
### POST /api/orders/{id}/resume（Supervisor）→ `{ OrderDetail }`
### POST /api/orders/{id}/reject（Supervisor）`{ "reason": "" }`（Submitted → InProgress，退回处理中）→ `{ OrderDetail }`
### POST /api/orders/{id}/close（Supervisor）Submitted → Closed；有 MissingPunch → 40900「请先补录缺卡」→ `{ OrderDetail }`

---

## 5. 打卡 `/api/punches`（Engineer）

### GET /api/punches/context?orderId=3
`orderId` 可省略（底部「打卡」按钮进入）：优先取今天有未离场到场的工单；否则取本人 InProgress 的现场单；否则取 Accepted 的现场单中预约时间最早的（无预约排最后，再按认领时间；与首页 next 规则一致）；都没有 → `data: null`。
工单为 Accepted/InProgress 时可打卡；OnHold/Voided 时仅允许为本单今天未离场的到场卡补打离场卡，其余 → blockedReason「工单当前为「…」，不能打卡」/ POST 返回 40900。
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
  "engineerName": "李工"
}
```

### POST /api/punches
```json
{ "orderId": 3, "kind": "Leave", "latitude": 31.301, "longitude": 120.721, "address": "江苏省苏州市...", "photoFileId": 30 }
```
`kind` 为前端看到的类型；服务端重新判断，不一致 → 40900「打卡状态已变化，请刷新」。首次到场使 Accepted → InProgress（写状态记录）。
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
      "arrive": { PunchDto }, "leave": { PunchDto } | null, "minutes": 0 | 145, "isSupplement": false }
  ]
}
```

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
                     "isSupplement": false, "supplementReason": null, "missing": false } ] }
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
  "monthMinutes": 1157
}
```
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
    { "type": "DistanceAbnormal", "orderId": 5, "title": "陈工 · 打卡距离异常", "subtitle": "南京某检测中心 13:00 到场，距客户 2.4 km" }
  ]
}
```
Unassigned 只列：紧急的 Pending，或发布超过 2 小时的 Pending。DistanceAbnormal 只列最近 3 天的。

### GET /api/engineers（Supervisor）
指派面板用。
→ `[ { "user": { UserBrief }, "doingCount": 1, "acceptedCount": 1, "submittedCount": 0, "onSiteNow": true, "loadText": "处理中 1 单、待启动 1 单 · 苏州" } ]`
loadText 规则：无任何进行中 → 「今日空闲」；否则拼接「处理中 N 单」「待启动 N 单」「待验收 N 单」，用「、」连接；若今天有到场打卡，追加「 · {最近到场工单城市}」，否则若无进行中则追加「 · 今日未出差」。

---

## 8. 主管后台 `/api/admin/*`（Supervisor）

### GET /api/admin/overview
→ `{ "pending": 3, "urgentPending": 1, "inProgress": 2, "onSiteEngineers": 2, "submitted": 1, "closedThisWeek": 6, "weekMinutes": 5388, "updatedAt": "..." }`

### POST /api/admin/punches/supplement
`{ "orderId": 7, "arrivePunchId": 9, "kind": "Leave", "time": "2026-11-05T18:20:00", "reason": "手机电量耗尽，工程师电话确认约 18:20 离场" }`
校验：reason 必填；Leave 时间必须晚于对应到场且同一天；→ `{ OrderDetail }`（写状态记录「赵主管 补录离场 18:20」）

### GET /api/admin/export/orders?from=&to=&status=
返回 xlsx（`Content-Disposition: attachment`）。列与 SOP 进度台账一致：项目编号、工单号、客户全称、设备型号、设备编号、产品阶段、故障描述、故障发生时间、报修人、报修电话、维修类型、处理人、处理时间(首次到场)、解决措施、进度状态、是否收费、服务价格、现场工时(小时,1 位小数)、验收结果(运行状况)、客户签字(有/无)、建单人、建单时间。
### GET /api/admin/export/hours?year=&month=
xlsx：工程师、日期、工单号、客户、到场、离场、工时(小时,1 位小数)、是否补录、补录原因。

### 账号管理
- GET /api/admin/employees → `[ { "id","name","phone","role","roleName","department","enabled","bound": true,"avatarUrl" } ]`
- POST /api/admin/employees `{ "name","phone","role","department" }`
- PUT /api/admin/employees/{id} `{ "name","phone","role","department","enabled" }`
- POST /api/admin/employees/{id}/unbind → 清空 openid，并使该员工旧 token 失效
- 停用、修改角色或手机号同样会使该员工旧 token 失效（下次请求返回 40100）
