// 拍照打卡（原型 07）：到场 / 离场由服务端 nextKind 决定，前端只展示
const auth = require('../../utils/auth.js');
const api = require('../../api/index.js');
const fmt = require('../../utils/format.js');
const enums = require('../../utils/enums.js');
const location = require('../../utils/location.js');
const watermark = require('../../utils/watermark.js');

const HOME = '/pages/home/index';
const DOT_OK = '#34C77B';
const DOT_FAR = '#FFB340'; // 深色背景上的琥珀色

Page({
  data: {
    loading: true,
    loadError: '',
    ctx: null, // 打卡上下文；null = 无可打卡工单
    empty: false,
    title: '拍照打卡',
    kindLabel: '',
    kinds: [],

    // 水印预览
    timeText: '--:--',
    dateText: '',
    coordsText: '',
    ownerText: '',
    hint: '',

    // 距离徽标
    distanceText: '正在定位…',
    distanceDot: DOT_OK,
    distanceFar: false,

    // 相机
    showCamera: false,
    cameraDenied: false,
    cameraError: '',
    devicePosition: 'back',
    flash: 'off',
    busy: false
  },

  onLoad(query) {
    const id = Number(query && query.orderId);
    this.orderId = id > 0 ? id : null;
    this.loc = null;
  },

  onShow() {
    this.startTick();
    if (this.data.busy) return;
    auth.ensureLogin().then(() => this.loadContext()).catch(() => {});
  },

  onHide() {
    this.stopTick();
  },

  onUnload() {
    this.stopTick();
  },

  /* ---------- 数据 ---------- */
  loadContext() {
    if (!this.data.ctx) this.setData({ loading: true, loadError: '' });
    return api.punches.context(this.orderId, { silent: true }).then((ctx) => {
      if (!ctx) {
        this.setData({ loading: false, loadError: '', ctx: null, empty: true, showCamera: false, title: '拍照打卡' });
        return;
      }
      fmt.syncServerTime(ctx.serverTime);
      const kindLabel = enums.label(enums.PUNCH_KIND, ctx.nextKind);
      this.setData({
        loading: false,
        loadError: '',
        empty: false,
        ctx: ctx,
        title: kindLabel + '打卡' + (ctx.myRole === 'Assistant' ? '（协助）' : ''),
        kindLabel: kindLabel,
        kinds: enums.toOptions(enums.PUNCH_KIND).map((k) => ({
          value: k.value,
          label: k.label + '打卡',
          active: k.value === ctx.nextKind
        })),
        ownerText: (ctx.engineerName || '') + ' · ' + (ctx.orderNo || ''),
        showCamera: this.data.showCamera || !this.data.cameraDenied
      });
      this.updateClock(true);
      this.updateDistance();
      if (!this.loc) this.refreshLocation();
    }).catch((err) => {
      this.setData({ loading: false, loadError: (err && err.message) || '加载失败，请重试' });
    });
  },

  onRetry() {
    this.loadContext();
  },

  // 预览用定位（失败只影响距离徽标）
  refreshLocation() {
    if (this.locating) return Promise.resolve(this.loc);
    this.locating = true;
    return location.getLocation({ silent: true }).then((loc) => {
      this.locating = false;
      this.loc = loc;
      this.updateDistance();
      return loc;
    }, (err) => {
      this.locating = false;
      if (!this.loc) {
        this.setData({ distanceText: err && err.code === 'AUTH_DENY' ? '未授权定位' : '定位失败', distanceDot: DOT_FAR, distanceFar: true });
      }
      return Promise.reject(err);
    }).catch(() => null);
  },

  updateDistance() {
    const ctx = this.data.ctx;
    const loc = this.loc;
    if (!ctx) return;
    if (!loc) {
      if (!this.locating) this.setData({ distanceText: '正在定位…' });
      this.setData({ coordsText: location.formatCoords(ctx.latitude, ctx.longitude) });
      return;
    }
    const d = location.distanceMeters(loc.latitude, loc.longitude, ctx.latitude, ctx.longitude);
    const coordsText = location.formatCoords(loc.latitude, loc.longitude);
    if (isNaN(d)) {
      this.setData({ distanceText: '客户地址无坐标', distanceDot: DOT_FAR, distanceFar: true, coordsText: coordsText });
      return;
    }
    const range = ctx.rangeMeters || 1000;
    const far = d > range;
    this.setData({
      distanceText: '距客户 ' + fmt.formatDistance(d) + ' · ' + (far ? '超出范围' : '在范围内'),
      distanceDot: far ? DOT_FAR : DOT_OK,
      distanceFar: far,
      coordsText: coordsText
    });
  },

  /* ---------- 时钟 ---------- */
  startTick() {
    this.stopTick();
    this.timer = setInterval(() => this.updateClock(false), 1000);
  },

  stopTick() {
    if (this.timer) clearInterval(this.timer);
    this.timer = null;
  },

  updateClock(force) {
    const ctx = this.data.ctx;
    if (!ctx) return;
    const n = fmt.now();
    const timeText = fmt.formatHm(n);
    let hint = '到场后开始计时，离开现场时再打离场卡';
    if (ctx.nextKind === 'Leave') {
      const mins = Math.floor(fmt.secondsBetween(ctx.openArriveTime, n) / 60);
      hint = ctx.openArriveTime
        ? '上次 ' + fmt.formatHm(ctx.openArriveTime) + ' 到场，本次离场后计入 ' + fmt.formatDuration(mins)
        : '离开现场时打离场卡，结束本次计时';
    }
    if (!force && timeText === this.data.timeText && hint === this.data.hint) return;
    this.setData({
      timeText: timeText,
      dateText: fmt.formatDateDot(n) + ' ' + fmt.weekday(n),
      hint: hint
    });
  },

  /* ---------- 相机 ---------- */
  onCameraError(e) {
    const msg = (e && e.detail && e.detail.errMsg) || '';
    if (/auth|authorize|permission/i.test(msg)) {
      this.setData({ cameraDenied: true, cameraError: '', showCamera: false });
    } else {
      this.setData({ cameraError: '相机启动失败，请重试', showCamera: false });
    }
  },

  onCameraStop() {
    // 被系统中断（如切到后台），回来时重新挂载
    this.setData({ showCamera: false });
    setTimeout(() => this.setData({ showCamera: !this.data.cameraDenied && !!this.data.ctx }), 300);
  },

  onOpenSetting() {
    wx.openSetting({
      success: (res) => {
        const ok = res.authSetting && res.authSetting['scope.camera'];
        if (ok) this.remountCamera();
        else wx.showToast({ title: '未开启相机权限，无法拍照打卡', icon: 'none' });
      }
    });
  },

  remountCamera() {
    this.setData({ showCamera: false, cameraDenied: false, cameraError: '' });
    setTimeout(() => this.setData({ showCamera: true }), 100);
  },

  onToggleFlash() {
    this.setData({ flash: this.data.flash === 'on' ? 'off' : 'on' });
  },

  onSwitchCamera() {
    this.setData({ devicePosition: this.data.devicePosition === 'back' ? 'front' : 'back' });
  },

  onKindTap() {
    wx.showToast({ title: '到场 / 离场由系统自动判断', icon: 'none' });
  },

  /* ---------- 拍照打卡 ---------- */
  takePhoto() {
    return new Promise((resolve, reject) => {
      if (!this.cameraCtx) this.cameraCtx = wx.createCameraContext();
      this.cameraCtx.takePhoto({
        quality: 'high',
        success: (res) => resolve(res.tempImagePath),
        fail: () => reject({ message: '拍照失败，请重试' })
      });
    });
  },

  onShutter() {
    const ctx = this.data.ctx;
    if (this.data.busy || !ctx || ctx.blockedReason || !this.data.showCamera) return;
    this.setData({ busy: true });
    wx.showLoading({ title: '拍照中', mask: true });

    let photoPath = '';
    let loc = null;
    this.takePhoto().then((path) => {
      photoPath = path;
      wx.hideLoading();
      wx.showLoading({ title: '定位中', mask: true });
      return location.getLocation({ silent: true }).catch((err) => {
        wx.hideLoading();
        return Promise.reject({ message: (err && err.message) || '定位失败，请稍后重试' });
      });
    }).then((l) => {
      loc = l;
      this.loc = l;
      this.updateDistance();
      wx.showLoading({ title: '处理照片', mask: true });
      const t = fmt.now();
      return watermark.composeWatermark(photoPath, [
        this.data.kindLabel + ' ' + fmt.formatHm(t),
        fmt.formatDateDot(t) + ' ' + fmt.weekday(t),
        ctx.address,
        location.formatCoords(l.latitude, l.longitude),
        this.data.ownerText
      ]);
    }).then((path) => {
      wx.showLoading({ title: '上传照片', mask: true });
      return api.files.upload(path, 'PunchPhoto', { silent: true });
    }).then((file) => {
      wx.showLoading({ title: '提交打卡', mask: true });
      return api.punches.create({
        orderId: ctx.orderId,
        kind: ctx.nextKind,
        latitude: loc.latitude,
        longitude: loc.longitude,
        address: ctx.address || '',
        photoFileId: file.id
      }, { silent: true });
    }).then((res) => {
      wx.hideLoading();
      this.onPunched(res);
    }).catch((err) => {
      wx.hideLoading();
      this.setData({ busy: false });
      this.onPunchError(err);
    });
  },

  onPunched(res) {
    const punch = (res && res.punch) || {};
    const msg = punch.kind === 'Leave'
      ? '离场打卡成功 · 计入 ' + fmt.formatDuration(punch.pairMinutes || 0)
      : '到场打卡成功';
    if (punch.outOfRange) {
      wx.showModal({
        title: msg,
        content: '距客户 ' + fmt.formatDistance(punch.distanceMeters) + '，已标记距离异常，主管会查看照片',
        showCancel: false,
        confirmText: '知道了',
        confirmColor: '#1D1D1F',
        complete: () => this.goBack()
      });
      return;
    }
    wx.showToast({ title: msg, icon: 'none', duration: 1500 });
    setTimeout(() => this.goBack(), 1500);
  },

  onPunchError(err) {
    const message = (err && err.message) || '打卡失败，请重试';
    if (err && err.code === 40900) {
      wx.showModal({
        title: '无法打卡',
        content: message,
        showCancel: false,
        confirmColor: '#1D1D1F',
        complete: () => this.loadContext()
      });
      return;
    }
    if (err && (err.code === 40100 || err.httpStatus === 401)) return; // 已自动跳登录
    wx.showToast({ title: message, icon: 'none', duration: 2500 });
  },

  goBack() {
    this.setData({ busy: false });
    if (getCurrentPages().length > 1) {
      wx.navigateBack({ fail: () => wx.reLaunch({ url: HOME }) });
    } else {
      wx.reLaunch({ url: HOME });
    }
  },

  goHall() {
    wx.switchTab({ url: '/pages/orders/index' });
  }
});
