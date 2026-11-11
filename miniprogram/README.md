# 霍桐售后工单 · 微信小程序

原生 WXML / WXSS / JS（CommonJS），基础库 ≥ 3.0，**无 npm、无 TS、无构建步骤**。
接口契约：`../docs/API.md`；业务与视觉：`../docs/DESIGN.md`；原型：`../docs/prototype*/`。

## 导入与运行

1. 后端已部署在 `https://shouhou.sh-htlab.com`（`config.js` 的 `baseUrl`）；本地联调时可临时改成本机地址。
2. 微信开发者工具 → 导入项目 → 目录选 **`miniprogram/`**（`project.config.json` 就在这里）。
3. AppID 默认 `touristappid`（游客模式）。要真机预览、获取手机号，请在 `project.config.json` 里换成公司小程序的 AppID。
4. 右上角「详情 → 本地设置」勾选 **「不校验合法域名、web-view（业务域名）、TLS 版本以及 HTTPS 证书」**（`urlCheck` 已设为 false）。

## 目录结构

```
miniprogram/
├── app.js / app.json / app.wxss     入口、页面与全局组件注册、设计令牌与通用类
├── config.js                        baseUrl、version
├── project.config.json, sitemap.json
├── api/index.js                     每个接口一个函数（与 API.md 一一对应）
├── utils/
│   ├── request.js                   wx.request / uploadFile / downloadFile 封装
│   ├── attachments.js               工单附件（v1.2）：选择、预检、排队上传、类型角标、预览 / 打开
│   ├── auth.js                      登录态：ensureLogin / setSession / logout …
│   ├── format.js                    时间、时长、价格、距离、手机号格式化；服务器时间校准
│   ├── enums.js                     枚举中文 + 配色 + picker options
│   ├── watermark.js                 打卡照片水印（离屏 canvas）
│   ├── location.js                  定位、地图选点、Haversine 距离
│   ├── icons.js                     线性 SVG 图标（data URI）
│   ├── system.js                    状态栏 / 胶囊尺寸
│   └── tabbar.js                    自定义 tabBar 选中 / 隐藏
├── custom-tab-bar/                  按角色切换的底部导航
├── components/                      全局组件（app.json 已注册，页面直接用）
│   ├── nav-bar  avatar  status-tag  urgency-tag  empty  assign-sheet  ui-icon
│   └── progress-bar                 5 段进度条（v1.1：暂停显示灰色暂停标记 + 原因）
└── pages/
    ├── home        首页（tab，按角色：工程师 00/01/02，销售 11，主管 14）
    ├── orders      工单（tab，工程师=工单大厅 05，销售/主管=全部工单 12）
    ├── hours       我的工时（tab，04）
    ├── me          我的（tab，09）
    ├── login       登录绑定（03）✅ 已实现
    ├── privacy     用户隐私保护指引 ✅ 已实现
    ├── my-orders   我的工单（10）
    ├── order-detail 工单详情（工程师 06 / 销售主管只读 13，按 actions 显示按钮）
    ├── checkin     拍照打卡（07）
    ├── complete    完工提交（08）
    ├── factory-node 返厂节点记录 / 更正（v1.1，?id=&node=）
    └── order-new   新建工单（15）
```

## 页面约定（给页面开发看）

- **登录**：每个页面在 `onShow`（或 `onLoad`）里
  ```js
  const auth = require('../../utils/auth.js');
  auth.ensureLogin().then((user) => this.load()).catch(() => {}); // 未登录会自动跳登录页
  ```
