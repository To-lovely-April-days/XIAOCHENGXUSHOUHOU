/**
 * 运行配置。
 * baseUrl：后端地址（不带 /api），按小程序版本自动选择：
 *   - 开发版（开发者工具 / 真机调试）：devBaseUrl
 *       开发者工具：http://localhost:5080（需勾选「不校验合法域名」）
 *       手机真机调试：改为电脑的局域网 IP，如 http://192.168.50.146:5080（手机与电脑同一网络）
 *   - 体验版 / 正式版：prodBaseUrl（已在小程序后台配置为 request/uploadFile/downloadFile 合法域名）
 * devLogin：是否在登录页显示「开发调试：输入手机号直接登录」（对应后端 WeChat:MockMode=true 的 /api/auth/dev-login）。正式环境保持 false。
 */
const devBaseUrl = 'http://localhost:5080';
const prodBaseUrl = 'https://shouhou.sh-htlab.com';

function envVersion() {
  try {
    return wx.getAccountInfoSync().miniProgram.envVersion || 'release';
  } catch (e) {
    return 'release';
  }
}

const env = envVersion();

module.exports = {
  env,
  baseUrl: env === 'develop' ? devBaseUrl : prodBaseUrl,
  devLogin: false,
  version: '1.0.0'
};
