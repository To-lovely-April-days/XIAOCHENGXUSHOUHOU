// 公共 UI：toast / confirm / 图片预览 + 通用组件。组件已在 main.js 全局注册，视图模板里可直接用 <Avatar> 等。
import { ORDER_STATUS, ISSUE } from './enums.js';

const { reactive, nextTick } = window.Vue;

/** 全局 UI 状态（由 UiHost 渲染） */
export const uiState = reactive({ toasts: [], dialog: null, lightbox: null });

let toastSeq = 0;
/**
 * 轻提示。
 * @param {string} message
 * @param {'info'|'success'|'error'} [type='info']
 * @param {number} [duration=2400] 毫秒
 */
export function toast(message, type = 'info', duration = 2400) {
  if (!message) return;
  const id = ++toastSeq;
  uiState.toasts.push({ id, message: String(message), type });
  setTimeout(() => {
    const i = uiState.toasts.findIndex(t => t.id === id);
    if (i >= 0) uiState.toasts.splice(i, 1);
  }, duration);
}

/** 把 catch 到的错误弹成 toast（40100 已跳登录，不再提示） */
export function toastError(err) {
  if (err && err.code === 40100) return;
  toast((err && err.message) || '操作失败', 'error', 3200);
}

/**
 * 确认弹窗（自绘 modal）。
 * 无输入框：确定 → true，取消 → false。
 * 有输入框（opts.input）：确定 → 输入的字符串（已 trim），取消 → null。
 * @param {string} title
 * @param {string} [message]
 * @param {{input?:boolean,multiline?:boolean,placeholder?:string,required?:boolean,defaultValue?:string,
 *          inputLabel?:string,okText?:string,cancelText?:string,danger?:boolean,maxLength?:number}} [opts]
 * @returns {Promise<boolean|string|null>}
 */
export function confirm(title, message = '', opts = {}) {
  if (uiState.dialog) uiState.dialog.resolve(opts.input ? null : false);
  return new Promise(resolve => {
    uiState.dialog = Object.assign({
      okText: '确定', cancelText: '取消', placeholder: '', inputLabel: '', required: false,
      input: false, multiline: false, danger: false, maxLength: 200,
    }, opts, { title, message, value: opts.defaultValue || '', error: '', resolve });
  });
}

/**
 * 图片 / 视频预览（lightbox）。
 * 条目可以是地址字符串、{ url }（按图片）或 { url, type: 'image'|'video' }。
 * @param {string|Array<string|{url:string,type?:'image'|'video'}>|{url:string,type?:string}} items
 * @param {number} [index=0]
 */
export function preview(items, index = 0) {
  const list = (Array.isArray(items) ? items : [items])
    .map(u => (typeof u === 'string' ? { url: u, type: 'image' } : (u && u.url ? { url: u.url, type: u.type === 'video' ? 'video' : 'image' } : null)))
    .filter(Boolean);
  if (!list.length) return;
  uiState.lightbox = { list, index: Math.min(Math.max(0, index), list.length - 1) };
}

// ---------------------------------------------------------------- 图标

