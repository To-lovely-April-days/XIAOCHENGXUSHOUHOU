# v1.5 账号密码登录、小程序注册、主管审批

## 变化

- 小程序登录改为 **手机号 + 密码**；去掉「手机号快速验证」和按微信身份静默登录（旧接口 `/api/auth/login`、`/api/auth/bind` 仍保留给旧版本客户端）。
- 小程序新增 **注册**：姓名 / 手机号 / 密码 / 确认密码，提交后等主管审批。
- 电脑后台「账号管理」新增 **注册申请**：通过（必须选角色，可改姓名、填部门）/ 驳回（必须填原因）。菜单上显示待审批数量。
- 主管直接「新增账号」时可顺带填初始密码；这样建的账号不用审批。
- 同一套手机号 + 密码同时用于小程序和电脑后台（电脑后台仍只允许售后主管）。
- 小程序「我的」新增「修改密码」；忘记密码由主管在后台重置。
- 主管小程序首页在有待审批账号时显示提示（审批操作只在电脑后台）。

## 数据

迁移 `V15_Registrations`：新增 `Registrations` 表（Name, Phone 唯一, PasswordHash, OpenId, Status = Pending/Approved/Rejected, RejectReason, ReviewedById, ReviewedAt, EmployeeId, CreatedAt, SubmittedAt）。不改动已有表和数据。

注册申请不写进 `Employees`，审批通过才生成员工（密码哈希原样转过去，申请行里清空），所以没通过的人不会出现在任何员工列表里。一个手机号只有一行申请，驳回后重新提交复用这一行。

## 接口

| 接口 | 说明 |
|---|---|
| `POST /api/auth/register` | `{name, phone, password, code}`，code 为 `wx.login` 的 code（必填）。返回 `{status:'Pending', message}` |
| `POST /api/auth/account-login` | `{phone, password, code?}`。成功同 `LoginResult`；有 code 时把当前微信绑到账号上（订阅消息用） |
| `POST /api/me/logout` | 解除微信绑定 |
| `GET /api/admin/registrations` | `{pendingCount, items}`：待审批的全部 + 最近处理的 50 条 |
| `POST /api/admin/registrations/{id}/approve` | `{role, name?, department?}` |
| `POST /api/admin/registrations/{id}/reject` | `{reason}` |
| `POST /api/admin/employees` | 新增可选字段 `password`（初始密码） |

`GET /api/admin/overview` 与 `GET /api/home/supervisor` 新增 `pendingRegistrations`。

登录错误码：`40310` 账号审核中；`40311` 注册被驳回（message 带原因）；`40300` 已停用；`40000` 手机号或密码错误 / 尝试过多。只有密码对得上注册申请时才返回 40310 / 40311，否则一律「手机号或密码错误」。

## 规则

- 密码：至少 8 位，同时含字母和数字（沿用原规则）。
- 同一手机号连续输错 5 次锁 10 分钟（沿用，与电脑后台共用计数）。
- 注册：同一个微信一天最多提交 3 次；待审批总数上限 200；手机号已有账号或已在审批中时拒绝。
- 手机号不做短信验证，靠主管审批时核对。
- 微信绑定：登录时自动绑到当前微信；同一个微信换登别的账号，绑定跟着走；退出登录时解绑。微信接口不可用不影响登录，只是暂时收不到提醒。
- 登录态 30 天（`Jwt:ExpireDays`），到期或主动退出后重新输入密码。
