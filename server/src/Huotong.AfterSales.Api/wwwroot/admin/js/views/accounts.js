// 账号管理（#/accounts）：员工列表 + 新增/编辑 + 解绑微信 + 设置电脑登录密码 + 停用/启用。
// 数据：GET/POST /api/admin/employees、PUT /api/admin/employees/{id}、POST .../{id}/unbind、POST .../{id}/password
import * as api from '../api.js';
import { route, updateQuery } from '../router.js';
import { store } from '../store.js';
import { toast, toastError, confirm } from '../ui.js';
import { ROLE } from '../enums.js';

const ROLE_TABS = [
  { key: '', label: '全部' },
  { key: 'Engineer', label: ROLE.Engineer.label },
  { key: 'Sales', label: ROLE.Sales.label },
  { key: 'Supervisor', label: ROLE.Supervisor.label },
];
const ROLE_KEYS = ['Engineer', 'Sales', 'Supervisor'];
const MOBILE_RE = /^1\d{10}$/;

/** 密码强度：≥ 8 位且同时含字母和数字（与后端规则一致） */
function passwordError(p) {
  if (!p) return '请输入密码';
  if (p.length < 8 || !/[A-Za-z]/.test(p) || !/\d/.test(p)) return '密码至少 8 位，且需同时包含字母和数字';
  return '';
}

// ---------------------------------------------------------------- 新增 / 编辑弹窗