const ICONS = {
  orders: '<rect x="5" y="4" width="14" height="17" rx="2"/><path d="M9 9h6M9 13h6M9 17h3"/>',
  camera: '<path d="M4 8h3l2-3h6l2 3h3v11H4z"/><circle cx="12" cy="13" r="3.5"/>',
  clock: '<circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/>',
  users: '<circle cx="9" cy="8" r="3.5"/><path d="M3 20c0-3.3 2.7-5.5 6-5.5s6 2.2 6 5.5"/><path d="M16 4.5a3.5 3.5 0 0 1 0 7M18 14.8c1.8.7 3 2.5 3 5.2"/>',
  settings: '<circle cx="12" cy="12" r="3"/><path d="M12 3v2.5M12 18.5V21M3 12h2.5M18.5 12H21M5.6 5.6l1.8 1.8M16.6 16.6l1.8 1.8M5.6 18.4l1.8-1.8M16.6 7.4l1.8-1.8"/>',
  search: '<circle cx="11" cy="11" r="6.5"/><path d="M16 16l4 4"/>',
  download: '<path d="M12 4v11M7 10l5 5 5-5M5 20h14"/>',
  plus: '<path d="M12 5v14M5 12h14"/>',
  chevronDown: '<path d="M6 9l6 6 6-6"/>',
  chevronLeft: '<path d="M15 6l-6 6 6 6"/>',
  chevronRight: '<path d="M9 6l6 6-6 6"/>',
  close: '<path d="M6 6l12 12M18 6L6 18"/>',
  alert: '<path d="M12 4l9 16H3z"/><path d="M12 10v4M12 17v.5"/>',
  check: '<path d="M5 12.5l4.5 4.5L19 7.5"/>',
  info: '<circle cx="12" cy="12" r="9"/><path d="M12 11v5M12 8v.5"/>',
  logout: '<path d="M15 4h3a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2h-3"/><path d="M10 8l-4 4 4 4M6 12h10"/>',
  key: '<circle cx="8" cy="15" r="4"/><path d="M11 12l9-9M17 6l3 3M15 8l2 2"/>',
  image: '<rect x="3" y="5" width="18" height="14" rx="2"/><circle cx="9" cy="10" r="1.8"/><path d="M21 16l-5-5-8 8"/>',
  refresh: '<path d="M20 11a8 8 0 1 0-2.3 5.7"/><path d="M20 5v6h-6"/>',
  folder: '<path d="M3 7a2 2 0 0 1 2-2h4l2 2.5h8a2 2 0 0 1 2 2V18a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/>',
  file: '<path d="M7 3h7l5 5v11a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2z"/><path d="M14 3v5h5"/>',
  video: '<rect x="3" y="6" width="13" height="12" rx="2"/><path d="M16 10.5l5-3v9l-5-3z"/>',
  lock: '<rect x="5" y="11" width="14" height="9" rx="2"/><path d="M8 11V8a4 4 0 0 1 8 0v3"/>',
  trash: '<path d="M4 7h16M10 7V4h4v3M6 7l1 13h10l1-13M10 11v5M14 11v5"/>',
  paperclip: '<path d="M20 11.5l-8 8a5 5 0 0 1-7-7l8.5-8.5a3.3 3.3 0 0 1 4.7 4.7L9.8 17.1a1.7 1.7 0 0 1-2.4-2.4l7.6-7.6"/>',
};

/** <Icon name="search" :size="15" :stroke="1.8" /> */
export const Icon = {
  name: 'Icon',
  props: { name: { type: String, required: true }, size: { type: [Number, String], default: 16 }, stroke: { type: [Number, String], default: 1.8 } },
  computed: { svg() { return ICONS[this.name] || ''; } },
  template: `<svg class="icon" :width="size" :height="size" viewBox="0 0 24 24" aria-hidden="true" fill="none" stroke="currentColor"
    :stroke-width="stroke" stroke-linecap="round" stroke-linejoin="round" v-html="svg"></svg>`,
};

// ---------------------------------------------------------------- 展示组件

/**
 * 头像：有 avatarUrl 显示图片，否则显示姓氏圆。
 * <Avatar :user="order.assignee" :size="20" />
 */
export const Avatar = {
  name: 'Avatar',
  props: { user: { type: Object, default: null }, size: { type: [Number, String], default: 28 } },
  data() { return { broken: false }; },
  computed: {
    url() { return !this.broken && this.user && this.user.avatarUrl; },
    text() {
      const u = this.user || {};
      return u.surname || (u.name ? u.name.slice(0, 1) : '?');
    },
    style() {
      const s = Number(this.size);
      return { width: s + 'px', height: s + 'px', fontSize: Math.max(10, Math.round(s * 0.43)) + 'px' };
    },
  },
  watch: { user() { this.broken = false; } },
  template: `<span class="avatar" :style="style" aria-hidden="true">
    <img v-if="url" :src="url" alt="" @error="broken = true"><template v-else>{{ text }}</template></span>`,
};

