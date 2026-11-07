# 霍桐售后 · 主管后台（PC 网页）

纯静态页面，没有构建步骤。后端把 `wwwroot/admin/` 托管在 `/admin/`，页面和 `/api` 同源，不需要 CORS。

- 本地访问：先启动后端（`cd server/src/Huotong.AfterSales.Api && dotnet run`），然后打开 http://localhost:5080/admin/
- Development 演示账号：赵主管 `13900000001`，密码来自 `Seed:SupervisorPassword`（默认 `Huotong@2026`，只在空库写种子数据时生效）。
- 只有 Supervisor 能登录。接口契约见 `docs/API.md` 第 8、9 节，视觉以 `docs/prototype/16_*.html` 为准。

## 技术约定

- Vue 3.4.38 **全局构建（含模板编译器）** 放在本地 `vendor/vue.global.prod.min.js`，用普通 `<script>` 引入。在模块里通过 `const { reactive, ... } = window.Vue` 取用。
- 业务代码都是原生 ES modules（`<script type="module" src="js/main.js">`），import 用相对路径，并且**必须带 `.js` 后缀**。
- 组件写成选项对象：`{ name, props, emits, data, computed, watch, methods, template: \`...\` }`。模板在运行时编译。不要用 SFC、JSX，也不要依赖任何构建工具。
- 所有文案用简体中文。时间、数字、价格一律用 `js/format.js` 格式化，枚举中文和配色一律用 `js/enums.js`。

## 目录

```
admin/
├── index.html            入口（title「霍桐售后 · 主管后台」）
├── css/admin.css         设计令牌 + 全部公共样式（最小宽度 1280）
├── vendor/vue.global.prod.min.js
└── js/
    ├── main.js           createApp、AppShell（侧栏）、路由表 ROUTES、登录守卫、修改密码弹窗
    ├── router.js         hash 路由：route / navigate / updateQuery / onRouteChange
    ├── store.js          store.user、store.counts（侧栏数字）、loadMe / refreshCounts / logout
    ├── api.js            fetch 封装 + 所有接口函数 + download
    ├── format.js         时间 / 时长 / 价格 / 距离 / 手机号格式化
    ├── enums.js          枚举中文 + 原型 16 配色、STATUS_TABS、QUICK_FILTERS、ORDER_ACTIONS
    ├── ui.js             toast / confirm / preview + 公共组件（已全局注册）
    └── views/
        ├── login.js      登录页（已完成）
        ├── orders.js     工单（占位，待覆盖）
        ├── punches.js    打卡记录（占位，待覆盖）
        ├── hours.js      工时统计（占位，待覆盖）
        └── accounts.js   账号管理（占位，待覆盖）
```

## 路由

| hash | 视图 |
|---|---|
| `#/login?redirect=...` | 登录 |
| `#/orders` | 工单。`?quick=missing|distance|submitted` 对应侧栏「待处理」快捷筛选 |
| `#/punches` | 打卡记录 |
| `#/hours` | 工时统计 |
| `#/accounts` | 账号管理 |

- 没有 token 时跳 `#/login?redirect=<原地址>`。登录后先 `GET /api/me`，角色不是 Supervisor 就清掉 token。
- 路由只看 path，query 变化**不会重新挂载**视图。视图要自己 `watch: { 'route.query': {handler, deep: true, immediate: true} }`（先在 `data()` 里返回 `{ route }`）。
- 改查询参数用 `updateQuery({ status: 'Pending', page: 1 })`（默认 replace，不产生历史记录）。值为 `''` 或 `null` 时删除该参数。

## 写一个视图

视图文件 `export default` 一个组件，结构是「顶栏 + `.page` 正文」。可以有多个根节点：

```js
import * as api from '../api.js';
import { route, updateQuery } from '../router.js';
import { store } from '../store.js';
import { toast, toastError, confirm, preview } from '../ui.js';
import { fmtHM, fmtPrice, fmtMDHM } from '../format.js';
import { ORDER_STATUS, STATUS_TABS, QUICK_FILTERS } from '../enums.js';

export default {
  name: 'OrdersView',
  data() { return { route, items: [], loading: false }; },
  template: `
    <PageHeader :crumbs="['工单', '全部工单']">
      <SearchBox v-model="keyword" placeholder="搜索工单号、客户、设备编号" @search="load" />
      <button class="btn btn-secondary"><Icon name="download" :size="15" />导出台账</button>
      <button class="btn btn-primary"><Icon name="plus" :size="15" :stroke="2.2" />新建工单</button>
    </PageHeader>
    <div class="page">...</div>`,
};
```

