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
    ├── format.js         时间 / 时长 / 价格 / 距离 / 手机号 / 文件大小格式化、文件类型角标
    ├── enums.js          枚举中文 + 原型 16 配色、STATUS_TABS、QUICK_FILTERS、ORDER_ACTIONS
    ├── ui.js             toast / confirm / preview + 公共组件（已全局注册）
    └── views/
        ├── login.js      登录页（已完成）
        ├── orders.js          工单列表 + 概况条 + 筛选
        ├── orders-drawer.js   工单详情抽屉（进度条、定价、设备资料、暂停、转交、申请审批、处理人员、返厂流程、附件、打卡、状态记录）
        ├── orders-sat.js      修改 SAT 信息弹窗（v1.3：是否已完成 SAT + 时间）
        ├── orders-assign.js   指派 / 改派（负责人 + 协助人）与「调整协助人」弹窗
        ├── orders-hold.js     暂停搁置弹窗（原因 + 说明）
        ├── orders-price.js    定价 / 改价弹窗（展示销售的价格建议）
        ├── orders-factory.js  返厂节点记录 / 更正弹窗（快递、照片上传）
        ├── orders-create.js   新建工单（不含价格；v1.2 起带附件；v1.3 多台设备、附件分三类）
        ├── attachments-upload.js  附件上传组件（v1.2，新建工单与抽屉共用；v1.3 带附件类别）
        ├── orders-export.js   导出台账
        ├── punches.js    打卡记录
        ├── hours.js      工时统计
        ├── files.js      工单文件中心（v1.2）
        └── accounts.js   账号管理
```

## v1.1 功能（契约见 `docs/CHANGES-v1.1.md`）

PC 后台只有主管能登录，只实现主管视角的动作。所有可执行动作都看服务端返回的 `detail.actions`、`requests[].canDecide`、`factory.nodes[].canRecord / canEdit`，前端不推导规则。

- **价格**：新建工单不再填价格。列表价格列与抽屉里未定价显示红色「未定价」；抽屉「服务价格」旁有「定价 / 改价」（`orders-price.js`，0 = 保内免费）。销售的最新价格建议显示在抽屉里，`pending` 时高亮并可点「采用」（预填金额后仍需保存）。未定价的待验收单「确认闭环」为禁用，提示取 `closeBlockedReason`。
- **暂停搁置**：「待启动」「处理中」都可点「标为暂停搁置」（`orders-hold.js`：原因下拉 4 项 + 说明，选「其他」时说明必填）。列表状态列显示 `暂停搁置 · {holdText}`；抽屉 5 段进度条里暂停所在的段显示为灰色斜纹 + 暂停图标，下方显示 `暂停 · {holdText}`，并有暂停提示条（暂停人、时间、恢复后回到的状态）。「恢复处理」回到暂停前的状态。
- **申请审批**：工程师的暂停申请 / 协助人申请在抽屉里以琥珀色卡片展示，可「同意」或「驳回」（回复选填）。列表里有待审批申请的工单带「待审批」标；侧栏快捷筛选「待审批申请」、概况条第二行「待审批申请 N」可直达。
- **多人协作**：指派 / 改派弹窗里单选负责人、多选协助人（最多 5 人，负责人不能同时是协助人，改派时预选当前协助人）；抽屉「处理人员」区按人列出角色、累计工时、现场中 / 缺卡，并可「调整协助人」。列表负责人列显示 `+N`（悬停看协助人名单）。打卡记录每条都显示打卡人姓名。工时统计的每日明细里，该工程师作为协助人的条目带「协助」标签。
- **返厂流程**：返厂单抽屉里有 5 节点时间线（操作人、时间、快递公司与单号——点击单号复制、备注、照片缩略图可预览）；可记录的节点显示按钮，已记录的节点可「更正」（`orders-factory.js`，照片用 `api.files.upload` 上传，category 按节点，最多 9 张）。底部按钮区也会出现下一个可记录节点的按钮。列表类型列在「返厂」下显示 `factoryText`。闭环确认框里展示 `closeHints`（如「客户签收尚未记录」）。
- **概况条第二行**：暂停搁置 / 待审批申请 / 未定价 / 价格建议（来自 `GET /api/admin/overview`），前三项可点击跳到对应筛选。
- **导出**：台账末尾新增 5 列（协助人、人员工时、暂停原因、返厂进度、寄回单号），未定价显示「未定价」；工时导出新增第 10 列「本单角色」。导出由后端生成，前端无逻辑改动。

## v1.2 功能（契约见 `docs/CHANGES-v1.2.md`）

附件相关权限只看服务端返回的 `canAddAttachment`、`canRestrictAttachment`、`attachments[].canDelete / canToggleRestricted`，前端不推导规则。

- **工单附件**：工单除图片、视频外可以带文档（Word / Excel / PPT / PDF / TXT / CSV）。图片单个 ≤ 20MB，视频和文档 ≤ 100MB，每单最多 50 个（建单照片也算在内；v1.2 为 20 个，v1.3 调整为 50）。
  - 上传分两步：先 `api.files.upload(file, 'Attachment', { onProgress })`（带原始文件名与进度，XHR 超时 15 分钟），再 `api.orders.create({ attachments })` 或 `api.orders.addAttachments(id, [{ fileId, restricted }])` 挂到工单。
  - 公共组件 `views/attachments-upload.js`：`<AttachmentsUpload v-model="items" :limit :can-restrict :disabled />`，支持多选和拖入，上传前按扩展名 / 大小 / 数量预检，同时最多上传 2 个，每项有进度条、错误文字 + 重试、移除、「仅销售和主管可见」复选框。父组件用导出的 `attUploading(items)` / `attHasError(items)` / `attDone(items)`（→ `[{ fileId, restricted }]`）判断与取值（组件实例上也有同名的 `uploading` / `hasError` / `done`）。
  - 新建工单：原「现场照片」区块改为「附件」，提交体用 `attachments`，不再传 `photoFileIds`。
  - 工单抽屉：「返厂流程」与「打卡记录」之间新增「附件」区。点名称：图片 / 视频在 lightbox 里预览（同单可左右切换），文档直接下载；行内按钮「下载」「设为仅销售和主管可见 / 设为所有人可见」「删除」；「添加附件」在区内展开上传组件；「全部文件」跳到 `#/files?orderId={id}`。
  - 下载一律走 `api.files.download(id, name)`（`GET /api/files/{id}/download`，带 Authorization，文件名取响应头里的原始文件名）。