/** 状态圆点 + 文字：<StatusDot status="InProgress" /> ；:label="false" 只显示圆点 */
export const StatusDot = {
  name: 'StatusDot',
  props: { status: { type: String, default: '' }, label: { type: Boolean, default: true } },
  computed: { meta() { return ORDER_STATUS[this.status] || { label: this.status, dot: '#D4D4D8' }; } },
  template: `<span class="status"><span class="dot" :style="{ background: meta.dot }"></span><template v-if="label">{{ meta.label }}</template></span>`,
};

/** 通用标签：<Tag bg="#FEF2F2" fg="#B91C1C">紧急</Tag>，或 tone="urgent|missing|distance|success|neutral|warn" */
export const Tag = {
  name: 'Tag',
  props: { bg: String, fg: String, tone: { type: String, default: 'neutral' } },
  computed: { style() { return this.bg || this.fg ? { background: this.bg, color: this.fg } : null; } },
  template: `<span class="tag" :class="'tag-' + tone" :style="style"><slot /></span>`,
};

/** 异常标签：<IssueTag type="MissingPunch" /> */
export const IssueTag = {
  name: 'IssueTag',
  props: { type: { type: String, required: true } },
  computed: { meta() { return ISSUE[this.type] || { label: this.type, bg: '#F4F4F5', fg: '#3F3F46' }; } },
  template: `<span class="tag" :style="{ background: meta.bg, color: meta.fg }">{{ meta.label }}</span>`,
};

/** 空状态：<Empty text="没有符合条件的工单" /> */
export const Empty = {
  name: 'Empty',
  props: { text: { type: String, default: '暂无数据' } },
  template: `<div class="empty"><div>{{ text }}</div><slot /></div>`,
};

/** 加载中占位：<Loading /> */
export const Loading = {
  name: 'Loading',
  props: { text: { type: String, default: '加载中…' } },
  template: `<div class="loading"><span class="spinner"></span>{{ text }}</div>`,
};

/**
 * 分页：<Pagination :total="total" :page="page" :page-size="20" @change="p => page = p" />
 * 也支持 v-model:page。
 */
export const Pagination = {
  name: 'Pagination',
  props: { total: { type: Number, default: 0 }, page: { type: Number, default: 1 }, pageSize: { type: Number, default: 20 } },
  emits: ['update:page', 'change'],
  computed: { pages() { return Math.max(1, Math.ceil(this.total / Math.max(1, this.pageSize))); } },
  methods: {
    go(p) {
      if (p < 1 || p > this.pages || p === this.page) return;
      this.$emit('update:page', p);
      this.$emit('change', p);
    },
  },
  template: `<div class="pagination">
    <button type="button" class="page-btn" :disabled="page <= 1" aria-label="上一页" @click="go(page - 1)"><Icon name="chevronLeft" :size="14" /></button>
    <span class="num">第 {{ page }} / {{ pages }} 页</span>
    <button type="button" class="page-btn" :disabled="page >= pages" aria-label="下一页" @click="go(page + 1)"><Icon name="chevronRight" :size="14" /></button>
  </div>`,
};

/**
 * 弹窗：<Modal v-if="show" title="新建工单" :width="640" @close="show = false">内容<template #footer>按钮</template></Modal>
 * persistent=true 时点遮罩不关闭。
 */
export const Modal = {
  name: 'Modal',
  props: { title: { type: String, default: '' }, width: { type: [Number, String], default: 480 }, persistent: { type: Boolean, default: false } },
  emits: ['close'],
  mounted() {
    this._onKey = (e) => { if (e.key === 'Escape') this.$emit('close'); };
    document.addEventListener('keydown', this._onKey);
  },
  beforeUnmount() { document.removeEventListener('keydown', this._onKey); },
  methods: { onMask() { if (!this.persistent) this.$emit('close'); } },
  template: `<Teleport to="body"><div class="modal-mask" @mousedown.self="onMask">
    <div class="modal" role="dialog" :aria-label="title" :style="{ width: width + 'px' }">
      <div class="modal-head"><span class="modal-title">{{ title }}</span>
        <button type="button" class="icon-btn" aria-label="关闭" @click="$emit('close')"><Icon name="close" /></button></div>
      <div class="modal-body"><slot /></div>
      <div v-if="$slots.footer" class="modal-foot"><slot name="footer" /></div>
    </div></div></Teleport>`,
};

