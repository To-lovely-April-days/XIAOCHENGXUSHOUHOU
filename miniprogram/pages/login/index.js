/**
 * 登录 / 注册（v1.5：账号密码）。
 * login    ：手机号 + 密码 → POST /api/auth/account-login（顺带 wx.login 的 code，让后端把当前微信绑到账号上，用于工单提醒）
 * register ：姓名 / 手机号 / 密码 / 确认密码 → POST /api/auth/register，等主管在电脑后台审批
 * pending  ：已提交或登录时发现还在审核中
 * avatar   ：首次登录且还没头像时引导设置（chooseAvatar → 上传 Avatar → PUT /api/me/avatar），可跳过
 */
const api = require('../../api/index.js');
const auth = require('../../utils/auth.js');
const avatar = require('../../utils/avatar.js');
const format = require('../../utils/format.js');

const LAST_PHONE_KEY = 'lastPhone';
const CODE_PENDING = 40310;
const CODE_REJECTED = 40311;
const MOBILE_RE = /^1\d{10}$/;

/** 与后端一致：至少 8 位，同时含字母和数字 */
function passwordError(p) {
  if (!p) return '请输入密码';
  if (p.length < 8 || !/[A-Za-z]/.test(p) || !/\d/.test(p)) return '密码至少 8 位，且需同时包含字母和数字';
  if (p.length > 64) return '密码不能超过 64 位';
  return '';
}