- **tab 页**（home/orders/hours/me）：`onShow` 里调用 `tabbar.setTabSelected(this, 'home')`，WXML 根节点加 `class="page tabbar-space"` 给 tabBar 留位置。
- **自定义导航**：页面 json 写 `"navigationStyle": "custom"`，WXML 顶部放 `<nav-bar title="工单详情" />`。tab 页通常 `back="{{false}}"` 或自己画大标题（用 `utils/system.js` 的 `getNavMetrics()` 对齐胶囊）。
- **跳转路径**：详情 `/pages/order-detail/index?id=3`；打卡 `/pages/checkin/index?orderId=3`（不带参数 = 底部打卡按钮进入）；完工 `/pages/complete/index?id=3`；返厂节点 `/pages/factory-node/index?id=3&node=ShippedBack`；新建 `/pages/order-new/index`。
- 页面 json 的 `usingComponents` 写 `{}` 即可，全局组件已在 app.json 注册。
- 尺寸：原型 1px = 2rpx。颜色用 `var(--xxx)` 令牌，不要硬编码新颜色。
- 所有面向用户的文案用简体中文。

## 公共模块 API 速查

### api/index.js
```js
const api = require('../../api/index.js');
// 每个函数最后一个参数 opts 可选：{ silent: true 不弹错误 toast, loading: true|'文案' }
api.auth.login(code) / api.auth.bind(bindTicket, phoneCode)
api.me.get() → {user, monthMinutes, activeOrderCount, monthTripDays}
api.me.setAvatar(fileId) → User
api.files.upload(filePath, category, {fileName?, timeout?, onProgress?}) → FileDto   // 附件一律 category='Attachment'
api.files.downloadPath(id) → '/api/files/{id}/download'                            // 配合 request.download（带 Authorization）
api.orders.hall(filter)            // 'all'|'urgent'|'onsite'|'remote' → {items, counts}
api.orders.mine(group)             // 'doing'|'check'|'closed'|'all'
api.orders.created(group)          // 'all'|'wait'|'doing'|'check'|'closed'
api.orders.list({status, keyword, issue, assigneeId, serviceType, from, to, page, pageSize})
api.orders.recentCustomers()
api.orders.detail(id) → OrderDetail（含 actions）
api.orders.create(body) → OrderDetail
api.orders.claim(id) / appoint(id, 'yyyy-MM-ddTHH:mm:ss') / start(id) / complete(id, body)
api.orders.assign(id, engineerId, assistantIds?) / void(id, reason) / hold(id, reasonType, note) / resume(id) / reject(id, reason) / close(id)
// v1.1（详见 docs/CHANGES-v1.1.md）
api.orders.setPrice(id, price) / suggestPrice(id, amount, note)               // 主管定价 / 销售价格建议；建单不再带 price
api.orders.requestHold(id, reasonType, note)                                  // 负责人申请暂停
api.orders.approveRequest(id, requestId) / rejectRequest(id, requestId, reply) / cancelRequest(id, requestId)
api.orders.setAssistants(id, engineerIds) / requestAssistant(id, engineerId, note)
api.orders.factoryNode(id, {node, courier, trackingNo, note, photoIds})       // 返厂节点记录 / 更正
// v1.2（详见 docs/CHANGES-v1.2.md）
api.orders.addAttachments(id, [{fileId, restricted}]) / deleteAttachment(id, fileId) / setAttachmentRestricted(id, fileId, restricted)
api.punches.context(orderId?) → 上下文或 null
api.punches.create({orderId, kind, latitude, longitude, address, photoFileId}) → {punch, order}
api.hours.today(engineerId?) / api.hours.month(year, month, engineerId?)
api.home.engineer() / api.home.supervisor()
api.engineers.list()
api.admin.overview() / supplement(body) / exportOrders(params) / exportHours(year, month)
api.admin.employees() / createEmployee(body) / updateEmployee(id, body) / unbindEmployee(id)
```
出错 reject `{ code, message, httpStatus, data }`（如 `40900`），默认已 toast；需要自定义提示时传 `{ silent: true }` 自己处理。网络失败 `code = -1`。