/**
 * 右侧抽屉（原型 16 工单详情，宽 400，顶部让出 56px 顶栏）。
 * <Drawer @close="..."><template #head>工单号 + 状态</template>正文<template #foot>按钮</template></Drawer>
 */
export const Drawer = {
  name: 'Drawer',
  props: { width: { type: [Number, String], default: 400 }, label: { type: String, default: '详情' } },
  emits: ['close'],
  mounted() {
    this._onKey = (e) => { if (e.key === 'Escape' && !uiState.dialog && !uiState.lightbox && !document.querySelector('.modal-mask')) this.$emit('close'); };
    document.addEventListener('keydown', this._onKey);
  },
  beforeUnmount() { document.removeEventListener('keydown', this._onKey); },
  template: `<aside class="drawer" role="dialog" :aria-label="label" :style="{ width: width + 'px' }">
    <div class="drawer-head"><slot name="head" />
      <button type="button" class="icon-btn drawer-close" aria-label="关闭详情" @click="$emit('close')"><Icon name="close" :stroke="2" /></button></div>
    <div class="drawer-body"><slot /></div>
    <div v-if="$slots.foot" class="drawer-foot"><slot name="foot" /></div>
  </aside>`,
};

/**
 * 顶栏（56px）：面包屑 + 右侧插槽（搜索、按钮）。
 * <PageHeader :crumbs="['工单', '全部工单']"><button class="btn btn-primary">新建工单</button></PageHeader>
 */
export const PageHeader = {
  name: 'PageHeader',
  props: { crumbs: { type: Array, default: () => [] } },
  template: `<header class="topbar">
    <nav class="crumbs" aria-label="位置">
      <template v-for="(c, i) in crumbs" :key="i">
        <span v-if="i > 0" class="crumb-sep" aria-hidden="true">/</span>
        <span :class="{ 'crumb-current': i === crumbs.length - 1 }">{{ c }}</span>
      </template>
    </nav>
    <slot />
  </header>`,
};

/**
 * 搜索框（顶栏样式）：<SearchBox v-model="keyword" placeholder="搜索工单号、客户、设备编号" @search="load" />
 * 回车或停止输入 400ms 后触发 search。
 */
export const SearchBox = {
  name: 'SearchBox',
  props: { modelValue: { type: String, default: '' }, placeholder: { type: String, default: '搜索' }, width: { type: [Number, String], default: 280 } },
  emits: ['update:modelValue', 'search'],
  methods: {
    onInput(e) {
      this.$emit('update:modelValue', e.target.value);
      clearTimeout(this._t);
      this._t = setTimeout(() => this.$emit('search', e.target.value), 400);
    },
    onEnter(e) { clearTimeout(this._t); this.$emit('search', e.target.value); },
  },
  template: `<label class="search" :style="{ width: width + 'px' }"><Icon name="search" :size="15" />
    <input type="search" :value="modelValue" :placeholder="placeholder" :aria-label="placeholder" @input="onInput" @keydown.enter="onEnter"></label>`,
};

// ---------------------------------------------------------------- UiHost：toast / confirm / lightbox

