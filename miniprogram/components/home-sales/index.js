/**
 * 销售首页（原型 11）。数据：GET /api/orders/created + GET /api/orders/recent-customers。
 * 页面调用 refresh() → Promise。
 */
const api = require('../../api/index.js');
const format = require('../../utils/format.js');
const enums = require('../../utils/enums.js');

const FILTERS = [
  { key: 'all', label: '全部' },
  { key: 'wait', label: '待认领' },
  { key: 'doing', label: '进行中' },
  { key: 'check', label: '待验收' },
  { key: 'closed', label: '已闭环' }
];

/** '已发布 3 小时' */
function publishedAgo(createdAt) {
  const t = format.parseTime(createdAt);
  if (!t) return '已发布';
  const min = Math.max(0, Math.floor((format.now().getTime() - t.getTime()) / 60000));
  if (min < 1) return '刚刚发布';
  if (min < 60) return '已发布 ' + min + ' 分钟';
  if (min < 1440) return '已发布 ' + Math.floor(min / 60) + ' 小时';
  return '已发布 ' + Math.floor(min / 1440) + ' 天';
}

function dayHm(t) {
  return (format.isToday(t) ? '今日 ' : format.formatMonthDay(t) + ' ') + format.formatHm(t);
}

/** 按状态生成备注 */
function noteOf(o) {
  const who = (o.assignee && o.assignee.name) || '';
  const join = (s) => (who ? who + ' · ' + s : s);
  switch (o.status) {
    case 'Pending':
      return publishedAgo(o.createdAt) + '，暂无人认领';
    case 'Accepted':
      return join(o.appointmentTime ? '预约 ' + format.formatDateTimeShort(o.appointmentTime) : '待预约');
    case 'InProgress':
      if (o.onSiteSince) return join(dayHm(o.onSiteSince) + ' 到场');
      if (o.serviceType !== 'Onsite') return join(enums.label(enums.SERVICE_TYPE, o.serviceType) + '处理中');
      if (o.lastArriveTime) return join(dayHm(o.lastArriveTime) + ' 到场');
      return join('处理中');
    case 'OnHold':
      return join('暂停搁置中');
    case 'Submitted':
      return join(format.formatMonthDay(o.submittedAt) + '提交完工');
    case 'Closed':
      return join(format.formatMonthDay(o.closedAt) + '主管验收闭环');
    default:
      return who;
  }
}

function decorate(o) {
  const s = enums.STATUS[o.status] || {};
  return Object.assign({}, o, {
    priceText: format.formatPrice(o.price),
    free: Number(o.price) === 0,
    note: noteOf(o),
    segs: enums.progressSegments(o.status).map((x, i) => ({ i: i, on: x.on })),
    progressLabel: '进度：' + (s.label || '') + '，5 个阶段中的第 ' + (s.step || 0) + ' 个'
  });
}

Component({
  options: { addGlobalClass: true },

  data: {
    status: 'loading', // loading | ok | error
    errorText: '',
    filter: 'all',
    filters: FILTERS.map((f) => Object.assign({ count: 0 }, f)),
    items: [],
    listLoading: false,
    customers: []
  },

  methods: {
    refresh() {
      const first = this.data.status !== 'ok';
      if (first) this.setData({ status: 'loading', errorText: '' });
      api.orders.recentCustomers({ silent: true }).then((list) => {
        this.setData({ customers: (list || []).map((c, i) => Object.assign({ idx: i }, c)) });
      }).catch(() => {});
      return this.loadList().catch((err) => {
        const msg = (err && err.message) || '加载失败，请稍后重试';
        if (first) this.setData({ status: 'error', errorText: msg });
        else wx.showToast({ title: msg, icon: 'none' });
      });
    },

    loadList() {
      const seq = (this._seq = (this._seq || 0) + 1);
      const group = this.data.filter;
      this.setData({ listLoading: true });
      return api.orders.created(group, { silent: true }).then((d) => {
        if (seq !== this._seq) return;
        const counts = (d && d.counts) || {};
        this.setData({
          status: 'ok',
          listLoading: false,
          items: ((d && d.items) || []).map(decorate),
          filters: FILTERS.map((f) => Object.assign({}, f, { count: counts[f.key] || 0 }))
        });
      }, (err) => {
        if (seq === this._seq) this.setData({ listLoading: false });
        return Promise.reject(err);
      });
    },

    onFilter(e) {
      const key = e.currentTarget.dataset.key;
      if (!key || key === this.data.filter) return;
      this.setData({ filter: key });
      this.loadList().catch((err) => {
        wx.showToast({ title: (err && err.message) || '加载失败', icon: 'none' });
      });
    },

    onRetry() { this.refresh(); },

    toNew() { wx.navigateTo({ url: '/pages/order-new/index' }); },

    onCustomer(e) {
      const idx = e.currentTarget.dataset.idx;
      const c = this.data.customers[idx];
      if (!c) return;
      const app = getApp();
      const copy = Object.assign({}, c);
      delete copy.idx;
      app.globalData.prefillCustomer = copy;
      wx.navigateTo({ url: '/pages/order-new/index?customer=' + idx });
    },

    toAll() { wx.switchTab({ url: '/pages/orders/index' }); },

    toDetail(e) {
      const id = e.currentTarget.dataset.id;
      if (id) wx.navigateTo({ url: '/pages/order-detail/index?id=' + id });
    }
  }
});
