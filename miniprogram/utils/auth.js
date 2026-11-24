/**
 * 登录态管理。token 存 wx.setStorageSync('token')，user 只放 getApp().globalData.user（每次启动 GET /api/me 刷新）。
 *
 * 页面用法：
 *   onShow() {
 *     auth.ensureLogin().then((user) => { ...加载数据 }).catch(() => {}); // 未登录时已自动跳登录页
 *   }
 */
const api = require('../api/index.js');
const http = require('./request.js');
const subscribe = require('./subscribe.js');

const LOGIN_PAGE = '/pages/login/index';
const HOME_PAGE = '/pages/home/index';
let pending = null; // 进行中的 ensureLogin，避免并发重复登录

function app() {
  return getApp();
}

/** 当前用户（可能为 null） @returns {?Object} User */
function getUser() {
  const a = app();
  return (a && a.globalData && a.globalData.user) || null;
}

/** 当前角色：'Engineer' | 'Sales' | 'Supervisor' | '' */
function getRole() {
  const u = getUser();
  return (u && u.role) || '';
}

function getToken() {
  return http.getToken();
}

/** 通知所有页面的自定义 tabBar 刷新（角色可能变化） */
function notifyUserChange() {
  try {
    getCurrentPages().forEach((p) => {
      const tb = typeof p.getTabBar === 'function' ? p.getTabBar() : null;
      if (tb && typeof tb.refresh === 'function') tb.refresh();
    });
  } catch (e) { /* ignore */ }
}

/**
 * 保存登录态。
 * @param {string} token
 * @param {Object} user User
 */
function setSession(token, user) {
  const a = app();
  if (token) {
    wx.setStorageSync(http.TOKEN_KEY, token);
    a.globalData.token = token;
  }
  a.globalData.user = user || null;
  notifyUserChange();
}

/** 仅更新 user（如换头像后） @param {Object} user */
function setUser(user) {
  app().globalData.user = user || null;
  notifyUserChange();
}

/** 退出登录：先让后端解除微信绑定（失败也照常退出），再清本地登录态并回到登录页 */
function logout() {
  const done = () => {
    http.clearToken();
    const a = app();
    if (a && a.globalData) { a.globalData.user = null; a.globalData.token = ''; }
    pending = null;
    notifyUserChange();
    wx.reLaunch({ url: LOGIN_PAGE });
  };
  if (!getToken()) { done(); return; }
  api.me.logout({ silent: true }).then(done, done);
}

/**
 * 重新拉取 GET /api/me，更新 globalData.user。
 * @returns {Promise<{user:Object,monthMinutes:number,activeOrderCount:number,monthTripDays:number}>} /api/me 的完整 data
 */
function refreshUser(opts) {
  return api.me.get(opts).then((data) => {
    setUser(data && data.user);
    return data;
  });
}

function wxLogin() {
  return new Promise((resolve, reject) => {
    wx.login({
      success: (r) => (r.code ? resolve(r.code) : reject({ code: -1, message: '微信登录失败' })),
      fail: (e) => reject({ code: -1, message: '微信登录失败', data: e })
    });
  });
}

function toLogin() {
  const pages = getCurrentPages();
  const cur = pages[pages.length - 1];
  if (cur && '/' + cur.route === LOGIN_PAGE) return;
  wx.reLaunch({ url: LOGIN_PAGE });
}

/**
 * 确保已登录。
 * - 已有 token 且 globalData.user → 直接返回
 * - 有 token 无 user → GET /api/me
 * - 无 token → reLaunch 登录页（v1.5：账号密码登录，不再用微信身份静默登录），reject（{code:'NEED_LOGIN'}）；页面 catch 后忽略即可。
 * 有 token 但 GET /api/me 因网络/服务端异常失败（非 401）时 reject（{code:'LOAD_FAILED', message}），不跳转；
 * 页面应展示错误态并提供重试（再次调用 ensureLogin）。可用 isLoadFailed(err) 判断。
 * v1.4：登录就绪后顺带刷新订阅消息的配置与授权状态缓存（utils/subscribe.js；静默，不影响登录结果）。
 * 每个页面的 onShow 都会走到这里，所以页面不需要自己刷新。
 * @returns {Promise<Object>} User
 */
function ensureLogin() {
  const p = resolveLogin();
  p.then(() => {
    try { subscribe.refresh(); } catch (e) { /* 订阅消息只是附带功能，任何异常都不影响登录 */ }
  }, () => {});
  return p;
}

function resolveLogin() {
  const user = getUser();
  const token = getToken();
  if (token && user) return Promise.resolve(user);
  if (pending) return pending;

  if (token) {
    app().globalData.token = token;
    pending = refreshUser({ silent: true }).then((data) => {
      if (!data || !data.user) return Promise.reject({ code: 'LOAD_FAILED', message: '加载账号信息失败，请重试' });
      return data.user;
    }, (err) => {
      // 401 已由 request.js 清 token 并跳登录页
      if (err && err.code === 40100) return Promise.reject({ code: 'AUTH_EXPIRED', message: err.message, data: err });
      return Promise.reject({ code: 'LOAD_FAILED', message: (err && err.message) || '网络异常，请重试', data: err });
    });
  } else {
    toLogin();
    return Promise.reject({ code: 'NEED_LOGIN', message: '请先登录' });
  }

  const p = pending;
  p.then(() => { pending = null; }, () => { pending = null; });
  return p;
}

/** ensureLogin 的失败是否为可重试的加载失败（网络/服务端异常，未跳转登录页） */
function isLoadFailed(err) {
  return !!err && err.code === 'LOAD_FAILED';
}

module.exports = {
  ensureLogin,
  isLoadFailed,
  getUser,
  getRole,
  getToken,
  setSession,
  setUser,
  logout,
  refreshUser,
  wxLogin,
  LOGIN_PAGE,
  HOME_PAGE
};
