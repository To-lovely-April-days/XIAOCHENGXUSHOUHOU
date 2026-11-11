// fetch 封装 + 全部后台接口。契约见 docs/API.md。
import { navigate, route } from './router.js';

const BASE = '/api';
export const TOKEN_KEY = 'admin_token';

/** @returns {string|null} */
export function getToken() {
  try { return localStorage.getItem(TOKEN_KEY); } catch (e) { return null; }
}
/** @param {string} t */
export function setToken(t) {
  try { localStorage.setItem(TOKEN_KEY, t); } catch (e) { /* 隐私模式忽略 */ }
}
export function clearToken() {
  try { localStorage.removeItem(TOKEN_KEY); } catch (e) { /* ignore */ }
}

/** 接口错误：message 为可直接展示的中文；code 为业务码（网络错误 -1） */
export class ApiError extends Error {
  /**
   * @param {string} message
   * @param {number} code
   * @param {number} [status] HTTP 状态
   */
  constructor(message, code, status) {
    super(message);
    this.name = 'ApiError';
    this.code = code;
    this.status = status;
  }
}

/** 拼查询串，跳过 undefined/null/'' */
export function qs(params) {
  const p = new URLSearchParams();
  Object.keys(params || {}).forEach(k => {
    const v = params[k];
    if (v !== undefined && v !== null && v !== '') p.set(k, String(v));
  });
  const s = p.toString();
  return s ? '?' + s : '';
}

function authHeaders() {
  const t = getToken();
  return t ? { Authorization: 'Bearer ' + t } : {};
}

/** 40100：清 token 并跳登录页（带回跳地址） */
function handleUnauthorized() {
  clearToken();
  if (route.path !== '/login') {
    navigate('/login', { redirect: route.fullPath }, { replace: true });
  }
}

async function readEnvelope(res) {
  const text = await res.text();
  let body = null;
  try { body = text ? JSON.parse(text) : null; } catch (e) { body = null; }
  if (!body || typeof body.code !== 'number') {
    if (res.status === 401) return { code: 40100, message: '登录已失效，请重新登录', data: null };
    throw new ApiError(res.ok ? '服务器返回格式错误' : `服务器错误（HTTP ${res.status}）`, 50000, res.status);
  }
  return body;
}

/**
 * 通用请求。解包 {code,message,data}，成功返回 data。
 * 40100 → 清 token 跳 #/login 并抛 ApiError（err.code === 40100）。
 * @param {'GET'|'POST'|'PUT'|'DELETE'} method
 * @param {string} path 以 / 开头，相对 /api
 * @param {{query?: object, body?: any, form?: FormData}} [opts]
 * @returns {Promise<any>}
 */
export async function request(method, path, opts = {}) {
  const headers = Object.assign({ Accept: 'application/json' }, authHeaders());
  let body;
  if (opts.form) {
    body = opts.form; // 让浏览器自己带 multipart boundary
  } else if (opts.body !== undefined) {
    headers['Content-Type'] = 'application/json';
    body = JSON.stringify(opts.body);
  }
  let res;
  try {
    res = await fetch(BASE + path + qs(opts.query), { method, headers, body });
  } catch (e) {
    throw new ApiError('网络异常，请检查网络后重试', -1);
  }
  const env = await readEnvelope(res);
  if (env.code === 0) return env.data;
  if (env.code === 40100) handleUnauthorized();
  throw new ApiError(env.message || '请求失败', env.code, res.status);
}

const get = (p, query) => request('GET', p, { query });
const post = (p, body) => request('POST', p, { body: body === undefined ? {} : body });
const put = (p, body) => request('PUT', p, { body: body === undefined ? {} : body });

function filenameFromDisposition(cd) {
  if (!cd) return null;
  const star = /filename\*\s*=\s*(?:UTF-8|utf-8)''([^;]+)/.exec(cd);
  if (star) { try { return decodeURIComponent(star[1].trim().replace(/^"|"$/g, '')); } catch (e) { /* fallthrough */ } }
  const plain = /filename\s*=\s*"?([^";]+)"?/.exec(cd);
  return plain ? plain[1].trim() : null;
}

/**
 * 带 Authorization 下载文件（fetch → blob → a[download]）。
 * 服务端返回 JSON 包装体时视为错误并抛出 ApiError。
 * @param {string} url 完整路径，如 '/api/admin/export/orders?from=...'（不以 /api 开头时自动补 /api）
 * @param {string} [filename] 兜底文件名；优先使用 Content-Disposition 里的文件名
 * @returns {Promise<void>}
 */
