/**
 * 新建工单（原型 15）。字段与 API.md POST /api/orders 一一对应。
 * 预填：getApp().globalData.prefillCustomer（销售首页「最近客户」chip 设置，结构同 /api/orders/recent-customers 的一项），读取后清空。
 */
const api = require('../../api/index.js');
const auth = require('../../utils/auth.js');
const format = require('../../utils/format.js');
const enums = require('../../utils/enums.js');
const location = require('../../utils/location.js');
const system = require('../../utils/system.js');

const MAX_MEDIA = 9;
const MAX_SIZE = 20 * 1024 * 1024;
const PERIODS = ['上午', '下午', '晚上'];

/** 校验顺序 = 页面从上到下的顺序 */
const REQUIRED = [
  { key: 'customerName', msg: '请填写客户单位' },
  { key: 'contactName', msg: '请填写联系人' },
  { key: 'contactPhone', msg: '请填写联系电话' },
  { key: 'address', msg: '请在地图上选择客户地址' },
  { key: 'deviceModel', msg: '请填写设备型号' },
  { key: 'deviceSn', msg: '请填写设备编号' },
  { key: 'satDate', msg: '请选择购买 / SAT 时间' },
  { key: 'faultTime', msg: '请选择故障发生时间' },
  { key: 'symptom', msg: '请填写故障现象 / 报错' },
  { key: 'customerTried', msg: '请填写客户已尝试的操作' },
  { key: 'siteEnvironment', msg: '请填写现场环境条件' },
  { key: 'price', msg: '请填写服务价格' }
];

function indexOf(options, value) {
  const i = options.findIndex((o) => o.value === value);
  return i < 0 ? 0 : i;
}

function trim(v) {
  return String(v === undefined || v === null ? '' : v).trim();
}

function hasCoords(lat, lng) {
  return lat !== null && lat !== undefined && lng !== null && lng !== undefined && !(Number(lat) === 0 && Number(lng) === 0);
}

let mediaSeq = 0;

