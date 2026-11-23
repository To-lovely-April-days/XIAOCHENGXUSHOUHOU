# v1.4 变更记录：微信小程序「订阅消息」工单提醒

需求：主管指派了工程师之后，对方能直接收到提醒。方案是微信小程序的**一次性订阅消息**（类目「工具 > 办公」拿不到长期订阅）：
用户点一次「允许」只能收一条；勾选「总是保持以上选择，不再询问」后，之后每次由点击触发的 `wx.requestSubscribeMessage` 都会静默再攒一条额度。

模板要在小程序后台扫码登录后才能选，所以**模板 ID 与字段全部由配置决定**：代码已经做完，选好模板后只改配置、重启后端，不用改代码、不用重新发布小程序。
模板没配置时一切静默关闭（后端不发、小程序不显示入口），这一版可以照常发布。

没有数据库迁移；没有改动任何已有接口的请求 / 响应；钉钉群通知原样保留。接口契约见 `docs/API.md` §2。

---

## 1. 改了什么

| 位置 | 内容 |
|---|---|
| `server/.../Infrastructure/AppOptions.cs` | `WeChatOptions.Subscribe`（`SubscribeOptions`） |
| `server/.../Services/SubscribeFields.cs`（新） | 语义槽 `SubscribeSlot`、字段清洗 / 截断的纯函数 |
| `server/.../Services/SubscribeService.cs`（新） | `ISubscribeNotifier` 与实现：收件人过滤、stable_token 缓存、发送、MockMode 记录、配置检查 |
| `server/.../Services/OrderSubscribe.cs`（新） | `OrderService` 里发提醒的公共方法 |
| `server/.../Services/OrderService.cs`、`OrderRequestService.cs`、`TransferService.cs` | 各动作在提交之后发提醒（§3） |
| `server/.../Controllers/MeController.cs` | `GET /api/me/subscribe` |
| `server/.../Infrastructure/HttpClientSetup.cs`（新）、`Program.cs` | 注册服务；去掉 HttpClient 自带的请求日志（§4） |
| `server/.../appsettings.json` | `WeChat:Subscribe` 配置节（默认未启用） |
| `server/tests/V14Subscribe*.cs`（新 3 个）、`V11TestBase.cs`、`server/scripts/smoke.sh` | 测试（§6） |
| `miniprogram/` | `utils/subscribe.js`、「我的」页的「工单提醒」、首页提示条、点击时静默攒额度（§8），`config.js` 版本号 1.4.0 |
| `deploy/huotong-aftersales.env.example` | 订阅消息配置的示例（注释掉的） |

## 2. 配置

`appsettings.json` 的 `WeChat` 节下（模板 ID 不是机密，可以进仓库；AppSecret 仍然只走环境变量）：

```json
"Subscribe": {
  "TemplateId": "",
  "MiniProgramState": "trial",
  "Page": "pages/order-detail/index?id={id}",
  "Fields": {}
}
```

- **已启用** = `TemplateId` 非空 且 `Fields` 至少一项。未启用时不发、不查库、不记任何日志。
- `MiniProgramState`：点开消息进入哪个版本，`developer` 开发版 / `trial` 体验版 / `formal` 正式版，原样传给微信。**小程序正式发布后要改成 `formal`**。
- `Page`：点开消息进入的页面，`{id}` 替换为工单 id。
- `Fields`：键 = 模板详情里的字段键（`thing1`、`character_string2`、`time3`、`phrase4`、`name5`…，以小程序后台模板详情页显示的为准），值 = 下表的槽名（不区分大小写）。模板里的**每一个字段都要映射**，漏掉的字段微信会拒收（`47003`）。
- 配置写错（槽名不认识、`MiniProgramState` 不是三个值之一、只配了一半）时启动日志有一条「警告：WeChat:Subscribe 配置有误：…」，不阻止启动。

| 槽名 | 内容 | 例 |
|---|---|---|
| `Title` | 事项 | 新工单指派给你 |
| `OrderNo` | 工单号 | SH-20261106-001 |
| `Customer` | 客户名称 | 苏州某某新材料有限公司 |
| `Content` | 说明，一句话 | 赵主管把工单指派给你，请及时处理 |
| `Time` | 事件发生时间 | 2026年11月06日 13:42 |
| `Operator` | 操作人姓名 | 赵主管 |
| `Status` | 动作之后的工单状态 | 待启动 |
| `Device` | 设备汇总文字 | 高压反应釜 HTR-250 |

