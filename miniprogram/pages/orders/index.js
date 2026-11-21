// 工单 tab：工程师 = 工单大厅（原型 05）；销售/主管 = 全部工单（手风琴聚焦清单，首标签默认「待认领」）
const api = require('../../api/index.js');
const auth = require('../../utils/auth.js');
const tabbar = require('../../utils/tabbar.js');
const format = require('../../utils/format.js');
const enums = require('../../utils/enums.js');
const system = require('../../utils/system.js');

const PAGE_SIZE = 20;
const MAX_PAGE_SIZE = 200; // 与服务端 OrderService.Query 的 pageSize 上限一致（须为 PAGE_SIZE 的整数倍）

const HALL_FILTERS = [
  { key: 'all', label: '全部' },
  { key: 'urgent', label: '紧急' },
  { key: 'onsite', label: '现场' },
  { key: 'remote', label: '远程' }
];

// 全部工单状态筛选：按流转顺序排列，首标签默认「待认领」，「全部」置末
const STATUS_FILTERS = [
  { key: 'Pending', label: '待认领', countKey: 'Pending' },
  { key: 'Accepted', label: '待启动', countKey: 'Accepted' },
  { key: 'InProgress', label: '处理中', countKey: 'InProgress' },
  { key: 'OnHold', label: '暂停', countKey: 'OnHold' },
  { key: 'Submitted', label: '待验收', countKey: 'Submitted' },
  { key: 'Closed', label: '已闭环', countKey: 'Closed' },
  { key: '', label: '全部', countKey: 'all' }
];

const SERVICE_FILTER_OPTIONS = [
  { key: '', label: '全部' },
  { key: 'Onsite', label: '现场' },
  { key: 'Remote', label: '远程' },
  { key: 'Factory', label: '返厂' }
];

const STATUS_STYLE = {
  Pending:    { label: '待认领',   dot: '#D97706', text: '#9A5B00', bar: '#1D1D1F', live: false },
  Accepted:   { label: '待启动',   dot: '#8E8E93', text: '#636366', bar: '#1D1D1F', live: false },
  InProgress: { label: '处理中',   dot: '#C81E2B', text: '#C81E2B', bar: '#C81E2B', live: true  },
  OnHold:     { label: '暂停搁置', dot: '#AEAEB2', text: '#636366', bar: '#1D1D1F', live: false },
  Submitted:  { label: '待验收',   dot: '#1D1D1F', text: '#1D1D1F', bar: '#1D1D1F', live: false },
  Closed:     { label: '已闭环',   dot: '#2F7A4B', text: '#2F7A4B', bar: '#2F7A4B', live: false },
  Voided:     { label: '已作废',   dot: '#C7C7C5', text: '#AEAEB2', bar: '#C7C7C5', live: false }
};

function typeLabel(o) {
  return enums.label(enums.SERVICE_TYPE, o.serviceType);
}

function tabLabelOf(statusKey) {
  const hit = STATUS_FILTERS.find((f) => f.key === statusKey);
  return hit ? hit.label : '工单';
}

function toHallItem(o) {
  return {
    id: o.id,
    customerName: o.customerName,
    urgency: o.urgency,
    device: o.deviceText || '',
    fault: o.symptom ? '“' + o.symptom + '”' : '',
    meta: [typeLabel(o), o.region || o.city, format.timeAgo(o.createdAt)].filter(Boolean).join(' · ')
  };
}

/** 全部工单（销售 / 主管）手风琴列表项 */
function toListItem(o, role) {
  const unpriced = o.priceSet === false || o.price === null || o.price === undefined;
  const st = STATUS_STYLE[o.status] || STATUS_STYLE.Pending;
  const urgent = o.urgency === 'Urgent';
  const assistants = Array.isArray(o.assistants) ? o.assistants : [];
  const assistCount = assistants.length;
  const assignee = o.assignee || null;
  const avatarStack = assignee ? [assignee].concat(assistants.slice(0, 2)) : [];
  const assistOverflow = Math.max(0, assistCount - 2);

  const holdText = o.status === 'OnHold' ? enums.holdTextOf(o) : '';
  let detailText = o.symptom || '';
  if (holdText) {
    detailText = '暂停：' + holdText;
  } else if (o.serviceType === 'Factory' && o.factoryText) {
    detailText = o.factoryText + (o.symptom ? ' · ' + o.symptom : '');
  }

  const step = enums.progressStepOf(o);
  const segs = enums.progressSegments(o).map((seg, idx) => ({
    label: enums.PROGRESS_LABELS[idx],
    current: idx + 1 === step,
    barColor: seg.paused ? '#B0B0B4' : (seg.on ? st.bar : '#ECECE8')
  }));

  const pendingReqCount = role === 'Supervisor' ? (o.pendingRequestCount || 0) : 0;
  let actionText = '查看详情';
  if (role === 'Supervisor' && o.status === 'Pending') {
    actionText = '去指派';
  } else if (role === 'Supervisor' && o.status === 'Submitted') {
    actionText = unpriced ? '定价验收' : '去验收';
  }

  return {
    id: o.id,
    orderNo: o.orderNo || '',
    projectNo: o.projectNo || '',
    customerName: o.customerName || '',
    city: o.city || o.region || '',
    serviceTypeLabel: typeLabel(o),
    deviceText: o.deviceText || '',
    detailText: detailText,
    status: o.status,
    statusLabel: st.label,
    statusDot: urgent ? '#C81E2B' : st.dot,
    statusTextColor: st.text,
    statusLive: st.live,
    urgent: urgent,
    hold: o.status === 'OnHold',
    priceText: format.formatPrice(o.price, false, o.priceSet),
    unpriced: unpriced,
    priceFree: !unpriced && Number(o.price) === 0,
    assignee: assignee,
    assistants: assistants,
    assistCount: assistCount,
    avatarStack: avatarStack,
    assistOverflow: assistOverflow,
    creatorName: (o.creator && o.creator.name) || '',
    timeAgoText: format.timeAgo(o.createdAt),
    segments: segs,
    pending: pendingReqCount > 0,
    pendingReqCount: pendingReqCount,
    transferPending: !!o.pendingTransfer,
    actionText: actionText
  };
}

