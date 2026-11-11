/**
 * 返厂节点记录 / 更正（v1.1）。query：id（工单 id）、node（CustomerShipped | FactoryReceived | ShippedBack | CustomerSigned）。
 * 节点已记录 = 更正（带出原内容）；能否提交以服务端 factory.nodes[].canRecord / canEdit 为准。
 * 照片上传逻辑与 pages/complete 相同（相机或相册，最多 9 张），category 按节点。
 */
const auth = require('../../utils/auth.js');
const api = require('../../api/index.js');
const enums = require('../../utils/enums.js');
const format = require('../../utils/format.js');

const HOME = '/pages/home/index';
const MAX_PHOTOS = 9;
const COURIERS = ['顺丰', '京东', '德邦', '中通', '圆通', '韵达', 'EMS', '跨越'];

/** 各节点的页面文案 */
const NODE_UI = {
  CustomerShipped: {
    notice: '记录客户寄出设备的快递信息，负责工程师会收到提醒。',
    photoLabel: '', photoTip: '', photoMsg: '',
    notePlaceholder: '选填，如：含电源线 1 根、原包装',
    okText: '已记录客户寄出'
  },
  FactoryReceived: {
    notice: '确认设备已到厂。保存后工单自动进入「处理中」，建单人会收到提醒。',
    photoLabel: '收货照片', photoTip: '至少 1 张，拍外观和随机配件',
    photoMsg: '请至少上传 1 张收货照片（外观/配件）',
    notePlaceholder: '选填，如：外箱破损、缺电源线',
    okText: '已确认工厂收货'
  },
  ShippedBack: {
    notice: '记录寄回客户的快递信息。寄回后才能提交完工，建单人会收到提醒。',
    photoLabel: '寄回照片', photoTip: '至少 1 张，拍打包情况或面单',
    photoMsg: '请至少上传 1 张寄回照片',
    notePlaceholder: '选填，如：含电源线 1 根',
    okText: '已记录寄回客户'
  },
  CustomerSigned: {
    notice: '记录客户已签收设备。可上传签收凭证（选填）。',
    photoLabel: '签收凭证', photoTip: '选填，如签收截图',
    photoMsg: '',
    notePlaceholder: '选填，如：客户王工电话确认已收到',
    okText: '已记录客户签收'
  }
};

let seq = 0;
function fileItem(path, fileId) {
  seq += 1;
  return { key: 'f' + Date.now() + '_' + seq, path: path, fileId: fileId || null, status: fileId ? 'done' : 'uploading' };
}

function trim(v) {
  return String(v === null || v === undefined ? '' : v).trim();
}

