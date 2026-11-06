// 工单 tab：工程师 = 工单大厅（原型 05）；销售/主管 = 全部工单（原型 12，只读）
const api = require('../../api/index.js');
const auth = require('../../utils/auth.js');
const tabbar = require('../../utils/tabbar.js');
const format = require('../../utils/format.js');
const enums = require('../../utils/enums.js');

const PAGE_SIZE = 20;
const MAX_PAGE_SIZE = 200; // 与服务端 OrderService.Query 的 pageSize 上限一致（须为 PAGE_SIZE 的整数倍）

const HALL_FILTERS = [
  { key: 'all', label: '全部' },
  { key: 'urgent', label: '紧急' },
  { key: 'onsite', label: '现场' },
  { key: 'remote', label: '远程' }
];

// 全部工单状态筛选；countKey 对应 counts 的键
const STATUS_FILTERS = [
  { key: '', label: '全部', countKey: 'all' },
  { key: 'Pending', label: '待认领', countKey: 'Pending' },
  { key: 'Accepted', label: '待启动', countKey: 'Accepted' },
  { key: 'InProgress', label: '处理中', countKey: 'InProgress' },
  { key: 'Submitted', label: '待验收', countKey: 'Submitted' },
  { key: 'Closed', label: '已闭环', countKey: 'Closed' },
  { key: 'OnHold', label: '暂停搁置', countKey: 'OnHold' }
];

function typeLabel(o) {
  return enums.label(enums.SERVICE_TYPE, o.serviceType);
}

function toHallItem(o) {
  return {
    id: o.id,
    customerName: o.customerName,
    urgency: o.urgency,
    priceText: format.formatPrice(o.price),
    priceFree: Number(o.price) === 0,
    device: [o.deviceModel, o.deviceSn].filter(Boolean).join(' · '),
    fault: o.symptom ? '“' + o.symptom + '”' : '',
    meta: [typeLabel(o), o.region || o.city, format.timeAgo(o.createdAt)].filter(Boolean).join(' · ')
  };
}

function toListItem(o) {
  return {
    id: o.id,
    customerName: o.customerName,
    urgency: o.urgency,
    status: o.status,
    priceText: format.formatPrice(o.price, true),
    priceFree: Number(o.price) === 0,
    device: [o.orderNo, o.deviceModel, o.symptom].filter(Boolean).join(' · '),
    meta: (o.assignee ? o.assignee.name : '暂无处理人') + ' · ' + typeLabel(o)
  };
}

