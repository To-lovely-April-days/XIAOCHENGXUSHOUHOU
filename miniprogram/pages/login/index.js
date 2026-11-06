/**
 * 登录绑定（原型 03）。
 * 第 1 步：勾选隐私指引 → 手机号快速验证（getPhoneNumber）→ POST /api/auth/bind
 * 第 2 步：设置头像（chooseAvatar → 上传 Avatar → PUT /api/me/avatar），可跳过
 * config.devLogin 为 true 时提供手机号直登（POST /api/auth/dev-login，需后端 MockMode）。
 * query.ticket：ensureLogin 发现未绑定时带过来的 bindTicket（10 分钟有效）。
 */
const config = require('../../config.js');
const api = require('../../api/index.js');
const auth = require('../../utils/auth.js');
const format = require('../../utils/format.js');

const TICKET_TTL = 8 * 60 * 1000; // 留余量，票据实际 10 分钟
const NOT_FOUND_TEXT = '未找到账号，请联系售后主管在后台添加';

Page({
  data: {
    step: 'verify',
    agreed: false,
    tried: false,
    busy: false,
    error: '',
    loginHint: '',
    showDev: config.devLogin,
    devOpen: false,
    devPhone: '',
    user: null,
    roleName: '',
    phoneMasked: '',
    department: '',
    avatarUrl: '',
    uploading: false
  },

  onLoad(query) {
    this.ticket = query && query.ticket ? decodeURIComponent(query.ticket) : '';
    this.ticketAt = this.ticket ? Date.now() : 0;
    if (!this.ticket) this.prepareTicket(true);
  },

  /** 取 bindTicket；已绑定则直接登录 */
  prepareTicket(quiet) {
    return auth.silentLogin().then((res) => {
      if (res && !res.needBind && res.token) {
        this.goHome();
        return null;
      }
      this.ticket = (res && res.bindTicket) || '';
      this.ticketAt = Date.now();
      this.setData({ loginHint: '' });
      return this.ticket;
    }).catch((err) => {
      const msg = (err && err.message) || '微信登录失败，请检查网络后重试';
      this.setData({ loginHint: msg });
      if (!quiet) wx.showToast({ title: msg, icon: 'none' });
      return Promise.reject(err);
    });
  },

  ensureTicket() {
    if (this.ticket && Date.now() - this.ticketAt < TICKET_TTL) return Promise.resolve(this.ticket);
    return this.prepareTicket(false);
  },

  toggleAgree() {
    this.setData({ agreed: !this.data.agreed, error: '' });
  },

  openPrivacy() {
    wx.navigateTo({ url: '/pages/privacy/index' });
  },

  /** 未勾选时点击验证按钮 */
  onVerifyBlocked() {
    this.setData({ tried: true });
  },

  onAgreePrivacy() { /* 按钮同时带 agreePrivacyAuthorization，点击即同意隐私协议 */ },

  onGetPhone(e) {
    const code = e.detail && e.detail.code;
    if (!code) {
      const msg = (e.detail && e.detail.errMsg) || '';
      if (!/deny|cancel/.test(msg)) wx.showToast({ title: '获取手机号失败，请重试', icon: 'none' });
      return;
    }
    if (this.data.busy) return;
    this.setData({ busy: true, error: '' });
    wx.showLoading({ title: '验证中', mask: true });
    this.ensureTicket()
      .then((ticket) => {
        if (ticket === null) return null; // 已直接登录
        return api.auth.bind(ticket, code, { silent: true }).then((res) => this.onLoggedIn(res));
      })
      .catch((err) => this.onBindError(err))
      .then(() => {
        wx.hideLoading();
        this.setData({ busy: false });
      });
  },

  onBindError(err) {
    if (!err) return;
    const code = err.code;
    if (code === 40401) {
      this.setData({ error: err.message || NOT_FOUND_TEXT });
      wx.showModal({ title: '未找到账号', content: (err.message || NOT_FOUND_TEXT) + '，并核对登记的手机号是否与本机微信绑定的手机号一致。', showCancel: false, confirmColor: '#1D1D1F' });
      return;
    }
    if (code === 40900) {
      this.setData({ error: err.message || '该账号已绑定其他微信，请联系主管解绑' });
      wx.showModal({ title: '无法绑定', content: err.message || '该账号已绑定其他微信，请联系主管解绑', showCancel: false, confirmColor: '#1D1D1F' });
      return;
    }
    if (code === 40000 || code === 40100) this.ticket = ''; // 票据可能失效，下次重新获取
    this.setData({ error: err.message || '验证失败，请重试' });
  },

  /* ---------- 开发调试直登 ---------- */
  toggleDev() {
    this.setData({ devOpen: !this.data.devOpen });
  },
  onDevPhone(e) {
    this.setData({ devPhone: e.detail.value });
  },
  onDevLogin() {
    const phone = String(this.data.devPhone || '').replace(/\s/g, '');
    if (!/^\d{5,20}$/.test(phone)) {
      wx.showToast({ title: '请输入手机号', icon: 'none' });
      return;
    }
    if (this.data.busy) return;
    this.setData({ busy: true, error: '' });
    api.auth.devLogin(phone, { silent: true, loading: '登录中' })
      .then((res) => this.onLoggedIn(res))
      .catch((err) => {
        if (err && err.code === 40400) {
          this.setData({ error: '后端未开启 MockMode，无法使用调试登录' });
        } else {
          this.onBindError(err);
        }
      })
      .then(() => this.setData({ busy: false }));
  },

  /* ---------- 绑定成功 → 第 2 步 ---------- */
  onLoggedIn(res) {
    if (!res || !res.token || !res.user) {
      this.setData({ error: '登录失败，请重试' });
      return;
    }
    auth.setSession(res.token, res.user);
    this.applyUser(res.user);
    this.setData({ step: 'avatar', error: '' });
  },

  applyUser(u) {
    this.setData({
      user: u,
      roleName: u.roleName || '',
      phoneMasked: u.phoneMasked || format.maskPhone(u.phone),
      department: u.department || '',
      avatarUrl: u.avatarUrl || ''
    });
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

  goHome() {
    wx.reLaunch({ url: auth.HOME_PAGE });
  }
});
