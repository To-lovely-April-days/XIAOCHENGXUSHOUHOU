// 入口：根组件、布局 AppShell、hash 路由、登录守卫。
import * as api from './api.js';
import { route, navigate, onRouteChange } from './router.js';
import { store, loadMe, logout, refreshCounts } from './store.js';
import { registerUi, toast, toastError } from './ui.js';
import { QUICK_FILTERS } from './enums.js';
import LoginView from './views/login.js';
import OrdersView from './views/orders.js';
import PunchesView from './views/punches.js';
import HoursView from './views/hours.js';
import FilesView from './views/files.js';
import AccountsView from './views/accounts.js';

const { createApp } = window.Vue;

/** 路由表（#/path） */
export const ROUTES = {
  '/orders': { title: '工单', icon: 'orders', component: OrdersView },
  '/punches': { title: '打卡记录', icon: 'camera', component: PunchesView },
  '/hours': { title: '工时统计', icon: 'clock', component: HoursView },
  '/files': { title: '文件', icon: 'folder', component: FilesView },
  '/accounts': { title: '账号管理', icon: 'users', component: AccountsView },
};
const DEFAULT_PATH = '/orders';

// ---------------------------------------------------------------- 修改密码

const ChangePasswordModal = {
  name: 'ChangePasswordModal',
  emits: ['close'],
  data() { return { oldPassword: '', newPassword: '', confirmPassword: '', error: '', saving: false }; },
  methods: {
    async submit() {
      this.error = '';
      if (this.newPassword.length < 8 || !/[A-Za-z]/.test(this.newPassword) || !/\d/.test(this.newPassword)) {
        this.error = '新密码至少 8 位，且需同时包含字母和数字'; return;
      }
      if (this.newPassword !== this.confirmPassword) { this.error = '两次输入的新密码不一致'; return; }
      this.saving = true;
      try {
        const user = await api.me.changePassword(this.oldPassword, this.newPassword);
        if (user && user.id) store.user = user;
        toast('密码已修改', 'success');
        this.$emit('close');
      } catch (e) {
        if (e.code === 40100) { this.$emit('close'); return; }
        this.error = e.message;
      } finally { this.saving = false; }
    },
  },
  template: `<Modal title="修改密码" :width="400" persistent @close="$emit('close')">
    <form class="form-stack" @submit.prevent="submit">
      <label class="field"><span class="field-label">当前密码</span>
        <input class="input" type="password" v-model="oldPassword" autocomplete="current-password" placeholder="未设置过密码可留空"></label>
      <label class="field"><span class="field-label">新密码</span>
        <input class="input" type="password" v-model="newPassword" autocomplete="new-password" placeholder="至少 8 位，包含字母和数字"></label>
      <label class="field"><span class="field-label">确认新密码</span>
        <input class="input" type="password" v-model="confirmPassword" autocomplete="new-password" placeholder="再次输入新密码"></label>
      <div v-if="error" class="field-error">{{ error }}</div>
      <button type="submit" hidden></button>
    </form>
    <template #footer>
      <button type="button" class="btn btn-secondary" @click="$emit('close')">取消</button>
      <button type="button" class="btn btn-primary" :disabled="saving" @click="submit">{{ saving ? '保存中…' : '保存' }}</button>
    </template>
  </Modal>`,
};

// ---------------------------------------------------------------- 布局