- **工单文件中心**（`#/files`，`views/files.js`）：汇总所有工单名下的文件（附件、建单照片、完工照片、签字件、打卡照片、返厂照片）。类型 tab（全部 / 图片 / 视频 / 文档，带数量）、搜索（文件名 / 工单号 / 客户）、来源、上传人、上传日期范围、`orderId` 筛选标签；汇总「共 N 个文件 · 总大小」；每页 30 条。≤ 2MB 的图片显示缩略图，其余显示类型角标。点工单号在本页打开工单抽屉。附件行可删除、切换可见范围（直接调工单的附件接口）。不做批量下载。
- **上线注意**：超过 30MB 的上传需要把站点 nginx 的 `client_max_body_size` 提到 `110m`（见 `deploy/README.md`），否则会被 nginx 直接返回 413，页面提示「文件不能超过 100MB」。

## v1.3 功能（契约见 `docs/CHANGES-v1.3.md`）

三项变更：设备资料（多台设备）、附件分类 + 随时可传、转交。新增的权限同样只看服务端返回的 `canEditSat`、`canAddAttachment`、`attachments[].canChangeType`、`actions`（含 `cancelTransfer`），前端不推导规则；设备汇总文字一律显示服务端的 `deviceText`，前端不自己拼。

- **设备资料**
  - 新建工单的「设备资料」区顺序固定为：`项目编号 *` → 设备（选填，可添加多台，每台 设备名称 / 设备编号 / 设备型号，最多 20 台，整行不填的忽略）→ `是否已完成 SAT *`（已完成 / 未完成）→ 时间（选填；标签随上一项变为「SAT 完成时间」或「发货时间」，在「已完成 / 未完成」之间切换时清空，第一次选择不清空）。提交体用 `projectNo`、`devices: [{ name, sn, model }]`、`satSigned`、`satDate('yyyy-MM-dd' | '')`，不再传 `deviceModel` / `deviceSn`。
  - 工单列表的副标题为 `项目编号 · deviceText · 故障现象`；搜索可按项目编号和任意一台设备的名称 / 编号 / 型号。
  - 抽屉新增「设备资料」区：项目编号（空显示 `—`）、是否已完成 SAT、SAT 完成时间 / 发货时间（空显示 `未填写`）、设备表（`#` / 设备名称 / 设备编号 / 设备型号）。`canEditSat` 为 true 时可点「修改 SAT」（`orders-sat.js`，`api.orders.setSat`）；项目编号与设备清单建单后不能改。旧数据的 `yyyy-MM` 时间原样显示，在弹窗里以「原记录：…」提示；没切换「已完成 / 未完成」、也没重新选日期就保存时原样带回旧记录（不会因为点了「保存」被清空），切换后按规则清空。
