// 我的工单（工程师，原型 10）：本人负责、协助或已转交（已退出）的工单，含已闭环历史（v1.1：不显示价格）
// v1.3：新增「已退出」分组（former）；已退出的行带「已转交 / 已改派」标记，只能查看
const api = require('../../api/index.js');
const auth = require('../../utils/auth.js');
const format = require('../../utils/format.js');
const enums = require('../../utils/enums.js');

const FILTERS = [
  { key: 'doing', label: '进行中' },
  { key: 'check', label: '待验收' },
  { key: 'closed', label: '已闭环' },
  { key: 'former', label: '已退出' },
  { key: 'all', label: '全部' }
];

/** 本人今天在本单未离场的到场时间（v1.1：myOnSiteSince；旧服务端兜底 onSiteSince） */
function mySince(o) {
  return o.myOnSiteSince !== undefined ? o.myOnSiteSince : o.onSiteSince;
}

/** 状态备注 */
function noteOf(o) {
  const factory = o.serviceType === 'Factory' && o.factoryText;
  switch (o.status) {
    case 'InProgress':
      if (mySince(o)) return { text: '今日 ' + format.formatHm(mySince(o)) + ' 到场 · 计时中', running: true };
      if (factory) return { text: o.factoryText };
      if (o.lastArriveTime) return { text: '最近到场 ' + format.formatDateTimeShort(o.lastArriveTime) };
      return { text: o.serviceType === 'Onsite' ? '处理中' : enums.label(enums.SERVICE_TYPE, o.serviceType) + '处理中' };
    case 'Accepted':
      if (factory) return { text: o.factoryText };
      return { text: o.appointmentTime ? '预约 ' + format.formatDateTimeShort(o.appointmentTime) : '待预约' };
    case 'OnHold': {
      const holdText = enums.holdTextOf(o);
      return { text: '暂停' + (holdText ? ' · ' + holdText : '搁置，等待恢复'), hold: true };
    }
    case 'Submitted':
      return { text: o.submittedAt ? format.formatMonthDay(o.submittedAt) + '提交完工' : '已提交完工' };
    case 'Closed':
      return { text: o.closedAt ? format.formatMonthDay(o.closedAt) + '主管验收闭环' : '主管验收闭环' };
    default:
      return { text: '' };
  }
}

/** 本人现场工时：已配对（myMinutes）+ 正在计时（加载时刻）；非现场单或无工时显示 — */
function hoursOf(o) {
  const since = mySince(o);
  let min = (o.myMinutes !== null && o.myMinutes !== undefined ? o.myMinutes : o.totalMinutes) || 0;
  if (since) min += Math.floor(format.secondsBetween(since) / 60);
  if (o.serviceType !== 'Onsite' || (!min && !since)) return '—';
  return format.formatDuration(min);
}

function toRow(o) {
  const former = o.myRole === 'Former';
  // 已退出：状态备注固定，不走 noteOf；工时仍显示本人工时
  const note = former ? { text: '已退出本单 · 仅可查看' } : noteOf(o);
  return {
    id: o.id,
    customerName: o.customerName,
    status: o.status,
    assist: o.myRole === 'Assistant',
    exitText: former ? (o.myExitText || '已退出') : '',
    // 本人是负责人且本单有待确认的转交
    transferPending: !!o.pendingTransfer && o.myRole === 'Lead',
    device: [o.orderNo, o.deviceText, o.symptom].filter(Boolean).join(' · '),
    note: note.text,
    running: !!note.running,
    hold: !!note.hold,
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
