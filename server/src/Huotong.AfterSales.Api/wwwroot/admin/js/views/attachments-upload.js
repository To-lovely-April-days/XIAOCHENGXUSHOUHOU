// 附件上传组件（v1.2，新建工单与工单抽屉共用）。契约见 docs/CHANGES-v1.2.md §7.3。
// 选择 / 拖入文件 → 预检（类型、大小、数量）→ POST /api/files（category=Attachment，带原始文件名与进度）。
// 这里只上传、不挂工单：父组件用 attDone(items) 取 [{ fileId, restricted }] 再提交。
//
// <AttachmentsUpload ref="att" v-model="items" :limit="20" :can-restrict="true" />
// 条目：{ key, file, name, size, kind, ext, previewUrl, status: 'uploading'|'done'|'error', fatal, error, percent, id, restricted }
//   （另有内部字段 queued：排队中尚未开始；removed：已被移除）
import * as api from '../api.js';
import { fmtSize, fileBadge } from '../format.js';

const { reactive } = window.Vue;

export const MAX_COUNT = 20;
export const MAX_IMAGE = 20 * 1024 * 1024;
export const MAX_FILE = 100 * 1024 * 1024;
export const DOC_EXTS = ['doc', 'docx', 'xls', 'xlsx', 'ppt', 'pptx', 'pdf', 'txt', 'csv'];
const IMAGE_EXTS = ['jpg', 'jpeg', 'png', 'gif', 'bmp', 'webp', 'heic', 'heif'];
const VIDEO_EXTS = ['mp4', 'm4v', 'mov', '3gp'];
export const ACCEPT = 'image/*,video/*,.doc,.docx,.xls,.xlsx,.ppt,.pptx,.pdf,.txt,.csv';
/** 同时最多上传几个 */
const PARALLEL = 2;

/** 取最后一个扩展名（小写、不带点）；没有则 '' */
export function fileExt(name) {
  const s = String(name || '');
  const i = s.lastIndexOf('.');
  return i > 0 && i < s.length - 1 ? s.slice(i + 1).toLowerCase() : '';
}

/**
 * 预检：返回 { kind, ext, error }，error 非空 = 不能上传（文案同服务端 / 小程序）。
 * @param {File} file
 */