- **附件分类 + 随时可传**
  - 三类（`enums.ATTACHMENT_TYPE`，文字与后端、小程序逐字一致）：`Sat` SAT 验收单（签字版）、`RepairForm` 现场维修登记表/返厂维修登记表（签字版）、`Other` 设备照片、沟通记录及其他附件。只分类，不做任何强制检查。
  - 每单上限 50 个。新建工单里三类各一个上传区，三类合计最多 50 个；提交 `attachments: [{ fileId, restricted, type }]`。
  - 抽屉「附件」区按三类分组，空组显示「暂无」；在哪一组点「添加」就归哪一类（同一时间只展开一组，换组前若有已选未提交的文件会先确认）。`canChangeType` 的附件行有类别下拉，可直接改类别（`api.orders.setAttachmentType`）。已闭环的工单只要 `canAddAttachment` 为 true 就照常显示「添加」。
  - 上传组件新增 prop `type`：`<AttachmentsUpload v-model="items" type="Sat" :limit :can-restrict :disabled />`，`attDone(items)` 返回 `[{ fileId, restricted, type }]`；另导出 `attUsed(items)`（占名额的条目数，多个上传区合计上限时用）。
  - 文件中心新增「附件类别」筛选（`attachmentType`，给了它只返回附件行）与「类别」列（非附件行显示 `—`），附件行同样可以用下拉改类别；搜索增加项目编号。
- **转交**（工程师之间换人，不需要主管批准；主管在 PC 上不发起转交，换人仍用「改派」）
  - 列表里有待确认转交的工单带「转交待确认」标（悬停看 `pendingTransfer.text`）。
  - 抽屉里 `pendingTransfer` 非空时显示「转交待确认」提示块（文字、原因、时间）；`actions` 含 `cancelTransfer` 时可「撤销转交」（`api.orders.cancelTransfer`）。「处理人员」之后有「转交记录」区（全部历史，时间倒序）。
  - 「处理人员」里有退出记录的「已退出」后面带灰字 `已转交` / `已改派`（`participants[].exitText`）。
- **导出**：台账末尾再追加 3 列（设备台数、SAT 完成时间、发货时间），多台设备在「设备型号」「设备编号」两列里用「；」连接。导出由后端生成，前端只同步了导出弹窗里的说明文字。

## 路由