Page({
  data: {
    navHeight: 64,
    sourceOptions: enums.SOURCE_OPTIONS,
    problemOptions: enums.PROBLEM_TYPE_OPTIONS,
    urgencyOptions: enums.URGENCY_OPTIONS,
    serviceOptions: enums.SERVICE_TYPE_OPTIONS,
    periods: PERIODS,
    sourceIndex: 0,
    problemIndex: 0,
    urgencyIndex: 0,
    serviceIndex: 0,
    monthEnd: '',
    dayEnd: '',

    form: {
      source: 'SalesReferral',
      problemType: 'Fault',
      urgency: 'Normal',
      serviceType: 'Onsite',
      customerName: '',
      contactName: '',
      contactPhone: '',
      address: '',
      latitude: null,
      longitude: null,
      deviceModel: '',
      deviceSn: '',
      satDate: '',
      satSigned: false,
      projectNo: '',
      symptom: '',
      customerTried: '',
      siteEnvironment: '',
      customerRequest: '',
      price: ''
    },
    loc: { name: '', address: '', picked: false },
    faultDate: '',
    faultDateText: '',
    periodIndex: -1,
    media: [],
    maxMedia: MAX_MEDIA,
    errKey: '',
    submitting: false
  },

  onLoad() {
    const m = system.getNavMetrics();
    const today = format.today();
    this.setData({ navHeight: m.totalHeight, dayEnd: today, monthEnd: today.slice(0, 7) });
    this.dirty = false;
    auth.ensureLogin().then((user) => {
      this.initDefaults(user);
      this.applyPrefill();
    }).catch(() => {});
  },

  onUnload() {
    this.disableAlert();
  },

  /* ---------- 默认值 / 预填 ---------- */

  initDefaults(user) {
    const role = user && user.role;
    const source = role === 'Engineer' ? 'CustomerDirect' : 'SalesReferral';
    this.setData({
      'form.source': source,
      'form.problemType': 'Fault',
      'form.urgency': 'Normal',
      'form.serviceType': 'Onsite',
      sourceIndex: indexOf(enums.SOURCE_OPTIONS, source),
      problemIndex: indexOf(enums.PROBLEM_TYPE_OPTIONS, 'Fault'),
      urgencyIndex: indexOf(enums.URGENCY_OPTIONS, 'Normal'),
      serviceIndex: indexOf(enums.SERVICE_TYPE_OPTIONS, 'Onsite')
    });
  },

  applyPrefill() {
    const app = getApp();
    const c = app && app.globalData && app.globalData.prefillCustomer;
    if (!c) return;
    app.globalData.prefillCustomer = null;
    const picked = hasCoords(c.latitude, c.longitude);
    this.setData({
      'form.customerName': c.customerName || '',
      'form.contactName': c.contactName || '',
      'form.contactPhone': c.contactPhone || '',
      'form.address': c.address || '',
      'form.latitude': picked ? c.latitude : null,
      'form.longitude': picked ? c.longitude : null,
      'form.deviceModel': c.deviceModel || '',
      'form.deviceSn': c.deviceSn || '',
      'form.satDate': c.satDate || '',
      'form.satSigned': !!c.satSigned,
      'form.projectNo': c.projectNo || '',
      loc: { name: '', address: c.address || '', picked: picked }
    });
    this.markDirty();
  },

  /* ---------- 离开提醒 ---------- */

  markDirty() {
    if (this.dirty) return;
    this.dirty = true;
    if (wx.enableAlertBeforeUnload) {
      wx.enableAlertBeforeUnload({ message: '填写的内容尚未提交，确定离开吗？' });
    }
  },

  disableAlert() {
    if (this.dirty && wx.disableAlertBeforeUnload) wx.disableAlertBeforeUnload();
    this.dirty = false;
  },

  clearErr(key) {
    if (this.data.errKey && (!key || this.data.errKey === key)) this.setData({ errKey: '' });
  },

  /* ---------- 类型 ---------- */

  onPickSource(e) {
    const i = +e.detail.value;
    this.setData({ sourceIndex: i, 'form.source': enums.SOURCE_OPTIONS[i].value });
    this.markDirty();
  },
  onPickProblem(e) {
    const i = +e.detail.value;
    this.setData({ problemIndex: i, 'form.problemType': enums.PROBLEM_TYPE_OPTIONS[i].value });
    this.markDirty();
  },
  onPickUrgency(e) {
    const i = +e.detail.value;
    this.setData({ urgencyIndex: i, 'form.urgency': enums.URGENCY_OPTIONS[i].value });
    this.markDirty();
  },
  onPickService(e) {
    const i = +e.detail.value;
    this.setData({ serviceIndex: i, 'form.serviceType': enums.SERVICE_TYPE_OPTIONS[i].value });
    this.markDirty();
  },

  /* ---------- 文本输入 ---------- */

  onInput(e) {
    const key = e.currentTarget.dataset.key;
    const patch = {};
    patch['form.' + key] = e.detail.value;
    this.setData(patch);
    this.clearErr(key);
    this.markDirty();
  },

  onPriceInput(e) {
    // 只保留数字和一个小数点，最多 2 位小数
    let v = String(e.detail.value || '').replace(/[^\d.]/g, '');
    const dot = v.indexOf('.');
    if (dot >= 0) v = v.slice(0, dot + 1) + v.slice(dot + 1).replace(/\./g, '').slice(0, 2);
    if (/^0\d/.test(v)) v = v.replace(/^0+(?=\d)/, '');
    this.setData({ 'form.price': v });
    this.clearErr('price');
    this.markDirty();
    return v;
  },

  /* ---------- 地址 ---------- */

  onChooseAddress() {
    const f = this.data.form;
    location.chooseLocation(hasCoords(f.latitude, f.longitude) ? { latitude: f.latitude, longitude: f.longitude } : null)
      .then((r) => {
        const name = r.name || '';
        const addr = r.address || '';
        const full = !name ? addr : (addr.indexOf(name) >= 0 ? addr : (addr ? addr + ' ' + name : name));
        this.setData({
          'form.address': full,
          'form.latitude': r.latitude,
          'form.longitude': r.longitude,
          loc: { name: name, address: addr, picked: true }
        });
        this.clearErr('address');
        this.markDirty();
      })
      .catch(() => {});
  },

  /* ---------- 设备 ---------- */

  onPickSat(e) {
    this.setData({ 'form.satDate': e.detail.value });
    this.clearErr('satDate');
    this.markDirty();
  },

  onSatSigned(e) {
    this.setData({ 'form.satSigned': !!e.detail.value });
    this.markDirty();
  },

  /* ---------- 故障发生时间 ---------- */

  onPickFaultDate(e) {
    const v = e.detail.value;
    const d = format.parseTime(v);
    const sameYear = d && d.getFullYear() === format.now().getFullYear();
    this.setData({ faultDate: v, faultDateText: (sameYear ? '' : d.getFullYear() + '年') + format.formatMonthDay(d) });
    if (this.data.periodIndex >= 0) this.clearErr('faultTime');
    this.markDirty();
  },

  onPickPeriod(e) {
    this.setData({ periodIndex: +e.detail.value });
    if (this.data.faultDate) this.clearErr('faultTime');
    this.markDirty();
  },

  faultTimeText() {
    if (!this.data.faultDate || this.data.periodIndex < 0) return '';
    return this.data.faultDateText + ' ' + PERIODS[this.data.periodIndex];
  },

  /* ---------- 照片 / 视频 ---------- */

  onAddMedia() {
    const left = MAX_MEDIA - this.data.media.length;
    if (left <= 0) {
      wx.showToast({ title: '最多 ' + MAX_MEDIA + ' 个', icon: 'none' });
      return;
    }
    wx.chooseMedia({
      count: left,
      mediaType: ['mix'],
      sourceType: ['album', 'camera'],
      sizeType: ['compressed'],
      maxDuration: 60,
      success: (res) => {
        const files = res.tempFiles || [];
        const tooBig = files.filter((f) => f.size && f.size > MAX_SIZE).length;
        if (tooBig) wx.showToast({ title: '有 ' + tooBig + ' 个文件超过 20MB，已忽略', icon: 'none' });
        const add = files.filter((f) => !(f.size && f.size > MAX_SIZE)).slice(0, left).map((f) => {
          const isVideo = f.fileType === 'video' || res.type === 'video';
          mediaSeq += 1;
          return {
            key: 'm' + Date.now() + '_' + mediaSeq,
            path: f.tempFilePath,
            thumb: isVideo ? (f.thumbTempFilePath || '') : f.tempFilePath,
            type: isVideo ? 'video' : 'image',
            duration: isVideo && f.duration ? Math.round(f.duration) : 0,
            status: 'uploading',
            fileId: 0
          };
        });
        if (!add.length) return;
        this.setData({ media: this.data.media.concat(add) });
        this.markDirty();
        add.forEach((m) => this.uploadMedia(m.key));
      }
    });
  },

  uploadMedia(key) {
    const idx = () => this.data.media.findIndex((m) => m.key === key);
    const i = idx();
    if (i < 0) return;
    const item = this.data.media[i];
    const p = {};
    p['media[' + i + '].status'] = 'uploading';
    this.setData(p);
    api.files.upload(item.path, 'OrderPhoto', { silent: true })
      .then((file) => {
        const j = idx();
        if (j < 0) return;
        const q = {};
        q['media[' + j + '].status'] = 'done';
        q['media[' + j + '].fileId'] = file.id;
        this.setData(q);
      })
      .catch((err) => {
        const j = idx();
        if (j < 0) return;
        const q = {};
        q['media[' + j + '].status'] = 'fail';
        this.setData(q);
        wx.showToast({ title: (err && err.message) || '上传失败，点击重试', icon: 'none' });
      });
  },

  onMediaTap(e) {
    const i = e.currentTarget.dataset.index;
    const item = this.data.media[i];
    if (!item) return;
    if (item.status === 'fail') {
      this.uploadMedia(item.key);
      return;
    }
    const sources = this.data.media.map((m) => ({ url: m.path, type: m.type, poster: m.type === 'video' ? m.thumb : undefined }));
    if (wx.previewMedia) {
      wx.previewMedia({ sources: sources, current: i });
    } else if (item.type === 'image') {
      wx.previewImage({ current: item.path, urls: this.data.media.filter((m) => m.type === 'image').map((m) => m.path) });
    }
  },

  onMediaRemove(e) {
    const i = e.currentTarget.dataset.index;
    const media = this.data.media.slice();
    media.splice(i, 1);
    this.setData({ media: media });
  },

  /* ---------- 提交 ---------- */

  validate() {
    const f = this.data.form;
    const values = Object.assign({}, f, { faultTime: this.faultTimeText() });
    for (let i = 0; i < REQUIRED.length; i++) {
      const r = REQUIRED[i];
      if (!trim(values[r.key])) return { key: r.key, msg: r.msg };
      if (r.key === 'contactPhone' && !/^[\d-]{7,20}$/.test(trim(f.contactPhone).replace(/\s/g, ''))) {
        return { key: 'contactPhone', msg: '联系电话格式不正确' };
      }
      if (r.key === 'address' && f.serviceType === 'Onsite' && !hasCoords(f.latitude, f.longitude)) {
        return { key: 'address', msg: '现场工单需要在地图上选点' };
      }
      if (r.key === 'price' && !(/^\d+(\.\d{1,2})?$/.test(trim(f.price)) && Number(f.price) >= 0)) {
        return { key: 'price', msg: '服务价格格式不正确' };
      }
    }
    if (this.data.media.some((m) => m.status === 'uploading')) return { key: 'media', msg: '照片还在上传，请稍候' };
    if (this.data.media.some((m) => m.status === 'fail')) return { key: 'media', msg: '有照片上传失败，请点击重试或删除' };
    return null;
  },

  scrollToField(key) {
    wx.pageScrollTo({
      selector: '#f-' + key,
      offsetTop: -(this.data.navHeight + 16),
      duration: 250,
      fail: () => {}
    });
  },

  buildBody() {
    const f = this.data.form;
    const opt = (v) => (trim(v) ? trim(v) : null);
    return {
      source: f.source,
      problemType: f.problemType,
      urgency: f.urgency,
      serviceType: f.serviceType,
      customerName: trim(f.customerName),
      contactName: trim(f.contactName),
      contactPhone: trim(f.contactPhone).replace(/\s/g, ''),
      address: trim(f.address),
      latitude: hasCoords(f.latitude, f.longitude) ? f.latitude : null,
      longitude: hasCoords(f.latitude, f.longitude) ? f.longitude : null,
      deviceModel: trim(f.deviceModel),
      deviceSn: trim(f.deviceSn),
      satDate: f.satDate,
      satSigned: !!f.satSigned,
      projectNo: opt(f.projectNo),
      faultTime: this.faultTimeText(),
      symptom: trim(f.symptom),
      customerTried: trim(f.customerTried),
      siteEnvironment: trim(f.siteEnvironment),
      customerRequest: opt(f.customerRequest),
      price: Number(trim(f.price)),
      photoFileIds: this.data.media.filter((m) => m.fileId).map((m) => m.fileId)
    };
  },

  onSubmit() {
    if (this.data.submitting) return;
    const err = this.validate();
    if (err) {
      this.setData({ errKey: err.key });
      wx.showToast({ title: err.msg, icon: 'none' });
      this.scrollToField(err.key);
      return;
    }
    this.setData({ submitting: true, errKey: '' });
    api.orders.create(this.buildBody(), { loading: '提交中' })
      .then((order) => {
        this.disableAlert();
        wx.showToast({ title: '已发布到工单大厅', icon: 'none' });
        setTimeout(() => {
          wx.redirectTo({ url: '/pages/order-detail/index?id=' + order.id });
        }, 700);
      })
      .catch(() => {
        this.setData({ submitting: false });
      });
  }
});