### utils/request.js
`request({url, method, data, silent, loading})`、`get(url, data, opts)`、`post`、`put`、`uploadFile(filePath, category, opts) → FileDto`（opts：`fileName`、`timeout` 默认 60000、`onProgress(percent)`、`onTask(task)`；HTTP 413 且响应不是 JSON → `文件不能超过 100MB`）、`download(url, query, opts) → 本地路径`（opts：`filePath`、`timeout`、`onProgress(percent)`、`silent`；非 200：404「文件不存在或已删除」、403「无权查看该文件」、其他「下载失败（状态码）」）。401/40100 自动清 token 并 reLaunch 登录页（防抖）。

### utils/auth.js
`ensureLogin() → Promise<User>`、`getUser()`、`getRole()`、`setSession(token, user)`、`setUser(user)`、`refreshUser() → /api/me 的 data`、`logout()`。

### utils/format.js
| 函数 | 示例 |
|---|---|
| `parseTime(str)` | `'2026-11-06T10:05:00'` → Date（iOS 安全） |
| `syncServerTime(serverTime)` / `now()` | 用接口 `serverTime` 校准，`now()` 返回校准后的 Date（计时用它） |
| `formatDuration(min)` | `217` → `'3:37'`，null → `'—'` |
| `splitClock(seconds)` | `13038` → `{hm:'3:37', ss:':18'}` |
| `secondsBetween(start, end?)` | end 缺省为 `now()` |
| `formatPrice(price, short, priceSet)` | `1800` → `'¥1,800'`，`0` → `'保内免费'`（short：`'保内'`）；`priceSet === false` 或 price 为 null → `'未定价'`。工程师端不展示价格 |
| `formatMonthDay(d)` / `weekday(d)` / `formatHm(d)` | `'11月6日'` / `'周五'` / `'10:05'` |
| `formatDate(d)` / `formatDateDot(d)` / `today()` | `'2026-11-06'` / `'2026.11.06'` |
| `formatDateTimeShort(str)` | `'今天 10:00'` / `'11月9日 10:00'` |
| `timeAgo(str)` | `'刚刚'` / `'10 分钟前'` / `'3 小时前'` / `'昨天'` / `'11月4日'` |
| `formatDistance(m)` | `86` → `'86 m'`，`2400` → `'2.4 km'` |
| `maskPhone` / `formatPhone` | `'139****0000'` / `'138 0000 0000'` |
| `toApiTime(date)` | Date → `'2026-11-09T10:00:00'` |
| `isToday(d)` | |

### utils/enums.js
`STATUS[status] → {label, bg, fg, step, group}`、`URGENCY`、`SERVICE_TYPE`（`needPunch`）、`SOURCE`、`PROBLEM_TYPE`、`RUN_STATUS`、`ROLE`、`ISSUE`、`PUNCH_KIND`、`FILE_CATEGORY`、`ACTION`（actions 按钮文案）、`TODO_TYPE`、`COLORS`；
`*_OPTIONS`（`[{value,label}]`）；`label(map, value)`；`progressSegments(status) → [{on}] ×5`。

### utils/watermark.js
`composeWatermark(tempFilePath, ['到场 10:05', '2026.11.06 周五', 地址, 'N 31.30° · E 120.72°', '李工 · SH-…'], {maxSide:1600, quality:0.8}) → Promise<tempFilePath>`，第一行大字；合成失败 reject `{code:'WATERMARK_FAILED', message:'照片处理失败，请重拍'}`，打卡页提示重拍，不上传原图（DESIGN §4.2）。

### utils/location.js
`getLocation() → {latitude, longitude, accuracy}`（gcj02，拒绝授权时引导 openSetting）、`chooseLocation(init?) → {name, address, latitude, longitude}`（取消 reject `{code:'CANCEL'}`）、`distanceMeters(lat1,lng1,lat2,lng2)`、`formatCoords(lat, lng)` → `'N 31.30° · E 120.72°'`。不做逆地址解析。

### utils/tabbar.js
`setTabSelected(page, 'home'|'orders'|'hours'|'me')`、`setTabBarHidden(page, true|false)`（自己写的底部弹层要隐藏 tabBar 时用）、`refreshAll()`。