export async function download(url, filename) {
  const full = url.startsWith('/api') || /^https?:/.test(url) ? url : BASE + url;
  let res;
  try {
    res = await fetch(full, { headers: authHeaders() });
  } catch (e) {
    throw new ApiError('网络异常，请检查网络后重试', -1);
  }
  const ct = res.headers.get('Content-Type') || '';
  if (!res.ok || ct.includes('application/json')) {
    const env = await readEnvelope(res);
    if (env.code === 40100) handleUnauthorized();
    throw new ApiError(env.message || '下载失败', env.code || 50000, res.status);
  }
  const blob = await res.blob();
  const name = filenameFromDisposition(res.headers.get('Content-Disposition')) || filename || 'download';
  const href = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = href;
  a.download = name;
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(href), 10000);
}

// ---------------------------------------------------------------- 接口

export const auth = {
  /**
   * 手机号 + 密码登录（仅 Supervisor）。
   * @param {string} phone @param {string} password
   * @returns {Promise<{token:string,user:object,needBind:boolean}>}
   */
  passwordLogin: (phone, password) => post('/auth/password-login', { phone, password }),
};

export const me = {
  /** @returns {Promise<{user:object,monthMinutes:number,activeOrderCount:number,monthTripDays:number}>} */
  get: () => get('/me'),
  /**
   * 修改本人密码（新密码 ≥8 位且含字母和数字）。
   * @param {string} oldPassword @param {string} newPassword
   * @returns {Promise<object>} User
   */
  changePassword: (oldPassword, newPassword) => put('/me/password', { oldPassword, newPassword }),
};

export const orders = {
  /**
   * 全部工单（分页）。
   * priceSet：按是否已定价过滤；pendingRequest=true：只返回有待审批申请的工单。
   * @param {{status?:string,keyword?:string,issue?:'MissingPunch'|'DistanceAbnormal',assigneeId?:number,serviceType?:string,from?:string,to?:string,priceSet?:boolean,pendingRequest?:boolean,page?:number,pageSize?:number}} [params]
   * @returns {Promise<{items:object[],total:number,page:number,pageSize:number,counts:Record<string,number>}>}
   */
  list: (params) => get('/orders', params),
  /** @param {number} id @returns {Promise<object>} OrderDetail */
  detail: (id) => get(`/orders/${id}`),
  /** @param {object} body 见 API.md POST /api/orders @returns {Promise<object>} OrderDetail */
  create: (body) => post('/orders', body),
  /**
   * 指派/改派。assistantIds 省略 = 不调整协助人；传数组（含空数组）= 协助人最终全集。
   * @param {number} id @param {number} engineerId @param {number[]} [assistantIds]
   */
  assign: (id, engineerId, assistantIds) =>
    post(`/orders/${id}/assign`, Array.isArray(assistantIds) ? { engineerId, assistantIds } : { engineerId }),
  /** 调整协助人（最终全集，[] = 清空） @param {number} id @param {number[]} engineerIds */
  setAssistants: (id, engineerIds) => post(`/orders/${id}/assistants`, { engineerIds: engineerIds || [] }),
  /** 作废 @param {number} id @param {string} [reason] */
  void: (id, reason) => post(`/orders/${id}/void`, { reason: reason || '' }),
  /** 暂停搁置 @param {number} id @param {'WaitCustomer'|'WaitParts'|'WaitThirdParty'|'Other'} reasonType @param {string} [note] 「其他」时必填 */
  hold: (id, reasonType, note) => post(`/orders/${id}/hold`, { reasonType, note: note || null }),
  /** 恢复处理 @param {number} id */
  resume: (id) => post(`/orders/${id}/resume`),
  /** 退回处理中 @param {number} id @param {string} [reason] */
  reject: (id, reason) => post(`/orders/${id}/reject`, { reason: reason || '' }),
  /** 确认闭环 @param {number} id */
  close: (id) => post(`/orders/${id}/close`),
  /** 定价 / 改价（0 = 保内免费） @param {number} id @param {number} price */
  setPrice: (id, price) => post(`/orders/${id}/price`, { price }),
  /** 同意申请（暂停 / 协助人共用） @param {number} id @param {number} requestId */
  approveRequest: (id, requestId) => post(`/orders/${id}/requests/${requestId}/approve`),
  /** 驳回申请 @param {number} id @param {number} requestId @param {string} [reply] 选填，≤ 200 字 */
  rejectRequest: (id, requestId, reply) => post(`/orders/${id}/requests/${requestId}/reject`, { reply: reply || null }),
  /**
   * 记录 / 更正返厂节点。photoIds 为该节点照片的最终全集。
   * @param {number} id
   * @param {{node:'CustomerShipped'|'FactoryReceived'|'ShippedBack'|'CustomerSigned',courier?:string,trackingNo?:string,note?:string,photoIds?:number[]}} body
   */
  factoryNode: (id, body) => post(`/orders/${id}/factory-nodes`, body),
};