槽位集合是固定的，**没有价格**；任何消息都不包含价格、价格建议、受限附件名。

### 选好模板后怎么填

在小程序后台「订阅消息 → 我的模板」点开选定的模板，「详细内容」里每一行形如 `事项主题 {{thing1.DATA}}`：花括号里 `.DATA` 前面的那一段（`thing1`）就是字段键，前面的中文是它的含义。
按含义给每个字段挑一个槽名：事项 / 主题 → `Title`，单号 / 编号 → `OrderNo`，客户 → `Customer`，备注 / 说明 / 内容 → `Content`，时间 → `Time`，处理人 / 操作人 → `Operator`，状态 → `Status`，设备 → `Device`。
字段键的序号不一定连续（可能是 `thing1`、`character_string5`、`time12`），**照抄模板详情里的，不要自己推**。

下面以一个常见组合为例（模板 ID 与字段键是示意，要换成实际选定模板的）：

| 模板详情里的一行 | 字段键 | 槽名 |
|---|---|---|
| 事项主题 `{{thing1.DATA}}` | `thing1` | `Title` |
| 工单编号 `{{character_string2.DATA}}` | `character_string2` | `OrderNo` |
| 客户名称 `{{thing3.DATA}}` | `thing3` | `Customer` |
| 时间 `{{time4.DATA}}` | `time4` | `Time` |

**写法一：服务器上的环境变量文件**（`/etc/huotong-aftersales.env`，推荐：更新程序包不会覆盖它，改完 `systemctl restart huotong-aftersales` 即生效）。每行一项，等号两边不要空格、不要加引号：

```
WeChat__Subscribe__TemplateId=k3Jx0aBcDeFgHiJkLmNoPqRsTuVwXyZ0123456789ab
WeChat__Subscribe__MiniProgramState=trial
WeChat__Subscribe__Fields__thing1=Title
WeChat__Subscribe__Fields__character_string2=OrderNo
WeChat__Subscribe__Fields__thing3=Customer
WeChat__Subscribe__Fields__time4=Time
```

**写法二：仓库里的 `appsettings.json`**（`WeChat` 节下；要重新打包、上传、安装才生效。服务器上直接改 `appsettings.json` 没用，下次更新会被程序包覆盖）：

```json
"Subscribe": {
  "TemplateId": "k3Jx0aBcDeFgHiJkLmNoPqRsTuVwXyZ0123456789ab",
  "MiniProgramState": "trial",
  "Page": "pages/order-detail/index?id={id}",
  "Fields": {
    "thing1": "Title",
    "character_string2": "OrderNo",
    "thing3": "Customer",
    "time4": "Time"
  }
}
```

- 模板有第 5 个字段（如 `备注 {{thing5.DATA}}`）就再加一行：`WeChat__Subscribe__Fields__thing5=Content` / `"thing5": "Content"`。
- **两种写法只用一种**。`Fields` 在多个配置来源之间是合并的：环境变量只能加键、改值，不能删掉 `appsettings.json` 里已有的键；两边各写一套不同的字段键，合起来会多出模板里没有的字段，微信拒收（`47003`）。
- 换模板时把旧的 `Fields` 行全部删掉再写新的。
- 生效与否的检查：重启后启动日志里没有「警告：WeChat:Subscribe 配置有误」；小程序重新进入后「我的」页出现「工单提醒」一行（即 `GET /api/me/subscribe` 返回 `enabled: true`）。
- 体验版测试期间 `MiniProgramState` 用 `trial`；小程序正式发布后改成 `formal` 并重启，否则点开消息进的还是体验版。

**字段清洗**（按字段键的前缀；微信对每种字段的取值有格式限制，不合规整条消息被拒）：

| 前缀 | 规则 | 清洗后为空时 |
|---|---|---|
| `thing` | 20 字符以内，超出截成 19 字 + 「…」；换行等空白压成一个空格 | `-` |
| `character_string` | 只留数字 / 字母 / 英文符号，32 位以内 | `-` |
| `phrase` | 只留汉字，5 个以内（超出取前 5 个） | `无` |
| `name` | 含汉字：只留汉字与字母，10 字以内；不含汉字：只留字母与 `空格 . - ·`，20 字以内 | `无` |
| `number` | 只留数字，32 位以内 | `0` |
| `time` | 固定填事件时间 `yyyy年MM月dd日 HH:mm`（不管映射到哪个槽） | — |
| `date` | 固定填事件日期 `yyyy年MM月dd日`（不管映射到哪个槽） | — |
| 其它前缀 | 20 字符以内 | `-` |

