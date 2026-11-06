/**
 * 我的（原型 09，tab）。数据：GET /api/me；更换头像：chooseAvatar → 上传 Avatar → PUT /api/me/avatar。
 */
const api = require('../../api/index.js');
const auth = require('../../utils/auth.js');
const tabbar = require('../../utils/tabbar.js');
const format = require('../../utils/format.js');
const enums = require('../../utils/enums.js');
const system = require('../../utils/system.js');
const config = require('../../config.js');

function appVersion() {
  try {
    const info = wx.getAccountInfoSync();
    return (info && info.miniProgram && info.miniProgram.version) || '1.0.0';
  } catch (e) {
    return '1.0.0';
  }
}

Page({
  data: {
    statusBarHeight: 20,
    navBarHeight: 44,
    user: null,
    roleName: '',
    subline: '',
    isEngineer: false,
    loaded: false,
    loading: false,
    error: '',
    stats: [],
    monthText: '—',
    activeText: '—',
    uploading: false,
    version: '1.0.0',
    showDev: !!config.devLogin
  },

  onLoad() {
    const m = system.getNavMetrics();
    this.setData({ statusBarHeight: m.statusBarHeight, navBarHeight: m.navBarHeight, version: appVersion() });
  },

  onShow() {
    tabbar.setTabSelected(this, 'me');
    return this.login();
  },

  /** ensureLogin 后加载；网络/服务端异常时展示错误条（点击重试） */
  login() {
    return auth.ensureLogin().then((user) => {
      tabbar.setTabSelected(this, 'me');
      this.applyUser(user);
      return this.load();
    }).catch((err) => {
      if (auth.isLoadFailed(err) && !this.data.user) this.setData({ error: err.message || '网络异常，请重试' });
    });
  },

  onPullDownRefresh() {
    const p = this.data.user ? this.load() : this.login();
    Promise.resolve(p).then(() => wx.stopPullDownRefresh(), () => wx.stopPullDownRefresh());
  },

  applyUser(u) {
    if (!u) return;
    const phone = u.phoneMasked || format.maskPhone(u.phone);
    this.setData({
      user: u,
      roleName: u.roleName || enums.label(enums.ROLE, u.role),
      subline: [u.department, phone].filter(Boolean).join(' · '),
      isEngineer: u.role === 'Engineer'
    });
  },

  load() {
    if (this.data.loading) return Promise.resolve();
    this.setData({ loading: true });
    return api.me.get({ silent: true }).then((data) => {
      if (data && data.user) {
        auth.setUser(data.user);
        this.applyUser(data.user);
      }
      this.applyStats(data || {});
      this.setData({ loaded: true, error: '' });
    }).catch((err) => {
      this.setData({ error: (err && err.message) || '加载失败' });
      if (!this.data.loaded) this.applyStats({});
    }).then(() => {
      this.setData({ loading: false });
    });
  },

  /** 按角色组织统计格 */
  applyStats(d) {
    const role = (this.data.user && this.data.user.role) || '';
    const has = (v) => v !== undefined && v !== null;
    const num = (v) => (has(v) ? String(v) : '—');
    const month = has(d.monthMinutes) ? format.formatDuration(d.monthMinutes) : '—';
    let stats = [];
    if (role === 'Engineer') {
      stats = [
        { key: 'hours', value: month, label: '本月现场工时' },
        { key: 'myOrders', value: num(d.activeOrderCount), label: '进行中工单' },
        { key: 'hours', value: num(d.monthTripDays), label: '本月出差天数' }
      ];
    } else if (role === 'Sales') {
      stats = [{ key: 'home', value: num(d.activeOrderCount), label: '我提交的进行中工单' }];
    } else if (role === 'Supervisor') {
      stats = [{ key: 'home', value: num(d.activeOrderCount), label: '待认领 / 待验收工单' }];
    }
    this.setData({
      stats: stats,
      monthText: month === '—' ? '—' : '本月 ' + month,
      activeText: has(d.activeOrderCount) ? '进行中 ' + d.activeOrderCount + ' 张' : '—'
    });
  },

  onStatTap(e) {
    const key = e.currentTarget.dataset.key;
    if (key === 'hours') this.goHours();
    else if (key === 'myOrders') this.goMyOrders();
    else if (key === 'home') wx.switchTab({ url: '/pages/home/index' });
  },

  goHours() {
    wx.switchTab({ url: '/pages/hours/index' });
  },

  goMyOrders() {
    wx.navigateTo({ url: '/pages/my-orders/index' });
  },

  goPrivacy() {
    wx.navigateTo({ url: '/pages/privacy/index' });
  },

  onChooseAvatar(e) {
    const path = e.detail && e.detail.avatarUrl;
    if (!path || this.data.uploading) return;
    this.setData({ uploading: true });
    api.files.upload(path, 'Avatar', { loading: '上传中' })
      .then((file) => api.me.setAvatar(file.id))
      .then((user) => {
        auth.setUser(user);
        this.applyUser(user);
        wx.showToast({ title: '头像已更新', icon: 'none' });
      })
      .catch(() => {})
      .then(() => this.setData({ uploading: false }));
  },

  onVersionTap() {
    wx.showToast({ title: '当前版本 ' + this.data.version, icon: 'none' });
  },

  onRetry() {
    if (!this.data.user) {
      this.setData({ error: '' });
      this.login();
      return;
    }
    this.load();
  },

  onDevLogout() {
    wx.showModal({
      title: '退出登录',
      content: '仅用于开发调试。退出后需要重新登录。',
      confirmText: '退出',
      confirmColor: '#C81E2B',
      success: (r) => { if (r.confirm) auth.logout(); }
    });
  }
});
