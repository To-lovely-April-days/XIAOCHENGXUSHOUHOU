// 工单文件中心 · 按工单归档（v1.9）：一个工单一个文件夹。
// 文件夹列表：GET /api/admin/files/folders?keyword=&page=&pageSize=；文件夹内容：GET /api/admin/files?orderId=&pageSize=200。
// hash 上的参数：?q=关键字&fp=页码&folder=工单id（打开的文件夹）。删除 / 改类别 / 改可见范围去「全部文件」视图里做。
import * as api from '../api.js';
import { route, navigate, updateQuery } from '../router.js';
import { toastError, preview } from '../ui.js';
import { fmtDateTime, fmtSize, fileBadge } from '../format.js';
import { ORDER_STATUS, SERVICE_TYPE, FILE_SOURCE, ATTACHMENT_TYPE } from '../enums.js';

const PAGE_SIZE = 30;
const THUMB_MAX = 2 * 1024 * 1024;
/** 附件类别的顺序 = 文件夹里前几个分组的顺序 */
const ATT_KEYS = Object.keys(ATTACHMENT_TYPE);
/** 非附件来源的分组顺序（没列到的排在最后） */
const SOURCE_ORDER = ['PunchPhoto', 'ProcessPhoto', 'Signature', 'Factory'];

function isAttachment(f) { return f.source === 'Attachment' || f.source === 'OrderPhoto'; }