所有文字字段在清洗前先去掉表情、零宽字符等微信不一定接受的字符（常用的 `℃`、`→`、括号等符号保留）；只剩这类字符的值按空值给占位。

选模板的建议：`Title`、`Content`、`Customer`、`Device` 适合放进 `thing` 字段；`OrderNo` 适合 `character_string`；`Status` 适合 `phrase`（所有状态都在 5 个汉字以内）；`Operator` 适合 `name`；
`phrase` 字段不要映射 `Title` / `Content`（会被截成 5 个汉字）。

## 3. 事件 → 收件人 → 内容

所有消息的 `OrderNo` / `Customer` / `Device` 取自工单，`Time` = 动作时间，`Operator` = 操作人姓名，`Status` = 动作之后的状态。下表只列 `Title` 与 `Content`。

| 事件（接口） | 收件人 | Title | Content |
|---|---|---|---|
| 非主管建单（`POST /orders`） | 全部主管 | 新工单待指派 | `{建单人}新建{现场/远程/返厂}工单，待指派`；紧急单前面加 `【紧急】` |
| 提交完工（`complete`） | 全部主管 | 工单待验收 | `{工程师}提交了完工，请验收` |
| 申请暂停（`hold-requests`） | 全部主管 | 有申请待审批 | `{工程师}申请暂停：{暂停原因}` |
| 申请协助人（`assistant-requests`） | 全部主管 | 有申请待审批 | `{工程师}申请添加协助人{对方}` |
| 主管指派（`assign`，待认领的单） | 新负责人 | 新工单指派给你 | `{主管}把工单指派给你，请及时处理` |
| 主管改派（`assign`，已有负责人） | 新负责人 | 新工单指派给你 | `{主管}把工单改派给你，请及时处理` |
| 改派使待确认的转交失效 | 转交发起人（原负责人） | 转交已失效 | `主管已改派，转交自动失效` |
| 主管加协助人（`assign` 带协助人、`assistants`） | 每个新加的协助人 | 你被加为协助人 | `{主管}安排你协助，负责人{负责人}` |
| 同意协助申请（`requests/{id}/approve`） | 申请人 | 协助申请已同意 | `{主管}已同意，{对方}加入协助`（对方本来就是参与人时：`{对方}已是本单参与人`） |
| 同上 | 被加的协助人 | 你被加为协助人 | `{主管}同意你协助，负责人{负责人}` |
| 驳回协助申请（`requests/{id}/reject`） | 申请人 | 协助申请被驳回 | `驳回原因：{回复}`；没写回复：`{主管}驳回了你的申请` |
| 同意暂停申请 | 申请人 | 暂停申请已同意 | `{主管}已同意，工单已暂停搁置` |
| 驳回暂停申请 | 申请人 | 暂停申请被驳回 | 同「驳回协助申请」 |
| 发起转交（`transfers`） | 接手人 | 有工单转交给你，请确认 | `{发起人}想把工单转交给你：{原因}` |
| 确认接手（`transfers/{id}/accept`） | 发起人 | 转交已被接手 | `{接手人}已接手，你已退出本单` |
| 拒绝接手（`transfers/{id}/reject`） | 发起人 | 转交被拒绝 | `{接手人}拒绝接手：{理由}`；没写理由：`{接手人}拒绝接手，工单仍由你负责` |
| 主管撤销转交（`transfers/{id}/cancel`） | 发起人 | 转交已失效 | `{主管}撤销了转交，工单仍由你负责` |
| 驳回完工（`reject`） | 负责人 | 完工被驳回，请重新处理 | `退回原因：{原因}`；没写原因：`{主管}退回了完工资料` |
| 主管暂停（`hold`） | 负责人 | 工单已暂停 | `暂停原因：{暂停原因}` |
| 主管恢复（`resume`） | 负责人 | 工单已恢复处理 | `{主管}已恢复工单，可继续处理` |
| 确认闭环（`close`） | 建单人（销售或工程师） | 工单已闭环 | `{主管}已确认闭环` |

