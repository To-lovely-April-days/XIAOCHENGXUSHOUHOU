// 工单（#/orders）——原型 16 主体：本周概况 + 状态 tab + 筛选 + 工单表格 + 右侧详情抽屉。
// 路由 query（均可选）：
//   quick=missing|distance|submitted  侧栏「待处理」快捷筛选（见 enums.QUICK_FILTERS）
//   status=<OrderStatus>              状态 tab（'' = 全部，不含已作废）
//   keyword=<string>                  搜索工单号/客户/设备
//   assigneeId=<id>                   处理人
//   range=week|month                  建单时间：本周（本周一~今天）/ 本月
//   abnormal=1                        只看异常（缺卡 ∪ 距离异常）
//   page=<n>                          页码
//   id=<orderId>                      打开详情抽屉
import * as api from '../api.js';
import { route, updateQuery } from '../router.js';
import { store } from '../store.js';
import { toast, toastError } from '../ui.js';
import { fmtHM, fmtHours, fmtPrice, fmtMDHM, isWarranty, parseTime, thisWeekRange, monthRange, toApiDate } from '../format.js';
import { SERVICE_TYPE, STATUS_TABS, QUICK_FILTERS, label } from '../enums.js';
import OrdersDrawer from './orders-drawer.js';
import OrdersCreateModal from './orders-create.js';
import OrdersExportModal from './orders-export.js';

const PAGE_SIZE = 20;
const MAX_PAGE_SIZE = 200;

/** 列表 tab：原型 7 个 + 末尾「已作废」 */
const TABS = STATUS_TABS.concat([{ key: 'Voided', label: '已作废', countKey: null }]);

const RANGE_OPTIONS = [
  { key: '', label: '全部时间' },
  { key: 'week', label: '本周' },
  { key: 'month', label: '本月' },
];

/** 拉取某条件下的全部工单（逐页，每页 200），用于「只看异常」两次查询合并 */
async function fetchAll(params) {
  let page = 1;
  let items = [];
  let res = null;
  for (;;) {
    res = await api.orders.list(Object.assign({}, params, { page, pageSize: MAX_PAGE_SIZE }));
    items = items.concat(res.items || []);
    if (!res.items || !res.items.length || items.length >= (res.total || 0)) break;
    page += 1;
  }
  return { items, total: items.length, counts: (res && res.counts) || {} };
}

/** 建单时间倒序（与后端一致：createdAt desc, id desc） */
function byCreatedDesc(a, b) {
  if (a.createdAt !== b.createdAt) return a.createdAt < b.createdAt ? 1 : -1;
  return b.id - a.id;
}

