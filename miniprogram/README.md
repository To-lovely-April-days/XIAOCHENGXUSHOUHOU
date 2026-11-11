# 霍桐售后工单 · 微信小程序

原生 WXML / WXSS / JS（CommonJS），基础库 ≥ 3.0，**无 npm、无 TS、无构建步骤**。
接口契约：`../docs/API.md`；业务与视觉：`../docs/DESIGN.md`；原型：`../docs/prototype*/`。

## 导入与运行

1. 先启动后端（`server/`，默认 `http://localhost:5080`，开发时建议 `WeChat:MockMode=true`）。
2. 微信开发者工具 → 导入项目 → 目录选 **`miniprogram/`**（`project.config.json` 就在这里）。
3. AppID 默认 `touristappid`（游客模式）。要真机预览、获取手机号，请在 `project.config.json` 里换成公司小程序的 AppID。
4. 右上角「详情 → 本地设置」勾选 **「不校验合法域名、web-view（业务域名）、TLS 版本以及 HTTPS 证书」**（`urlCheck` 已设为 false）。
5. 游客模式拿不到真实手机号：登录页点「开发调试：输入手机号直接登录」，输入后台登记的手机号（走 `/api/auth/dev-login`，需后端 MockMode）。
6. 真机调试：把 `config.js` 的 `baseUrl` 改成电脑的局域网 IP（如 `http://192.168.1.20:5080`）；上线必须是已在小程序后台配置的 https 域名，并把 `devLogin` 改为 `false`。

## 目录结构

```
miniprogram/
├── app.js / app.json / app.wxss     入口、页面与全局组件注册、设计令牌与通用类
├── config.js                        baseUrl、devLogin
├── project.config.json, sitemap.json
├── api/index.js                     每个接口一个函数（与 API.md 一一对应）
├── utils/
│   ├── request.js                   wx.request / uploadFile / downloadFile 封装
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
api.auth.login(code) / api.auth.bind(bindTicket, phoneCode) / api.auth.devLogin(phone)
api.me.get() → {user, monthMinutes, activeOrderCount, monthTripDays}
api.me.setAvatar(fileId) → User
api.files.upload(filePath, category) → FileDto
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
`request({url, method, data, silent, loading})`、`get(url, data, opts)`、`post`、`put`、`uploadFile(filePath, category, opts) → FileDto`、`download(url, query) → tempFilePath`。401/40100 自动清 token 并 reLaunch 登录页（防抖）。

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
`icons.get(name, color, strokeWidth) → data URI`；名字：home orders camera clock user plus plus-square back right down check close phone location search image edit warning info flash switch shield wechat。
WXML：`<ui-icon name="camera" color="#FFFFFF" size="24" stroke="2" />`（size 为设计稿 px）。

### 组件
| 组件 | 属性 | 事件 |
|---|---|---|
| `<nav-bar>` | `title`、`back`(默认 true)、`background`(默认 #F6F6F4)、`color`、`fixed`、`placeholder`、`customBack`；slot：`left` / `right` / 默认(标题) | `back` |
| `<avatar>` | `user`(UserBrief)、`size`(px，默认 40)、`src`、`text`、`bg`、`color` | |
| `<status-tag>` | `status`、`size`('md'/'sm')、`text` | |
| `<urgency-tag>` | `urgency`、`showNormal`、`size`('sm'/'md') | |
| `<empty>` | `text`、`desc`、`card`；默认 slot 放按钮 | |
| `<assign-sheet>` | `show`、`order`(OrderListItem：id/customerName/urgency/deviceModel/assignee) | `done`(detail=OrderDetail)、`close` |

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
`app.json` 开启 `__usePrivacyCheck__`。登录页的验证按钮为 `open-type="getPhoneNumber|agreePrivacyAuthorization"`，点击即同意；其他页面调用隐私接口而用户未同意时，`app.js` 的 `wx.onNeedPrivacyAuthorization` 会打开 `pages/privacy/index?mode=authorize`，用户点「同意并继续」后继续原操作。小程序后台「用户隐私保护指引」需声明：手机号、位置信息、摄像头 / 相册、微信头像。