export const engineers = {
  /** 指派面板 @returns {Promise<Array<{user:object,doingCount:number,acceptedCount:number,submittedCount:number,assistCount:number,onSiteNow:boolean,loadText:string}>>} */
  list: () => get('/engineers'),
};

export const hours = {
  /**
   * 单个工程师月度每日明细（Supervisor 带 engineerId）。
   * @param {number} year @param {number} month @param {number} [engineerId]
   */
  month: (year, month, engineerId) => get('/hours/month', { year, month, engineerId }),
};

export const admin = {
  /** @returns {Promise<{pending:number,urgentPending:number,inProgress:number,onSiteEngineers:number,submitted:number,closedThisWeek:number,weekMinutes:number,onHold:number,pendingRequests:number,unpriced:number,priceSuggestions:number,updatedAt:string}>} */
  overview: () => get('/admin/overview'),
  /**
   * 补录打卡。
   * @param {{orderId:number,arrivePunchId:number,kind:'Leave'|'Arrive',time:string,reason:string}} body time 为 yyyy-MM-ddTHH:mm:ss
   * @returns {Promise<object>} OrderDetail
   */
  supplement: (body) => post('/admin/punches/supplement', body),
  /**
   * 打卡记录。
   * @param {{from?:string,to?:string,engineerId?:number,abnormal?:'distance'|'supplement'|'missing',page?:number,pageSize?:number}} [params]
   */
  punches: (params) => get('/admin/punches', params),
  /** 工时统计 @param {number} year @param {number} month */
  hours: (year, month) => get('/admin/hours', { year, month }),
  /** @returns {Promise<Array<{id:number,name:string,phone:string,role:string,roleName:string,department:string,enabled:boolean,bound:boolean,avatarUrl:string|null,hasPassword:boolean}>>} */
  employees: () => get('/admin/employees'),
  /** @param {{name:string,phone:string,role:string,department:string}} body */
  createEmployee: (body) => post('/admin/employees', body),
  /** @param {number} id @param {{name:string,phone:string,role:string,department:string,enabled:boolean}} body */
  updateEmployee: (id, body) => put(`/admin/employees/${id}`, body),
  /** 解绑微信 @param {number} id */
  unbind: (id) => post(`/admin/employees/${id}/unbind`),
  /** 设置/重置密码 @param {number} id @param {string} password */
  setPassword: (id, password) => post(`/admin/employees/${id}/password`, { password }),
  /**
   * 导出工单台账 xlsx。
   * @param {{from?:string,to?:string,status?:string}} [params]
   * @param {string} [filename]
   */
  exportOrders: (params, filename) => download('/api/admin/export/orders' + qs(params), filename || '工单台账.xlsx'),
  /** 导出月度工时 xlsx @param {number} year @param {number} month @param {string} [filename] */
  exportHours: (year, month, filename) => download('/api/admin/export/hours' + qs({ year, month }), filename || `工时_${year}年${month}月.xlsx`),
};

export const files = {
  /**
   * 上传文件（multipart）。
   * @param {File|Blob} file @param {string} category FileCategory，如 'OrderPhoto'；返厂节点照片用 FactoryReceivePhoto / FactoryShipPhoto / FactorySignPhoto
   * @returns {Promise<{id:number,url:string,category:string,mimeType:string,createdAt:string}>}
   */
  upload: (file, category) => {
    const fd = new FormData();
    fd.append('file', file);
    fd.append('category', category);
    return request('POST', '/files', { form: fd });
  },
};

export default { request, download, auth, me, orders, engineers, hours, admin, files };