const AccountsEditModal = {
  name: 'AccountsEditModal',
  props: { employee: { type: Object, default: null } },
  emits: ['close', 'saved'],
  data() {
    const e = this.employee;
    return {
      form: {
        name: e ? e.name : '',
        phone: e ? e.phone : '',
        role: e ? e.role : 'Engineer',
        department: e ? (e.department || '') : '',
        enabled: e ? e.enabled !== false : true,
      },
      errors: {},
      serverError: '',
      saving: false,
      roleOptions: ROLE_KEYS.map(k => ({ value: k, label: ROLE[k].label })),
    };
  },
  computed: {
    isEdit() { return !!this.employee; },
    isSelf() { return this.isEdit && store.user && store.user.id === this.employee.id; },
    phoneChanged() { return this.isEdit && this.employee.bound && this.form.phone.trim() !== this.employee.phone; },
  },
  methods: {
    validate() {
      const f = this.form;
      const errors = {};
      const name = f.name.trim();
      if (!name) errors.name = '请填写姓名';
      else if (name.length > 20) errors.name = '姓名不能超过 20 个字';
      const phone = f.phone.trim();
      if (!phone) errors.phone = '请填写手机号';
      else if (!MOBILE_RE.test(phone)) errors.phone = '请填写 11 位手机号';
      if (!f.role) errors.role = '请选择角色';
      if (f.department.trim().length > 50) errors.department = '部门不能超过 50 个字';
      this.errors = errors;
      return !Object.keys(errors).length;
    },
    clearError(k) { if (this.errors[k]) this.errors = { ...this.errors, [k]: '' }; this.serverError = ''; },
    async submit() {
      if (this.saving || !this.validate()) return;
      const f = this.form;
      const body = { name: f.name.trim(), phone: f.phone.trim(), role: f.role, department: f.department.trim() };
      this.saving = true;
      this.serverError = '';
      try {
        let saved;
        if (this.isEdit) {
          saved = await api.admin.updateEmployee(this.employee.id, { ...body, enabled: f.enabled });
          toast('已保存', 'success');
        } else {
          saved = await api.admin.createEmployee(body);
          toast('已新增账号', 'success');
        }
        this.$emit('saved', saved);
      } catch (err) {
        if (err && err.code === 40100) return;
        this.serverError = (err && err.message) || '保存失败';
      } finally {
        this.saving = false;
      }
    },
  },
  template: `<Modal :title="isEdit ? '编辑账号' : '新增账号'" :width="520" persistent @close="$emit('close')">
    <form class="form-grid" novalidate @submit.prevent="submit">
      <label class="field">
        <span class="field-label">姓名<span class="req">*</span></span>
        <input class="input" :class="{ invalid: errors.name }" v-model="form.name" maxlength="20" placeholder="员工真实姓名"
          autocomplete="off" @input="clearError('name')">
        <span v-if="errors.name" class="field-error">{{ errors.name }}</span>
      </label>
      <label class="field">
        <span class="field-label">手机号<span class="req">*</span></span>
        <input class="input num" :class="{ invalid: errors.phone }" v-model="form.phone" maxlength="11" inputmode="numeric"
          placeholder="11 位手机号" autocomplete="off" @input="clearError('phone')">
        <span v-if="errors.phone" class="field-error">{{ errors.phone }}</span>
        <span v-else-if="phoneChanged" class="field-hint text-warn">修改手机号后将解除微信绑定，需用新手机号重新验证</span>
      </label>
      <label class="field">
        <span class="field-label">角色<span class="req">*</span></span>
        <select class="select" :class="{ invalid: errors.role }" v-model="form.role" :disabled="isSelf" @change="clearError('role')">
          <option v-for="o in roleOptions" :key="o.value" :value="o.value">{{ o.label }}</option>
        </select>
        <span v-if="errors.role" class="field-error">{{ errors.role }}</span>
        <span v-else-if="isSelf" class="field-hint">不能修改自己的角色</span>
      </label>
      <label class="field">
        <span class="field-label">部门</span>
        <input class="input" :class="{ invalid: errors.department }" v-model="form.department" maxlength="50"
          placeholder="如：售后服务部" autocomplete="off" @input="clearError('department')">
        <span v-if="errors.department" class="field-error">{{ errors.department }}</span>
      </label>
      <div v-if="isEdit" class="field span-2">
        <span class="field-label">账号状态</span>
        <div class="accounts-enable-row">
          <button type="button" class="btn btn-secondary switch-btn" role="switch" :aria-checked="form.enabled ? 'true' : 'false'"
            :disabled="isSelf" @click="form.enabled = !form.enabled; serverError = ''">
            <span class="switch" :class="{ on: form.enabled }"></span>{{ form.enabled ? '启用' : '停用' }}
          </button>
          <span class="field-hint">{{ isSelf ? '不能停用自己的账号' : '停用后该员工无法登录小程序和电脑后台' }}</span>
        </div>
      </div>
      <div v-if="!isEdit" class="span-2 field-hint">新账号默认启用。员工首次使用小程序时，用登记的手机号快速验证即可绑定微信。</div>
      <div v-if="serverError" class="span-2 field-error" role="alert">{{ serverError }}</div>
      <button type="submit" hidden></button>
    </form>
    <template #footer>
      <button type="button" class="btn btn-secondary" @click="$emit('close')">取消</button>
      <button type="button" class="btn btn-primary" :disabled="saving" @click="submit">{{ saving ? '保存中…' : (isEdit ? '保存' : '新增') }}</button>
    </template>
  </Modal>`,
};

// ---------------------------------------------------------------- 设置密码弹窗