| hash | 视图 |
|---|---|
| `#/login?redirect=...` | 登录 |
| `#/orders` | 工单。`?quick=missing|distance|submitted|requests|unpriced` 对应侧栏「待处理」快捷筛选 |
| `#/punches` | 打卡记录 |
| `#/hours` | 工时统计 |
| `#/files` | 工单文件中心（v1.2）。`?keyword=&type=Image|Video|Document&source=&attachmentType=Sat|RepairForm|Other&uploaderId=&orderId=&from=&to=&page=`，`&id=` 为打开的工单抽屉（`attachmentType` 为 v1.3 新增） |
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
      <SearchBox v-model="keyword" placeholder="搜索工单号、客户、项目编号、设备" @search="load" />
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
  - `orders.list({status, keyword, issue, assigneeId, serviceType, from, to, priceSet, pendingRequest, page, pageSize})`、`orders.detail(id)`、`orders.create(body)`
  - `orders.assign(id, engineerId, assistantIds?)`（`assistantIds` 省略 = 不调整协助人）、`orders.setAssistants(id, engineerIds)`
  - `orders.void(id, reason)`、`orders.hold(id, reasonType, note)`、`orders.resume(id)`、`orders.reject(id, reason)`、`orders.close(id)`
  - `orders.setPrice(id, price)`、`orders.approveRequest(id, requestId)`、`orders.rejectRequest(id, requestId, reply)`、`orders.factoryNode(id, {node, courier, trackingNo, note, photoIds})`
  - `engineers.list()`
  - `hours.month(year, month, engineerId)`
  - `admin.overview()`、`admin.supplement({orderId, arrivePunchId, kind, time, reason})`
  - `admin.punches({from, to, engineerId, abnormal, page, pageSize})`、`admin.hours(year, month)`
  - `admin.employees()`、`admin.createEmployee(body)`、`admin.updateEmployee(id, body)`、`admin.unbind(id)`、`admin.setPassword(id, password)`
  - `admin.exportOrders({from, to, status}, filename?)`、`admin.exportHours(year, month, filename?)`
  - `files.upload(file, category, { fileName?, onProgress?, signal? })`（FormData；传 `onProgress` 时用 XHR 并回调 0–100，`signal`（AbortSignal）可取消上传——上传组件在移除条目 / 卸载时会取消；`Attachment` 自动带 `fileName = file.name`）、`files.download(id, name?)`
  - v1.2：`orders.addAttachments(id, [{fileId, restricted}])`、`orders.deleteAttachment(id, fileId)`、`orders.setAttachmentRestricted(id, fileId, restricted)`、`admin.files({keyword, type, source, uploaderId, orderId, restricted, from, to, page, pageSize})`；`orders.create(body)` 的附件字段为 `attachments`
  - v1.3：`orders.setSat(id, {satSigned, satDate})`、`orders.setAttachmentType(id, fileId, type)`、`orders.cancelTransfer(id, transferId)`；`orders.addAttachments(id, [{fileId, restricted, type}])` 的条目多了 `type`；`admin.files(params)` 多了 `attachmentType`；`orders.create(body)` 用 `projectNo` / `devices` / `satSigned` / `satDate`

### format.js

`parseTime(s)`（按本地时间解析 `yyyy-MM-ddTHH:mm:ss` / `yyyy-MM-dd`）、`toApiDate(d)`、`toApiDateTime(d)`、`fmtTime` → `HH:mm`、`fmtMD` → `M月D日`、`fmtMDHM` → `M月D日 HH:mm`、`fmtDateTime` → `yyyy-MM-dd HH:mm`、`fmtWeekday`、`fmtHM(minutes)` → `H:mm`（空值为 `—`）、`fmtHours(minutes)` → 1 位小数、`fmtNumber`、`fmtPrice(p, {free, priceSet})` → `¥1,800` / `保内`（`free: true` 时为 `保内免费`；`priceSet === false` 时为 `未定价`）、`isWarranty(p, priceSet)`、`fmtRelative`、`fmtDistance(m)` → `86 m` / `2.4 km`、`maskPhone`、`thisWeekRange()`、`lastDaysRange(n)`、`monthRange(y, m)`。v1.2：`fmtSize(bytes)` → `812 B` / `35 KB` / `2.4 MB` / `1.25 GB`、`fileBadge({kind, ext})` → `{ text, color }`（W / X / P / PDF / TXT / 图 / 视频）。

### enums.js