export default {
  name: 'OrdersView',
  components: { OrdersDrawer, OrdersCreateModal, OrdersExportModal },
  data() {
    return {
      route,
      tabs: TABS,
      rangeOptions: RANGE_OPTIONS,
      keywordInput: '',
      // 列表
      items: [],
      total: 0,
      counts: {},
      listLoading: false,
      listError: '',
      listKey: '',
      listSeq: 0,
      checked: {},
      // 概况
      overview: null,
      overviewLoading: false,
      overviewError: '',
      serverOffset: 0, // 服务器时间 - 本地时间（ms），用于实时累计现场计时
      nowTs: Date.now(),
      // 筛选
      engineers: [],
      engineersError: '',
      menu: '', // 'assignee' | 'range' | ''
      // 抽屉 / 弹窗
      selectedId: null,
      showCreate: false,
      showExport: false,
    };
  },
  computed: {
    q() { return route.query || {}; },
    quick() { return QUICK_FILTERS.find(f => f.key === this.q.quick) || null; },
    status() {
      if (this.q.status !== undefined) return this.q.status;
      return (this.quick && this.quick.query.status) || '';
    },
    quickIssue() { return (this.quick && this.quick.query.issue) || ''; },
    /** 'MissingPunch' | 'DistanceAbnormal' | 'any'（两种合并）| '' */
    issueMode() {
      if (this.quickIssue) return this.quickIssue;
      return this.q.abnormal === '1' ? 'any' : '';
    },
    assigneeId() { return this.q.assigneeId ? Number(this.q.assigneeId) : null; },
    range() { return this.q.range === 'week' || this.q.range === 'month' ? this.q.range : ''; },
    page() { return Math.max(1, parseInt(this.q.page, 10) || 1); },
    dateRange() {
      if (this.range === 'week') return thisWeekRange(this.serverNow());
      if (this.range === 'month') {
        const n = this.serverNow();
        return { from: monthRange(n.getFullYear(), n.getMonth() + 1).from, to: toApiDate(n) };
      }
      return { from: '', to: '' };
    },
    baseParams() {
      return {
        status: this.status || '',
        keyword: (this.q.keyword || '').trim(),
        assigneeId: this.assigneeId || '',
        from: this.dateRange.from,
        to: this.dateRange.to,
      };
    },
    currentListKey() {
      return JSON.stringify([this.baseParams, this.issueMode, this.page]);
    },
    crumb() {
      if (this.quick) return this.quick.label;
      const t = TABS.find(x => x.key === this.status);
      return t && t.key ? t.label : '全部工单';
    },
    tabList() {
      return TABS.map(t => ({
        ...t,
        active: t.key === this.status,
        count: t.countKey ? (this.counts[t.countKey] ?? '') : '',
      }));
    },
    assigneeLabel() {
      if (!this.assigneeId) return '处理人';
      const e = this.engineers.find(x => x.user.id === this.assigneeId);
      return e ? e.user.name : '处理人';
    },
    rangeLabel() {
      const r = RANGE_OPTIONS.find(x => x.key === this.range);
      return r && r.key ? r.label : '建单时间';
    },
    stats() {
      const o = this.overview;
      const v = (x) => (o ? String(x) : '—');
      return [
        { label: '待认领', value: v(o && o.pending), note: o ? `紧急 ${o.urgentPending}` : '', danger: !!(o && o.urgentPending) },
        { label: '处理中', value: v(o && o.inProgress), note: o ? `${o.onSiteEngineers} 人在现场` : '' },
        { label: '待验收', value: v(o && o.submitted), note: o ? (o.submitted ? '需确认闭环' : '已处理完') : '' },
        { label: '本周闭环', value: v(o && o.closedThisWeek), note: '件' },
        { label: '本周现场工时', value: o ? fmtHours(o.weekMinutes) : '—', note: '小时' },
      ];
    },
    updatedText() {
      return this.overview && this.overview.updatedAt ? fmtMDHM(this.overview.updatedAt) : '—';
    },
    rows() {
      return this.items.map(o => ({
        o,
        key: o.id,
        no: o.orderNo,
        customer: o.customerName,
        device: [o.deviceModel, o.symptom].filter(Boolean).join(' · '),
        urgent: o.urgency === 'Urgent',
        issues: o.issues || [],
        type: label(SERVICE_TYPE, o.serviceType),
        price: fmtPrice(o.price),
        warranty: isWarranty(o.price),
        hours: this.rowHours(o),
        selected: o.id === this.selectedId,
        checked: !!this.checked[o.id],
      }));
    },
    allChecked() { return this.items.length > 0 && this.items.every(o => this.checked[o.id]); },
    checkedCount() { return Object.keys(this.checked).filter(k => this.checked[k]).length; },
    emptyText() {
      if (this.q.keyword) return `没有找到与「${this.q.keyword}」相关的工单`;
      return '没有符合条件的工单';
    },
  },
  watch: {
    'route.query': {
      deep: true,
      immediate: true,
      handler() {
        if (route.path !== '/orders') return;
        const kw = this.q.keyword || '';
        if (kw !== this.keywordInput) this.keywordInput = kw;
        if (this.currentListKey !== this.listKey) this.loadList();
        const id = this.q.id ? Number(this.q.id) : null;
        if (id !== this.selectedId) this.selectedId = id;
      },
    },
  },
  created() {
    this.loadOverview();
    this.loadEngineers();
  },
  mounted() {
    this._tick = setInterval(() => { this.nowTs = Date.now(); }, 30000);
    this._onDoc = (e) => {
      if (!this.menu) return;
      const box = this.$refs[this.menu + 'Menu'];
      if (box && !box.contains(e.target)) this.menu = '';
    };
    document.addEventListener('mousedown', this._onDoc);
  },
  beforeUnmount() {
    clearInterval(this._tick);
    document.removeEventListener('mousedown', this._onDoc);
  },
  methods: {
    serverNow() { return new Date(this.nowTs + this.serverOffset); },

    /** 现场工时：已配对 + 今天正在计时；非现场单或无打卡显示 — */
    rowHours(o) {
      if (o.serviceType !== 'Onsite') return '—';
      let m = o.totalMinutes || 0;
      const since = parseTime(o.onSiteSince);
      if (since) m += Math.max(0, Math.floor((this.nowTs + this.serverOffset - since.getTime()) / 60000));
      if (!m && !since && !o.lastArriveTime) return '—';
      return fmtHM(m);
    },

    // ------------------------------------------------ 数据
    async loadList() {
      const key = this.currentListKey;
      this.listKey = key;
      const seq = ++this.listSeq;
      this.listLoading = true;
      this.listError = '';
      try {
        let res;
        const base = this.baseParams;
        if (this.issueMode === 'any') {
          const [a, b] = await Promise.all([
            fetchAll(Object.assign({}, base, { issue: 'MissingPunch' })),
            fetchAll(Object.assign({}, base, { issue: 'DistanceAbnormal' })),
          ]);
          const map = new Map();
          a.items.concat(b.items).forEach(o => map.set(o.id, o));
          const merged = Array.from(map.values()).sort(byCreatedDesc);
          const start = (this.page - 1) * PAGE_SIZE;
          res = { items: merged.slice(start, start + PAGE_SIZE), total: merged.length, counts: a.counts };
        } else {
          res = await api.orders.list(Object.assign({}, base, {
            issue: this.issueMode || '', page: this.page, pageSize: PAGE_SIZE,
          }));
        }
        if (seq !== this.listSeq) return;
        this.items = res.items || [];
        this.total = res.total || 0;
        this.counts = res.counts || {};
        // 当前页超出范围（如操作后数量减少）→ 回到最后一页
        const pages = Math.max(1, Math.ceil(this.total / PAGE_SIZE));
        if (this.page > pages) updateQuery({ page: pages > 1 ? pages : null });
      } catch (e) {
        if (seq !== this.listSeq) return;
        this.listError = e.message || '加载失败';
        this.listKey = ''; // 允许同条件重试
        if (e.code !== 40100) toastError(e);
      } finally {
        if (seq === this.listSeq) this.listLoading = false;
      }
    },
    reloadList() { this.listKey = ''; this.loadList(); },
    async loadOverview() {
      this.overviewLoading = true;
      this.overviewError = '';
      try {
        const o = await api.admin.overview();
        this.overview = o;
        const t = parseTime(o && o.updatedAt);
        if (t) { this.serverOffset = t.getTime() - Date.now(); this.nowTs = Date.now(); }
      } catch (e) {
        this.overviewError = e.message || '加载失败';
      } finally {
        this.overviewLoading = false;
      }
    },
    async loadEngineers() {
      this.engineersError = '';
      try {
        this.engineers = (await api.engineers.list()) || [];
      } catch (e) {
        this.engineersError = e.message || '加载失败';
      }
    },
    refreshAll() {
      this.reloadList();
      this.loadOverview();
      this.loadEngineers();
      store.refreshCounts();
    },

    // ------------------------------------------------ 筛选
    onSearch(v) {
      const kw = (v || '').trim();
      if (kw === (this.q.keyword || '')) return;
      updateQuery({ keyword: kw, page: null });
    },
    pickTab(t) {
      const patch = { status: t.key, page: null };
      if (this.quick && this.quick.query.status) patch.quick = null; // 「待验收」快捷筛选与状态 tab 互斥
      updateQuery(patch);
    },
    toggleMenu(name) { this.menu = this.menu === name ? '' : name; },
    pickAssignee(id) { this.menu = ''; updateQuery({ assigneeId: id || null, page: null }); },
    pickRange(key) { this.menu = ''; updateQuery({ range: key || null, page: null }); },
    toggleIssue() {
      if (this.issueMode) {
        updateQuery({ abnormal: null, quick: this.quickIssue ? null : this.q.quick, page: null });
      } else {
        updateQuery({ abnormal: '1', page: null });
      }
    },
    goPage(p) { updateQuery({ page: p > 1 ? p : null }); },

    // ------------------------------------------------ 选择 / 抽屉
    toggleCheck(o) { this.checked = Object.assign({}, this.checked, { [o.id]: !this.checked[o.id] }); },
    toggleAll() {
      const on = !this.allChecked;
      const next = Object.assign({}, this.checked);
      this.items.forEach(o => { next[o.id] = on; });
      this.checked = next;
    },
    openRow(o) {
      this.selectedId = o.id;
      updateQuery({ id: o.id });
    },
    onRowKey(e, o) {
      if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); this.openRow(o); }
    },
    closeDrawer() {
      this.selectedId = null;
      updateQuery({ id: null });
    },
    /** 抽屉内操作成功：刷新列表、概况、侧栏计数；工程师负载也可能变化 */
    onDrawerChanged() { this.refreshAll(); },

    // ------------------------------------------------ 新建 / 导出
    onCreated(detail) {
      this.showCreate = false;
      toast(`工单已创建 ${detail.orderNo}`, 'success');
      this.refreshAll();
      if (detail && detail.id) this.openRow(detail);
    },
  },
  template: `
  <PageHeader :crumbs="['工单', crumb]">
    <SearchBox v-model="keywordInput" placeholder="搜索工单号、客户、设备编号" @search="onSearch" />
    <button type="button" class="btn btn-secondary" @click="showExport = true"><Icon name="download" :size="15" />导出台账</button>
    <button type="button" class="btn btn-primary" @click="showCreate = true"><Icon name="plus" :size="15" :stroke="2.2" />新建工单</button>
  </PageHeader>

  <div class="page">
    <div class="page-head">
      <div>
        <h1>工单</h1>
        <div class="page-sub">数据更新于 <span class="num">{{ updatedText }}</span> · 工时按打卡记录实时计算</div>
      </div>
      <div class="page-actions">
        <button type="button" class="btn btn-ghost btn-sm" :disabled="listLoading || overviewLoading" @click="refreshAll" title="刷新数据">
          <Icon name="refresh" :size="14" />刷新</button>
      </div>
    </div>

    <section class="stats orders-stats" aria-label="本周概况">
      <div v-for="s in stats" :key="s.label" class="stat">
        <div class="stat-label">{{ s.label }}</div>
        <div class="stat-main">
          <span class="stat-value">{{ s.value }}</span>
          <span class="stat-note" :class="{ danger: s.danger }">{{ s.note }}</span>
        </div>
      </div>
      <div v-if="overviewError" class="orders-stats-error">
        <Icon name="alert" :size="14" />概况加载失败：{{ overviewError }}
        <button type="button" class="btn-link" @click="loadOverview">重试</button>
      </div>
    </section>

    <section class="card" aria-label="工单列表">
      <div class="toolbar">
        <div class="tabs" role="tablist" aria-label="按状态筛选">
          <button v-for="t in tabList" :key="t.key || 'all'" type="button" role="tab" class="tab"
                  :class="{ active: t.active }" :aria-selected="t.active ? 'true' : 'false'" @click="pickTab(t)">
            {{ t.label }} <span v-if="t.count !== ''" class="tab-count">{{ t.count }}</span>
          </button>
        </div>
        <div class="toolbar-right">
          <div class="dropdown" ref="assigneeMenu">
            <button type="button" class="btn btn-secondary switch-btn" :aria-checked="assigneeId ? 'true' : 'false'"
                    aria-haspopup="menu" :aria-expanded="menu === 'assignee' ? 'true' : 'false'" @click="toggleMenu('assignee')">
              {{ assigneeLabel }}<Icon name="chevronDown" :size="14" /></button>
            <div v-if="menu === 'assignee'" class="menu orders-menu" role="menu">
              <button type="button" class="menu-item" :class="{ active: !assigneeId }" role="menuitemradio"
                      :aria-checked="!assigneeId ? 'true' : 'false'" @click="pickAssignee(null)">全部处理人</button>
              <button v-for="e in engineers" :key="e.user.id" type="button" class="menu-item" role="menuitemradio"
                      :class="{ active: assigneeId === e.user.id }" :aria-checked="assigneeId === e.user.id ? 'true' : 'false'"
                      @click="pickAssignee(e.user.id)">
                <Avatar :user="e.user" :size="20" /><span class="ellipsis">{{ e.user.name }}</span>
                <span v-if="e.onSiteNow" class="orders-menu-note">在现场</span>
              </button>
              <div v-if="engineersError" class="orders-menu-msg text-danger">
                {{ engineersError }} <button type="button" class="btn-link" @click="loadEngineers">重试</button></div>
              <div v-else-if="!engineers.length" class="orders-menu-msg">暂无工程师</div>
            </div>
          </div>
          <div class="dropdown" ref="rangeMenu">
            <button type="button" class="btn btn-secondary switch-btn" :aria-checked="range ? 'true' : 'false'"
                    aria-haspopup="menu" :aria-expanded="menu === 'range' ? 'true' : 'false'" @click="toggleMenu('range')"
                    :title="range ? ('建单日期 ' + dateRange.from + ' ~ ' + dateRange.to) : '按建单日期筛选'">
              {{ rangeLabel }}<Icon name="chevronDown" :size="14" /></button>
            <div v-if="menu === 'range'" class="menu orders-menu" role="menu">
              <button v-for="r in rangeOptions" :key="r.key || 'all'" type="button" class="menu-item" role="menuitemradio"
                      :class="{ active: range === r.key }" :aria-checked="range === r.key ? 'true' : 'false'"
                      @click="pickRange(r.key)">{{ r.label }}</button>
            </div>
          </div>
          <button type="button" role="switch" class="btn btn-secondary switch-btn" :aria-checked="issueMode ? 'true' : 'false'"
                  @click="toggleIssue" title="缺卡或打卡距离异常的工单">
            <span class="switch" :class="{ on: !!issueMode }" aria-hidden="true"></span>只看异常</button>
        </div>
      </div>

      <div class="grid-table" role="table" aria-label="工单" :aria-busy="listLoading ? 'true' : 'false'">
        <div class="grid-head" role="row">
          <span role="columnheader">
            <span class="checkbox-box orders-check" :class="{ on: allChecked }" role="checkbox"
                  :aria-checked="allChecked ? 'true' : 'false'" aria-label="全选本页" tabindex="0"
                  @click="toggleAll" @keydown.enter.prevent="toggleAll" @keydown.space.prevent="toggleAll">
              <Icon v-if="allChecked" name="check" :size="10" :stroke="3" /></span>
          </span>
          <span role="columnheader">工单号</span>
          <span role="columnheader">客户 / 设备</span>
          <span role="columnheader">类型</span>
          <span role="columnheader">处理人</span>
          <span role="columnheader">状态</span>
          <span role="columnheader" class="text-right">价格</span>
          <span role="columnheader" class="text-right">现场工时</span>
        </div>

        <Loading v-if="listLoading && !items.length" />
        <Empty v-else-if="listError && !items.length" :text="'工单加载失败：' + listError">
          <button type="button" class="btn btn-secondary btn-sm" @click="reloadList">重试</button>
        </Empty>
        <Empty v-else-if="!items.length" :text="emptyText" />
        <div v-else :class="{ 'orders-dim': listLoading }">
          <div v-for="r in rows" :key="r.key" class="grid-row" role="row" tabindex="0"
               :class="{ selected: r.selected }" :aria-selected="r.selected ? 'true' : 'false'"
               @click="openRow(r.o)" @keydown="onRowKey($event, r.o)">
            <span role="cell">
              <span class="checkbox-box orders-check" :class="{ on: r.checked }" role="checkbox"
                    :aria-checked="r.checked ? 'true' : 'false'" :aria-label="'选择 ' + r.no" tabindex="-1"
                    @click.stop="toggleCheck(r.o)"><Icon v-if="r.checked" name="check" :size="10" :stroke="3" /></span>
            </span>
            <span role="cell" class="cell-no">{{ r.no }}</span>
            <span role="cell" style="min-width: 0">
              <span class="cell-title">
                <span class="cell-title-text">{{ r.customer }}</span>
                <Tag v-if="r.urgent" tone="urgent">紧急</Tag>
                <IssueTag v-for="t in r.issues" :key="t" :type="t" />
              </span>
              <span class="cell-sub">{{ r.device }}</span>
            </span>
            <span role="cell" class="orders-type">{{ r.type }}</span>
            <span role="cell" class="cell-user" :class="{ unassigned: !r.o.assignee }">
              <Avatar v-if="r.o.assignee" :user="r.o.assignee" :size="20" />
              <span class="ellipsis">{{ r.o.assignee ? r.o.assignee.name : '未分配' }}</span>
            </span>
            <span role="cell"><StatusDot :status="r.o.status" /></span>
            <span role="cell" class="cell-num orders-price" :class="{ 'text-success': r.warranty }">{{ r.price }}</span>
            <span role="cell" class="cell-num">{{ r.hours }}</span>
          </div>
        </div>
      </div>

      <div class="table-foot">
        <span>共 <span class="num">{{ total }}</span> 条<template v-if="checkedCount"> · 已选 <span class="num">{{ checkedCount }}</span> 条</template> · 导出台账的列与 SOP 进度台账一致</span>
        <Pagination :total="total" :page="page" :page-size="20" @change="goPage" />
      </div>
    </section>
  </div>

  <OrdersDrawer v-if="selectedId" :key="selectedId" :order-id="selectedId" :server-offset="serverOffset" :now-ts="nowTs"
                @close="closeDrawer" @changed="onDrawerChanged" />
  <OrdersCreateModal v-if="showCreate" @close="showCreate = false" @created="onCreated" />
  <OrdersExportModal v-if="showExport" :default-status="status" @close="showExport = false" />
  `,
};