改了数据以后调用 `store.refreshCounts()` 刷新侧栏数字。

## 公共模块速查

### api.js

- `request(method, path, {query, body, form})`：自动带 `Authorization: Bearer <localStorage.admin_token>`，解开 `{code,message,data}` 后返回 `data`。
  - code ≠ 0 时抛出 `ApiError`，带 `.message`（中文，可直接展示）、`.code`、`.status`。
  - 40100 时先清 token、跳 `#/login`，然后照样抛错。
  - 网络错误的 code 是 `-1`。
- `download(url, filename?)`：fetch → blob → `a[download]`。优先用 `Content-Disposition` 里的文件名（支持 `filename*=UTF-8''`）。如果返回的是 JSON 错误，就抛出 ApiError。
- `getToken / setToken / clearToken`，`TOKEN_KEY = 'admin_token'`，`qs(params)`（跳过空值）。
- 接口函数（参数名与 API.md 一致）：
  - `auth.passwordLogin(phone, password)`
  - `me.get()`、`me.changePassword(oldPassword, newPassword)`
  - `orders.list({status, keyword, issue, assigneeId, serviceType, from, to, page, pageSize})`、`orders.detail(id)`、`orders.create(body)`
  - `orders.assign(id, engineerId)`、`orders.void(id, reason)`、`orders.hold(id, reason)`、`orders.resume(id)`、`orders.reject(id, reason)`、`orders.close(id)`
  - `engineers.list()`
  - `hours.month(year, month, engineerId)`
  - `admin.overview()`、`admin.supplement({orderId, arrivePunchId, kind, time, reason})`
  - `admin.punches({from, to, engineerId, abnormal, page, pageSize})`、`admin.hours(year, month)`
  - `admin.employees()`、`admin.createEmployee(body)`、`admin.updateEmployee(id, body)`、`admin.unbind(id)`、`admin.setPassword(id, password)`
  - `admin.exportOrders({from, to, status}, filename?)`、`admin.exportHours(year, month, filename?)`
  - `files.upload(file, category)`（FormData）

### format.js

`parseTime(s)`（按本地时间解析 `yyyy-MM-ddTHH:mm:ss` / `yyyy-MM-dd`）、`toApiDate(d)`、`toApiDateTime(d)`、`fmtTime` → `HH:mm`、`fmtMD` → `M月D日`、`fmtMDHM` → `M月D日 HH:mm`、`fmtDateTime` → `yyyy-MM-dd HH:mm`、`fmtWeekday`、`fmtHM(minutes)` → `H:mm`（空值为 `—`）、`fmtHours(minutes)` → 1 位小数、`fmtNumber`、`fmtPrice(p, {free})` → `¥1,800` / `保内`（`free: true` 时为 `保内免费`）、`isWarranty(p)`、`fmtRelative`、`fmtDistance(m)` → `86 m` / `2.4 km`、`maskPhone`、`thisWeekRange()`、`lastDaysRange(n)`、`monthRange(y, m)`。

### enums.js

- `ROLE`、`ORDER_STATUS`（`label` / `dot` / `bg` / `fg`）、`ORDER_SOURCE`、`PROBLEM_TYPE`、`URGENCY`、`SERVICE_TYPE`、`PUNCH_KIND`、`RUN_STATUS`、`ISSUE`（标签色 + 警示框色 `alertBg` / `alertBorder` / `alertFg`）、`FILE_CATEGORY`。
- `STATUS_TABS`（原型顺序，`countKey` 对应 `orders.list().counts`）。
- `QUICK_FILTERS`（`key` 对应 `?quick=`，`query` 是 orders.list 参数）。
- `ORDER_ACTIONS`：action → 按钮文案和样式。
- `label(map, v)`、`options(map)`。

### ui.js（组件已全局注册，模板里直接用）