- `ROLE`、`ORDER_STATUS`（`label` / `dot` / `bg` / `fg`）、`ORDER_SOURCE`、`PROBLEM_TYPE`、`URGENCY`、`SERVICE_TYPE`、`PUNCH_KIND`、`RUN_STATUS`、`ISSUE`（标签色 + 警示框色 `alertBg` / `alertBorder` / `alertFg`）、`FILE_CATEGORY`。
- `STATUS_TABS`（原型顺序，`countKey` 对应 `orders.list().counts`）。
- `QUICK_FILTERS`（`key` 对应 `?quick=`，`query` 是 orders.list 参数；v1.1 加了 `requests`、`unpriced`）。
- v1.1：`PROGRESS_STEPS`（5 段进度条文字）、`HOLD_REASON`、`REQUEST_TYPE`、`REQUEST_STATUS`、`FACTORY_NODE`（`label` / `action` 按钮文案 / `courier` / `photo` / `category`）、`PARTICIPANT_ROLE`；`FILE_CATEGORY` 加了 3 类返厂照片。
- v1.2：`FILE_CATEGORY` 加了 `Attachment`；`FILE_KIND`（图片 / 视频 / 文档）、`FILE_SOURCE`（文件中心的 6 种来源）。
- v1.3：`ATTACHMENT_TYPE`（附件类别，`label` 完整标题 / `short` 短标题，键的顺序即展示顺序）、`ATTACHMENT_TYPE_OPTIONS`（短标题下拉选项）、`TRANSFER_STATUS`（待确认 / 已接手 / 已拒绝 / 已撤回 / 已失效）；`ORDER_ACTIONS` 加了 `cancelTransfer`（撤销转交，只在抽屉的转交提示块里用）。
- `ORDER_ACTIONS`：action → 按钮文案和样式。
- `label(map, v)`、`options(map)`。

### ui.js（组件已全局注册，模板里直接用）

- `toast(msg, 'info'|'success'|'error')`、`toastError(err)`：40100 时不弹。
- `confirm(title, message, opts)` → Promise：
  - 没有输入框时，确定为 `true`，取消为 `false`。
  - `opts.input` 为 true 时，确定返回 trim 过的字符串，取消返回 `null`。
  - 其他 opts：`required`、`placeholder`、`inputLabel`、`multiline`、`defaultValue`、`okText`、`cancelText`、`danger`、`maxLength`。
- `preview(items, index)`：图片 / 视频预览，支持左右键和 Esc。条目可以是地址字符串、`{ url }`，或 `{ url, type: 'image'|'video' }`（video 渲染为 `<video controls autoplay>`）。
- 组件：
  - `<Icon name size stroke>`，可用图标名：orders、camera、clock、users、settings、search、download、plus、chevronDown/Left/Right、close、alert、check、info、logout、key、image、refresh、folder、file、video、lock、trash、paperclip
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
- v1.1：5 段进度条 `progress5` > `progress5-seg(.on|.current|.paused)`；价格建议 `orders-suggest(.pending)`；申请卡片 `alert alert-warn orders-dr-req`；人员行 `orders-dr-person`；返厂时间线 `factory-line` > `factory-node.is-done|is-current|is-todo|is-skipped`
- v1.2：类型角标 `att-badge`（背景色用 `fileBadge().color`）、文字操作链接 `btn-link att-link(.att-link-danger)`；上传组件 `att-up-*`；抽屉附件区 `att-list` > `att-row`；文件中心 `files-*`。v1.2 新增的类一律以 `att-` / `files-` 为前缀
- v1.3：设备资料 `dev-*`（建单的设备表 `dev-table`、抽屉的 `kv dev-kv` + `dev-list`）；转交 `tr-*`（转交记录 `tr-list` > `tr-row`、退出方式 `tr-exit`）；附件分类 `att-zone`（建单的上传区）、`att-group` > `att-group-head` / `att-group-empty`（抽屉分组）、`att-type-select`（类别下拉）。v1.3 新增的类一律以 `dev-` / `tr-` / `att-` 为前缀
- 表单：`field` > `field-label`（`<span class="req">*</span>`）+ `input` / `select` / `textarea`（`.invalid`）+ `field-hint` / `field-error`；布局用 `form-stack` / `form-grid`（`.span-2`）/ `form-section-title`；选项用 `radio-group` > `radio-chip(.active)`，复选用 `checkbox`
- 菜单：`dropdown` > `menu` > `menu-item`
- 工具类：`num`、`mono`、`text-muted` / `text-faint` / `text-danger` / `text-warn` / `text-success`、`text-right`、`ellipsis`、`nowrap`

## 调试

控制台里可以用 `window.__admin`，它包含 `{ api, store, route, navigate, refreshCounts }`。
