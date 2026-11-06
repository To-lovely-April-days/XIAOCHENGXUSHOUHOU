// 我的工单（工程师，原型 10）：本人负责的工单，含已闭环历史
const api = require('../../api/index.js');
const auth = require('../../utils/auth.js');
const format = require('../../utils/format.js');

const FILTERS = [
  { key: 'doing', label: '进行中' },
  { key: 'check', label: '待验收' },
  { key: 'closed', label: '已闭环' },
  { key: 'all', label: '全部' }
];

/** 状态备注 */
function noteOf(o) {
  switch (o.status) {
    case 'InProgress':
      if (o.onSiteSince) return { text: '今日 ' + format.formatHm(o.onSiteSince) + ' 到场 · 计时中', running: true };
      if (o.lastArriveTime) return { text: '最近到场 ' + format.formatDateTimeShort(o.lastArriveTime) };
      return { text: o.serviceType === 'Onsite' ? '处理中' : '远程/返厂处理中' };
    case 'Accepted':
      return { text: o.appointmentTime ? '预约 ' + format.formatDateTimeShort(o.appointmentTime) : '待预约' };
    case 'OnHold':
      return { text: '暂停搁置，等待恢复' };
    case 'Submitted':
      return { text: o.submittedAt ? format.formatMonthDay(o.submittedAt) + '提交完工' : '已提交完工' };
    case 'Closed':
      return { text: o.closedAt ? format.formatMonthDay(o.closedAt) + '主管验收闭环' : '主管验收闭环' };
    default:
      return { text: '' };
  }
}

/** 现场工时：已配对 + 正在计时（加载时刻）；非现场单或无工时显示 — */
function hoursOf(o) {
  let min = o.totalMinutes || 0;
  if (o.onSiteSince) min += Math.floor(format.secondsBetween(o.onSiteSince) / 60);
  if (o.serviceType !== 'Onsite' || (!min && !o.onSiteSince)) return '—';
  return format.formatDuration(min);
}

function toRow(o) {
  const note = noteOf(o);
  return {
    id: o.id,
    customerName: o.customerName,
    status: o.status,
    priceText: format.formatPrice(o.price, true),
    priceFree: Number(o.price) === 0,
    device: [o.orderNo, o.deviceModel, o.symptom].filter(Boolean).join(' · '),
    note: note.text,
    running: !!note.running,
    hours: hoursOf(o)
  };
}

Page({
  data: {
    filter: 'doing',
    filters: FILTERS.map((f) => Object.assign({ count: 0 }, f)),
    rows: [],
    ready: false,
    error: ''
  },

  onLoad(query) {
    this._seq = 0;
    const g = query && query.group;
    if (g && FILTERS.some((f) => f.key === g)) this.setData({ filter: g });
  },

  onShow() {
    auth.ensureLogin().then(() => this.load()).catch(() => {});
  },

  onPullDownRefresh() {
    this.load().then(() => wx.stopPullDownRefresh(), () => wx.stopPullDownRefresh());
  },

  load() {
    const seq = ++this._seq;
    return api.orders.mine(this.data.filter, { silent: true }).then((res) => {
      if (seq !== this._seq) return;
      const counts = (res && res.counts) || {};
      this.setData({
        ready: true,
        error: '',
        rows: ((res && res.items) || []).map(toRow),
        filters: FILTERS.map((f) => Object.assign({ count: counts[f.key] || 0 }, f))
      });
    }).catch((err) => {
      if (seq !== this._seq) return;
      const msg = (err && err.message) || '加载失败';
      if (this.data.rows.length) {
        this.setData({ ready: true });
        wx.showToast({ title: msg, icon: 'none' });
      } else {
        this.setData({ ready: true, error: msg });
      }
    });
  },

  retry() {
    this.setData({ ready: false, error: '' });
    this.load();
  },

  onFilter(e) {
    const key = e.currentTarget.dataset.key;
    if (key === this.data.filter) return;
    this.setData({ filter: key, rows: [], ready: false, error: '' });
    this.load();
  },

  goDetail(e) {
    const id = e.currentTarget.dataset.id;
    if (id) wx.navigateTo({ url: '/pages/order-detail/index?id=' + id });
  },

  goHall() {
    wx.switchTab({ url: '/pages/orders/index' });
  }
});
