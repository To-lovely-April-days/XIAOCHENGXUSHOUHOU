/**
 * 注册审批（v1.8）。管理员或有「注册审批权限」的人可用（user.canApprove）。
 * 数据：GET /api/registrations；通过 POST /api/registrations/{id}/approve；驳回 POST .../{id}/reject。
 * 这里不能授予管理员岗位 / 管理员权限，那要去电脑后台。
 * 微信「注册申请提醒」点进来也是这个页面。
 */
const api = require('../../api/index.js');
const auth = require('../../utils/auth.js');
const format = require('../../utils/format.js');
const subscribe = require('../../utils/subscribe.js');

const POSITIONS = [
  { key: 'Engineer', label: '售后工程师', dept: '售后部门' },
  { key: 'Sales', label: '销售', dept: '销售部门' },
  { key: 'SalesManager', label: '销售主管', dept: '销售部门' },
  { key: 'Supervisor', label: '售后主管', dept: '售后部门' }
];
const DEPARTMENTS = ['售后部门', '销售部门', '软件部门', '总经办'];

function row(x) {
  return {
    id: x.id,
    name: x.name,
    phone: x.phone,
    status: x.status,
    statusText: x.statusText,
    rejectReason: x.rejectReason || '',
    timeText: format.formatDateTimeShort(x.submittedAt),
    doneText: [x.reviewedByName, x.reviewedAt ? format.formatDateTimeShort(x.reviewedAt) : ''].filter(Boolean).join(' · ')
  };
}

Page({
  data: {
    status: 'loading', // loading | ok | error | denied
    errorText: '',
    pending: [],
    handled: [],
    showHandled: false,
    subShow: false,
    subText: '',
    // 通过 / 驳回的底部面板
    sheet: '',          // '' | 'approve' | 'reject'
    target: null,
    name: '',
    positions: POSITIONS.map((p) => p.label),
    positionIndex: -1,
    departments: DEPARTMENTS,
    departmentIndex: -1,
    reason: '',
    sheetError: '',
    busy: false
  },

  onShow() {
    auth.ensureLogin().then((user) => {
      if (!user || !user.canApprove) {
        this.setData({ status: 'denied' });
        return;
      }
      subscribe.refresh().then(() => this.applySubscribe());
      return this.load();
    }).catch(() => {});
  },

  onPullDownRefresh() {
    Promise.resolve(this.load()).then(() => wx.stopPullDownRefresh(), () => wx.stopPullDownRefresh());
  },

  load() {
    if (this.data.status !== 'ok') this.setData({ status: 'loading', errorText: '' });
    return api.registrations.list({ silent: true }).then((d) => {
      const items = (d && d.items) || [];
      this.setData({
        status: 'ok',
        pending: items.filter((x) => x.status === 'Pending').map(row),
        handled: items.filter((x) => x.status !== 'Pending').map(row)
      });
    }).catch((err) => {
      const denied = err && err.code === 40300;
      this.setData({ status: denied ? 'denied' : 'error', errorText: (err && err.message) || '加载失败，请稍后重试' });
    });
  },

  onRetry() {
    this.load();
  },

  toggleHandled() {
    this.setData({ showHandled: !this.data.showHandled });
  },

  /* ---------- 新申请提醒 ---------- */
  applySubscribe() {
    const show = subscribe.isEnabled();
    this.setData({ subShow: show, subText: show ? subscribe.statusText() : '' });
  },

  onSubscribe() {
    subscribe.open().then(() => this.applySubscribe());
  },

  /* ---------- 通过 ---------- */
  onApprove(e) {
    const t = this.data.pending[e.currentTarget.dataset.index];
    if (!t) return;
    this.setData({ sheet: 'approve', target: t, name: t.name, positionIndex: -1, departmentIndex: -1, sheetError: '' });
  },

  onName(e) {
    this.setData({ name: e.detail.value, sheetError: '' });
  },

  onPosition(e) {
    const i = Number(e.detail.value);
    const patch = { positionIndex: i, sheetError: '' };
    // 还没选部门时，按岗位带出常用部门
    if (this.data.departmentIndex < 0 && POSITIONS[i]) patch.departmentIndex = DEPARTMENTS.indexOf(POSITIONS[i].dept);
    this.setData(patch);
  },

  onDepartment(e) {
    this.setData({ departmentIndex: Number(e.detail.value), sheetError: '' });
  },

  submitApprove() {
    const d = this.data;
    if (d.busy || !d.target) return;
    const name = String(d.name || '').trim();
    if (!name) return this.setData({ sheetError: '请填写姓名' });
    if (name.length > 20) return this.setData({ sheetError: '姓名不能超过 20 个字' });
    if (d.positionIndex < 0) return this.setData({ sheetError: '请选择岗位' });
    if (d.departmentIndex < 0) return this.setData({ sheetError: '请选择部门' });
    this.setData({ busy: true, sheetError: '' });
    api.registrations.approve(d.target.id, {
      name: name, position: POSITIONS[d.positionIndex].key, department: DEPARTMENTS[d.departmentIndex]
    }, { silent: true }).then(() => {
      this.setData({ sheet: '', target: null });
      wx.showToast({ title: '已通过', icon: 'success' });
      subscribe.topUp(); // 处理完一条，顺便给下一条提醒攒额度（只在已「总是允许」时静默生效）
      return this.load();
    }).catch((err) => {
      this.setData({ sheetError: (err && err.message) || '操作失败，请重试' });
    }).then(() => this.setData({ busy: false }));
  },

  /* ---------- 驳回 ---------- */
  onReject(e) {
    const t = this.data.pending[e.currentTarget.dataset.index];
    if (!t) return;
    this.setData({ sheet: 'reject', target: t, reason: '', sheetError: '' });
  },

  onReason(e) {
    this.setData({ reason: e.detail.value, sheetError: '' });
  },

  submitReject() {
    const d = this.data;
    if (d.busy || !d.target) return;
    const reason = String(d.reason || '').trim();
    if (!reason) return this.setData({ sheetError: '请填写驳回原因' });
    this.setData({ busy: true, sheetError: '' });
    api.registrations.reject(d.target.id, reason, { silent: true }).then(() => {
      this.setData({ sheet: '', target: null });
      wx.showToast({ title: '已驳回', icon: 'none' });
      subscribe.topUp();
      return this.load();
    }).catch((err) => {
      this.setData({ sheetError: (err && err.message) || '操作失败，请重试' });
    }).then(() => this.setData({ busy: false }));
  },

  closeSheet() {
    if (this.data.busy) return;
    this.setData({ sheet: '', target: null });
  },

  noop() {}
});