export const UiHost = {
  name: 'UiHost',
  data() { return { ui: uiState }; },
  computed: {
    dialog() { return this.ui.dialog; },
    lb() { return this.ui.lightbox; },
    lbItem() { const lb = this.ui.lightbox; return (lb && lb.list[lb.index]) || { url: '', type: 'image' }; },
  },
  watch: {
    dialog(d) {
      if (d && d.input) nextTick(() => { const el = this.$refs.dlgInput; if (el) el.focus(); });
      else if (d) nextTick(() => { const el = this.$refs.dlgOk; if (el) el.focus(); });
    },
  },
  mounted() {
    this._onKey = (e) => {
      if (this.lb) {
        if (e.key === 'Escape') { e.stopPropagation(); this.ui.lightbox = null; }
        else if (e.key === 'ArrowLeft') this.step(-1);
        else if (e.key === 'ArrowRight') this.step(1);
      } else if (this.dialog && e.key === 'Escape') {
        e.stopPropagation(); this.cancel();
      }
    };
    document.addEventListener('keydown', this._onKey, true);
  },
  beforeUnmount() { document.removeEventListener('keydown', this._onKey, true); },
  methods: {
    ok() {
      const d = this.dialog; if (!d) return;
      const v = (d.value || '').trim();
      if (d.input && d.required && !v) { d.error = '请填写' + (d.inputLabel || '内容'); return; }
      this.ui.dialog = null;
      d.resolve(d.input ? v : true);
    },
    cancel() {
      const d = this.dialog; if (!d) return;
      this.ui.dialog = null;
      d.resolve(d.input ? null : false);
    },
    onEnter(e) { if (!this.dialog.multiline) { e.preventDefault(); this.ok(); } },
    step(n) {
      const lb = this.lb; if (!lb) return;
      lb.index = (lb.index + n + lb.list.length) % lb.list.length;
    },
  },
  template: `
  <div class="toast-wrap" aria-live="polite">
    <div v-for="t in ui.toasts" :key="t.id" class="toast" :class="'toast-' + t.type">
      <Icon v-if="t.type === 'success'" name="check" :size="15" :stroke="2.2" />
      <Icon v-else-if="t.type === 'error'" name="alert" :size="15" :stroke="2" />
      <span>{{ t.message }}</span>
    </div>
  </div>
  <div v-if="dialog" class="modal-mask" @mousedown.self="cancel">
    <div class="modal" role="alertdialog" :aria-label="dialog.title" style="width: 420px">
      <div class="modal-head"><span class="modal-title">{{ dialog.title }}</span></div>
      <div class="modal-body">
        <p v-if="dialog.message" class="dialog-msg">{{ dialog.message }}</p>
        <label v-if="dialog.input" class="field">
          <span v-if="dialog.inputLabel" class="field-label">{{ dialog.inputLabel }}<em v-if="dialog.required">（必填）</em></span>
          <textarea v-if="dialog.multiline" ref="dlgInput" class="textarea" rows="3" v-model="dialog.value"
            :placeholder="dialog.placeholder" :maxlength="dialog.maxLength" @input="dialog.error = ''"></textarea>
          <input v-else ref="dlgInput" class="input" v-model="dialog.value" :placeholder="dialog.placeholder"
            :maxlength="dialog.maxLength" @input="dialog.error = ''" @keydown.enter="onEnter">
          <span v-if="dialog.error" class="field-error">{{ dialog.error }}</span>
        </label>
      </div>
      <div class="modal-foot">
        <button type="button" class="btn btn-secondary" @click="cancel">{{ dialog.cancelText }}</button>
        <button type="button" ref="dlgOk" class="btn" :class="dialog.danger ? 'btn-danger' : 'btn-primary'" @click="ok">{{ dialog.okText }}</button>
      </div>
    </div>
  </div>
  <div v-if="lb" class="lightbox" @click.self="ui.lightbox = null">
    <video v-if="lbItem.type === 'video'" :key="lbItem.url" class="att-lightbox-video" :src="lbItem.url" controls autoplay playsinline></video>
    <img v-else :src="lbItem.url" alt="图片预览">
    <button type="button" class="lightbox-close" aria-label="关闭预览" @click="ui.lightbox = null"><Icon name="close" :size="20" :stroke="2" /></button>
    <template v-if="lb.list.length > 1">
      <button type="button" class="lightbox-nav lightbox-prev" aria-label="上一张" @click="step(-1)"><Icon name="chevronLeft" :size="22" :stroke="2" /></button>
      <button type="button" class="lightbox-nav lightbox-next" aria-label="下一张" @click="step(1)"><Icon name="chevronRight" :size="22" :stroke="2" /></button>
      <div class="lightbox-count">{{ lb.index + 1 }} / {{ lb.list.length }}</div>
    </template>
  </div>`,
};

/** 全局注册所有公共组件 */
export function registerUi(app) {
  [Icon, Avatar, StatusDot, Tag, IssueTag, Empty, Loading, Pagination, Modal, Drawer, PageHeader, SearchBox, UiHost]
    .forEach(c => app.component(c.name, c));
}