const AccountsPasswordModal = {
  name: 'AccountsPasswordModal',
  props: { employee: { type: Object, required: true } },
  emits: ['close', 'saved'],
  data() { return { password: '', confirmPassword: '', error: '', saving: false }; },
  methods: {
    roleLabel(r) { return ROLE[r] ? ROLE[r].label : (r || ''); },
    async submit() {
      if (this.saving) return;
      const err = passwordError(this.password);
      if (err) { this.error = err; return; }
      if (this.password !== this.confirmPassword) { this.error = '两次输入的密码不一致'; return; }
      this.saving = true;
      this.error = '';
      try {
        const saved = await api.admin.setPassword(this.employee.id, this.password);
        toast(this.employee.hasPassword ? '密码已重置' : '密码已设置', 'success');
        this.$emit('saved', saved);
      } catch (e) {
        if (e && e.code === 40100) return;
        this.error = (e && e.message) || '设置失败';
      } finally {
        this.saving = false;
      }
    },
  },
  template: `<Modal :title="employee.hasPassword ? '重置电脑登录密码' : '设置电脑登录密码'" :width="420" persistent @close="$emit('close')">
    <form class="form-stack" novalidate @submit.prevent="submit">
      <div class="accounts-pwd-who">
        <Avatar :user="employee" :size="32" />
        <div class="accounts-pwd-who-text">
          <div class="accounts-name">{{ employee.name }}</div>
          <div class="cell-sub num">{{ employee.phone }} · {{ employee.roleName || roleLabel(employee.role) }}</div>
        </div>
      </div>
      <p class="field-hint accounts-pwd-tip">员工用手机号 + 此密码登录电脑后台（目前仅售后主管可登录）。{{ employee.hasPassword ? '重置后原密码立即失效。' : '' }}</p>
      <input type="text" :value="employee.phone" autocomplete="username" hidden>
      <label class="field"><span class="field-label">新密码<span class="req">*</span></span>
        <input class="input" type="password" v-model="password" autocomplete="new-password" placeholder="至少 8 位，包含字母和数字"
          @input="error = ''"></label>
      <label class="field"><span class="field-label">确认密码<span class="req">*</span></span>
        <input class="input" type="password" v-model="confirmPassword" autocomplete="new-password" placeholder="再次输入密码"
          @input="error = ''"></label>
      <div v-if="error" class="field-error" role="alert">{{ error }}</div>
      <button type="submit" hidden></button>
    </form>
    <template #footer>
      <button type="button" class="btn btn-secondary" @click="$emit('close')">取消</button>
      <button type="button" class="btn btn-primary" :disabled="saving" @click="submit">{{ saving ? '保存中…' : '确定' }}</button>
    </template>
  </Modal>`,
};

// ---------------------------------------------------------------- 页面

