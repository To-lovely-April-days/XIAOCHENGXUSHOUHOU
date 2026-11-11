// 工单文件中心（#/files，v1.2）。数据：GET /api/admin/files（CHANGES-v1.2 §5），上传人下拉：GET /api/admin/employees。
// 汇总所有工单名下的文件：附件、建单照片、完工照片、签字件、打卡照片、返厂照片。
// 查询参数全部放在 hash 上：?keyword=&type=&source=&uploaderId=&orderId=&from=&to=&page=&id=（id = 打开的工单抽屉）
// 行上的删除 / 切换受限只看服务端给的 canDelete / canToggleRestricted，这里不推导规则。
import * as api from '../api.js';
import { route, navigate, updateQuery } from '../router.js';
import { toast, toastError, confirm, preview } from '../ui.js';
import { fmtDateTime, fmtSize, fileBadge } from '../format.js';
import { FILE_KIND, FILE_SOURCE, options } from '../enums.js';
import OrdersDrawer from './orders-drawer.js';

const PAGE_SIZE = 30;
/** 图片不超过 2 MB 才显示缩略图（缩略图就是原图地址） */
const THUMB_MAX = 2 * 1024 * 1024;
const DATE_RE = /^\d{4}-\d{2}-\d{2}$/;

/** 类型 tab：key 对应 type 参数与 counts 的键 */
const TYPE_TABS = [{ key: '', label: '全部', countKey: 'all' }]
  .concat(options(FILE_KIND).map(o => ({ key: o.value, label: o.label, countKey: o.value })));

const emptyCounts = () => ({ all: 0, Image: 0, Video: 0, Document: 0 });