export function precheck(file) {
  const ext = fileExt(file.name);
  const type = String(file.type || '').toLowerCase();
  let kind = '';
  if (DOC_EXTS.includes(ext)) kind = 'Document';
  else if (IMAGE_EXTS.includes(ext) || (/^image\//.test(type) && type !== 'image/svg+xml')) kind = 'Image';
  else if (VIDEO_EXTS.includes(ext) || /^video\//.test(type)) kind = 'Video';
  if (!kind) return { kind: 'Document', ext, error: '不支持的文件类型' };
  if (kind === 'Image' && file.size > MAX_IMAGE) return { kind, ext, error: '图片不能超过 20MB' };
  if (kind !== 'Image' && file.size > MAX_FILE) return { kind, ext, error: '文件不能超过 100MB' };
  return { kind, ext, error: '' };
}

/** 是否还有文件在上传 / 排队 */
export const attUploading = (items) => (items || []).some(x => x.status === 'uploading');
/** 是否有上传失败（含预检不通过）的条目 */
export const attHasError = (items) => (items || []).some(x => x.status === 'error');
/** 已上传成功的条目 → 接口需要的 [{ fileId, restricted }] */
export const attDone = (items) => (items || []).filter(x => x.status === 'done' && x.id)
  .map(x => ({ fileId: x.id, restricted: !!x.restricted }));

let seq = 0;

export default {
  name: 'AttachmentsUpload',
  props: {
    /** 还能加几个（包含本组件里已选的条目） */
    limit: { type: Number, default: MAX_COUNT },
    /** 是否显示「仅销售和主管可见」复选框 */
    canRestrict: { type: Boolean, default: false },
    modelValue: { type: Array, default: () => [] },
    disabled: { type: Boolean, default: false },
  },
  emits: ['update:modelValue'],
  data() { return { accept: ACCEPT, notice: '', dragging: false }; },
  computed: {
    items() { return this.modelValue || []; },
    /** 占名额的条目数（预检不通过的不占） */
    used() { return this.items.filter(x => !x.fatal).length; },
    room() { return Math.max(0, this.limit - this.used); },
    uploading() { return attUploading(this.items); },
    hasError() { return attHasError(this.items); },
    done() { return attDone(this.items); },
    rows() {
      return this.items.map(it => ({
        it,
        key: it.key,
        badge: fileBadge(it),
        sizeText: fmtSize(it.size),
        stateText: it.status === 'done' ? '已上传' : (it.status === 'uploading' ? (it.queued ? '等待上传' : `上传中 ${it.percent}%`) : ''),
      }));
    },
  },
  beforeUnmount() {
    this._gone = true;
    this.items.forEach(it => { it.removed = true; this.abort(it); this.revoke(it); });
  },
  methods: {
    /** 取消该条目正在进行的上传（移除条目 / 组件卸载时），避免大文件继续占用带宽 */
    abort(it) {
      const c = this._ctrls && this._ctrls.get(it.key);
      if (c) { this._ctrls.delete(it.key); try { c.abort(); } catch (e) { /* ignore */ } }
    },
    revoke(it) {
      if (it && it.previewUrl) { try { URL.revokeObjectURL(it.previewUrl); } catch (e) { /* ignore */ } it.previewUrl = ''; }
    },
    pick() {
      if (this.disabled) return;
      const el = this.$refs.file;
      if (el) { el.value = ''; el.click(); }
    },
    onFiles(e) { this.addFiles(e.target.files); },
    onDragOver(e) {
      if (this.disabled) return;
      if (e.dataTransfer) e.dataTransfer.dropEffect = 'copy';
      this.dragging = true;
    },
    onDragLeave(e) {
      // 只在离开整个区域时取消高亮
      if (!e.currentTarget.contains(e.relatedTarget)) this.dragging = false;
    },
    onDrop(e) {
      this.dragging = false;
      if (this.disabled) return;
      this.addFiles(e.dataTransfer && e.dataTransfer.files);
    },
    /** @param {FileList|File[]|null} fileList */
    addFiles(fileList) {
      const files = Array.from(fileList || []);
      if (!files.length) return;
      this.notice = '';
      const next = this.items.slice();
      let room = this.room;
      let ignored = 0;
      files.forEach(file => {
        const chk = precheck(file);
        if (!chk.error) {
          if (room <= 0) { ignored += 1; return; }
          room -= 1;
        }
        const item = reactive({
          key: 'a' + (++seq) + '-' + Math.random().toString(36).slice(2, 8),
          file, name: file.name || '未命名', size: file.size, kind: chk.kind, ext: chk.ext,
          previewUrl: '', status: chk.error ? 'error' : 'uploading', fatal: !!chk.error, error: chk.error,
          percent: 0, id: null, restricted: false, queued: !chk.error, removed: false,
        });
        if (!chk.error && chk.kind === 'Image') {
          try { item.previewUrl = URL.createObjectURL(file); } catch (e) { item.previewUrl = ''; }
        }
        next.push(item);
      });
      if (ignored > 0) this.notice = `附件最多 ${MAX_COUNT} 个，已忽略多余的 ${ignored} 个`;
      this.$emit('update:modelValue', next);
      this._pending = next;
      this.pump(next);
    },
    /** 启动排队中的上传，保持同时最多 PARALLEL 个 */
    pump(list) {
      const items = list || this._pending || this.items;
      let active = items.filter(x => x.status === 'uploading' && !x.queued && !x.removed).length;
      for (let i = 0; i < items.length && active < PARALLEL; i += 1) {
        const it = items[i];
        if (it.status === 'uploading' && it.queued && !it.removed) { active += 1; this.start(it); }
      }
    },
    async start(it) {
      it.queued = false;
      it.percent = 0;
      it.error = '';
      const ctrl = typeof AbortController === 'function' ? new AbortController() : null;
      if (ctrl) { (this._ctrls || (this._ctrls = new Map())).set(it.key, ctrl); }
      try {
        const res = await api.files.upload(it.file, 'Attachment', {
          fileName: it.file.name,
          // 进度到 100% 只表示请求体已发完，服务端还在校验 / 落盘：成功前最多显示 99%
          onProgress: (p) => { if (!it.removed) it.percent = Math.min(99, p); },
          signal: ctrl ? ctrl.signal : undefined,
        });
        if (it.removed) return;
        it.id = res.id;
        it.percent = 100;
        it.status = 'done';
      } catch (e) {
        if (it.removed) return;
        it.status = 'error';
        it.error = (e && e.message) || '上传失败';
      } finally {
        if (this._ctrls && this._ctrls.get(it.key) === ctrl) this._ctrls.delete(it.key);
        if (!this._gone) { this._pending = null; this.pump(); }
      }
    },
    retry(it) {
      if (it.fatal || it.status !== 'error') return;
      it.status = 'uploading';
      it.queued = true;
      it.error = '';
      it.percent = 0;
      this.pump();
    },
    remove(it) {
      it.removed = true;
      this.abort(it);
      this.revoke(it);
      this.notice = '';
      const next = this.items.filter(x => x !== it && x.key !== it.key);
      this.$emit('update:modelValue', next);
      this._pending = next;
      this.pump(next);
    },
    toggleRestricted(it, e) { it.restricted = !!e.target.checked; },
  },
  template: `
  <div class="att-up" :class="{ 'att-up-drag': dragging }"
       @dragover.prevent="onDragOver" @dragleave="onDragLeave" @drop.prevent="onDrop">
    <ul v-if="rows.length" class="att-up-list">
      <li v-for="r in rows" :key="r.key" class="att-up-item" :class="'is-' + r.it.status">
        <span v-if="r.it.previewUrl" class="att-thumb"><img :src="r.it.previewUrl" alt=""></span>
        <span v-else class="att-badge" :style="{ background: r.badge.color }" aria-hidden="true">{{ r.badge.text }}</span>
        <div class="att-up-main">
          <div class="att-up-name ellipsis" :title="r.it.name">{{ r.it.name }}</div>
          <div class="att-up-meta">
            <span class="num">{{ r.sizeText }}</span>
            <span v-if="r.it.status === 'error'" class="att-up-err">{{ r.it.error }}</span>
            <span v-else-if="r.stateText" class="num" :class="{ 'text-success': r.it.status === 'done' }">{{ r.stateText }}</span>
          </div>
          <div v-if="r.it.status === 'uploading'" class="att-up-bar" role="progressbar" aria-valuemin="0" aria-valuemax="100"
               :aria-valuenow="r.it.percent" :aria-label="r.it.name + ' 上传进度'"><i :style="{ width: r.it.percent + '%' }"></i></div>
          <label v-if="canRestrict && !r.it.fatal" class="checkbox att-up-restrict">
            <input type="checkbox" :checked="r.it.restricted" @change="toggleRestricted(r.it, $event)">仅销售和主管可见</label>
        </div>
        <div class="att-up-actions">
          <button v-if="r.it.status === 'error' && !r.it.fatal" type="button" class="btn-link att-link" @click="retry(r.it)">重试</button>
          <button type="button" class="btn-link att-link" :aria-label="'移除 ' + r.it.name" @click="remove(r.it)">移除</button>
        </div>
      </li>
    </ul>
    <button type="button" class="att-up-drop" :disabled="disabled || room <= 0" @click="pick">
      <Icon name="paperclip" :size="16" />
      <span v-if="room > 0">点击选择文件，或把文件拖到这里<span class="att-up-room">还可添加 <span class="num">{{ room }}</span> 个</span></span>
      <span v-else>附件最多 20 个，已达上限</span>
    </button>
    <input ref="file" type="file" multiple hidden :accept="accept" @change="onFiles">
    <div v-if="notice" class="field-error att-up-notice" role="status">{{ notice }}</div>
  </div>`,
};
