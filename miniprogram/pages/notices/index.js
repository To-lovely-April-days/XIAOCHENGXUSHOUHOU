// pages/notices 提醒设置（v2.2）：微信通知授权 + 按类别选择要打开哪些提醒
// 授权状态与弹窗沿用 utils/subscribe.js；类别开关存在服务器（GET / PUT /api/me/notices），换手机也一样。
const api = require('../../api/index.js');
const subscribe = require('../../utils/subscribe.js');

Page({
  data: {
    loading: true,
    error: '',
    items: [],
    authText: '',
    authOn: false,
    authDesc: ''
  },

  onLoad() {
    this.load();
  },

  onShow() {
    // 从系统设置页回来时状态可能变了
    subscribe.refresh().then(() => this.applyAuth()).catch(() => this.applyAuth());
  },

  load() {
    this.setData({ loading: true, error: '' });
    api.me.notices({ silent: true }).then((d) => {
      this.setData({ loading: false, items: (d && d.items) || [] });
    }).catch((err) => {
      this.setData({ loading: false, error: (err && err.message) || '加载失败' });
    });
  },

  applyAuth() {
    const text = subscribe.isEnabled() ? subscribe.statusText() : '未启用';
    const on = text === '已开启';
    this.setData({
      authText: text,
      authOn: on,
      authDesc: on ? '已允许发到你的微信；点一下可以多攒一次提醒额度' : '点这里允许小程序把提醒发到你的微信'
    });
  },

  onAuth() {
    if (!subscribe.isEnabled()) {
      wx.showToast({ title: '服务器还没有启用微信提醒', icon: 'none' });
      return;
    }
    subscribe.open().then(() => this.applyAuth()).catch(() => this.applyAuth());
  },

  onToggle(e) {
    const key = e.currentTarget.dataset.key;
    const on = !!e.detail.value;
    const before = this.data.items;
    // 先改界面，失败再改回去
    this.setData({ items: before.map((i) => (i.key === key ? Object.assign({}, i, { on: on }) : i)) });
    api.me.setNotice(key, on, { silent: true }).then((d) => {
      if (d && d.items) this.setData({ items: d.items });
      wx.showToast({ title: on ? '已打开' : '已关闭', icon: 'none' });
    }).catch((err) => {
      this.setData({ items: before });
      wx.showToast({ title: (err && err.message) || '保存失败', icon: 'none' });
    });
  }
});
