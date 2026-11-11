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

const LOGIN_PAGE = '/pages/login/index';
const HOME_PAGE = '/pages/home/index';
/** 用户主动退出后置位：此后不再静默自动登录，直到用户在登录页点「微信登录」 */
const SIGNED_OUT_KEY = 'signedOut';

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
    setSignedOut(false);
  }
  a.globalData.user = user || null;
  notifyUserChange();
}

/** 仅更新 user（如换头像后） @param {Object} user */
function setUser(user) {
  app().globalData.user = user || null;
  notifyUserChange();
}

/** 是否处于「用户主动退出」状态 */
function isSignedOut() {
  try { return !!wx.getStorageSync(SIGNED_OUT_KEY); } catch (e) { return false; }
}

function setSignedOut(flag) {
  try {
    if (flag) wx.setStorageSync(SIGNED_OUT_KEY, 1);
    else wx.removeStorageSync(SIGNED_OUT_KEY);
  } catch (e) { /* ignore */ }
}

/** 退出登录：清 token/user 并记住已退出（不再自动登录），reLaunch 登录页 */
function logout() {
  http.clearToken();
  const a = app();
  if (a && a.globalData) { a.globalData.user = null; a.globalData.token = ''; }
  setSignedOut(true);
  pending = null;
  notifyUserChange();
  wx.reLaunch({ url: LOGIN_PAGE });
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

/**
 * 用 wx.login 走一遍 POST /api/auth/login。
 * @returns {Promise<Object>} LoginResult；已绑定时已 setSession
 */
function silentLogin() {
  return wxLogin()
    .then((code) => api.auth.login(code, { silent: true }))
    .then((res) => {
      if (res && !res.needBind && res.token) setSession(res.token, res.user);
      return res;
    });
}

function toLogin(ticket) {
  const pages = getCurrentPages();
  const cur = pages[pages.length - 1];
  if (cur && '/' + cur.route === LOGIN_PAGE) return;
  wx.reLaunch({ url: LOGIN_PAGE + (ticket ? '?ticket=' + encodeURIComponent(ticket) : '') });
}

/**
 * 确保已登录。
 * - 已有 token 且 globalData.user → 直接返回
 * - 有 token 无 user → GET /api/me
 * - 无 token 且用户主动退出过 → reLaunch 登录页，reject（{code:'SIGNED_OUT'}），不自动登录
 * - 无 token → wx.login + POST /api/auth/login；needBind → reLaunch 登录页（带 ticket）
 * 需要绑定或登录失败时 reject（{code:'NEED_BIND'|'LOGIN_FAILED'}），并已跳转登录页；页面 catch 后忽略即可。
 * 有 token 但 GET /api/me 因网络/服务端异常失败（非 401）时 reject（{code:'LOAD_FAILED', message}），不跳转；
 * 页面应展示错误态并提供重试（再次调用 ensureLogin）。可用 isLoadFailed(err) 判断。
 * @returns {Promise<Object>} User
 */
function ensureLogin() {
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
  } else if (isSignedOut()) {
    // 用户主动退出过：不静默登录，停在登录页等用户点「微信登录」
    toLogin();
    return Promise.reject({ code: 'SIGNED_OUT', message: '已退出登录' });
  } else {
    pending = silentLogin().then((res) => {
      if (res && !res.needBind && res.user) return res.user;
      toLogin(res && res.bindTicket);
      return Promise.reject({ code: 'NEED_BIND', message: '需要绑定账号' });
    }, (err) => {
      toLogin();
      return Promise.reject({ code: 'LOGIN_FAILED', message: (err && err.message) || '登录失败', data: err });
    });
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
  isSignedOut,
  setSignedOut,
  refreshUser,
  silentLogin,
  LOGIN_PAGE,
  HOME_PAGE
};
