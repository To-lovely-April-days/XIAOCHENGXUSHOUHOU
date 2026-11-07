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
3. 登录页用「开发调试：输入手机号直接登录」：
   - 工程师 李工 13900000000（陈工 …02、刘工 …03、王工 …04）
   - 销售 王敏 13900000010（孙磊 …11）
   - 主管 赵主管 13900000001

4. 主管电脑后台：浏览器打开 http://localhost:5080/admin/ ，用 赵主管 13900000001 / 密码 `Huotong@2026`（仅开发环境种子密码）登录。

上线前：`config.js` 改 `devLogin=false`、`baseUrl` 换 https 域名；`project.config.json` 换真实 appid；后端配 `WeChat:AppId/AppSecret`、`WeChat:MockMode=false`、新的 `Jwt` 密钥。