规则：
- **收件人过滤**（统一在 `SubscribeService` 里做）：启用状态、已绑定微信（OpenId 非空）、**不是操作人本人**。「全部主管」= 启用且已绑定微信的主管。
- **一次动作里同一个人最多一条**。唯一会撞上的情况是改派：顺序为 新负责人 → 新协助人 → 转交发起人，所以改派时把原负责人加为协助人，他只收「你被加为协助人」，不再收「转交已失效」。
- **不发的情况**：认领、预约、开始处理、打卡、作废、定价、价格建议、附件、SAT、返厂节点；发起人自己撤回转交；主管自己建的单（不提醒主管）；主管建的单闭环（不提醒主管）；
  作废、接手人被停用导致的转交失效（没有列入本期事件）。
- 所有提醒都在业务事务**提交之后**发；发送失败、超时只记日志，不影响接口返回。一次动作的全部发送（多个收件人并行，含取 token）最多等 5 秒。
- 后端不记录谁订阅了、还剩几条：直接发，对方没订阅或额度用完时微信返回 `43101`，记一条 Information 日志。

## 4. 微信调用与日志

- access_token：`POST https://api.weixin.qq.com/cgi-bin/stable_token`（`grant_type=client_credential`、`appid`、`secret`，不用强制刷新），内存缓存到过期前 5 分钟，并发时只取一次。
  取不到 token（密钥不对、服务器 IP 不在小程序后台的白名单里、网络不通）时这一次动作整批不发：只请求一次、只记一条 Warning。
  与手机号验证（`WeChatService`，仍用原来的 `cgi-bin/token`）各用各的 token：微信保证这两个接口的 token 互相隔离、互不影响，所以登录 / 绑定流程没有任何改动。
- 发送：`POST https://api.weixin.qq.com/cgi-bin/message/subscribe/send?access_token=…`，body 为 `touser`、`template_id`、`page`、`miniprogram_state`、`lang=zh_CN`、`data: { 字段键: { value } }`。
- `40001` / `42001` / `40014`（token 失效）：清缓存、重新取、重试一次。`43101`（没订阅 / 额度用完）、`43107`：Information。其它错误码、网络异常、超时：Warning。成功：Information「订阅消息已发送：{事项} → {姓名}」。
- `WeChat:MockMode=true`：不发任何网络请求，把将要发送的消息记在内存里（`SubscribeService.MockSent`，测试用），并打一条 `[订阅消息·Mock]` 日志，可以直接在日志里看每条消息的收件人与字段。
- 非 MockMode 且没配 `AppId` / `AppSecret`：不发（启动时已有警告）。
- **日志里不出现 access_token 与 AppSecret**：错误日志只记错误码与说明并做脱敏。另外去掉了 `wechat`、`dingtalk` 两个 HttpClient 自带的请求日志
  ——.NET 8 默认会在 Information 级把完整请求地址写进日志，而 access_token（以及原有登录接口的 AppSecret、钉钉 webhook 的签名）就在地址的查询串里。

## 5. 接口

`GET /api/me/subscribe`（任何已登录角色）→ `{ "enabled": bool, "templateIds": string[] }`，未启用时 `{ "enabled": false, "templateIds": [] }`。详见 `docs/API.md` §2。

## 6. 测试

- `V14SubscribeFieldTests`：各前缀的清洗 / 截断 / 占位、时间格式、槽名解析、`Build`、「已启用」的判定、配置检查。
- `V14SubscribeEventTests`：§3 每个事件的收件人与内容；不发给自己；未绑定微信 / 停用不发；未启用不发；全流程消息里没有价格；发送抛异常业务照常成功；提醒在事务提交之后才发；配置接口的返回形状。
- `V14SubscribeHttpTests`（假的 `HttpMessageHandler`，不联网）：请求形状、token 只取一次（含并发）、过期重取、`40001` / `42001` 重试一次、错误分级、异常与超时不抛、日志里没有 token 与密钥。
- `smoke.sh`：新增「v1.4 订阅消息配置」4 项（未登录 401、字段形状、三种角色一致）。

## 7. 上线要做的事

后端与小程序可以分开发、先后不限：后端没配模板时新小程序不显示任何提醒入口；旧小程序不调新接口。没有数据库迁移，nginx 不用改。