### utils/icons.js / `<ui-icon>`
`icons.get(name, color, strokeWidth) → data URI`；名字：home orders camera clock user plus plus-square back right down check close phone location search image edit warning info flash switch shield wechat lock more。
WXML：`<ui-icon name="camera" color="#FFFFFF" size="24" stroke="2" />`（size 为设计稿 px）。

### 组件
| 组件 | 属性 | 事件 |
|---|---|---|
| `<nav-bar>` | `title`、`back`(默认 true)、`background`(默认 #F6F6F4)、`color`、`fixed`、`placeholder`、`customBack`；slot：`left` / `right` / 默认(标题) | `back` |
| `<avatar>` | `user`(UserBrief)、`size`(px，默认 40)、`src`、`text`、`bg`、`color` | |
| `<status-tag>` | `status`、`size`('md'/'sm')、`text` | |
| `<urgency-tag>` | `urgency`、`showNormal`、`size`('sm'/'md') | |
| `<empty>` | `text`、`desc`、`card`；默认 slot 放按钮 | |
| `<assign-sheet>` | `show`、`order`(OrderListItem：id/customerName/urgency/deviceText/assignee) | `done`(detail=OrderDetail)、`close` |

`assign-sheet` 用法：
```xml
<assign-sheet show="{{sheetShow}}" order="{{sheetOrder}}" bind:done="onAssigned" bind:close="onSheetClose" />
```
```js
onSheetClose() { this.setData({ sheetShow: false }); },
onAssigned(e) { const detail = e.detail; this.reload(); }
```

### 通用样式类（app.wxss）
`.page` `.tabbar-space` `.safe-bottom` `.footer-bar`/`.footer-space` `.card` `.card-lg` `.page-title` `.page-subtitle` `.section-title`(.link) `.title`
`.row` `.row-between` `.col` `.flex1` `.ellipsis` `.ellipsis2` `.mt8/12/16/24`
`.muted` `.muted-3` `.text-body` `.small` `.tiny` `.bold` `.red` `.green` `.amber` `.num` `.num-bold`
`.btn-primary` `.btn-secondary` `.btn-danger` `.btn-disabled` `.btn-text` `.btn-sm` `hover-class="btn-hover"`
`.tag` `.tag-sm` `.tag-red` `.tag-amber` `.tag-green` `.divider` `.list-item`
`.chips` + `.chip`(.active，内含 `.chip-count`) `.segmented` + `.segmented-item`(.active)
`.form-item` `.form-label`(.required) `.form-input` `.input-box` `.placeholder`
`.progress` + `.progress-seg`(.on) `.mask` `.sheet` `.sheet-handle` `.dot` `.notice` `.alert`

CSS 变量：`--bg --card --text --text-2 --text-3 --text-body --divider --border --soft --track --segment-bg --red --red-soft --green --amber --gray --disabled --radius --radius-lg --radius-btn --shadow --tabbar-height --font-num`。

## 隐私
`app.json` 开启 `__usePrivacyCheck__`。登录页的验证按钮为 `open-type="getPhoneNumber|agreePrivacyAuthorization"`，点击即同意；其他页面调用隐私接口而用户未同意时，`app.js` 的 `wx.onNeedPrivacyAuthorization` 会打开 `pages/privacy/index?mode=authorize`，用户点「同意并继续」后继续原操作。小程序后台「用户隐私保护指引」需声明：手机号、位置信息、摄像头 / 相册、微信头像、选中的文件（v1.2 新增，见下文「上线前的手工步骤」）。

## v1.2 工单附件（详见 docs/CHANGES-v1.2.md §6）

- **建单页**：「现场照片 / 视频」改为「附件（照片 / 视频 / 文件）」，最多 50 个（v1.3 由 20 调整为 50）。点「+」选「拍照或从相册选择」（`wx.chooseMedia`，`sizeType: ['original']` 原图不压缩）或「从微信聊天选择文件」（`wx.chooseMessageFile`，仅 Word / Excel / PPT / PDF / TXT / CSV）。小程序里文档只能从微信聊天中选：先把文件发到「文件传输助手」再选。销售 / 主管长按格子可设为「仅销售和主管可见」。提交用 `attachments: [{fileId, restricted}]`，不再传 `photoFileIds`。
- **工单详情**：tab「资料」改名「附件资料」，顶部是「附件」卡片（类型角标、名称、大小 · 上传人 · 时间、受限角标），其下「流程资料」保留打卡照片 / 处理照片 / 签字件 / 返厂照片（建单照片已并入附件）。添加、删除、切换可见范围只看服务端返回的 `canAddAttachment`、`canRestrictAttachment`、`attachments[].canDelete / canToggleRestricted`、`attachmentRemaining`，**页面不自行推导权限**。工程师从任何接口都拿不到受限附件。
- **打开附件**：图片 / 视频用原文件地址 `wx.previewMedia`；文档先经 `GET /api/files/{id}/download`（`Authorization` 头）下载到 `USER_DATA_PATH/att/{id}/{name}`（每次下载前清空 `att/`），再 `wx.openDocument({ fileType, showMenu: true })`，右上角菜单可转发 / 保存；TXT / CSV 微信不支持预览，改为 `wx.shareFileMessage` 转发到聊天。
- **utils/attachments.js**：`MAX_COUNT=50`（v1.3 由 20 调整为 50）、`MAX_IMAGE=20MB`、`MAX_FILE=100MB`、`DOC_EXTS`、`OPEN_DOC_EXTS`；`pickMedia(left)` / `pickChatFiles(left)` / `pick(left)`（先弹选择方式）→ 统一条目；`upload(item, onChange(item, patch))` 逐个排队上传（同一时间只传 1 个，`timeout` 10 分钟，带进度），`cancel(key)`；`fmtSize(bytes)`、`badge(att) → {text, color}`；`open(att, list)`（服务端附件）、`openLocal(item, list)`（未提交的本地条目）、`resetCache()`。预检文案：`不支持的文件类型` / `图片不能超过 20MB` / `文件不能超过 100MB`，服务端校验是最终依据。
- **大小与超时**：图片 ≤ 20 MB，视频与文档 ≤ 100 MB；附件上传与文档下载超时 10 分钟。完工照片、打卡照片、返厂照片、头像不变（压缩图、20 MB）。

### 上线前的手工步骤（小程序后台）
1. 「设置 → 服务内容声明 → 用户隐私保护指引」新增一项 **选中的文件**（用途：`用于将聊天中的文件作为工单附件上传`）。不声明的话 `wx.chooseMessageFile` 在正式版会被拒绝。
2. request / uploadFile / downloadFile 合法域名已有 `https://shouhou.sh-htlab.com`，无需改动。
3. 小程序需重新提审发布。后端与 PC 后台先上线不影响 v1.1 小程序；v1.2 小程序依赖 v1.2 后端（`attachments` 等字段），**必须在后端升级之后发布**。服务器 nginx 的 `client_max_body_size` 需已改为 `110m`（见 deploy/README.md），否则超过 30 MB 的上传会被拦截并提示「文件不能超过 100MB」。

## v1.3 设备资料（多台设备）+ 附件分类与随时可传 + 转交（详见 docs/CHANGES-v1.3.md §9）

依赖 v1.3 后端（`devices`、`deviceText`、`attachmentType`、`pendingTransfer`、`transfers` 等字段与下列新接口），**必须在后端升级之后发布**；本期不新增页面、不新增 tab，也不需要新的隐私声明项。工程师端任何位置仍不展示价格。

- **设备资料（建单页）**：分组改为「设备资料」，顺序固定为 `*项目编号 → 设备（可多台，选填）→ *是否已完成 SAT → 时间（选填）`。「+ 添加设备」逐台添加（最多 20 台，整行不填的忽略；只剩 1 台时「删除」= 清空）。时间栏标签随 SAT 变化（已完成 `SAT 完成时间` / 未完成 `发货时间`），在两者之间切换时清空已选时间（第一次选择不清空）。提交 `projectNo`、`devices:[{name,sn,model}]`、`satSigned`、`satDate('yyyy-MM-dd'|'')`，不再传 `deviceModel` / `deviceSn`。
- **设备资料（详情页）**：「信息」tab 新增「设备资料」卡片（项目编号、逐台设备、SAT 信息）；`canEditSat` 为 true 时显示「修改 SAT」→ 弹层保存（`orders.setSat`）。列表、标题、选人弹层副标题一律显示服务端的 `deviceText`，**前端不自己拼设备汇总文字**。
- **附件分类**：三类（`enums.ATTACHMENT_TYPE`：`Sat` SAT 验收单（签字版）/ `RepairForm` 现场维修登记表/返厂维修登记表（签字版）/ `Other` 设备照片、沟通记录及其他附件），只分类、不做任何强制检查。建单页按三个小节上传（数据仍是一个 `media` 数组，渲染用 `mediaGroups`）；详情页按三组展示（空组显示「暂无」），每组各有「添加」，弹层里可切换类别（整批归入所选类别）；「…」菜单里 `canChangeType` 时可「归为「…」」（`orders.setAttachmentType`）。类别在 WXML 里一律用 `data-att-type`（`data-type` 已被「拍照 / 聊天文件」两个按钮占用）。
- **随时可传**：是否显示「添加」只看 `canAddAttachment`（已闭环也可以，前端不按状态隐藏）；每单上限 50（`attachments.MAX_COUNT`）。
- **转交**：负责工程师底部「转交」（多时在「更多」里）→ `<assign-sheet mode="transfer">` 选同事 + 原因（必填）→ `orders.transfer`。详情页顶部「转交待确认」卡片按 `actions` 显示按钮：接手人「确认接手 / 拒绝」（`acceptTransfer` / `rejectTransfer`），发起人「撤回转交」、主管「撤销转交」（`cancelTransfer`）。`rejectTransfer` 可能返回 `null`（已无查看权）→ 离开详情页；停在详情页时失去查看权（非首次加载 40300）同样带离。有待确认转交时不能提交完工（首页按钮、完工页 `blockText` 都有提示）。
- **已退出**：`order.readOnlyText` 非空时在头部下方显示只读提示条；参与人列表里 `Former` 带 `exitText`（已转交 / 已改派）；「我的工单」新增「已退出」分组（`mine('former')`），行上带「已转交 / 已改派」标记、备注固定为「已退出本单 · 仅可查看」。
- **首页（工程师）**：最前面新增「待我确认的转交」（`transfersToMe`，红色描边卡片，点进详情确认 / 拒绝）；本人负责且有待确认转交的工单带「转交待确认」小标签（我的工单、全部工单同理）。

```js
// v1.3 新接口（api/index.js）
api.orders.setSat(id, satSigned, satDate)               → OrderDetail   // POST /api/orders/{id}/sat
api.orders.setAttachmentType(id, fileId, type)          → OrderDetail   // POST …/attachments/{fileId}/type
api.orders.transfer(id, engineerId, reason)             → OrderDetail   // POST /api/orders/{id}/transfers
api.orders.acceptTransfer(id, transferId)               → OrderDetail   // POST …/transfers/{transferId}/accept
api.orders.rejectTransfer(id, transferId, reply)        → OrderDetail | null   // POST …/transfers/{transferId}/reject
api.orders.cancelTransfer(id, transferId)               → OrderDetail   // POST …/transfers/{transferId}/cancel
api.orders.mine('former')                               // 新分组；counts 多一个 former
// utils/attachments.js：pick(left, type) / pickMedia(left, type) / pickChatFiles(left, type) 把 type 写进条目（默认 'Other'）
```
