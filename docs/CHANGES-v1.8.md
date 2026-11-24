# v1.8 小程序里审批注册 + 新申请微信提醒

## 变化

- 新权限 **注册审批权限**（`Employee.CanApprove`），在电脑后台「账号管理」的新增 / 编辑弹窗里开关。
- 小程序新增页面 `pages/approvals/index`（注册审批）：待审批列表、通过（姓名 / 岗位 / 部门）、驳回（原因）、已处理记录。
  入口：「我的 → 注册审批」（带待办数）；售后主管首页的「有 N 个账号待审批」横幅可点。
- 谁能在小程序里审批：管理员，或开了注册审批权限的人（`user.canApprove`）。
- 小程序里审批 **不能** 授予管理员岗位 / 管理员权限（`40000 管理员权限请到电脑后台设置`）。
- 有人提交注册时，给 **开了注册审批权限且已绑定微信** 的人发订阅消息，点开进入审批页。只有管理员权限、没单独开这个开关的人不提醒。

## 配置（提醒要单独一个模板，没配就不发，其余功能不受影响）

```json
"WeChat": { "Subscribe": { "Register": {
  "TemplateId": "<模板 ID>",
  "Page": "pages/approvals/index",
  "Fields": { "<微信字段键>": "<槽名>" }
} } }
```

槽名沿用工单提醒的：`Customer` / `Operator` = 申请人姓名，`OrderNo` = 申请人手机号，`Content` = 「某某 申请注册账号，请审批」，
`Title` = 「新的注册申请」，`Status` = 「待审批」，`Time` = 提交时间。跳转的小程序版本沿用 `WeChat:Subscribe:MiniProgramState`。

`GET /api/me/subscribe` 对有注册审批权限的人多返回这个模板 ID，小程序的「工单提醒 / 新申请提醒」一次授权两个模板。

## 接口

| 接口 | 说明 |
|---|---|
| `GET /api/registrations` | 同 `/api/admin/registrations`，授权策略 `Approver` |
| `POST /api/registrations/{id}/approve` | `{name?, position, department}`；不接受 `position=Admin` / `isAdmin=true` |
| `POST /api/registrations/{id}/reject` | `{reason}` |

`UserDto.canApprove`（管理员或有开关）、`EmployeeDto.canApprove`（开关本身）、`MeDto.pendingRegistrations`（不能审批时为 null）。
`POST/PUT /api/admin/employees` 新增 `canApprove`（不传保持原值）。迁移 `V18_CanApprove`。

## 审批结果通知申请人

- 申请人点「提交注册」时，小程序先弹一次订阅授权（`GET /api/auth/subscribe` 返回的模板，公开接口），同意与否都继续提交。
- 审批通过或驳回后，按提交注册时记下的微信（`Registration.OpenId`）发一条结果：
  通过 → 「审批已通过，请用手机号和密码登录」，点开进登录页；驳回 → 「未通过：原因」。
- 配置 `WeChat:Subscribe:Result`（`TemplateId` / `Page` 默认 `pages/login/index` / `Fields`），槽名：`Status` = 已通过 / 未通过，
  `Content` = 上面那句话，`Customer` / `Operator` = 申请人姓名，`OrderNo` = 手机号，`Time` = 审批时间，`Title` = 注册审批结果。没配就不发。
- 一次性订阅：一次授权只能收一条。被驳回后重新提交时会再弹一次授权。
