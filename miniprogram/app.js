/**
 * 应用入口。onLaunch 不强制跳转登录，由各页面调用 utils/auth.js 的 ensureLogin() 处理。
 */
const TOKEN_KEY = 'token';

App({
  globalData: {
    /** 当前登录用户（User，见 API.md），未登录为 null */
    user: null,
    /** JWT，同时存于 storage 'token' */
    token: '',
    /** 隐私授权待回调（pages/privacy 使用） */
    privacyResolve: null
  },

  onLaunch() {
    try {
      this.globalData.token = wx.getStorageSync(TOKEN_KEY) || '';
    } catch (e) {
      this.globalData.token = '';
    }
    this.listenPrivacy();
    this.checkUpdate();
  },

  /**
   * 隐私协议：页面调用隐私接口（定位、相机、选头像等）且用户尚未同意时触发。
   * 跳到隐私指引页，用户点「同意并继续」（open-type=agreePrivacyAuthorization）后放行。
   */
  listenPrivacy() {
    if (!wx.onNeedPrivacyAuthorization) return;
    wx.onNeedPrivacyAuthorization((resolve) => {
      this.globalData.privacyResolve = resolve;
      wx.navigateTo({
        url: '/pages/privacy/index?mode=authorize',
        fail: () => {
          resolve({ event: 'disagree' });
          this.globalData.privacyResolve = null;
        }
      });
    });
  },

  checkUpdate() {
    if (!wx.getUpdateManager) return;
    const um = wx.getUpdateManager();
    um.onUpdateReady(() => {
      wx.showModal({
        title: '发现新版本',
        content: '新版本已准备好，是否重启使用？',
        confirmColor: '#1D1D1F',
        success: (r) => { if (r.confirm) um.applyUpdate(); }
      });
    });
  }
});
