# v1.7 岗位、固定部门、管理员权限

## 变化

- **岗位**（显示用的角色）：售后工程师、销售、**销售主管**（新）、售后主管、**管理员**（新）。
- **管理员权限**：一个独立的开关。有它才能登录电脑后台、管理账号、审批注册。管理员岗位自带；其他岗位可以单独勾选（例如「售后主管 + 管理员」）。
- **部门**：固定四个 —— 售后部门、销售部门、软件部门、总经办，新增 / 审批时必选。旧数据里的老写法在不改动时原样保留。
- **销售主管**：权限同销售，另外能看到所有销售 / 销售主管建的工单和价格；不能指派、审批、验收。
- **售后主管（不带管理员权限）**：小程序里照常指派、审批、定价、验收，但不能登录电脑后台，也调不了账号管理和注册审批接口。

## 实现

权限判断仍然只看 `Employee.Role`（Engineer / Sales / Supervisor），所以原有的全部业务规则不变。新增：

- `Employee.Position`（Engineer / Sales / SalesManager / Supervisor / Admin）与 `Employee.IsAdmin`。
  `Role` 由岗位推出：Sales、SalesManager → Sales；Supervisor、Admin → Supervisor。
- 迁移 `V17_PositionAdmin`：加两列；旧数据 `Position = Role`，原来的售后主管全部 `IsAdmin = 1`（升级后没人被锁在后台外面）。
- 授权策略 `Admin`（声明 `adm=1`）：`/api/admin/employees*`、`/api/admin/registrations*`。`/api/auth/password-login`（电脑后台登录）要求 `IsAdmin`。
  其余 `/api/admin/*` 报表接口仍是售后主管即可。
- `adm` / `sm`（销售主管）两个声明每次请求按数据库重算，升级前签发的旧令牌不用重新登录；岗位或管理员权限变更时 `TokenVersion` 加一。

## 接口

- `EmployeeDto` / `UserDto` 新增 `position`、`isAdmin`；`roleName` 改为岗位名称；`role` 含义不变。
- `POST/PUT /api/admin/employees`、`POST /api/admin/registrations/{id}/approve`：新增 `position`、`isAdmin`；`department` 必须是四个固定部门之一。
  只传 `role` 的旧客户端仍可用（按 role 推岗位，`isAdmin` 保持原值）。
- 新增错误：`40000 请选择部门` / `部门不在可选范围内`；`40300 该账号没有电脑后台的管理员权限`；
  `40900 不能停用自己、修改自己的角色或取消自己的管理员权限`。