- `toast(msg, 'info'|'success'|'error')`、`toastError(err)`：40100 时不弹。
- `confirm(title, message, opts)` → Promise：
  - 没有输入框时，确定为 `true`，取消为 `false`。
  - `opts.input` 为 true 时，确定返回 trim 过的字符串，取消返回 `null`。
  - 其他 opts：`required`、`placeholder`、`inputLabel`、`multiline`、`defaultValue`、`okText`、`cancelText`、`danger`、`maxLength`。
- `preview(urls, index)`：图片预览，支持左右键和 Esc。
- 组件：
  - `<Icon name size stroke>`，可用图标名：orders、camera、clock、users、settings、search、download、plus、chevronDown/Left/Right、close、alert、check、info、logout、key、image、refresh
  - `<Avatar :user :size>`：有 `avatarUrl` 显示图片，没有就显示姓氏圆
  - `<StatusDot :status :label>`
  - `<Tag tone|bg|fg>`
  - `<IssueTag type>`
  - `<Empty text>`
  - `<Loading>`
  - `<Pagination :total :page :page-size @change>`（也支持 `v-model:page`）
  - `<Modal title width persistent @close>`，插槽 `#footer`
  - `<Drawer @close>`，插槽 `#head` / 默认 / `#foot`；fixed 定位，`top: 56px`，宽 400
  - `<PageHeader :crumbs>`，默认插槽放在右侧
  - `<SearchBox v-model @search>`：回车或停止输入 400ms 后触发

### css/admin.css 常用类

- 布局：`page`、`page-head`、`page-sub`、`page-actions`、`card`、`card-pad`
- 本周概况：`stats` > `stat` > `stat-label` / `stat-main` > `stat-value` + `stat-note(.danger)`
- 按钮：`btn` + `btn-primary` / `btn-secondary` / `btn-danger` / `btn-ghost` / `btn-link`。禁用态用 `:disabled`、`aria-disabled="true"` 或 `btn-disabled`。尺寸：`btn-sm` 28px、`btn-lg` 34px（抽屉底部按钮）、`btn-xl` 40px。另有 `icon-btn`。
- 开关：`switch-btn[aria-checked]` + `<span class="switch on">`
- 状态 tab：`toolbar` > `tabs` > `tab(.active)` + `tab-count`；右侧放 `toolbar-right`
- 网格表格：`grid-table` > `grid-head` + `grid-row(.selected)`。默认列宽就是原型工单表，其他列宽用 style 覆盖。单元格：`cell-no`、`cell-title` + `cell-title-text`、`cell-sub`、`cell-user(.unassigned)`、`cell-num`、`checkbox-box`
- 普通表格：`data-table`（`tr.clickable`、`tr.selected`、`td.num`）
- 表格底部：`table-foot`
- 标签：`tag` + `tag-urgent` / `tag-missing` / `tag-distance` / `tag-success` / `tag-neutral` / `tag-dark`；状态圆点：`dot`、`status`
- 抽屉内容：`drawer-title`、`drawer-sub`、`kv`（dl）、`section-title`
- 警示框：`alert` + `alert-danger` / `alert-warn` / `alert-info`，里面用 `alert-row`、`alert-actions`、`alert-form`（96px + 1fr）、`alert-note`
- 打卡行：`punch-row` > `thumb(.thumb-missing)` + `punch-main` > `punch-title` / `punch-note(.warn|.danger)` + `punch-dur`；缩略图网格：`thumb-grid`
- 状态记录：`log-list` > `log-item` > `dot` + `log-text` + `log-time`
- 表单：`field` > `field-label`（`<span class="req">*</span>`）+ `input` / `select` / `textarea`（`.invalid`）+ `field-hint` / `field-error`；布局用 `form-stack` / `form-grid`（`.span-2`）/ `form-section-title`；选项用 `radio-group` > `radio-chip(.active)`，复选用 `checkbox`
- 菜单：`dropdown` > `menu` > `menu-item`
- 工具类：`num`、`mono`、`text-muted` / `text-faint` / `text-danger` / `text-warn` / `text-success`、`text-right`、`ellipsis`、`nowrap`

## 调试

控制台里可以用 `window.__admin`，它包含 `{ api, store, route, navigate, refreshCounts }`。
