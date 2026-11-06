/**
 * 运行配置。
 * baseUrl：后端地址（不带 /api）。
 *   - 开发者工具：http://localhost:5080（需勾选「不校验合法域名」）
 *   - 真机调试：改为电脑的局域网 IP，如 http://192.168.1.20:5080（手机与电脑同一 Wi-Fi）
 *   - 上线：必须为已在小程序后台配置的 https 域名
 * devLogin：是否在登录页显示「开发调试：输入手机号直接登录」（对应后端 WeChat:MockMode=true 的 /api/auth/dev-login）。上线前改为 false。
 */
module.exports = {
  baseUrl: 'http://localhost:5080',
  devLogin: true,
  version: '1.0.0'
};
