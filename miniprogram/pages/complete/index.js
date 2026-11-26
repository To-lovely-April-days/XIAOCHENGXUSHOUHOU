// 完工提交（原型 08）。
// v2.0：按服务方式区分要交的资料（formVersion: 2）
//   上门 Onsite ：维修前照片、维修后照片、现场维修登记表（客户签字版）—— 三样必传
//   远程 Remote ：沟通截图 / 录屏画面选填；必须选「客户确认方式」
//   返厂 Factory：维修照片、寄回前测试结果、返厂维修登记表 —— 三样必传
// 维修登记表之前已在「附件资料」里传过的也算。
const auth = require('../../utils/auth.js');
const api = require('../../api/index.js');
const enums = require('../../utils/enums.js');
const subscribe = require('../../utils/subscribe.js');

const HOME = '/pages/home/index';
const MAX_PHOTOS = 9;
const CONFIRM_OPTIONS = ['电话确认', '微信确认', '邮件确认', '其他'];

/** 各服务方式要显示的上传区；list = data.lists 里的键 */
const SECTIONS = {
  Onsite: [
    { list: 'before', title: '维修前照片', tip: '动手前先拍设备原始状态', required: true },
    { list: 'after', title: '维修后照片', tip: '处理完成后的状态', required: true },
    { list: 'forms', title: '现场维修登记表（客户签字版）', tip: '拍下客户签字的登记表', required: true, form: 'OnsiteRepairForm' }
  ],
  Remote: [
    { list: 'photos', title: '沟通截图', tip: '选填，聊天记录、报错画面、录屏截图', required: false }
  ],
  Factory: [
    { list: 'photos', title: '维修照片', tip: '至少 1 张', required: true },
    { list: 'forms', title: '返厂维修登记表', tip: '拍下填好的登记表', required: true, form: 'FactoryRepairForm' }
  ]
};
const emptyLists = () => ({ before: [], after: [], photos: [], forms: [] });

let seq = 0;
function fileItem(path, fileId) {
  seq += 1;
  return { key: 'f' + Date.now() + '_' + seq, path: path, fileId: fileId || null, status: fileId ? 'done' : 'uploading' };
}