Page({
  data: {
    role: '',
    ready: false,      // 已完成首次加载（成功或失败）
    loading: false,
    error: '',
    loginError: '',    // ensureLogin 网络/服务端异常（尚无角色）

    // 工单大厅
    hallFilter: 'all',
    hallFilters: HALL_FILTERS.map((f) => Object.assign({ count: 0 }, f)),
    hallItems: [],
    hallTotal: 0,

    // 全部工单
    keyword: '',
    status: '',
    statusFilters: STATUS_FILTERS.map((f) => Object.assign({ count: 0 }, f)),
    items: [],
    total: 0,
    allCount: 0,
    page: 0,
    hasMore: false,
    loadingMore: false
  },

  onLoad() {
    this._seq = 0;
    this._claiming = false;
  },

  onShow() {
    tabbar.setTabSelected(this, 'orders');
    return this.login();
  },

  /** ensureLogin 后按角色加载；网络/服务端异常时展示错误态（可重试） */
  login() {
    return auth.ensureLogin().then((user) => {
      tabbar.setTabSelected(this, 'orders');
      const role = (user && user.role) || '';
      if (role !== this.data.role) {
        // 角色变化（换账号）时重置
        this.setData({ role: role, ready: false, error: '', hallItems: [], items: [], page: 0, hasMore: false });
      }
      this.refresh(true);
    }).catch((err) => {
      if (auth.isLoadFailed(err) && !this.data.role) this.setData({ loginError: err.message || '网络异常，请重试' });
    });
  },

  onHide() {
    clearTimeout(this._searchTimer);
  },

  onUnload() {
    clearTimeout(this._searchTimer);
  },

  onPullDownRefresh() {
    const p = this.data.role ? this.refresh(false) : this.login();
    p.then(() => wx.stopPullDownRefresh(), () => wx.stopPullDownRefresh());
  },

  onReachBottom() {
    if (this.data.role !== 'Engineer') this.loadMore();
  },

  /** keepPages：onShow 时保留已加载的页数，避免返回后列表被截断 */
  refresh(keepPages) {
    if (!this.data.role) return Promise.resolve();
    return this.data.role === 'Engineer' ? this.loadHall() : this.loadList(keepPages);
  },

  retry() {
    if (!this.data.role) {
      this.setData({ loginError: '' });
      this.login();
      return;
    }
    this.setData({ ready: false, error: '' });
    this.refresh(false);
  },

  /* ---------------- 工单大厅 ---------------- */

  loadHall() {
    const seq = ++this._seq;
    const filter = this.data.hallFilter;
    this.setData({ loading: true });
    return api.orders.hall(filter, { silent: true }).then((res) => {
      if (seq !== this._seq) return;
      const counts = (res && res.counts) || {};
      this.setData({
        ready: true,
        loading: false,
        error: '',
        hallItems: ((res && res.items) || []).map(toHallItem),
        hallTotal: counts.all || 0,
        hallFilters: HALL_FILTERS.map((f) => Object.assign({ count: counts[f.key] || 0 }, f))
      });
    }).catch((err) => {
      if (seq !== this._seq) return;
      this.onLoadError(err, this.data.hallItems.length > 0);
    });
  },

  onHallFilter(e) {
    const key = e.currentTarget.dataset.key;
    if (key === this.data.hallFilter) return;
    this.setData({ hallFilter: key, hallItems: [], ready: false, error: '' });
    this.loadHall();
  },

  onClaim(e) {
    const id = e.currentTarget.dataset.id;
    const name = e.currentTarget.dataset.name || '';
    if (!id || this._claiming) return;
    wx.showModal({
      title: '认领工单',
      content: '确认认领「' + name + '」？认领后由你负责跟进至闭环。',
      confirmText: '认领',
      confirmColor: enums.COLORS.text,
      success: (r) => {
        if (r.confirm) this.doClaim(id);
      }
    });
  },

  doClaim(id) {
    this._claiming = true;
    api.orders.claim(id, { silent: true, loading: '认领中' }).then(() => {
      this._claiming = false;
      wx.showToast({ title: '认领成功', icon: 'success', duration: 1200 });
      setTimeout(() => {
        wx.navigateTo({ url: '/pages/order-detail/index?id=' + id });
      }, 600);
    }).catch((err) => {
      this._claiming = false;
      wx.showToast({ title: (err && err.message) || '认领失败', icon: 'none', duration: 2500 });
      if (err && err.code === 40900) this.loadHall();
    });
  },

  goNew() {
    wx.navigateTo({ url: '/pages/order-new/index' });
  },

  /* ---------------- 全部工单 ---------------- */

  loadList(keepPages) {
    const seq = ++this._seq;
    const pages = keepPages ? Math.max(1, this.data.page) : 1;
    this.setData({ loading: true });
    return api.orders.list({
      status: this.data.status,
      keyword: this.data.keyword.trim(),
      page: 1,
      pageSize: Math.min(PAGE_SIZE * pages, MAX_PAGE_SIZE) // 服务端 pageSize 上限 200
    }, { silent: true }).then((res) => {
      if (seq !== this._seq) return;
      const counts = (res && res.counts) || {};
      const items = ((res && res.items) || []).map(toListItem);
      const total = (res && res.total) || 0;
      this.setData({
        ready: true,
        loading: false,
        error: '',
        items: items,
        total: total,
        allCount: counts.all || 0,
        // 按实际返回条数换算已加载页数，避免超出服务端上限时跳过中间的数据
        page: Math.max(1, Math.floor(items.length / PAGE_SIZE)),
        hasMore: items.length < total,
        loadingMore: false,
        statusFilters: STATUS_FILTERS.map((f) => Object.assign({ count: counts[f.countKey] || 0 }, f))
      });
    }).catch((err) => {
      if (seq !== this._seq) return;
      this.setData({ loadingMore: false });
      this.onLoadError(err, this.data.items.length > 0);
    });
  },

  loadMore() {
    if (!this.data.hasMore || this.data.loadingMore || this.data.loading) return;
    const seq = this._seq;
    const next = this.data.page + 1;
    this.setData({ loadingMore: true });
    api.orders.list({
      status: this.data.status,
      keyword: this.data.keyword.trim(),
      page: next,
      pageSize: PAGE_SIZE
    }, { silent: true }).then((res) => {
      if (seq !== this._seq) return; // 期间筛选条件已变化
      const exist = {};
      this.data.items.forEach((x) => { exist[x.id] = true; });
      const more = ((res && res.items) || []).filter((o) => !exist[o.id]).map(toListItem);
      const items = this.data.items.concat(more);
      const total = (res && res.total) || 0;
      this.setData({
        items: items,
        total: total,
        page: next,
        hasMore: more.length > 0 && items.length < total,
        loadingMore: false
      });
    }).catch((err) => {
      if (seq !== this._seq) return;
      this.setData({ loadingMore: false });
      wx.showToast({ title: (err && err.message) || '加载失败', icon: 'none' });
    });
  },

  onStatusFilter(e) {
    const key = e.currentTarget.dataset.key;
    if (key === this.data.status) return;
    this.setData({ status: key, items: [], page: 0, hasMore: false, ready: false, error: '' });
    this.loadList(false);
    wx.pageScrollTo({ scrollTop: 0, duration: 0 });
  },

  onSearchInput(e) {
    this.setData({ keyword: e.detail.value });
    clearTimeout(this._searchTimer);
    this._searchTimer = setTimeout(() => this.search(), 300);
  },

  onSearchConfirm() {
    clearTimeout(this._searchTimer);
    this.search();
  },

  onSearchClear() {
    clearTimeout(this._searchTimer);
    this.setData({ keyword: '' });
    this.search();
  },

  search() {
    this.setData({ page: 0, hasMore: false });
    this.loadList(false);
  },

  /* ---------------- 公共 ---------------- */

  /** 有旧数据时只 toast，否则显示错误态 */
  onLoadError(err, hasData) {
    const msg = (err && err.message) || '加载失败';
    if (hasData) {
      this.setData({ loading: false, ready: true });
      wx.showToast({ title: msg, icon: 'none' });
    } else {
      this.setData({ loading: false, ready: true, error: msg });
    }
  },

  goDetail(e) {
    const id = e.currentTarget.dataset.id;
    if (id) wx.navigateTo({ url: '/pages/order-detail/index?id=' + id });
  }
});
