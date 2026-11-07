// 打卡记录（#/punches）。数据：GET /api/admin/punches（API.md §9），工程师下拉：GET /api/engineers。
// 查询参数全部放在 hash 上：?from=&to=&engineerId=&abnormal=&page=
import * as api from '../api.js';
import { route, navigate, updateQuery } from '../router.js';
import { toastError, preview } from '../ui.js';
import { fmtMD, fmtMDHM, fmtWeekday, fmtDistance, toApiDate, lastDaysRange, thisWeekRange, monthRange, parseTime } from '../format.js';
import { label, PUNCH_KIND } from '../enums.js';

const PAGE_SIZE = 50;

/** 异常 tab：key 对应 abnormal 参数和 counts 的键 */
const ABNORMAL_TABS = [
  { key: '', label: '全部', countKey: 'all' },
  { key: 'distance', label: '距离异常', countKey: 'distance' },
  { key: 'supplement', label: '主管补录', countKey: 'supplement' },
  { key: 'missing', label: '缺卡', countKey: 'missing' },
];

const DATE_RE = /^\d{4}-\d{2}-\d{2}$/;

/** 日期范围快捷项 */
function rangePresets(now = new Date()) {
  const y = now.getFullYear();
  const m = now.getMonth() + 1;
  const prev = new Date(y, m - 2, 1);
  return [
    { key: 'last7', label: '最近 7 天', ...lastDaysRange(7, now) },
    { key: 'last30', label: '最近 30 天', ...lastDaysRange(30, now) },
    { key: 'week', label: '本周', ...thisWeekRange(now) },
    { key: 'month', label: '本月', from: monthRange(y, m).from, to: toApiDate(now) },
    { key: 'prevMonth', label: '上月', ...monthRange(prev.getFullYear(), prev.getMonth() + 1) },
  ];
}

