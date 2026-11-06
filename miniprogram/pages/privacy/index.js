/**
 * 用户隐私保护指引（静态文本）。
 * mode=authorize：由 app.js 的 wx.onNeedPrivacyAuthorization 打开，底部显示「同意并继续」/「不同意」。
 */
Page({
  data: {
    authorize: false,
    updated: '2026年11月6日'
  },

  onLoad(query) {
    this.setData({ authorize: query && query.mode === 'authorize' });
  },

  onUnload() {
    // 未点同意就离开 → 视为不同意
    this.finish({ event: 'disagree' });
  },

  finish(result) {
    const app = getApp();
    const resolve = app.globalData.privacyResolve;
    if (typeof resolve === 'function') {
      app.globalData.privacyResolve = null;
      resolve(result);
    }
  },

  onAgree(e) {
    this.finish({ event: 'agree', buttonId: 'agree-privacy-btn' });
    wx.navigateBack({ fail: () => wx.reLaunch({ url: '/pages/home/index' }) });
  },

  onDisagree() {
    this.finish({ event: 'disagree' });
    wx.navigateBack({ fail: () => wx.reLaunch({ url: '/pages/home/index' }) });
  },

  openOfficial() {
    if (wx.openPrivacyContract) wx.openPrivacyContract({});
  }
});
