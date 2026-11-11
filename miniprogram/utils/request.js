/**
 * 网络请求封装。所有接口返回 { code, message, data }，这里统一解包：
 * - code === 0 → resolve(data)
 * - 401 / 40100 → 清 token，reLaunch 到登录页（防抖），reject
 * - 其他 → wx.showToast(message)（silent 时不弹），reject(ApiError)
 *
 * ApiError 结构：{ code:number, message:string, httpStatus:number, data:any }
 * 网络失败时 code = -1。
 */
const config = require('../config.js');

const TOKEN_KEY = 'token';
const LOGIN_PAGE = '/pages/login/index';

let redirectingToLogin = false;

function buildUrl(url) {
  if (/^https?:\/\//.test(url)) return url;
  return config.baseUrl.replace(/\/+$/, '') + (url.charAt(0) === '/' ? url : '/' + url);
}

/** 当前 token（优先内存，其次 storage） */
function getToken() {
  const app = getApp && getApp();
  if (app && app.globalData && app.globalData.token) return app.globalData.token;
  try {
    return wx.getStorageSync(TOKEN_KEY) || '';
  } catch (e) {
    return '';
  }
}

/** 清除登录态（不跳转） */
function clearToken() {
  try { wx.removeStorageSync(TOKEN_KEY); } catch (e) { /* ignore */ }
  const app = getApp && getApp();
  if (app && app.globalData) {
    app.globalData.token = '';
    app.globalData.user = null;
  }
}

/** token 失效：清 token 并跳登录页，短时间内只跳一次 */
function redirectToLogin() {
  clearToken();
  if (redirectingToLogin) return;
  const pages = getCurrentPages();
  const cur = pages[pages.length - 1];
  if (cur && '/' + cur.route === LOGIN_PAGE) return;
  redirectingToLogin = true;
  wx.reLaunch({
    url: LOGIN_PAGE,
    complete() {
      setTimeout(() => { redirectingToLogin = false; }, 1500);
    }
  });
}

/** 根据 wx.request/uploadFile 的 fail errMsg 给出可排查的提示 */
function netErrorMessage(err, fallback) {
  const msg = String((err && err.errMsg) || '');
  console.error('[request fail]', config.baseUrl, msg);
  if (/domain list|合法域名|not in domain/i.test(msg)) return '请求域名未通过校验，请联系管理员';
  if (/timeout|超时/i.test(msg)) return '请求超时，请检查网络后重试';
  if (/refused|ECONNREFUSED|connect|ERR_/i.test(msg)) return '无法连接服务器，请检查网络后重试';
  return fallback;
}

function makeError(code, message, httpStatus, data) {
  return { code: code, message: message || '请求失败', httpStatus: httpStatus || 0, data: data === undefined ? null : data };
}

function toast(message) {
  wx.showToast({ title: message || '请求失败', icon: 'none', duration: 2500 });
}

/** GET 参数去掉 null/undefined/''，避免变成 "null" */
function cleanQuery(data) {
  if (!data || typeof data !== 'object') return data;
  const out = {};
  Object.keys(data).forEach((k) => {
    const v = data[k];
    if (v !== null && v !== undefined && v !== '') out[k] = v;
  });
  return out;
}

/**
 * 统一处理包装体。
 * @returns {{ok:boolean, data?:any, error?:object}}
 */
function handleBody(statusCode, body) {
  if (typeof body === 'string') {
    try { body = JSON.parse(body); } catch (e) { /* 非 JSON */ }
  }
  if (statusCode === 401 || (body && body.code === 40100)) {
    redirectToLogin();
    return { ok: false, error: makeError(40100, (body && body.message) || '登录已失效，请重新登录', statusCode, null), auth: true };
  }
  if (body && typeof body === 'object' && typeof body.code === 'number') {
    if (body.code === 0) return { ok: true, data: body.data === undefined ? null : body.data };
    return { ok: false, error: makeError(body.code, body.message, statusCode, body.data) };
  }
  if (statusCode >= 200 && statusCode < 300) return { ok: true, data: body };
  return { ok: false, error: makeError(statusCode * 100, '服务器异常（' + statusCode + '）', statusCode, null) };
}

/**
 * 发起请求。
 * @param {Object} options
 * @param {string} options.url 以 /api 开头的路径，或完整 URL
 * @param {'GET'|'POST'|'PUT'|'DELETE'} [options.method='GET']
 * @param {Object} [options.data] GET 时作为 query，其余作为 JSON body
 * @param {boolean} [options.silent=false] true 时出错不弹 toast（401 仍会跳登录）
 * @param {boolean|string} [options.loading=false] 显示 wx.showLoading，传字符串作为文案
 * @param {boolean} [options.auth=true] 是否携带 token
 * @returns {Promise<any>} 解包后的 data
 */
function request(options) {
  const method = (options.method || 'GET').toUpperCase();
  const header = { 'content-type': 'application/json' };
  const token = options.auth === false ? '' : getToken();
  if (token) header.Authorization = 'Bearer ' + token;
  const loading = options.loading;
  if (loading) wx.showLoading({ title: typeof loading === 'string' ? loading : '加载中', mask: true });

  return new Promise((resolve, reject) => {
    wx.request({
      url: buildUrl(options.url),
      method: method,
      data: method === 'GET' ? cleanQuery(options.data) : (options.data || {}),
      header: header,
      timeout: options.timeout || 20000,
      success(res) {
        if (loading) wx.hideLoading();
        const r = handleBody(res.statusCode, res.data);
        if (r.ok) return resolve(r.data);
        if (!options.silent && !r.auth) toast(r.error.message);
        reject(r.error);
      },
      fail(err) {
        if (loading) wx.hideLoading();
        const e = makeError(-1, netErrorMessage(err, '网络异常，请检查网络后重试'), 0, err);
        if (!options.silent) toast(e.message);
        reject(e);
      }
    });
  });
}

/** GET 便捷方法 */
function get(url, data, opts) {
  return request(Object.assign({}, opts, { url: url, method: 'GET', data: data }));
}

/** POST 便捷方法 */
function post(url, data, opts) {
  return request(Object.assign({}, opts, { url: url, method: 'POST', data: data }));
}

/** PUT 便捷方法 */
function put(url, data, opts) {
  return request(Object.assign({}, opts, { url: url, method: 'PUT', data: data }));
}

/** 进度回调包装：百分比取整，变化时才回调 */
function bindProgress(task, onProgress) {
  if (!task || typeof onProgress !== 'function' || !task.onProgressUpdate) return;
  let last = -1;
  task.onProgressUpdate((res) => {
    const p = Math.max(0, Math.min(100, Math.round(Number(res && res.progress) || 0)));
    if (p === last) return;
    last = p;
    onProgress(p);
  });
}

/**
 * 上传文件到 POST /api/files。
 * @param {string} filePath 本地临时路径
 * @param {'OrderPhoto'|'PunchPhoto'|'ProcessPhoto'|'Signature'|'Avatar'|'FactoryReceivePhoto'|'FactoryShipPhoto'|'FactorySignPhoto'|'Attachment'} category
 * @param {{silent?:boolean, loading?:boolean|string, fileName?:string, timeout?:number,
 *   onProgress?:function(number):void, onTask?:function(Object):void}} [opts]
 *   fileName：原始文件名（手里有真实文件名时传，如 wx.chooseMessageFile 的 name）；timeout 默认 60000；
 *   onProgress(percent 0–100)；onTask(UploadTask) 可用于 abort
 * @returns {Promise<{id:number,url:string,category:string,mimeType:string,createdAt:string,name:string,size:number,kind:string,ext:string}>} FileDto
 */
function uploadFile(filePath, category, opts) {
  opts = opts || {};
  const token = getToken();
  const header = {};
  if (token) header.Authorization = 'Bearer ' + token;
  const formData = { category: category };
  if (opts.fileName) formData.fileName = String(opts.fileName);
  if (opts.loading) wx.showLoading({ title: typeof opts.loading === 'string' ? opts.loading : '上传中', mask: true });
  return new Promise((resolve, reject) => {
    const task = wx.uploadFile({
      url: buildUrl('/api/files'),
      filePath: filePath,
      name: 'file',
      formData: formData,
      header: header,
      timeout: opts.timeout || 60000,
      success(res) {
        if (opts.loading) wx.hideLoading();
        let r;
        if (res.statusCode === 413 && !isEnvelope(res.data)) {
          // 被 nginx 等前置代理按请求体大小拦截（响应不是接口包装体）
          r = { ok: false, error: makeError(40000, '文件不能超过 100MB', 413, null) };
        } else {
          r = handleBody(res.statusCode, res.data);
        }
        if (r.ok) return resolve(r.data);
        if (!opts.silent && !r.auth) toast(r.error.message);
        reject(r.error);
      },
      fail(err) {
        if (opts.loading) wx.hideLoading();
        const e = makeError(-1, netErrorMessage(err, '上传失败，请检查网络后重试'), 0, err);
        if (!opts.silent) toast(e.message);
        reject(e);
      }
    });
    bindProgress(task, opts.onProgress);
    if (task && typeof opts.onTask === 'function') opts.onTask(task);
  });
}

/** 响应体是否为接口包装体 {code,message,data} */
function isEnvelope(body) {
  if (typeof body === 'string') {
    try { body = JSON.parse(body); } catch (e) { return false; }
  }
  return !!body && typeof body === 'object' && typeof body.code === 'number';
}

/** 下载接口非 200 时的提示（CHANGES-v1.2 §3.5） */
function downloadErrorMessage(statusCode) {
  if (statusCode === 404) return '文件不存在或已删除';
  if (statusCode === 403) return '无权查看该文件';
  return '下载失败（' + statusCode + '）';
}

/**
 * 下载需要认证的文件（xlsx 导出、工单附件）。
 * @param {string} url 路径或完整 URL
 * @param {Object} [query] 查询参数
 * @param {{filePath?:string, timeout?:number, onProgress?:function(number):void, silent?:boolean}} [opts]
 *   filePath：指定保存路径（wx.env.USER_DATA_PATH 下，目录须已存在）；timeout 默认沿用微信默认值；
 *   onProgress(percent 0–100)；silent=true 时出错不弹 toast（401 仍会跳登录）
 * @returns {Promise<string>} 本地文件路径（传了 filePath 时为该路径，否则为 tempFilePath）
 */
function download(url, query, opts) {
  opts = opts || {};
  const q = cleanQuery(query) || {};
  const qs = Object.keys(q).map((k) => encodeURIComponent(k) + '=' + encodeURIComponent(q[k])).join('&');
  const full = buildUrl(url) + (qs ? (url.indexOf('?') >= 0 ? '&' : '?') + qs : '');
  const token = getToken();
  return new Promise((resolve, reject) => {
    const params = {
      url: full,
      header: token ? { Authorization: 'Bearer ' + token } : {},
      success(res) {
        if (res.statusCode === 200) return resolve(res.filePath || res.tempFilePath || opts.filePath);
        if (res.statusCode === 401) redirectToLogin();
        const e = res.statusCode === 401
          ? makeError(40100, '登录已失效，请重新登录', 401, null)
          : makeError(res.statusCode * 100, downloadErrorMessage(res.statusCode), res.statusCode, null);
        if (!opts.silent && res.statusCode !== 401) toast(e.message);
        reject(e);
      },
      fail(err) {
        const e = makeError(-1, netErrorMessage(err, '下载失败，请检查网络后重试'), 0, err);
        if (!opts.silent) toast(e.message);
        reject(e);
      }
    };
    if (opts.filePath) params.filePath = opts.filePath;
    if (opts.timeout) params.timeout = opts.timeout;
    const task = wx.downloadFile(params);
    bindProgress(task, opts.onProgress);
  });
}

module.exports = {
  request,
  get,
  post,
  put,
  uploadFile,
  download,
  buildUrl,
  getToken,
  clearToken,
  redirectToLogin,
  TOKEN_KEY
};