Page({
  data: {
    id: 0,
    node: '',
    title: '返厂流程',
    loading: true,
    loadError: '',
    order: null,

    editing: false,     // 更正已记录的节点
    canSubmit: false,
    blockText: '',
    notice: '',
    skipHint: '',       // 主管跳过「客户寄出」时的提示
    recordedText: '',   // 更正时：原记录人与时间

    needCourier: false,
    photoMode: '',      // '' | required | optional
    photoLabel: '',
    photoTip: '',
    notePlaceholder: '',
    couriers: COURIERS,

    courier: '',
    trackingNo: '',
    note: '',
    photos: [],
    maxPhotos: MAX_PHOTOS,
    submitLabel: '保存',
    submitting: false
  },

  onLoad(query) {
    const id = Number(query && query.id);
    const node = (query && query.node) || '';
    const def = enums.FACTORY_NODE[node];
    const ui = NODE_UI[node];
    this.prefilled = false;
    this.setData({
      id: id > 0 ? id : 0,
      node: def && ui ? node : '',
      title: def ? def.label : '返厂流程',
      needCourier: !!(def && def.courier),
      photoMode: (def && def.photo) || '',
      notice: ui ? ui.notice : '',
      photoLabel: ui ? ui.photoLabel : '',
      photoTip: ui ? ui.photoTip : '',
      notePlaceholder: ui ? ui.notePlaceholder : '选填'
    });
  },

  onShow() {
    auth.ensureLogin().then(() => this.loadDetail()).catch(() => {});
  },

  onUnload() {
    this.disableLeaveAlert();
  },

  /* ---------- 数据 ---------- */
  loadDetail() {
    const d = this.data;
    if (!d.id || !d.node) {
      this.setData({ loading: false, loadError: '缺少工单或节点参数' });
      return Promise.resolve();
    }
    if (d.submitting) return Promise.resolve();
    if (!d.order) this.setData({ loading: true, loadError: '' });
    return api.orders.detail(d.id, { silent: true }).then((o) => {
      this.applyDetail(o);
    }).catch((err) => {
      if (this.data.order) return; // 已有数据时静默
      this.setData({ loading: false, loadError: (err && err.message) || '加载失败，请重试' });
    });
  },

  applyDetail(o) {
    const nodeKey = this.data.node;
    const def = enums.FACTORY_NODE[nodeKey];
    const nodes = (o.factory && o.factory.nodes) || [];
    const n = nodes.filter((x) => x.node === nodeKey)[0] || null;
    const editing = !!(n && n.recorded);
    const canSubmit = !!n && (editing ? !!n.canEdit : !!n.canRecord);

    let blockText = '';
    if (!o.factory) blockText = '只有返厂工单需要记录返厂流程';
    else if (!canSubmit) {
      blockText = editing
        ? '当前不能更正「' + def.label + '」（工单为「' + enums.label(enums.STATUS, o.status) + '」或你没有权限）'
        : '当前不能记录「' + def.label + '」，请返回工单详情查看返厂流程的进度';
    }

    let skipHint = '';
    if (!editing && nodeKey === 'FactoryReceived') {
      const first = nodes.filter((x) => x.node === 'CustomerShipped')[0];
      if (first && !first.recorded && canSubmit) skipHint = '「客户寄出」尚未记录，保存后该节点将标记为「已跳过」。';
    }

    this.setData({
      loading: false,
      loadError: '',
      order: o,
      editing: editing,
      canSubmit: canSubmit,
      blockText: blockText,
      skipHint: skipHint,
      title: (editing ? '更正' : '') + def.label,
      submitLabel: editing ? '保存更正' : def.action,
      recordedText: editing && n.operator
        ? n.operator.name + ' 于 ' + format.formatDateTimeShort(n.time) + ' 记录，保存后覆盖原内容'
        : ''
    });
    this.prefill(n);
  },

  // 更正：带出上次填写内容
  prefill(n) {
    if (this.prefilled) return;
    this.prefilled = true;
    if (!n || !n.recorded) return;
    this.setData({
      courier: n.courier || '',
      trackingNo: n.trackingNo || '',
      note: n.note || '',
      photos: (n.photos || []).map((f) => fileItem(f.url, f.id)).slice(0, MAX_PHOTOS)
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

  onPickCourier(e) {
    this.setData({ courier: e.currentTarget.dataset.value });
    this.markDirty();
  },

  onScan() {
    wx.scanCode({
      scanType: ['barCode', 'qrCode'],
      success: (res) => {
        const code = trim(res && res.result).replace(/\s/g, '');
        if (!code) return;
        this.setData({ trackingNo: code.slice(0, 50) });
        this.markDirty();
      },
      fail: (err) => {
        if (err && /cancel/i.test(err.errMsg || '')) return;
        wx.showToast({ title: '未识别到单号，请手动输入', icon: 'none' });
      }
    });
  },

  markDirty() {
    if (this.dirty || !wx.enableAlertBeforeUnload) return;
    this.dirty = true;
    wx.enableAlertBeforeUnload({ message: '内容尚未保存，确定离开吗？' });
  },

  disableLeaveAlert() {
    if (this.dirty && wx.disableAlertBeforeUnload) wx.disableAlertBeforeUnload();
    this.dirty = false;
  },

  /* ---------- 照片（同 pages/complete） ---------- */
  category() {
    const def = enums.FACTORY_NODE[this.data.node];
    return (def && def.category) || '';
  },

  onAddPhoto() {
    const category = this.category();
    if (!category) return;
    const remain = MAX_PHOTOS - this.data.photos.length;
    if (remain <= 0) {
      wx.showToast({ title: '照片最多 ' + MAX_PHOTOS + ' 张', icon: 'none' });
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
        this.setData({ photos: this.data.photos.concat(items) });
        this.markDirty();
        items.forEach((it) => this.uploadItem(it.key));
      },
      fail: (err) => {
        const msg = (err && err.errMsg) || '';
        if (/cancel/i.test(msg)) return;
        if (/auth|permission/i.test(msg)) {
          wx.showModal({
            title: '需要相机 / 相册权限',
            content: '上传照片需要使用相机或相册，请在设置中允许。',
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

  uploadItem(key) {
    const item = this.findItem(key);
    if (!item) return;
    this.updateItem(key, { status: 'uploading' });
    api.files.upload(item.path, this.category(), { silent: true }).then((file) => {
      this.updateItem(key, { status: 'done', fileId: file.id });
    }).catch(() => {
      this.updateItem(key, { status: 'fail' });
    });
  },

  findItem(key) {
    return this.data.photos.filter((x) => x.key === key)[0] || null;
  },

  updateItem(key, patch) {
    const idx = this.data.photos.findIndex((x) => x.key === key);
    if (idx < 0) return; // 已被删除
    const upd = {};
    Object.keys(patch).forEach((k) => { upd['photos[' + idx + '].' + k] = patch[k]; });
    this.setData(upd);
  },

  onTapPhoto(e) {
    const key = e.currentTarget.dataset.key;
    const item = this.findItem(key);
    if (!item) return;
    if (item.status === 'fail') {
      this.uploadItem(key);
      return;
    }
    wx.previewImage({ current: item.path, urls: this.data.photos.map((x) => x.path) });
  },

  onRemovePhoto(e) {
    const key = e.currentTarget.dataset.key;
    this.setData({ photos: this.data.photos.filter((x) => x.key !== key) });
    this.markDirty();
  },

  /* ---------- 提交 ---------- */
  validate() {
    const d = this.data;
    const ui = NODE_UI[d.node] || {};
    if (d.needCourier) {
      if (!trim(d.courier)) return '请填写快递公司';
      if (trim(d.courier).length > 50) return '快递公司不能超过 50 个字';
      const no = trim(d.trackingNo).replace(/\s/g, '');
      if (!no) return '请填写快递单号';
      if (no.length > 50) return '快递单号不能超过 50 个字';
    }
    if (d.photoMode) {
      if (d.photos.some((x) => x.status === 'uploading')) return '照片上传中，请稍候';
      if (d.photos.some((x) => x.status === 'fail')) return '有照片上传失败，请点击重试或删除';
      if (d.photoMode === 'required' && !d.photos.length) return ui.photoMsg || '请至少上传 1 张照片';
      if (d.photos.length > MAX_PHOTOS) return '照片最多 9 张';
    }
    if (trim(d.note).length > 500) return '备注不能超过 500 个字';
    return '';
  },

  onSubmit() {
    const d = this.data;
    if (d.submitting) return;
    if (!d.canSubmit) {
      wx.showToast({ title: d.blockText || '当前不能提交', icon: 'none' });
      return;
    }
    const msg = this.validate();
    if (msg) {
      wx.showToast({ title: msg, icon: 'none' });
      return;
    }
    const body = { node: d.node, note: trim(d.note) || null };
    if (d.needCourier) {
      body.courier = trim(d.courier);
      body.trackingNo = trim(d.trackingNo).replace(/\s/g, '');
    }
    if (d.photoMode) body.photoIds = d.photos.map((x) => x.fileId).filter(Boolean);

    this.setData({ submitting: true });
    api.orders.factoryNode(d.id, body, { silent: true, loading: '保存中' }).then(() => {
      this.disableLeaveAlert();
      const ui = NODE_UI[d.node] || {};
      wx.showToast({ title: d.editing ? '已更正' : (ui.okText || '已保存'), icon: 'none', duration: 1200 });
      setTimeout(() => this.goBack(), 1200);
    }).catch((err) => {
      this.setData({ submitting: false });
      this.onSubmitError(err);
    });
  },

  onSubmitError(err) {
    const message = (err && err.message) || '保存失败，请重试';
    if (err && (err.code === 40100 || err.httpStatus === 401)) return;
    if (err && (err.code === 40900 || err.code === 40300)) {
      // 状态 / 权限类错误：原样展示服务端文案，并刷新可操作状态
      wx.showModal({ title: '无法保存', content: message, showCancel: false, confirmColor: '#1D1D1F', complete: () => this.loadDetail() });
      return;
    }
    wx.showToast({ title: message, icon: 'none', duration: 2500 });
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