export default {
  name: 'AccountsView',
  components: { AccountsEditModal, AccountsPasswordModal },
  data() {
    return {
      route,
      store,
      items: [],
      loading: false,
      loaded: false,
      error: '',
      keyword: '',
      role: '',
      editing: null,      // null = 关闭；{ employee: null } = 新增；{ employee } = 编辑
      pwdTarget: null,
      busyId: null,
      roleTabs: ROLE_TABS,
    };
  },
  computed: {
    kw() { return this.keyword.trim().toLowerCase(); },
    /** 按关键字过滤后的列表（tab 数量也基于此） */
    searched() {
      const kw = this.kw;
      if (!kw) return this.items;
      const digits = kw.replace(/\s+/g, '');
      return this.items.filter(e => (e.name || '').toLowerCase().includes(kw) || (e.phone || '').includes(digits));
    },
    counts() {
      const c = { '': this.searched.length, Engineer: 0, Sales: 0, Supervisor: 0 };
      this.searched.forEach(e => { if (c[e.role] != null) c[e.role]++; });
      return c;
    },
    /** 与后端一致：按角色（工程师 → 销售 → 主管）再按 id 排序，新增的行也落在对应位置 */
    rows() {
      const list = this.role ? this.searched.filter(e => e.role === this.role) : this.searched.slice();
      const rank = (r) => { const i = ROLE_KEYS.indexOf(r); return i < 0 ? ROLE_KEYS.length : i; };
      return list.sort((a, b) => rank(a.role) - rank(b.role) || a.id - b.id);
    },
    summary() {
      const all = this.items;
      const bound = all.filter(e => e.bound).length;
      const disabled = all.filter(e => e.enabled === false).length;
      let s = `共 ${all.length} 人 · 已绑定微信 ${bound} 人`;
      if (disabled) s += ` · 停用 ${disabled} 人`;
      return s;
    },
    meId() { return store.user ? store.user.id : null; },
  },
  watch: {
    'route.query': {
      handler(q) {
        const r = q && q.role;
        this.role = ROLE_KEYS.includes(r) ? r : '';
        const k = (q && q.keyword) || '';
        if (k !== this.keyword.trim()) this.keyword = k;
      },
      deep: true,
      immediate: true,
    },
  },
  created() { this.load(); },
  methods: {
    async load() {
      this.loading = true;
      this.error = '';
      try {
        const list = await api.admin.employees();
        this.items = Array.isArray(list) ? list : [];
        this.loaded = true;
      } catch (err) {
        this.error = (err && err.message) || '加载失败';
        if (err && err.code !== 40100 && this.loaded) toastError(err);
      } finally {
        this.loading = false;
      }
    },
    setRole(key) { updateQuery({ role: key }); },
    onSearch(v) { updateQuery({ keyword: (v || '').trim() }); },
    roleLabel(e) { return e.roleName || (ROLE[e.role] ? ROLE[e.role].label : e.role); },
    isSelf(e) { return this.meId != null && e.id === this.meId; },
    /** 用接口返回的 EmployeeDto 替换/插入本地行 */
    upsert(dto) {
      if (!dto || dto.id == null) { this.load(); return; }
      const i = this.items.findIndex(x => x.id === dto.id);
      if (i >= 0) this.items.splice(i, 1, { ...this.items[i], ...dto });
      else this.items.push(dto);
    },
    openCreate() { this.editing = { employee: null }; },
    openEdit(e) { this.editing = { employee: { ...e } }; },
    onSaved(dto) {
      const wasCreate = !this.editing || !this.editing.employee;
      this.editing = null;
      this.upsert(dto);
      if (wasCreate && dto && this.role && dto.role !== this.role) updateQuery({ role: '' });
      if (dto && this.isSelf(dto)) store.user = { ...store.user, name: dto.name, phone: dto.phone, department: dto.department };
    },
    openPassword(e) { this.pwdTarget = { ...e }; },
    onPasswordSaved(dto) {
      const id = this.pwdTarget && this.pwdTarget.id;
      this.pwdTarget = null;
      if (dto && dto.id != null) this.upsert(dto);
      else { const row = this.items.find(x => x.id === id); if (row) row.hasPassword = true; }
    },
    async unbind(e) {
      const ok = await confirm('解绑微信', `确定解绑「${e.name}」的微信？解绑后该员工需重新验证手机号。`, { okText: '解绑', danger: true });
      if (!ok) return;
      this.busyId = e.id;
      try {
        const dto = await api.admin.unbind(e.id);
        if (dto && dto.id != null) this.upsert(dto);
        else e.bound = false;
        toast('已解绑微信', 'success');
      } catch (err) {
        toastError(err);
      } finally {
        this.busyId = null;
      }
    },
    async toggleEnabled(e) {
      const enable = e.enabled === false;
      if (!enable) {
        const ok = await confirm('停用账号', `确定停用「${e.name}」？停用后该员工将立即无法登录小程序和电脑后台，历史工单和打卡记录保留。`,
          { okText: '停用', danger: true });
        if (!ok) return;
      }
      this.busyId = e.id;
      try {
        const dto = await api.admin.updateEmployee(e.id, {
          name: e.name, phone: e.phone, role: e.role, department: e.department || '', enabled: enable,
        });
        if (dto && dto.id != null) this.upsert(dto);
        else e.enabled = enable;
        toast(enable ? '已启用' : '已停用', 'success');
      } catch (err) {
        toastError(err);
      } finally {
        this.busyId = null;
      }
    },
  },
  template: `
  <PageHeader :crumbs="['账号管理', '员工账号']">
    <SearchBox v-model="keyword" placeholder="搜索姓名、手机号" @search="onSearch" />
    <button type="button" class="btn btn-primary" @click="openCreate"><Icon name="plus" :size="15" :stroke="2.2" />新增账号</button>
  </PageHeader>
  <div class="page">
    <div class="page-head">
      <div>
        <h1>账号管理</h1>
        <div class="page-sub">{{ loaded ? summary : '售后工程师、销售与售后主管的登录账号' }}</div>
      </div>
      <div class="page-actions">
        <button type="button" class="btn btn-ghost" :disabled="loading" @click="load"><Icon name="refresh" :size="15" />刷新</button>
      </div>
    </div>

    <section class="card">
      <div class="toolbar">
        <div class="tabs" role="tablist" aria-label="按角色筛选">
          <button v-for="t in roleTabs" :key="t.key" type="button" class="tab" role="tab"
            :aria-selected="role === t.key ? 'true' : 'false'" @click="setRole(t.key)">
            {{ t.label }}<span class="tab-count">{{ loaded ? counts[t.key] : '' }}</span>
          </button>
        </div>
      </div>

      <Loading v-if="loading && !loaded" />
      <Empty v-else-if="error && !loaded" :text="error">
        <button type="button" class="btn btn-secondary btn-sm accounts-retry" @click="load">重试</button>
      </Empty>
      <template v-else>
        <table class="data-table accounts-table" :class="{ 'accounts-reloading': loading }">
          <colgroup>
            <col style="width: 200px"><col style="width: 130px"><col style="width: 110px"><col style="min-width: 120px">
            <col style="width: 100px"><col style="width: 120px"><col style="width: 80px"><col style="width: 260px">
          </colgroup>
          <thead>
            <tr>
              <th>姓名</th><th>手机号</th><th>角色</th><th>部门</th>
              <th>微信绑定</th><th>电脑登录密码</th><th>状态</th><th class="text-right">操作</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="e in rows" :key="e.id" :class="{ 'accounts-off': e.enabled === false }">
              <td>
                <div class="cell-user">
                  <Avatar :user="e" :size="28" />
                  <span class="accounts-name ellipsis">{{ e.name }}</span>
                  <Tag v-if="isSelf(e)" tone="neutral">我</Tag>
                </div>
              </td>
              <td class="accounts-phone">{{ e.phone }}</td>
              <td>{{ roleLabel(e) }}</td>
              <td><span class="ellipsis accounts-dept" :class="{ 'text-faint': !e.department }">{{ e.department || '—' }}</span></td>
              <td>
                <Tag v-if="e.bound" tone="success">已绑定</Tag>
                <span v-else class="text-faint">未绑定</span>
              </td>
              <td>
                <span v-if="e.hasPassword" class="accounts-pwd-set"><Icon name="key" :size="13" />已设置</span>
                <span v-else class="text-faint">未设置</span>
              </td>
              <td>
                <span class="status"><span class="dot" :style="{ background: e.enabled === false ? '#D4D4D8' : '#2F7A4B' }"></span>{{ e.enabled === false ? '停用' : '启用' }}</span>
              </td>
              <td>
                <div class="accounts-actions">
                  <button type="button" class="btn btn-ghost btn-sm" :disabled="busyId === e.id" @click="openEdit(e)">编辑</button>
                  <button v-if="e.bound" type="button" class="btn btn-ghost btn-sm" :disabled="busyId === e.id" @click="unbind(e)">解绑微信</button>
                  <button type="button" class="btn btn-ghost btn-sm" :disabled="busyId === e.id" @click="openPassword(e)">{{ e.hasPassword ? '重置密码' : '设置密码' }}</button>
                  <button v-if="e.enabled === false" type="button" class="btn btn-ghost btn-sm" :disabled="busyId === e.id" @click="toggleEnabled(e)">启用</button>
                  <button v-else type="button" class="btn btn-ghost btn-sm accounts-danger" :disabled="busyId === e.id || isSelf(e)"
                    :title="isSelf(e) ? '不能停用自己的账号' : ''" @click="toggleEnabled(e)">停用</button>
                </div>
              </td>
            </tr>
          </tbody>
        </table>
        <Empty v-if="!rows.length" :text="kw ? '没有找到匹配的员工' : (role ? '该角色下还没有账号' : '还没有员工账号')">
          <button v-if="!kw" type="button" class="btn btn-secondary btn-sm accounts-retry" @click="openCreate"><Icon name="plus" :size="14" />新增账号</button>
        </Empty>
        <div v-if="rows.length" class="table-foot"><span>共 {{ rows.length }} 人</span></div>
      </template>
    </section>

    <p class="accounts-note"><Icon name="info" :size="14" />员工首次使用小程序时，用登记的手机号快速验证即可绑定微信。</p>
  </div>

  <AccountsEditModal v-if="editing" :employee="editing.employee" @close="editing = null" @saved="onSaved" />
  <AccountsPasswordModal v-if="pwdTarget" :employee="pwdTarget" @close="pwdTarget = null" @saved="onPasswordSaved" />`,
};