export default {
  name: 'FilesFolders',
  data() {
    return {
      route,
      pageSize: PAGE_SIZE,
      keyword: '',
      keywordInput: '',
      page: 1,
      folders: [],
      total: 0,
      totalFiles: 0,
      totalSize: 0,
      loading: false,
      loaded: false,
      error: '',
      // 打开的文件夹
      folderId: null,
      folderInfo: null,
      files: [],
      filesLoading: false,
      filesError: '',
      busyId: 0,
    };
  },
  computed: {
    sizeTotalText() { return fmtSize(this.totalSize) || '0 B'; },
    rows() {
      return this.folders.map(x => {
        const st = ORDER_STATUS[x.status];
        const factory = x.serviceType === 'Factory';
        // 该有的签字单据：SAT 验收单 + 对应服务方式的维修登记表（远程服务没有维修登记表）
        const docs = [{ label: 'SAT 验收单', n: x.satCount }];
        if (factory || x.factoryRepairCount) docs.push({ label: '返厂维修登记表', n: x.factoryRepairCount });
        if (x.serviceType === 'Onsite' || x.onsiteRepairCount) docs.push({ label: '现场维修登记表', n: x.onsiteRepairCount });
        return {
          x,
          key: x.orderId,
          statusLabel: (st && st.label) || x.status,
          serviceLabel: (SERVICE_TYPE[x.serviceType] && SERVICE_TYPE[x.serviceType].label) || '',
          sub: [x.projectNo, x.deviceText].filter(Boolean).join(' · '),
          docs,
          sizeText: fmtSize(x.totalSize),
          timeText: fmtDateTime(x.lastUploadAt),
        };
      });
    },
    /** 文件夹里的分组：先四类附件（空组也显示，方便看缺什么），再按来源 */
    sections() {
      const list = this.files;
      // 空着也要显示的分组：SAT 验收单，加上这张工单该有的那一种维修登记表（不知道服务方式时两种都显示）
      const st = this.folderInfo && this.folderInfo.serviceType;
      const expect = (k) => k === 'Sat'
        // 送货单 / 验收报告是安装调试工单的，有文件才显示
        || (k === 'OnsiteRepairForm' && (!st || st === 'Onsite'))
        || (k === 'FactoryRepairForm' && (!st || st === 'Factory'));
      const out = ATT_KEYS.map(k => ({
        key: 'att-' + k,
        title: ATTACHMENT_TYPE[k].label,
        items: list.filter(f => isAttachment(f) && (ATTACHMENT_TYPE[f.attachmentType] ? f.attachmentType : 'Other') === k),
        keepEmpty: expect(k),
      }));
      const rest = list.filter(f => !isAttachment(f));
      const sources = Array.from(new Set(rest.map(f => f.source)))
        .sort((a, b) => (SOURCE_ORDER.indexOf(a) + 100) % 100 - (SOURCE_ORDER.indexOf(b) + 100) % 100);
      sources.forEach(s => {
        const items = rest.filter(f => f.source === s);
        out.push({
          key: 'src-' + s,
          title: (items[0] && items[0].sourceLabel) || (FILE_SOURCE[s] && FILE_SOURCE[s].label) || s,
          items,
          keepEmpty: false,
        });
      });
      return out.filter(g => g.items.length || g.keepEmpty).map(g => ({
        ...g,
        rows: g.items.map(f => {
          const media = (f.kind === 'Image' || f.kind === 'Video') && !!f.url;
          return {
            f,
            name: f.name || '未命名',
            media,
            thumb: f.kind === 'Image' && !!f.url && Number(f.size) <= THUMB_MAX,
            badge: fileBadge(f),
            restricted: !!f.restricted,
            uploader: (f.uploader && f.uploader.name) || '—',
            timeText: fmtDateTime(f.createdAt),
            sizeText: fmtSize(f.size),
            busy: this.busyId === f.id,
          };
        }),
      }));
    },
    media() {
      return this.files.filter(x => (x.kind === 'Image' || x.kind === 'Video') && x.url)
        .map(x => ({ id: x.id, url: x.url, type: x.kind === 'Video' ? 'video' : 'image' }));
    },
    folderTitle() {
      const i = this.folderInfo;
      return i ? i.orderNo : (this.folderId ? '工单 #' + this.folderId : '');
    },
    folderSub() {
      const i = this.folderInfo;
      if (!i) return '';
      return [i.customerName, i.projectNo, i.deviceText].filter(Boolean).join(' · ');
    },
    folderSizeText() { return fmtSize(this.files.reduce((s, f) => s + (Number(f.size) || 0), 0)) || '0 B'; },
  },
  watch: {
    'route.query': {
      handler(q, old) {
        if (route.path !== '/files') return;
        const o = old || {};
        this.keyword = (q.q || '').trim();
        this.keywordInput = this.keyword;
        this.page = Math.max(1, parseInt(q.fp, 10) || 1);
        const folder = /^\d+$/.test(q.folder || '') ? Number(q.folder) : null;
        if (!old || (q.q || '') !== (o.q || '') || (q.fp || '') !== (o.fp || '')) this.load();
        if (folder !== this.folderId) {
          this.folderId = folder;
          this.files = [];
          this.folderInfo = null;
          if (folder) this.loadFolder();
        }
      },
      deep: true,
      immediate: true,
    },
  },
  methods: {
    async load() {
      const seq = (this._seq = (this._seq || 0) + 1);
      this.loading = true;
      this.error = '';
      try {
        const d = await api.admin.fileFolders({ keyword: this.keyword || undefined, page: this.page, pageSize: PAGE_SIZE });
        if (seq !== this._seq) return;
        this.folders = (d && d.items) || [];
        this.total = (d && d.total) || 0;
        this.totalFiles = (d && d.totalFiles) || 0;
        this.totalSize = (d && d.totalSize) || 0;
        this.loaded = true;
        const pages = Math.max(1, Math.ceil(this.total / PAGE_SIZE));
        if (this.page > pages && this.total > 0) updateQuery({ fp: pages > 1 ? pages : null });
        this.fillFolderInfo();
      } catch (e) {
        if (seq !== this._seq) return;
        this.error = e.message || '加载失败';
        this.folders = [];
        toastError(e);
      } finally {
        if (seq === this._seq) this.loading = false;
      }
    },
    async loadFolder() {
      const id = this.folderId;
      this.filesLoading = true;
      this.filesError = '';
      try {
        const d = await api.admin.files({ orderId: id, page: 1, pageSize: 200 });
        if (id !== this.folderId) return;
        this.files = (d && d.items) || [];
        this.fillFolderInfo();
      } catch (e) {
        if (id !== this.folderId) return;
        this.filesError = e.message || '加载失败';
        toastError(e);
      } finally {
        if (id === this.folderId) this.filesLoading = false;
      }
    },
    /** 文件夹抬头的工单信息：优先用列表里的那一行，没有（如直接打开链接、搜索后不在当前页）就用文件行里带的 */
    fillFolderInfo() {
      if (!this.folderId) return;
      const hit = this.folders.find(x => x.orderId === this.folderId);
      if (hit) { this.folderInfo = hit; return; }
      const f = this.files[0];
      if (f && !this.folderInfo) this.folderInfo = { orderId: f.orderId, orderNo: f.orderNo, customerName: f.customerName };
    },
    onSearch(v) {
      const kw = String(v === undefined || v === null ? this.keywordInput : v).trim();
      if (kw === this.keyword) return;
      updateQuery({ q: kw || null, fp: null });
    },
    goPage(p) { updateQuery({ fp: p > 1 ? p : null }); },
    openFolder(r) { updateQuery({ folder: r.x.orderId }, { replace: false }); },
    closeFolder() { updateQuery({ folder: null }); },
    refresh() { this.load(); if (this.folderId) this.loadFolder(); },
    /** 去「全部文件」视图里管理这个工单的文件（删除、改类别、改可见范围） */
    manage() { navigate('/files', { view: 'list', orderId: this.folderId }); },
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
        toastError(e);
      } finally {
        this.busyId = 0;
      }
    },
  },
  template: `
  <div class="page-head">
    <div>
      <h1>工单文件中心</h1>
      <div class="page-sub">共 <span class="num">{{ total }}</span> 个工单文件夹 · <span class="num">{{ totalFiles }}</span> 个文件 · <span class="num">{{ sizeTotalText }}</span></div>
    </div>
    <div class="page-actions">
      <button type="button" class="btn btn-ghost" :disabled="loading" @click="refresh"><Icon name="refresh" :size="15" />刷新</button>
    </div>
  </div>

  <!-- 文件夹内容 -->
  <section v-if="folderId" class="card folder-detail" aria-label="工单文件夹">
    <div class="folder-head">
      <button type="button" class="btn btn-ghost btn-sm" @click="closeFolder">‹ 全部文件夹</button>
      <div class="folder-head-main">
        <div class="folder-head-title"><Icon name="folder" :size="18" /><span class="mono">{{ folderTitle }}</span></div>
        <div class="page-sub ellipsis" :title="folderSub">{{ folderSub || '—' }}</div>
      </div>
      <div class="folder-head-side">
        <span class="text-muted">共 <span class="num">{{ files.length }}</span> 个文件 · <span class="num">{{ folderSizeText }}</span></span>
        <button type="button" class="btn btn-secondary btn-sm" @click="manage">管理这些文件</button>
      </div>
    </div>

    <Loading v-if="filesLoading && !files.length" />
    <Empty v-else-if="filesError && !files.length" :text="'加载失败：' + filesError">
      <button type="button" class="btn btn-secondary btn-sm" @click="loadFolder">重试</button>
    </Empty>
    <template v-else>
      <div v-for="g in sections" :key="g.key" class="folder-section">
        <div class="folder-section-head">
          <span class="folder-section-title">{{ g.title }}</span>
          <span class="tab-count">{{ g.rows.length }}</span>
        </div>
        <div v-if="!g.rows.length" class="folder-section-empty">还没有上传</div>
        <table v-else class="data-table files-table folder-table">
          <colgroup><col style="width: 60px"><col><col style="width: 100px"><col style="width: 150px"><col style="width: 90px"><col style="width: 80px"></colgroup>
          <tbody>
            <tr v-for="r in g.rows" :key="r.f.id">
              <td>
                <button v-if="r.thumb" type="button" class="thumb thumb-sm" :aria-label="'预览 ' + r.name" @click="openFile(r)">
                  <img :src="r.f.url" alt="" loading="lazy"></button>
                <button v-else type="button" class="att-badge files-badge-btn" :style="{ background: r.badge.color }"
                        :aria-label="(r.media ? '预览 ' : '下载 ') + r.name" @click="openFile(r)">{{ r.badge.text }}</button>
              </td>
              <td>
                <div class="files-name-line att-name-wrap">
                  <button type="button" class="files-name ellipsis" :title="r.name" @click="openFile(r)">{{ r.name }}</button>
                  <Tag v-if="r.restricted" tone="warn">仅销售和主管可见</Tag>
                </div>
              </td>
              <td><span class="ellipsis files-uploader" :title="r.uploader">{{ r.uploader }}</span></td>
              <td class="nowrap"><span class="num text-muted">{{ r.timeText }}</span></td>
              <td class="text-right nowrap"><span class="num">{{ r.sizeText }}</span></td>
              <td class="text-right"><button type="button" class="btn-link att-link" :disabled="!!busyId" @click="download(r)">{{ r.busy ? '处理中…' : '下载' }}</button></td>
            </tr>
          </tbody>
        </table>
      </div>
    </template>
  </section>

  <!-- 文件夹列表 -->
  <section v-else class="card" aria-label="工单文件夹列表">
    <div class="files-filters">
      <SearchBox v-model="keywordInput" placeholder="工单号 / 客户 / 项目编号 / 文件名" :width="280" @search="onSearch" />
      <span class="text-muted folder-tip">一个工单一个文件夹，点开可以看到这张工单的全部资料</span>
    </div>
    <div class="files-table-wrap" :class="{ 'files-busy': loading && loaded }">
      <table class="data-table folder-list">
        <colgroup>
          <col style="width: 44px"><col style="width: 168px"><col><col style="width: 300px"><col style="width: 76px"><col style="width: 90px"><col style="width: 150px">
        </colgroup>
        <thead>
          <tr><th></th><th>工单</th><th>客户 / 项目</th><th>签字单据</th><th class="text-right">文件数</th><th class="text-right">大小</th><th>最近上传</th></tr>
        </thead>
        <tbody v-if="rows.length">
          <tr v-for="r in rows" :key="r.key" class="folder-row" tabindex="0" role="button" :aria-label="'打开工单 ' + r.x.orderNo + ' 的文件夹'"
              @click="openFolder(r)" @keydown.enter="openFolder(r)">
            <td><Icon name="folder" :size="20" /></td>
            <td>
              <span class="mono folder-no">{{ r.x.orderNo }}</span>
              <span class="cell-sub">{{ r.statusLabel }}<template v-if="r.serviceLabel"> · {{ r.serviceLabel }}</template></span>
            </td>
            <td>
              <span class="ellipsis folder-customer" :title="r.x.customerName">{{ r.x.customerName || '—' }}</span>
              <span class="cell-sub ellipsis" :title="r.sub">{{ r.sub || '—' }}</span>
            </td>
            <td>
              <div class="folder-docs">
                <span v-for="d in r.docs" :key="d.label" class="folder-doc" :class="{ missing: !d.n }">{{ d.label }}<template v-if="d.n"> {{ d.n }}</template><template v-else> 未传</template></span>
              </div>
            </td>
            <td class="text-right"><span class="num">{{ r.x.fileCount }}</span></td>
            <td class="text-right nowrap"><span class="num">{{ r.sizeText }}</span></td>
            <td class="nowrap"><span class="num text-muted">{{ r.timeText }}</span></td>
          </tr>
        </tbody>
      </table>
      <Loading v-if="loading && !loaded" />
      <Empty v-else-if="error && !rows.length" :text="'加载失败：' + error">
        <button type="button" class="btn btn-secondary btn-sm" @click="load"><Icon name="refresh" :size="14" />重试</button>
      </Empty>
      <Empty v-else-if="!loading && !rows.length" :text="keyword ? '没有找到匹配的工单文件夹' : '还没有工单上传过文件'" />
    </div>
    <div v-if="total > 0" class="table-foot">
      <span>共 <span class="num">{{ total }}</span> 个文件夹<template v-if="total > rows.length">，每页 {{ pageSize }} 个</template></span>
      <Pagination :total="total" :page="page" :page-size="pageSize" @change="goPage" />
    </div>
  </section>
  `,
};