export default {
  name: 'FilesView',
  components: { OrdersDrawer },
  data() {
    return {
      route,
      pageSize: PAGE_SIZE,
      keyword: '',
      keywordInput: '',
      type: '',
      source: '',
      uploaderId: '',
      orderId: '',
      from: '',
      to: '',
      page: 1,
      selectedId: null, // 打开的工单抽屉
      drawerRev: 0, // 列表里改了当前抽屉工单的附件后 +1，让抽屉重新加载
      nowTs: Date.now(),
      items: [],
      total: 0,
      totalSize: 0,
      counts: emptyCounts(),
      loading: false,
      loaded: false,
      error: '',
      employees: [],
      orderNoHint: '', // 带 orderId 进入但列表为空时，另查到的工单号
      busyId: 0, // 正在下载 / 删除 / 切换的文件 id
      sources: options(FILE_SOURCE),
    };
  },
  computed: {
    tabs() {
      return TYPE_TABS.map(t => ({ ...t, count: this.counts[t.countKey] ?? 0, active: t.key === this.type }));
    },
    sizeTotalText() { return fmtSize(this.totalSize) || '0 B'; },
    filtered() {
      return !!(this.keyword || this.type || this.source || this.uploaderId || this.orderId || this.from || this.to);
    },
    /** 筛选标签「工单 {orderNo}」 */
    orderChip() {
      if (!this.orderId) return '';
      const hit = this.items.find(x => String(x.orderId) === String(this.orderId));
      const no = (hit && hit.orderNo) || this.orderNoHint;
      return '工单 ' + (no || '#' + this.orderId);
    },
    /** 当前页全部图片 / 视频（预览时左右切换） */
    media() {
      return this.items.filter(x => (x.kind === 'Image' || x.kind === 'Video') && x.url)
        .map(x => ({ id: x.id, url: x.url, type: x.kind === 'Video' ? 'video' : 'image' }));
    },
    rows() {
      return this.items.map(f => {
        const media = (f.kind === 'Image' || f.kind === 'Video') && !!f.url;
        return {
          f,
          key: f.id,
          name: f.name || '未命名',
          badge: fileBadge(f),
          media,
          thumb: f.kind === 'Image' && !!f.url && Number(f.size) <= THUMB_MAX,
          previewTitle: media ? '预览' : '下载',
          sourceLabel: f.sourceLabel || (FILE_SOURCE[f.source] && FILE_SOURCE[f.source].label) || f.source || '',
          restricted: !!f.restricted,
          uploader: (f.uploader && f.uploader.name) || '—',
          timeText: fmtDateTime(f.createdAt),
          sizeText: fmtSize(f.size),
          canDelete: !!f.canDelete,
          canToggle: !!f.canToggleRestricted,
          toggleLabel: f.restricted ? '设为所有人可见' : '设为仅销售和主管可见',
          selected: this.selectedId === f.orderId,
          busy: this.busyId === f.id,
        };
      });
    },
  },
  watch: {
    'route.query': {
      handler(q, old) {
        if (route.path !== '/files') return;
        const o = old || {};
        const idChanged = (q.id || '') !== (o.id || '');
        this.selectedId = /^\d+$/.test(q.id || '') ? Number(q.id) : null;
        // 只是打开 / 关闭抽屉时不重新拉列表
        const keys = ['keyword', 'type', 'source', 'uploaderId', 'orderId', 'from', 'to', 'page'];
        if (old && idChanged && keys.every(k => (q[k] || '') === (o[k] || ''))) return;
        this.keyword = (q.keyword || '').trim();
        this.keywordInput = this.keyword;
        this.type = TYPE_TABS.some(t => t.key === q.type) ? q.type : '';
        this.source = FILE_SOURCE[q.source] ? q.source : '';
        this.uploaderId = /^\d+$/.test(q.uploaderId || '') ? q.uploaderId : '';
        const orderId = /^\d+$/.test(q.orderId || '') ? q.orderId : '';
        if (orderId !== this.orderId) this.orderNoHint = '';
        this.orderId = orderId;
        let from = DATE_RE.test(q.from || '') ? q.from : '';
        let to = DATE_RE.test(q.to || '') ? q.to : '';
        if (from && to && from > to) [from, to] = [to, from];
        this.from = from;
        this.to = to;
        this.page = Math.max(1, parseInt(q.page, 10) || 1);
        this.load();
      },
      deep: true,
      immediate: true,
    },
  },
  created() { this.loadEmployees(); },
  mounted() { this._tick = setInterval(() => { this.nowTs = Date.now(); }, 30000); },
  beforeUnmount() { clearInterval(this._tick); },
  methods: {
    async load() {
      const seq = (this._seq = (this._seq || 0) + 1);
      this.loading = true;
      this.error = '';
      try {
        const data = await api.admin.files({
          keyword: this.keyword || undefined,
          type: this.type || undefined,
          source: this.source || undefined,
          uploaderId: this.uploaderId || undefined,
          orderId: this.orderId || undefined,
          from: this.from || undefined,
          to: this.to || undefined,
          page: this.page,
          pageSize: PAGE_SIZE,
        });
        if (seq !== this._seq) return;
        this.items = (data && data.items) || [];
        this.total = (data && data.total) || 0;
        this.totalSize = (data && data.totalSize) || 0;
        this.counts = Object.assign(emptyCounts(), (data && data.counts) || {});
        this.loaded = true;
        // 当前页超出（如删除后数据变少）时回到最后一页
        const pages = Math.max(1, Math.ceil(this.total / PAGE_SIZE));
        if (this.page > pages && this.total > 0) updateQuery({ page: pages > 1 ? pages : null });
        this.resolveOrderNo();
      } catch (e) {
        if (seq !== this._seq) return;
        this.error = e.message || '加载失败';
        this.items = [];
        this.total = 0;
        this.totalSize = 0;
        toastError(e);
      } finally {
        if (seq === this._seq) this.loading = false;
      }
    },
    /** 带 orderId 筛选但当前页没有该单的文件时，单独查一次工单号用于筛选标签 */
    async resolveOrderNo() {
      const id = this.orderId;
      if (!id || this.orderNoHint || this.items.some(x => String(x.orderId) === String(id))) return;
      try {
        const d = await api.orders.detail(Number(id));
        if (this.orderId === id && d && d.orderNo) this.orderNoHint = d.orderNo;
      } catch (e) { /* 标签退回显示 #id */ }
    },
    async loadEmployees() {
      try {
        this.employees = (await api.admin.employees()) || [];
      } catch (e) {
        toastError(e);
      }
    },
    setQuery(patch) { updateQuery(Object.assign({ page: null }, patch)); },
    pickTab(t) { this.setQuery({ type: t.key || null }); },
    onSearch(v) {
      const kw = String(v === undefined || v === null ? this.keywordInput : v).trim();
      if (kw === this.keyword) return;
      this.setQuery({ keyword: kw || null });
    },
    onSource(e) { this.setQuery({ source: e.target.value || null }); },
    onUploader(e) { this.setQuery({ uploaderId: e.target.value || null }); },
    onFrom(e) {
      const v = e.target.value;
      if (v && !DATE_RE.test(v)) return;
      this.setQuery(v && this.to && v > this.to ? { from: v, to: v } : { from: v || null });
    },
    onTo(e) {
      const v = e.target.value;
      if (v && !DATE_RE.test(v)) return;
      this.setQuery(v && this.from && v < this.from ? { from: v, to: v } : { to: v || null });
    },
    clearOrder() { this.setQuery({ orderId: null }); },
    resetFilters() { navigate('/files', this.selectedId ? { id: this.selectedId } : null, { replace: true }); },
    goPage(p) { updateQuery({ page: p > 1 ? p : null }); },

    // ------------------------------------------------ 行操作
    /** 点预览列 / 文件名：图片视频 → lightbox（当前页全部图片视频可切换）；文档 → 直接下载 */
    openFile(r) {
      if (r.media) {
        const i = this.media.findIndex(m => m.id === r.f.id);
        preview(this.media, Math.max(0, i));
      } else {
        this.download(r);
      }
    },
    async download(r) {
      if (this.busyId) return;
      this.busyId = r.f.id;
      try {
        await api.files.download(r.f.id, r.name);
      } catch (e) {
        this.rowFailed(e);
      } finally {
        this.busyId = 0;
      }
    },
    async toggle(r) {
      if (this.busyId) return;
      this.busyId = r.f.id;
      try {
        await api.orders.setAttachmentRestricted(r.f.orderId, r.f.id, !r.restricted);
        toast('已更新', 'success');
        this.afterRowChange(r);
      } catch (e) {
        this.rowFailed(e);
      } finally {
        this.busyId = 0;
      }
    },
    async remove(r) {
      if (this.busyId) return;
      if (!(await confirm('删除附件', `确定删除「${r.name}」？删除后不可恢复。`, { danger: true, okText: '删除' }))) return;
      this.busyId = r.f.id;
      try {
        await api.orders.deleteAttachment(r.f.orderId, r.f.id);
        toast('已删除', 'success');
        this.afterRowChange(r);
      } catch (e) {
        this.rowFailed(e);
      } finally {
        this.busyId = 0;
      }
    },
    afterRowChange(r) {
      this.load();
      if (this.selectedId && this.selectedId === r.f.orderId) this.drawerRev += 1;
    },
    /** 文件已被别人删除 / 工单状态变化：提示后刷新列表 */
    rowFailed(e) {
      toastError(e);
      if (e && (e.code === 40400 || e.code === 40900)) this.load();
    },

    // ------------------------------------------------ 工单抽屉
    openOrder(r) {
      if (!r.f.orderId) return;
      if (this.selectedId === r.f.orderId) { this.closeDrawer(); return; }
      updateQuery({ id: r.f.orderId });
    },
    closeDrawer() { updateQuery({ id: null }); },
    onDrawerChanged() { this.load(); },
  },
  template: `
  <PageHeader :crumbs="['文件', '工单文件中心']">
    <button type="button" class="btn btn-secondary" :disabled="loading" @click="load"><Icon name="refresh" :size="15" />刷新</button>
  </PageHeader>
  <div class="page">
    <div class="page-head">
      <div>
        <h1>工单文件中心</h1>
        <div class="page-sub">共 <span class="num">{{ total }}</span> 个文件 · <span class="num">{{ sizeTotalText }}</span><span class="files-sub-note"> · 汇总所有工单的附件、建单照片、完工照片、签字件、打卡照片、返厂照片</span></div>
      </div>
    </div>

    <section class="card" aria-label="工单文件">
      <div class="toolbar">
        <div class="tabs" role="tablist" aria-label="按文件类型筛选">
          <button v-for="t in tabs" :key="t.key || 'all'" type="button" role="tab" class="tab" :class="{ active: t.active }"
                  :aria-selected="t.active ? 'true' : 'false'" @click="pickTab(t)">
            {{ t.label }} <span class="tab-count">{{ t.count }}</span>
          </button>
        </div>
      </div>
      <div class="files-filters">
        <SearchBox v-model="keywordInput" placeholder="搜索文件名 / 工单号 / 客户" :width="240" @search="onSearch" />
        <select class="select files-select" :value="source" aria-label="文件来源" @change="onSource">
          <option value="">全部来源</option>
          <option v-for="s in sources" :key="s.value" :value="s.value">{{ s.label }}</option>
        </select>
        <select class="select files-select" :value="uploaderId" aria-label="上传人" @change="onUploader">
          <option value="">全部上传人</option>
          <option v-for="e in employees" :key="e.id" :value="String(e.id)">{{ e.name }}</option>
        </select>
        <div class="files-range" title="按上传日期筛选，留空 = 不限">
          <input type="date" class="input files-date" :value="from" aria-label="上传开始日期" @change="onFrom">
          <span class="text-faint">–</span>
          <input type="date" class="input files-date" :value="to" aria-label="上传结束日期" @change="onTo">
        </div>
        <span v-if="orderId" class="files-chip">{{ orderChip }}
          <button type="button" class="files-chip-x" aria-label="取消按工单筛选" @click="clearOrder"><Icon name="close" :size="12" :stroke="2.4" /></button></span>
        <button type="button" class="btn btn-ghost btn-sm" :disabled="!filtered" @click="resetFilters">重置</button>
      </div>

      <div class="files-table-wrap" :class="{ 'files-busy': loading && loaded }">
        <table class="data-table files-table">
          <colgroup>
            <col style="width: 60px"><col><col style="width: 104px"><col style="width: 190px">
            <col style="width: 88px"><col style="width: 136px"><col style="width: 84px"><col style="width: 172px">
          </colgroup>
          <thead>
            <tr><th>预览</th><th>文件名</th><th>来源</th><th>工单</th><th>上传人</th><th>上传时间</th><th class="text-right">大小</th><th>操作</th></tr>
          </thead>
          <tbody v-if="rows.length">
            <tr v-for="r in rows" :key="r.key" :class="{ selected: r.selected }">
              <td>
                <button v-if="r.thumb" type="button" class="thumb thumb-sm" :aria-label="'预览 ' + r.name" @click="openFile(r)">
                  <img :src="r.f.url" alt="" loading="lazy"></button>
                <button v-else type="button" class="att-badge files-badge-btn" :style="{ background: r.badge.color }"
                        :title="r.previewTitle + ' ' + r.name" :aria-label="r.previewTitle + ' ' + r.name" @click="openFile(r)">{{ r.badge.text }}</button>
              </td>
              <td>
                <div class="files-name-line">
                  <button type="button" class="files-name ellipsis" :title="r.name" @click="openFile(r)">{{ r.name }}</button>
                  <Tag v-if="r.restricted" tone="warn">仅销售和主管可见</Tag>
                </div>
              </td>
              <td class="nowrap">{{ r.sourceLabel }}</td>
              <td class="files-order">
                <button type="button" class="files-order-link mono" :title="'查看工单 ' + r.f.orderNo" @click="openOrder(r)">{{ r.f.orderNo }}</button>
                <span class="cell-sub" :title="r.f.customerName">{{ r.f.customerName || '—' }}</span>
              </td>
              <td><span class="ellipsis files-uploader" :title="r.uploader">{{ r.uploader }}</span></td>
              <td class="nowrap"><span class="num text-muted">{{ r.timeText }}</span></td>
              <td class="text-right nowrap"><span class="num">{{ r.sizeText }}</span></td>
              <td>
                <div class="files-actions">
                  <button type="button" class="btn-link att-link" :disabled="!!busyId" @click="download(r)">{{ r.busy ? '处理中…' : '下载' }}</button>
                  <button v-if="r.canDelete" type="button" class="btn-link att-link att-link-danger" :disabled="!!busyId" @click="remove(r)">删除</button>
                  <button v-if="r.canToggle" type="button" class="btn-link att-link files-toggle" :disabled="!!busyId" @click="toggle(r)">{{ r.toggleLabel }}</button>
                </div>
              </td>
            </tr>
          </tbody>
        </table>
        <Loading v-if="loading && !loaded" />
        <Empty v-else-if="error && !rows.length" :text="'加载失败：' + error">
          <button type="button" class="btn btn-secondary btn-sm" @click="load"><Icon name="refresh" :size="14" />重试</button>
        </Empty>
        <Empty v-else-if="!loading && !rows.length" text="没有符合条件的文件">
          <button v-if="filtered" type="button" class="btn btn-secondary btn-sm" @click="resetFilters">清除筛选</button>
        </Empty>
      </div>

      <div v-if="total > 0" class="table-foot">
        <span>共 <span class="num">{{ total }}</span> 个文件 · <span class="num">{{ sizeTotalText }}</span><template v-if="total > rows.length">，每页 {{ pageSize }} 条</template></span>
        <Pagination :total="total" :page="page" :page-size="pageSize" @change="goPage" />
      </div>
    </section>
  </div>

  <OrdersDrawer v-if="selectedId" :key="selectedId + '-' + drawerRev" :order-id="selectedId" :server-offset="0" :now-ts="nowTs"
                @close="closeDrawer" @changed="onDrawerChanged" />
  `,
};
