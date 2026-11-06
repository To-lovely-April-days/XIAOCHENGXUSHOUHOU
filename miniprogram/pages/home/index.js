/**
 * 首页（tab）。按角色渲染：Engineer → home-engineer（原型 00/01/02）；Sales → home-sales（11）；Supervisor → home-supervisor（14）。
 * 页头：头像 + 姓名 + 「11月6日 周五 · 出差第 2 天 / 部门」。
 */
const auth = require('../../utils/auth.js');
const tabbar = require('../../utils/tabbar.js');
const format = require('../../utils/format.js');
const system = require('../../utils/system.js');

Page({
  data: {
    statusBarHeight: 20,
    navBarHeight: 44,
    sideWidth: 94,
    role: '',
    user: null,
    subText: '',
    loadError: ''
  },

  onLoad() {
    const m = system.getNavMetrics();
    this.setData({ statusBarHeight: m.statusBarHeight, navBarHeight: m.navBarHeight, sideWidth: m.sideWidth });
  },

  onShow() {
    tabbar.setTabSelected(this, 'home');
    return this.login();
  },

  /** ensureLogin → 渲染对应角色首页；网络/服务端异常时展示错误态（可重试），未登录时已自动跳登录页 */
  login() {
    return auth.ensureLogin().then((user) => {
      tabbar.setTabSelected(this, 'home');
      this._tripDay = this.data.role === user.role ? this._tripDay || 0 : 0;
      this.setData({ role: user.role, user: user, loadError: '' }, () => {
        this.updateHeader();
        this.refreshChild();
      });
    }).catch((err) => {
      if (auth.isLoadFailed(err) && !this.data.user) this.setData({ loadError: err.message || '网络异常，请重试' });
    });
  },

  onRetry() {
    this.setData({ loadError: '' });
    this.login();
  },

  onPullDownRefresh() {
    const p = this.data.user ? this.refreshChild() : this.login();
    p.then(() => wx.stopPullDownRefresh(), () => wx.stopPullDownRefresh());
  },

  refreshChild() {
    const child = this.selectComponent('#home-child');
    if (!child || typeof child.refresh !== 'function') return Promise.resolve();
    return Promise.resolve(child.refresh()).catch(() => {});
  },

  /** 子组件回传最新 user / tripDay */
  onMeta(e) {
    const d = e.detail || {};
    if (d.user) {
      auth.setUser(Object.assign({}, auth.getUser() || {}, d.user));
      this.setData({ user: d.user });
    }
    if (d.tripDay !== undefined) this._tripDay = d.tripDay || 0;
    this.updateHeader();
  },

  updateHeader() {
    const u = this.data.user || {};
    const n = format.now();
    let sub = format.formatMonthDay(n) + ' ' + format.weekday(n);
    if (this.data.role === 'Engineer') {
      if (this._tripDay > 0) sub += ' · 出差第 ' + this._tripDay + ' 天';
    } else if (u.department) {
      sub += ' · ' + u.department;
    }
    if (sub !== this.data.subText) this.setData({ subText: sub });
  },

  toMe() {
    wx.switchTab({ url: '/pages/me/index' });
  }
});
