// 完工提交（原型 08）
const auth = require('../../utils/auth.js');
const api = require('../../api/index.js');
const enums = require('../../utils/enums.js');

const HOME = '/pages/home/index';
const MAX_PHOTOS = 9;

let seq = 0;
function fileItem(path, fileId) {
  seq += 1;
  return { key: 'f' + Date.now() + '_' + seq, path: path, fileId: fileId || null, status: fileId ? 'done' : 'uploading' };
}

Page({
  data: {
    id: 0,
    loading: true,
    loadError: '',
    order: null,
    needSignature: true, // 现场单签字件必填
    canSubmit: false,
    blockText: '', // 不能提交的原因
    onSite: false, // 现场计时中

    faultCause: '',
    solution: '',
    partsReplaced: '',
    runStatus: '',
    runOptions: enums.RUN_STATUS_OPTIONS,
    photos: [], // 处理照片 {key,path,fileId,status}
    signatures: [], // 客户签字件
    maxPhotos: MAX_PHOTOS,
    submitting: false
  },

  onLoad(query) {
    const id = Number(query && query.id);
    this.setData({ id: id > 0 ? id : 0 });
    this.prefilled = false;
  },

  onShow() {
    auth.ensureLogin().then(() => this.loadDetail()).catch(() => {});
  },

  onUnload() {
    this.disableLeaveAlert();
  },

  /* ---------- 数据 ---------- */
  loadDetail() {
    if (!this.data.id) {
      this.setData({ loading: false, loadError: '缺少工单参数' });
      return Promise.resolve();
    }
    if (this.data.submitting) return Promise.resolve();
    if (!this.data.order) this.setData({ loading: true, loadError: '' });
    return api.orders.detail(this.data.id, { silent: true }).then((o) => {
      const st = enums.SERVICE_TYPE[o.serviceType];
      const needSignature = st ? !!st.needPunch : o.serviceType === 'Onsite';
      const actions = o.actions || [];
      const canSubmit = actions.indexOf('complete') >= 0;
      let blockText = '';
      if (!canSubmit) {
        blockText = o.status === 'InProgress'
          ? '你不是该工单的负责工程师，无法提交完工'
          : '工单当前为「' + enums.label(enums.STATUS, o.status) + '」，仅「处理中」的工单可以提交完工';
      }
      this.setData({
        loading: false,
        loadError: '',
        order: o,
        needSignature: needSignature,
        canSubmit: canSubmit,
        blockText: blockText,
        onSite: needSignature && !!o.onSiteSince
      });
      this.prefill(o);
    }).catch((err) => {
      if (this.data.order) return; // 已有数据时静默
      this.setData({ loading: false, loadError: (err && err.message) || '加载失败，请重试' });
    });
  },

  // 被退回后重新提交：带出上次填写内容
  prefill(o) {
    if (this.prefilled) return;
    this.prefilled = true;
    const c = o.completion;
    if (!c) return;
    const files = o.files || {};
    const toItems = (list) => (list || []).map((f) => fileItem(f.url, f.id));
    this.setData({
      faultCause: c.faultCause || '',
      solution: c.solution || '',
      partsReplaced: c.partsReplaced || '',
      runStatus: c.runStatus || '',
      photos: toItems(files.processPhotos).slice(0, MAX_PHOTOS),
      signatures: toItems(files.signatures).slice(0, MAX_PHOTOS)
    });
  },

  onRetry() {
    this.loadDetail();
  },

  /* ---------- 表单 ---------- */
  onInput(e) {
    const field = e.currentTarget.dataset.field;
    this.setData({ [field]: e.detail.value });
    this.markDirty();
  },

  onPickRun(e) {
    this.setData({ runStatus: e.currentTarget.dataset.value });
    this.markDirty();
  },

  markDirty() {
    if (this.dirty || !wx.enableAlertBeforeUnload) return;
    this.dirty = true;
    wx.enableAlertBeforeUnload({ message: '完工信息尚未提交，确定离开吗？' });
  },

  disableLeaveAlert() {
    if (this.dirty && wx.disableAlertBeforeUnload) wx.disableAlertBeforeUnload();
    this.dirty = false;
  },

  /* ---------- 照片 ---------- */
  onAddPhoto(e) {
    const listKey = e.currentTarget.dataset.list; // photos | signatures
    const category = listKey === 'signatures' ? 'Signature' : 'ProcessPhoto';
    const remain = MAX_PHOTOS - this.data[listKey].length;
    if (remain <= 0) {
      wx.showToast({ title: '最多 ' + MAX_PHOTOS + ' 张', icon: 'none' });
      return;
    }
    wx.chooseMedia({
      count: remain,
      mediaType: ['image'],
      sourceType: ['camera', 'album'],
      sizeType: ['compressed'],
      camera: 'back',
      success: (res) => {
        const items = (res.tempFiles || []).map((f) => fileItem(f.tempFilePath));
        if (!items.length) return;
        this.setData({ [listKey]: this.data[listKey].concat(items) });
        this.markDirty();
        items.forEach((it) => this.uploadItem(listKey, it.key, category));
      },
      fail: (err) => {
        const msg = (err && err.errMsg) || '';
        if (/cancel/i.test(msg)) return;
        if (/auth|permission/i.test(msg)) {
          wx.showModal({
            title: '需要相机 / 相册权限',
            content: '上传处理照片需要使用相机或相册，请在设置中允许。',
            confirmText: '去设置',
            confirmColor: '#1D1D1F',
            success: (r) => { if (r.confirm) wx.openSetting(); }
          });
          return;
        }
        wx.showToast({ title: '选择照片失败', icon: 'none' });
      }
    });
  },

  uploadItem(listKey, key, category) {
    const item = this.findItem(listKey, key);
    if (!item) return;
    this.updateItem(listKey, key, { status: 'uploading' });
    api.files.upload(item.path, category, { silent: true }).then((file) => {
      this.updateItem(listKey, key, { status: 'done', fileId: file.id });
    }).catch(() => {
      this.updateItem(listKey, key, { status: 'fail' });
    });
  },

  findItem(listKey, key) {
    return this.data[listKey].filter((x) => x.key === key)[0] || null;
  },

  updateItem(listKey, key, patch) {
    const idx = this.data[listKey].findIndex((x) => x.key === key);
    if (idx < 0) return; // 已被删除
    const upd = {};
    Object.keys(patch).forEach((k) => { upd[listKey + '[' + idx + '].' + k] = patch[k]; });
    this.setData(upd);
  },

  onTapPhoto(e) {
    const listKey = e.currentTarget.dataset.list;
    const key = e.currentTarget.dataset.key;
    const item = this.findItem(listKey, key);
    if (!item) return;
    if (item.status === 'fail') {
      this.uploadItem(listKey, key, listKey === 'signatures' ? 'Signature' : 'ProcessPhoto');
      return;
    }
    wx.previewImage({
      current: item.path,
      urls: this.data[listKey].map((x) => x.path)
    });
  },

  onRemovePhoto(e) {
    const listKey = e.currentTarget.dataset.list;
    const key = e.currentTarget.dataset.key;
    this.setData({ [listKey]: this.data[listKey].filter((x) => x.key !== key) });
  },

  /* ---------- 提交 ---------- */
  validate() {
    const d = this.data;
    if (!d.faultCause.trim()) return '请填写现场确认的故障原因';
    if (!d.solution.trim()) return '请填写解决措施';
    if (!d.runStatus) return '请选择设备运行状况';
    const all = d.photos.concat(d.signatures);
    if (all.some((x) => x.status === 'uploading')) return '照片上传中，请稍候';
    if (all.some((x) => x.status === 'fail')) return '有照片上传失败，请点击重试或删除';
    if (!d.photos.length) return '请至少上传 1 张处理照片（需包含维修前原状）';
    if (d.needSignature && !d.signatures.length) return '请拍摄客户签字的现场维修记录表';
    return '';
  },

  onSubmit() {
    const d = this.data;
    if (d.submitting) return;
    if (!d.canSubmit) {
      wx.showToast({ title: d.blockText || '当前不能提交完工', icon: 'none' });
      return;
    }
    const msg = this.validate();
    if (msg) {
      wx.showToast({ title: msg, icon: 'none' });
      return;
    }
    wx.showModal({
      title: '确认提交完工？',
      content: '提交后工单进入「待验收」，主管确认后闭环',
      confirmText: '提交',
      confirmColor: '#1D1D1F',
      success: (r) => { if (r.confirm) this.doSubmit(); }
    });
  },

  doSubmit() {
    const d = this.data;
    const body = {
      faultCause: d.faultCause.trim(),
      solution: d.solution.trim(),
      partsReplaced: d.partsReplaced.trim(),
      runStatus: d.runStatus,
      processPhotoIds: d.photos.map((x) => x.fileId),
      signaturePhotoIds: d.signatures.map((x) => x.fileId)
    };
    this.setData({ submitting: true });
    api.orders.complete(d.id, body, { silent: true, loading: '提交中' }).then(() => {
      this.disableLeaveAlert();
      wx.showToast({ title: '已提交，等待主管验收', icon: 'none', duration: 1500 });
      setTimeout(() => this.goBack(), 1500);
    }).catch((err) => {
      this.setData({ submitting: false });
      this.onSubmitError(err);
    });
  },

  onSubmitError(err) {
    const message = (err && err.message) || '提交失败，请重试';
    if (err && err.code === 40900 && /离场/.test(message)) {
      wx.showModal({
        title: '请先离场打卡',
        content: message === '请先离场打卡' ? '你还在现场计时中，离开现场前请先打离场卡，再提交完工。' : message,
        confirmText: '去打卡',
        confirmColor: '#1D1D1F',
        success: (r) => { if (r.confirm) this.goCheckin(); }
      });
      return;
    }
    if (err && err.code === 40900) {
      wx.showModal({ title: '无法提交', content: message, showCancel: false, confirmColor: '#1D1D1F', complete: () => this.loadDetail() });
      return;
    }
    if (err && (err.code === 40100 || err.httpStatus === 401)) return;
    wx.showToast({ title: message, icon: 'none', duration: 2500 });
  },

  goCheckin() {
    wx.navigateTo({ url: '/pages/checkin/index?orderId=' + this.data.id });
  },

  goBack() {
    this.setData({ submitting: false });
    if (getCurrentPages().length > 1) {
      wx.navigateBack({ fail: () => wx.reLaunch({ url: HOME }) });
    } else {
      wx.reLaunch({ url: HOME });
    }
  }
});