1. 后端照常更新（`deploy/build.sh` 打包 → 上传 → `install.sh`）。此时订阅消息还是未启用，行为与 v1.3 一致。
2. 小程序上传 1.4.0 并设为体验版。
3. 小程序后台「订阅消息」里选一个一次性订阅模板（类目 工具 > 办公），记下模板 ID 和模板详情里每个字段的键。
4. 按 §2「选好模板后怎么填」把配置写进 `/etc/huotong-aftersales.env`，`systemctl restart huotong-aftersales`。小程序重新进入后「我的」页出现「工单提醒」即生效（不用重新发布小程序）。
5. 体验版测试期间 `MiniProgramState` 用 `trial`；小程序正式发布后改成 `formal` 并重启。
6. 每个人要在小程序里开启一次「工单提醒」并勾选「总是保持以上选择」；没开启的人收不到（日志里是 `43101`）。
7. 真机验证清单（代码无法覆盖的部分）：模板字段与配置对得上（配错时日志里有 `47003` 及出错的字段名）；点开消息能进到工单详情；`trial` / `formal` 与实际版本一致；§8 末尾列的小程序端各项。

排查：`journalctl -u huotong-aftersales | grep 订阅消息`。`40164` = 服务器 IP 不在小程序后台的 IP 白名单（与手机号验证用的是同一个白名单）；`47003` = 字段映射与模板对不上；`43101` = 对方没订阅或额度用完（正常）。

**关于旧日志**：v1.4 之前，微信登录（`jscode2session`）与取 token 的请求地址里带着 AppSecret，而 .NET 8 的 HttpClient 默认会把完整请求地址写进 Information 级日志，
所以服务器上 v1.4 之前的 `journalctl` 日志里很可能有 AppSecret 明文。v1.4 起不再记录（§4）。建议升级后在小程序后台重置一次 AppSecret，并更新 `/etc/huotong-aftersales.env` 里的 `WeChat__AppSecret`、重启。

## 8. 小程序端

- 所有订阅逻辑集中在 `utils/subscribe.js`；`wx.requestSubscribeMessage` 全小程序只在这一个文件里调用。配置由 `auth.ensureLogin()` 成功后顺带拉取并缓存 10 分钟，授权状态（`wx.getSetting({ withSubscriptions: true })`）在每个页面 onShow 时刷新。
- 授权状态：`always` 已勾「总是保持以上选择」并允许 / `rejected` 已勾「总是」并拒绝 / `off` 通知总开关已关 / `ask` 还没做过「总是」选择。
- 入口一：「我的 → 工单提醒」，右侧显示 已开启 / 未开启 / 已关闭。`ask` → 先弹说明，确认后调起授权弹窗；`rejected` / `off` → 引导去设置页（`wx.openSetting`）；`always` → 提示「提醒已开启」。
  用户只点了「允许」没勾「总是」时，会提示「这次只能收到一条」，请他再开启一次并勾选。
- 入口二：三种角色首页顶部的提示条「开启工单提醒，指派和审批结果第一时间通知你」+「去开启」，只在 `ask` 时显示；点关闭后 7 天内不再出现（本机记住）。
- 静默攒额度（`topUp`）：**只有**缓存状态已是 `always`（此时不会弹窗）才调用，两次之间至少隔 1.5 秒，没攒上就暂停 60 秒。通过 `subscribe.bindTaps` 挂在这些点击上：
  底部 tab 切换与中间按钮；三种首页、全部工单、我的工单里点进工单详情；认领；首页的去打卡 / 去完工；打卡提交；提交完工；
  工单详情的底部操作按钮、转交的确认 / 拒绝 / 撤回、暂停、申请的同意 / 驳回、定价并闭环；指派面板的确认（指派 / 改派 / 调整协助人 / 申请协助 / 发起转交）。
  它不等待结果、不改变原有点击的行为与时序。
- 后端未启用、接口失败或基础库不支持时：入口行与提示条都不显示，不调用任何微信订阅接口。
- 小程序不能在本机运行，以下只能真机验证：在说明弹窗的回调里能否调起授权弹窗（iOS 与 Android）；勾了「总是」之后各处点击是否完全无感；
  授权弹窗关闭后立刻查到的状态是否已是新值；设置页里的名称是否是「通知管理 / 接收通知」；提示条在小屏上的排版；从服务通知冷启动进入工单详情。