export default {
  name: 'PunchesView',
  data() {
    const def = lastDaysRange(7);
    return {
      route,
      pageSize: PAGE_SIZE,
      from: def.from,
      to: def.to,
      engineerId: '',
      abnormal: '',
      page: 1,
      items: [],
      total: 0,
      counts: { all: 0, distance: 0, supplement: 0, missing: 0 },
      loading: false,
      loaded: false,
      error: '',
      engineers: [],
      presets: rangePresets(),
      today: toApiDate(new Date()),
    };
  },
  computed: {
    tabs() {
      return ABNORMAL_TABS.map(t => ({ ...t, count: this.counts[t.countKey] ?? 0, active: t.key === this.abnormal }));
    },
    presetKey() {
      const p = this.presets.find(x => x.from === this.from && x.to === this.to);
      return p ? p.key : '';
    },
    rangeText() {
      const a = parseTime(this.from);
      const b = parseTime(this.to);
      if (!a || !b) return '';
      const fa = a.getFullYear() === new Date().getFullYear() ? fmtMD(a) : `${a.getFullYear()}年${fmtMD(a)}`;
      const fb = b.getFullYear() === a.getFullYear() ? fmtMD(b) : `${b.getFullYear()}年${fmtMD(b)}`;
      return fa === fb ? fa : `${fa} – ${fb}`;
    },
    engineerName() {
      const e = this.engineers.find(x => String(x.id) === String(this.engineerId));
      return e ? e.name : '';
    },
    photoUrls() {
      return this.items.filter(p => p.photo && p.photo.url).map(p => p.photo.url);
    },
    emptyText() {
      if (this.abnormal === 'distance') return '该时间段内没有距离异常的打卡';
      if (this.abnormal === 'supplement') return '该时间段内没有主管补录的打卡';
      if (this.abnormal === 'missing') return '该时间段内没有缺卡';
      return '该时间段内没有打卡记录';
    },
    rows() {
      return this.items.map(p => {
        const kind = label(PUNCH_KIND, p.kind);
        const hasDist = p.distanceMeters !== null && p.distanceMeters !== undefined;
        return {
          ...p,
          kindLabel: kind,
          timeText: fmtMDHM(p.time),
          weekday: fmtWeekday(p.time),
          distText: hasDist ? fmtDistance(p.distanceMeters) : '',
          supText: p.isSupplement
            ? `${(p.supplementBy && p.supplementBy.name) || '主管'} 补录${p.supplementReason ? ' · ' + p.supplementReason : ''}`
            : '',
          supAt: p.isSupplement && p.supplementAt ? `补录于 ${fmtMDHM(p.supplementAt)}` : '',
          photoUrl: p.photo && p.photo.url ? p.photo.url : '',
        };
      });
    },
  },
  watch: {
    'route.query': {
      handler(q) {
        if (route.path !== '/punches') return;
        const def = lastDaysRange(7);
        let from = DATE_RE.test(q.from || '') ? q.from : def.from;
        let to = DATE_RE.test(q.to || '') ? q.to : def.to;
        if (from > to) [from, to] = [to, from];
        this.from = from;
        this.to = to;
        this.engineerId = /^\d+$/.test(q.engineerId || '') ? q.engineerId : '';
        this.abnormal = ABNORMAL_TABS.some(t => t.key === q.abnormal) ? q.abnormal : '';
        this.page = Math.max(1, parseInt(q.page, 10) || 1);
        this.load();
      },
      deep: true,
      immediate: true,
    },
  },
  created() { this.loadEngineers(); },
  methods: {
    async load() {
      const seq = (this._seq = (this._seq || 0) + 1);
      this.loading = true;
      this.error = '';
      try {
        const data = await api.admin.punches({
          from: this.from,
          to: this.to,
          engineerId: this.engineerId || undefined,
          abnormal: this.abnormal || undefined,
          page: this.page,
          pageSize: PAGE_SIZE,
        });
        if (seq !== this._seq) return;
        this.items = (data && data.items) || [];
        this.total = (data && data.total) || 0;
        this.counts = Object.assign({ all: 0, distance: 0, supplement: 0, missing: 0 }, (data && data.counts) || {});
        this.loaded = true;
        // 当前页超出（如筛选后数据变少）时回到最后一页
        const pages = Math.max(1, Math.ceil(this.total / PAGE_SIZE));
        if (this.page > pages && this.total > 0) updateQuery({ page: pages });
      } catch (e) {
        if (seq !== this._seq) return;
        this.error = e.message || '加载失败';
        this.items = [];
        this.total = 0;
        toastError(e);
      } finally {
        if (seq === this._seq) this.loading = false;
      }
    },
    async loadEngineers() {
      try {
        const list = await api.engineers.list();
        this.engineers = (list || []).map(x => x.user).filter(Boolean);
      } catch (e) {
        toastError(e);
      }
    },
    setQuery(patch) {
      updateQuery(Object.assign({ page: null }, patch));
    },
    pickTab(t) { this.setQuery({ abnormal: t.key || null }); },
    pickPreset(e) {
      const p = this.presets.find(x => x.key === e.target.value);
      if (p) this.setQuery({ from: p.from, to: p.to });
    },
    onFrom(e) {
      const v = e.target.value;
      if (!DATE_RE.test(v)) return;
      this.setQuery(v > this.to ? { from: v, to: v } : { from: v });
    },
    onTo(e) {
      const v = e.target.value;
      if (!DATE_RE.test(v)) return;
      this.setQuery(v < this.from ? { from: v, to: v } : { to: v });
    },
    onEngineer(e) { this.setQuery({ engineerId: e.target.value || null }); },
    goPage(p) { updateQuery({ page: p > 1 ? p : null }); },
    resetFilters() { navigate('/punches', null, { replace: true }); },
    openPhoto(row) {
      if (!row.photoUrl) return;
      preview(this.photoUrls, Math.max(0, this.photoUrls.indexOf(row.photoUrl)));
    },
    openOrder(row) {
      if (row.orderNo) navigate('/orders', { keyword: row.orderNo });
    },
  },
  template: `
  <PageHeader :crumbs="['打卡记录']">
    <button type="button" class="btn btn-secondary" :disabled="loading" @click="load"><Icon name="refresh" :size="15" />刷新</button>
  </PageHeader>
  <div class="page">
    <div class="page-head">
      <div>
        <h1>打卡记录</h1>
        <div class="page-sub">{{ rangeText }}<template v-if="engineerName"> · {{ engineerName }}</template> · 共 <span class="num">{{ counts.all }}</span> 条打卡，时间以服务器为准</div>
      </div>
    </div>

    <section class="card" aria-label="打卡记录">
      <div class="toolbar">
        <div class="tabs" role="tablist" aria-label="按异常筛选">
          <button v-for="t in tabs" :key="t.key" type="button" role="tab" class="tab" :class="{ active: t.active }"
                  :aria-selected="t.active ? 'true' : 'false'" @click="pickTab(t)">
            {{ t.label }} <span class="tab-count" :class="{ 'punches-count-danger': t.key === 'missing' && t.count > 0 }">{{ t.count }}</span>
          </button>
        </div>
        <div class="toolbar-right">
          <select class="select punches-preset" :value="presetKey" aria-label="日期范围" @change="pickPreset">
            <option v-if="!presetKey" value="">自定义</option>
            <option v-for="p in presets" :key="p.key" :value="p.key">{{ p.label }}</option>
          </select>
          <div class="punches-range">
            <input type="date" class="input punches-date" :value="from" :max="today" aria-label="开始日期" @change="onFrom">
            <span class="text-faint">–</span>
            <input type="date" class="input punches-date" :value="to" :max="today" aria-label="结束日期" @change="onTo">
          </div>
          <select class="select punches-engineer" :value="engineerId" aria-label="工程师" @change="onEngineer">
            <option value="">全部工程师</option>
            <option v-for="e in engineers" :key="e.id" :value="String(e.id)">{{ e.name }}</option>
          </select>
        </div>
      </div>

      <div class="punches-table-wrap" :class="{ 'punches-busy': loading && loaded }">
        <table class="data-table punches-table">
          <colgroup>
            <col style="width: 68px"><col style="width: 120px"><col style="width: 112px"><col style="width: 72px">
            <col style="width: 200px"><col><col style="width: 150px"><col style="width: 220px">
          </colgroup>
          <thead>
            <tr><th>照片</th><th>时间</th><th>工程师</th><th>类型</th><th>工单号 · 客户</th><th>地址</th><th>距离</th><th>备注</th></tr>
          </thead>
          <tbody v-if="rows.length">
            <tr v-for="r in rows" :key="r.id">
              <td>
                <button v-if="r.photoUrl" type="button" class="thumb punches-thumb" :aria-label="'查看' + r.kindLabel + '照片'" @click="openPhoto(r)">
                  <img :src="r.photoUrl" alt="" loading="lazy">
                </button>
                <span v-else class="thumb thumb-missing punches-thumb punches-thumb-none" :title="r.isSupplement ? '主管补录，无照片' : '无照片'">
                  <Icon name="image" :size="14" />
                </span>
              </td>
              <td class="nowrap">
                <div class="num">{{ r.timeText }}</div>
                <div class="cell-sub">{{ r.weekday }}</div>
              </td>
              <td>
                <span class="cell-user"><Avatar :user="r.engineer" :size="22" /><span class="ellipsis">{{ r.engineer ? r.engineer.name : '—' }}</span></span>
              </td>
              <td>
                <span class="tag" :class="r.kind === 'Arrive' ? 'punches-kind-arrive' : 'punches-kind-leave'">{{ r.kindLabel }}</span>
              </td>
              <td class="punches-order">
                <button v-if="r.orderNo" type="button" class="punches-order-link mono" :title="'查看工单 ' + r.orderNo" @click="openOrder(r)">{{ r.orderNo }}</button>
                <span v-else class="text-faint">—</span>
                <span class="cell-sub" :title="r.customerName">{{ r.customerName || '—' }}</span>
              </td>
              <td>
                <div class="punches-addr" :title="r.address">{{ r.address || '—' }}</div>
              </td>
              <td class="nowrap">
                <span v-if="r.distText && r.outOfRange" class="punches-far num"><Icon name="alert" :size="13" :stroke="2" />{{ r.distText }} · 超出范围</span>
                <span v-else-if="r.distText" class="num text-muted">{{ r.distText }}</span>
                <span v-else class="text-faint">—</span>
              </td>
              <td>
                <div v-if="r.missing" class="punches-note punches-note-missing"><Icon name="alert" :size="13" :stroke="2" />缺离场卡</div>
                <template v-if="r.isSupplement">
                  <div class="punches-note punches-note-sup" :title="r.supText">{{ r.supText }}</div>
                  <div v-if="r.supAt" class="cell-sub num">{{ r.supAt }}</div>
                </template>
                <span v-if="!r.missing && !r.isSupplement" class="text-faint">—</span>
              </td>
            </tr>
          </tbody>
        </table>
        <Loading v-if="loading && !loaded" />
        <Empty v-else-if="error && !rows.length" :text="'加载失败：' + error">
          <button type="button" class="btn btn-secondary btn-sm" @click="load"><Icon name="refresh" :size="14" />重试</button>
        </Empty>
        <Empty v-else-if="!loading && !rows.length" :text="emptyText">
          <button v-if="abnormal || engineerId || presetKey !== 'last7'" type="button" class="btn btn-secondary btn-sm" @click="resetFilters">清除筛选</button>
        </Empty>
      </div>

      <div v-if="total > 0" class="table-foot">
        <span>共 <span class="num">{{ total }}</span> 条<template v-if="total > rows.length">，每页 {{ pageSize }} 条</template></span>
        <Pagination :total="total" :page="page" :page-size="pageSize" @change="goPage" />
      </div>
    </section>
  </div>`,
};