const AppShell = {
  name: 'AppShell',
  components: { ChangePasswordModal },
  data() { return { store, route, menuOpen: false, showPwd: false }; },
  computed: {
    nav() {
      return Object.keys(ROUTES).map(path => ({
        path, title: ROUTES[path].title, icon: ROUTES[path].icon,
        active: route.path === path,
        count: path === '/orders' ? store.counts.pending : null,
      }));
    },
    quick() {
      return QUICK_FILTERS.map(q => ({
        ...q,
        count: store.counts[q.key],
        active: route.path === '/orders' && route.query.quick === q.key,
      }));
    },
    user() { return store.user || {}; },
  },
  mounted() {
    this._onDoc = (e) => { if (this.menuOpen && !this.$refs.userBox.contains(e.target)) this.menuOpen = false; };
    document.addEventListener('mousedown', this._onDoc);
  },
  beforeUnmount() { document.removeEventListener('mousedown', this._onDoc); },
  methods: {
    go(path) { navigate(path); },
    pickQuick(q) {
      if (q.active) navigate('/orders');
      else navigate('/orders', { quick: q.key });
    },
    openPwd() { this.menuOpen = false; this.showPwd = true; },
    doLogout() {
      this.menuOpen = false;
      logout();
      navigate('/login');
    },
  },
  template: `<div class="app">
    <aside class="sidebar">
      <div class="brand">
        <img class="brand-mark" src="img/logo-mark.png" alt="HTLAB">
        <div class="brand-text"><div class="brand-name">霍桐售后</div><div class="brand-sub">主管后台</div></div>
      </div>
      <nav class="nav" aria-label="后台菜单">
        <a v-for="n in nav" :key="n.path" :href="'#' + n.path" class="nav-item" :class="{ active: n.active }"
           :aria-current="n.active ? 'page' : null">
          <Icon :name="n.icon" /><span class="nav-label">{{ n.title }}</span>
          <span v-if="n.count" class="nav-count num">{{ n.count }}</span>
        </a>
      </nav>
      <div class="side-group">
        <div class="side-title">待处理</div>
        <div role="group" aria-label="待处理快捷筛选" class="nav">
          <button v-for="q in quick" :key="q.key" type="button" class="nav-item quick-item" :class="{ active: q.active }"
                  :aria-pressed="q.active ? 'true' : 'false'" @click="pickQuick(q)">
            <span class="quick-dot-wrap"><span class="dot" :style="{ background: q.dot }"></span></span>
            <span class="nav-label">{{ q.label }}</span>
            <span class="nav-count num">{{ q.count ?? '' }}</span>
          </button>
        </div>
      </div>
      <div class="side-user" ref="userBox">
        <Avatar :user="user" :size="28" />
        <div class="side-user-text"><div class="side-user-name">{{ user.name }}</div><div class="side-user-dept">{{ user.department || user.roleName }}</div></div>
        <button type="button" class="icon-btn" aria-label="设置" :aria-expanded="menuOpen ? 'true' : 'false'" @click="menuOpen = !menuOpen"><Icon name="settings" /></button>
        <div v-if="menuOpen" class="menu side-menu" role="menu">
          <div class="menu-head">{{ user.name }}<span class="text-muted"> · {{ user.phoneMasked }}</span></div>
          <button type="button" class="menu-item" role="menuitem" @click="openPwd"><Icon name="key" :size="15" />修改密码</button>
          <button type="button" class="menu-item" role="menuitem" @click="doLogout"><Icon name="logout" :size="15" />退出登录</button>
        </div>
      </div>
    </aside>
    <div class="main"><slot /></div>
    <ChangePasswordModal v-if="showPwd" @close="showPwd = false" />
  </div>`,
};

// ---------------------------------------------------------------- 根组件 & 守卫

const App = {
  name: 'App',
  components: { AppShell, LoginView },
  data() { return { route, store, checking: false }; },
  computed: {
    current() { return ROUTES[route.path] || null; },
    isLogin() { return route.path === '/login'; },
  },
  watch: {
    'route.path': { handler() { this.updateTitle(); }, immediate: true },
  },
  methods: {
    updateTitle() {
      const t = this.isLogin ? '登录' : (this.current && this.current.title);
      document.title = (t ? t + ' · ' : '') + '霍桐售后 · 主管后台';
    },
    retry() { store.bootError = ''; guard(); },
  },
  template: `
    <LoginView v-if="isLogin" />
    <div v-else-if="store.bootError" class="boot">
      <div>{{ store.bootError }}</div>
      <button type="button" class="btn btn-secondary" @click="retry">重试</button>
    </div>
    <div v-else-if="!store.user || !current" class="boot"><span class="spinner"></span>加载中…</div>
    <AppShell v-else><component :is="current.component" /></AppShell>
    <UiHost />`,
};

let loading = null;
/** 路由守卫：未登录 → #/login；已登录但未加载用户 → GET /api/me 校验角色 */
async function guard() {
  if (route.path === '/login') return;
  if (!ROUTES[route.path]) { navigate(DEFAULT_PATH, null, { replace: true }); return; }
  if (!api.getToken()) { navigate('/login', { redirect: route.fullPath }, { replace: true }); return; }
  if (store.user || loading) return;
  loading = loadMe()
    .catch(e => {
      if (e.code === 40100) return; // api.js 已跳登录
      if (e.code === 40300) {
        toast(e.message, 'error', 3200);
        navigate('/login', null, { replace: true });
        return;
      }
      store.bootError = e.message || '加载失败';
    })
    .finally(() => { loading = null; });
}

onRouteChange(guard);

const app = createApp(App);
registerUi(app);
app.config.errorHandler = (err) => { console.error(err); toastError(err); };
app.mount('#app');
guard();

// 调试/后续视图可用
window.__admin = { api, store, route, navigate, refreshCounts };