function buildActivePills(serviceType, unpricedOnly, pendingReqOnly, role) {
  const pills = [];
  if (serviceType) {
    pills.push({ key: 'serviceType', label: enums.label(enums.SERVICE_TYPE, serviceType) });
  }
  if (unpricedOnly) {
    pills.push({ key: 'unpriced', label: '未定价' });
  }
  if (pendingReqOnly && role === 'Supervisor') {
    pills.push({ key: 'pendingReq', label: '待审批' });
  }
  return pills;
}

Page({
  data: {
    navTotalHeight: 64,
    role: '',
    ready: false,      // 已完成首次加载（成功或失败）
    loading: false,
    error: '',
    loginError: '',    // ensureLogin 网络/服务端异常（尚无角色）

    // 工单大厅（工程师）
    hallFilter: 'all',
    hallFilters: HALL_FILTERS.map((f) => Object.assign({ count: 0 }, f)),
    hallItems: [],
    hallTotal: 0,

    // 全部工单（销售 / 主管，手风琴聚焦清单）
    status: 'Pending',
    curTabLabel: '待认领',
    statusFilters: STATUS_FILTERS.map((f) => Object.assign({ count: 0 }, f)),
    serviceOptions: SERVICE_FILTER_OPTIONS,
    keyword: '',
    searchOpen: false,
    searchFocus: false,
    filterOpen: false,
    serviceType: '',
    unpricedOnly: false,
    pendingReqOnly: false,
    // 筛选抽屉草稿值（点“确认”才生效）
    draftServiceType: '',
    draftUnpricedOnly: false,
    draftPendingReqOnly: false,
    extraFilterCount: 0,
    activePills: [],

    expandedId: 0,
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
    this._userToggledExpand = false;
    const m = system.getNavMetrics();
    this.setData({ navTotalHeight: m.totalHeight });
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
        this._userToggledExpand = false;
        const pendingReqOnly = role === 'Supervisor' ? this.data.pendingReqOnly : false;
        const pills = buildActivePills(this.data.serviceType, this.data.unpricedOnly, pendingReqOnly, role);
        this.setData({
          role: role,
          ready: false,
          error: '',
          hallItems: [],
          items: [],
          expandedId: 0,
          page: 0,
          hasMore: false,
          pendingReqOnly: pendingReqOnly,
          activePills: pills,
          extraFilterCount: pills.length
        });
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

  /* ---------------- 工单大厅（工程师） ---------------- */

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

  /* ---------------- 全部工单（销售 / 主管） ---------------- */

  buildQueryParams(page, pageSize) {
    const q = {
      status: this.data.status,
      keyword: this.data.keyword.trim(),
      page: page,
      pageSize: pageSize
    };
    if (this.data.serviceType) q.serviceType = this.data.serviceType;
    if (this.data.unpricedOnly) q.priceSet = 'false';
    if (this.data.pendingReqOnly && this.data.role === 'Supervisor') q.pendingRequest = true;
    return q;
  },

  loadList(keepPages) {
    const seq = ++this._seq;
    const pages = keepPages ? Math.max(1, this.data.page) : 1;
    this.setData({ loading: true });
    const params = this.buildQueryParams(1, Math.min(PAGE_SIZE * pages, MAX_PAGE_SIZE));
    return api.orders.list(params, { silent: true }).then((res) => {
      if (seq !== this._seq) return;
      const counts = (res && res.counts) || {};
      const role = this.data.role;
      const items = ((res && res.items) || []).map((o) => toListItem(o, role));
      const total = (res && res.total) || 0;

      // 保持用户已展开的项；否则默认展开第 1 条（除非用户刚手动收起）
      let expandedId = this.data.expandedId;
      const stillInList = expandedId && items.some((x) => x.id === expandedId);
      if (!stillInList) {
        expandedId = (!this._userToggledExpand && items.length) ? items[0].id : 0;
      }

      this.setData({
        ready: true,
        loading: false,
        error: '',
        items: items,
        expandedId: expandedId,
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
    const seq = ++this._seq;
    const next = this.data.page + 1;
    this.setData({ loadingMore: true });
    api.orders.list(this.buildQueryParams(next, PAGE_SIZE), { silent: true }).then((res) => {
      if (seq !== this._seq) return; // 期间筛选条件已变化
      const exist = {};
      this.data.items.forEach((x) => { exist[x.id] = true; });
      const role = this.data.role;
      const more = ((res && res.items) || []).filter((o) => !exist[o.id]).map((o) => toListItem(o, role));
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
    this._userToggledExpand = false;
    this.setData({
      status: key,
      curTabLabel: tabLabelOf(key),
      items: [],
      expandedId: 0,
      page: 0,
      hasMore: false,
      ready: false,
      error: ''
    });
    this.loadList(false);
    wx.pageScrollTo({ scrollTop: 0, duration: 0 });
  },

  /* ---------- 手风琴展开 / 收起 ---------- */

  onExpand(e) {
    const id = Number(e.currentTarget.dataset.id);
    if (!id) return;
    this._userToggledExpand = true;
    this.setData({ expandedId: id });
  },

  onCollapse() {
    this._userToggledExpand = true;
    this.setData({ expandedId: 0 });
  },

  /* ---------- 搜索 ---------- */

  onSearchOpen() {
    this.setData({ searchOpen: true, searchFocus: true });
  },

  onSearchCancel() {
    clearTimeout(this._searchTimer);
    const hadKw = !!this.data.keyword.trim();
    this.setData({ searchOpen: false, searchFocus: false, keyword: '' });
    if (hadKw) this.search();
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
    this.setData({ keyword: '', searchFocus: true });
    this.search();
  },

  search() {
    this._userToggledExpand = false;
    this.setData({ page: 0, hasMore: false, expandedId: 0 });
    this.loadList(false);
  },

  /* ---------- 多维筛选抽屉 ---------- */

  onFilterOpen() {
    this.setData({
      filterOpen: true,
      draftServiceType: this.data.serviceType,
      draftUnpricedOnly: this.data.unpricedOnly,
      draftPendingReqOnly: this.data.pendingReqOnly
    });
  },

  onFilterClose() {
    this.setData({ filterOpen: false });
  },

  onDraftServiceType(e) {
    this.setData({ draftServiceType: e.currentTarget.dataset.key || '' });
  },

  onToggleDraftUnpriced() {
    this.setData({ draftUnpricedOnly: !this.data.draftUnpricedOnly });
  },

  onToggleDraftPendingReq() {
    this.setData({ draftPendingReqOnly: !this.data.draftPendingReqOnly });
  },

  onFilterReset() {
    this.setData({
      draftServiceType: '',
      draftUnpricedOnly: false,
      draftPendingReqOnly: false
    });
  },

  onFilterApply() {
    const serviceType = this.data.draftServiceType;
    const unpricedOnly = this.data.draftUnpricedOnly;
    const pendingReqOnly = this.data.role === 'Supervisor' ? this.data.draftPendingReqOnly : false;
    const pills = buildActivePills(serviceType, unpricedOnly, pendingReqOnly, this.data.role);
    this._userToggledExpand = false;
    this.setData({
      filterOpen: false,
      serviceType: serviceType,
      unpricedOnly: unpricedOnly,
      pendingReqOnly: pendingReqOnly,
      activePills: pills,
      extraFilterCount: pills.length,
      items: [],
      expandedId: 0,
      page: 0,
      hasMore: false,
      ready: false,
      error: ''
    });
    this.loadList(false);
    wx.pageScrollTo({ scrollTop: 0, duration: 0 });
  },

  onRemovePill(e) {
    const key = e.currentTarget.dataset.key;
    let serviceType = this.data.serviceType;
    let unpricedOnly = this.data.unpricedOnly;
    let pendingReqOnly = this.data.pendingReqOnly;
    if (key === 'serviceType') serviceType = '';
    if (key === 'unpriced') unpricedOnly = false;
    if (key === 'pendingReq') pendingReqOnly = false;
    const pills = buildActivePills(serviceType, unpricedOnly, pendingReqOnly, this.data.role);
    this._userToggledExpand = false;
    this.setData({
      serviceType: serviceType,
      unpricedOnly: unpricedOnly,
      pendingReqOnly: pendingReqOnly,
      activePills: pills,
      extraFilterCount: pills.length,
      items: [],
      expandedId: 0,
      page: 0,
      hasMore: false,
      ready: false,
      error: ''
    });
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
  },

  noop() {}
});