Page(subscribe.bindTaps({
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
    mode: 'Onsite',      // 服务方式：Onsite | Remote | Factory
    sections: [],        // 当前服务方式的上传区（见 SECTIONS），含 existing = 之前已在附件里传过的登记表数
    lists: emptyLists(), // 各上传区的文件 {key,path,fileId,status}
    confirmOptions: CONFIRM_OPTIONS,
    customerConfirm: '', // 远程：客户确认方式
    testResult: '',      // 返厂：寄回前测试结果
    needWork: false,     // v2.1 负责人在这张单上还没写过工作内容：这里必须写
    workContent: '',
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
        const nodes = (o.factory && o.factory.nodes) || [];
        const shippedBack = nodes.some((n) => n.node === 'ShippedBack' && n.recorded);
        // v1.3：负责人有待确认的转交时不能提交完工（服务端同样拦截）
        if (o.pendingTransfer && o.myRole === 'Lead') blockText = '本单有待确认的转交，请先撤回转交后再提交完工';
        else if (o.status === 'OnHold') blockText = '工单已暂停搁置，恢复后才能提交完工';
        else if (o.status !== 'InProgress') blockText = '工单当前为「' + enums.label(enums.STATUS, o.status) + '」，仅「处理中」的工单可以提交完工';
        else if (o.myRole === 'Assistant') blockText = '你是本单协助人，完工由负责工程师提交';
        else if (o.serviceType === 'Factory' && o.myRole === 'Lead' && !shippedBack) blockText = '返厂工单请先记录「寄回客户」再提交完工';
        else blockText = '你不是该工单的负责工程师，无法提交完工';
      }
      const mode = SECTIONS[o.serviceType] ? o.serviceType : 'Onsite';
      const atts = o.attachments || [];
      const sections = SECTIONS[mode].map((sec) => {
        // 维修登记表之前已经在附件里传过：这里就不强制再传
        const existing = sec.form ? atts.filter((a) => a.attachmentType === sec.form).length : 0;
        return Object.assign({}, sec, { existing: existing, required: sec.required && !existing });
      });
      const me = auth.getUser() || {};
      const needWork = !(o.workLogs || []).some((l) => l.engineer && l.engineer.id === me.id);
      this.setData({
        loading: false,
        loadError: '',
        order: o,
        needWork: needWork,
        mode: mode,
        sections: sections,
        needSignature: needSignature,
        canSubmit: canSubmit,
        blockText: blockText,
        // v1.1：只看本人是否在现场（他人未离场由服务端在提交时提示）
        onSite: needSignature && !!(o.myOnSiteSince !== undefined ? o.myOnSiteSince : o.onSiteSince)
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
    const toItems = (list) => (list || []).map((f) => fileItem(f.url, f.id)).slice(0, MAX_PHOTOS);
    const all = files.processPhotos || [];
    const lists = emptyLists();
    if (o.serviceType === 'Onsite') {
      // 没标前后的旧照片归到「维修前」，让工程师自己再补「维修后」
      lists.after = toItems(all.filter((f) => f.stage === 'After'));
      lists.before = toItems(all.filter((f) => f.stage !== 'After'));
    } else {
      lists.photos = toItems(all);
    }
    this.setData({
      faultCause: c.faultCause || '',
      solution: c.solution || '',
      partsReplaced: c.partsReplaced || '',
      runStatus: c.runStatus || '',
      customerConfirm: c.customerConfirm || '',
      testResult: c.testResult || '',
      lists: lists
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

  onPickConfirm(e) {
    this.setData({ customerConfirm: e.currentTarget.dataset.value });
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
    const listKey = e.currentTarget.dataset.list; // before | after | photos | forms
    const category = this.categoryOf(listKey);
    const remain = MAX_PHOTOS - this.data.lists[listKey].length;
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
        this.setData({ ['lists.' + listKey]: this.data.lists[listKey].concat(items) });
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

  /** 登记表存成工单附件（提交时归到对应类别），其余都是完工照片 */
  categoryOf(listKey) {
    return listKey === 'forms' ? 'Attachment' : 'ProcessPhoto';
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
    return this.data.lists[listKey].filter((x) => x.key === key)[0] || null;
  },

  updateItem(listKey, key, patch) {
    const idx = this.data.lists[listKey].findIndex((x) => x.key === key);
    if (idx < 0) return; // 已被删除
    const upd = {};
    Object.keys(patch).forEach((k) => { upd['lists.' + listKey + '[' + idx + '].' + k] = patch[k]; });
    this.setData(upd);
  },

  onTapPhoto(e) {
    const listKey = e.currentTarget.dataset.list;
    const key = e.currentTarget.dataset.key;
    const item = this.findItem(listKey, key);
    if (!item) return;
    if (item.status === 'fail') {
      this.uploadItem(listKey, key, this.categoryOf(listKey));
      return;
    }
    wx.previewImage({
      current: item.path,
      urls: this.data.lists[listKey].map((x) => x.path)
    });
  },

  onRemovePhoto(e) {
    const listKey = e.currentTarget.dataset.list;
    const key = e.currentTarget.dataset.key;
    this.setData({ ['lists.' + listKey]: this.data.lists[listKey].filter((x) => x.key !== key) });
  },

  /* ---------- 提交 ---------- */
  validate() {
    const d = this.data;
    if (!d.faultCause.trim()) return d.mode === 'Onsite' ? '请填写现场确认的故障原因' : '请填写故障原因';
    if (!d.solution.trim()) return '请填写解决措施';
    if (!d.runStatus) return '请选择设备运行状况';
    if (d.needWork && !d.workContent.trim()) return '请填写工作内容';
    if (d.mode === 'Remote' && !d.customerConfirm) return '请选择客户确认方式';
    if (d.mode === 'Factory' && !d.testResult.trim()) return '请填写寄回前的测试结果';
    const shown = d.sections.map((sec) => d.lists[sec.list]);
    const all = shown.reduce((a, b) => a.concat(b), []);
    if (all.some((x) => x.status === 'uploading')) return '照片上传中，请稍候';
    if (all.some((x) => x.status === 'fail')) return '有照片上传失败，请点击重试或删除';
    const missing = d.sections.filter((sec) => sec.required && !d.lists[sec.list].length)[0];
    if (missing) return '请上传' + missing.title;
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
      formVersion: 2,
      processPhotoIds: d.lists.photos.map((x) => x.fileId),
      beforePhotoIds: d.lists.before.map((x) => x.fileId),
      afterPhotoIds: d.lists.after.map((x) => x.fileId),
      repairFormIds: d.sections.some((sec) => sec.list === 'forms') ? d.lists.forms.map((x) => x.fileId) : [],
      customerConfirm: d.mode === 'Remote' ? d.customerConfirm : '',
      testResult: d.mode === 'Factory' ? d.testResult.trim() : '',
      workContent: d.needWork ? d.workContent.trim() : ''
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
    // 只有本人未离场（文案恰为「请先离场打卡」）才引导去打卡
    if (err && err.code === 40900 && message === '请先离场打卡') {
      wx.showModal({
        title: '请先离场打卡',
        content: '你还在现场计时中，离开现场前请先打离场卡，再提交完工。',
        confirmText: '去打卡',
        confirmColor: '#1D1D1F',
        success: (r) => { if (r.confirm) this.goCheckin(); }
      });
      return;
    }
    // 其他 40900（他人未离场 / 返厂未寄回 / 已暂停 …）：原样展示服务端文案
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
  // v1.4：提交完工时顺带静默攒一条订阅消息额度（utils/subscribe.js topUp）
}, ['onSubmit']));