Page({
  data: {
    step: 'login',
    agreed: false,
    tried: false,
    shakeAgree: false,
    busy: false,
    error: '',
    canReapply: false, // 登录时发现注册被驳回：错误条里给「修改后重新提交」
    name: '',
    phone: '',
    password: '',
    confirm: '',
    showPwd: false,
    pendingTitle: '',
    pendingDesc: '',
    user: null,
    surname: '',
    roleName: '',
    phoneMasked: '',
    department: '',
    avatarUrl: '',
    uploading: false
  },

  onLoad() {
    let last = '';
    try { last = wx.getStorageSync(LAST_PHONE_KEY) || ''; } catch (e) { /* ignore */ }
    if (last) this.setData({ phone: String(last) });
    // 已登录（如从别处误入登录页）直接回首页
    if (auth.getToken() && auth.getUser()) this.goHome();
  },

  onUnload() {
    if (this._shakeTimer) {
      clearTimeout(this._shakeTimer);
      this._shakeTimer = null;
    }
  },

  onInput(e) {
    const k = e.currentTarget.dataset.k;
    const patch = { error: '', canReapply: false };
    patch[k] = e.detail.value;
    this.setData(patch);
  },

  togglePwd() {
    this.setData({ showPwd: !this.data.showPwd });
  },

  toRegister() {
    if (this.data.busy) return;
    this.setData({ step: 'register', error: '', canReapply: false, password: '', confirm: '', tried: false });
  },

  toLoginStep() {
    if (this.data.busy) return;
    this.setData({ step: 'login', error: '', canReapply: false, password: '', confirm: '', tried: false });
  },

  toggleAgree() {
    const next = !this.data.agreed;
    this.setData({ agreed: next, tried: next ? false : this.data.tried });
  },

  openPrivacy() {
    wx.navigateTo({ url: '/pages/privacy/index' });
  },

  /** 未勾选隐私指引：高亮并轻抖勾选区域 */
  nudgeAgree() {
    if (this._shakeTimer) clearTimeout(this._shakeTimer);
    this.setData({ tried: true, shakeAgree: false }, () => {
      this.setData({ shakeAgree: true });
      this._shakeTimer = setTimeout(() => {
        this._shakeTimer = null;
        this.setData({ shakeAgree: false });
      }, 420);
    });
  },

  fail(msg) {
    this.setData({ error: msg, canReapply: false });
  },

  /* ---------- 登录 ---------- */
  onSubmitLogin() {
    if (this.data.busy) return;
    const phone = String(this.data.phone || '').trim();
    const password = this.data.password || '';
    if (!MOBILE_RE.test(phone)) return this.fail('请输入 11 位手机号');
    if (!password) return this.fail('请输入密码');
    if (!this.data.agreed) return this.nudgeAgree();

    this.setData({ busy: true, error: '', canReapply: false });
    wx.showLoading({ title: '登录中', mask: true });
    // 拿不到微信 code 也能登录，只是暂时收不到工单提醒
    auth.wxLogin().catch(() => '')
      .then((code) => api.auth.accountLogin(phone, password, code, { silent: true }))
      .then((res) => this.onLoggedIn(res, phone))
      .catch((err) => this.onLoginError(err))
      .then(() => {
        wx.hideLoading();
        this.setData({ busy: false });
      });
  },

  onLoginError(err) {
    const code = err && err.code;
    if (code === CODE_PENDING) {
      this.setData({
        step: 'pending', password: '', error: '',
        pendingTitle: '账号正在审核中',
        pendingDesc: '管理员审批通过后，就可以用手机号和密码登录了。如果着急使用，请联系售后主管。'
      });
      return;
    }
    if (code === CODE_REJECTED) {
      this.setData({ error: err.message || '注册申请未通过，可修改后重新提交', canReapply: true });
      return;
    }
    this.fail((err && err.message) || '登录失败，请检查网络后重试');
  },

  onLoggedIn(res, phone) {
    if (!res || !res.token || !res.user) return this.fail('登录失败，请重试');
    try { wx.setStorageSync(LAST_PHONE_KEY, phone); } catch (e) { /* ignore */ }
    auth.setSession(res.token, res.user);
    if (res.user.avatarUrl) return this.goHome();
    this.applyUser(res.user);
    this.setData({ step: 'avatar', error: '', password: '' });
  },

  /* ---------- 注册 ---------- */
  onSubmitRegister() {
    if (this.data.busy) return;
    const d = this.data;
    const name = String(d.name || '').trim();
    const phone = String(d.phone || '').trim();
    if (!name) return this.fail('请填写姓名');
    if (name.length > 20) return this.fail('姓名不能超过 20 个字');
    if (!MOBILE_RE.test(phone)) return this.fail('请输入 11 位手机号');
    const pe = passwordError(d.password);
    if (pe) return this.fail(pe);
    if (d.password !== d.confirm) return this.fail('两次输入的密码不一致');
    if (!d.agreed) return this.nudgeAgree();

    this.setData({ busy: true, error: '', canReapply: false });
    wx.showLoading({ title: '提交中', mask: true });
    auth.wxLogin()
      .then((code) => api.auth.register({ name: name, phone: phone, password: d.password, code: code }, { silent: true }))
      .then(() => {
        try { wx.setStorageSync(LAST_PHONE_KEY, phone); } catch (e) { /* ignore */ }
        this.setData({
          step: 'pending', password: '', confirm: '',
          pendingTitle: '已提交，等待管理员审批',
          pendingDesc: '审批通过后，用刚才填写的手机号和密码登录即可。如果着急使用，请联系售后主管。'
        });
      })
      .catch((err) => this.fail((err && err.message) || '提交失败，请检查网络后重试'))
      .then(() => {
        wx.hideLoading();
        this.setData({ busy: false });
      });
  },

  /* ---------- 首次登录 → 设置头像 ---------- */
  applyUser(u) {
    this.setData({
      user: u,
      surname: (u && (u.surname || (u.name ? String(u.name).charAt(0) : ''))) || '',
      roleName: u.roleName || '',
      phoneMasked: u.phoneMasked || format.maskPhone(u.phone),
      department: u.department || '',
      avatarUrl: u.avatarUrl || ''
    });
  },

  onChooseAvatar(e) {
    this.runAvatar(() => avatar.fromChooseAvatar(e));
  },

  onPickAvatar() {
    this.runAvatar(() => avatar.pickFromAlbumOrCamera());
  },

  runAvatar(fn) {
    if (this.data.uploading) return;
    this.setData({ uploading: true });
    fn()
      .then((user) => { if (user) this.applyUser(user); })
      .catch((err) => console.warn('[avatar] 设置失败', err))
      .then(() => this.setData({ uploading: false }));
  },

  goHome() {
    wx.reLaunch({ url: auth.HOME_PAGE });
  }
});
