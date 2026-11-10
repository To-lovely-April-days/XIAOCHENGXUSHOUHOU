# 霍桐售后工单小程序

- `docs/` — 设计说明 `DESIGN.md`、接口契约 `API.md`、解包后的原型 `prototype/`
- `server/` — .NET 8 Web API（详见 `server/README.md`）
- `miniprogram/` — 微信小程序（详见 `miniprogram/README.md`）

## 本地跑起来

1. 启动后端（首次自动建库并写入演示数据，端口 5080，Swagger: http://localhost:5080/swagger）：
   ```bash
   cd server/src/Huotong.AfterSales.Api && dotnet run
   ```
2. 微信开发者工具 → 导入 `miniprogram` 目录 → 详情里勾选「不校验合法域名」。
3. 真实微信登录（小程序 AppID `wx83d09abacc984098`，后端已关闭 MockMode）：
   - 首次在后端目录配置 AppSecret（只存在本机，不进仓库）：
     ```bash
     cd server/src/Huotong.AfterSales.Api && dotnet user-secrets set "WeChat:AppSecret" "<小程序后台生成的 AppSecret>"
     ```
   - 先用主管账号登录电脑后台，在「账号管理」里把自己（真实姓名 + 真实手机号 + 角色）加进去，再在小程序里点「手机号快速验证」登录。
   - 演示数据里的 139 号段账号只能在电脑后台使用；如需恢复「开发调试：输入手机号直接登录」，把 `appsettings.Development.json` 的 `WeChat:MockMode` 和 `miniprogram/config.js` 的 `devLogin` 都改回 `true`。
4. 主管电脑后台：浏览器打开 http://localhost:5080/admin/ ，用 赵主管 13900000001 / 密码 `Huotong@2026`（仅开发环境种子密码）登录。

上线前：`config.js` 的 `baseUrl` 换成已备案并配置到小程序后台「服务器域名」的 https 域名；服务器用环境变量配置 `WeChat__AppSecret`、新的 `Jwt__Key`、`Bootstrap__SupervisorPassword`。
