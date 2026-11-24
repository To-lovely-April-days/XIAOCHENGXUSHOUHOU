// 账号管理（#/accounts）：注册审批（v1.5）+ 员工列表 + 新增/编辑 + 解绑微信 + 设置登录密码 + 停用/启用。
// 注册审批：GET /api/admin/registrations、POST .../{id}/approve、POST .../{id}/reject
// 数据：GET/POST /api/admin/employees、PUT /api/admin/employees/{id}、POST .../{id}/unbind、POST .../{id}/password
import * as api from '../api.js';
import { route, updateQuery } from '../router.js';
import { store, refreshCounts } from '../store.js';
import { toast, toastError, confirm } from '../ui.js';
import { fmtMDHM } from '../format.js';

// v1.7 岗位（显示用的角色）与固定部门；权限角色 role 由后端按岗位推出
const POS = {
  Engineer: '售后工程师',
  Sales: '销售',
  SalesManager: '销售主管',
  Supervisor: '售后主管',
  Admin: '管理员',
};
const ROLE_KEYS = ['Engineer', 'Sales', 'SalesManager', 'Supervisor', 'Admin'];
const ROLE_TABS = [{ key: '', label: '全部' }].concat(ROLE_KEYS.map(k => ({ key: k, label: POS[k] })));
const ROLE = Object.fromEntries(ROLE_KEYS.map(k => [k, { label: POS[k] }]));
const DEPARTMENTS = ['售后部门', '销售部门', '软件部门', '总经办'];
/** 选了岗位、还没选部门时的默认部门 */
const DEFAULT_DEPT = { Engineer: '售后部门', Supervisor: '售后部门', Sales: '销售部门', SalesManager: '销售部门', Admin: '总经办' };
const POS_HINT = {
  SalesManager: '销售主管能看到所有销售建的工单和价格，不能指派和审批。',
  Supervisor: '售后主管在小程序里指派、审批、定价、验收；要登录电脑后台还需勾选管理员权限。',
  Admin: '管理员拥有全部权限：电脑后台、账号管理，以及小程序里售后主管能做的所有操作。',
};
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
        role: e ? (e.position || e.role) : 'Engineer',
        department: e ? (e.department || '') : '售后部门',
        isAdmin: e ? !!e.isAdmin : false,
        enabled: e ? e.enabled !== false : true,
        password: '',
      },
      errors: {},
      serverError: '',
      saving: false,
      roleOptions: ROLE_KEYS.map(k => ({ value: k, label: ROLE[k].label })),
      posHint: POS_HINT,
    };
  },
  computed: {
    /** 部门下拉：固定四个；旧数据里的老写法保留为一个可选项，不强迫改 */
    deptOptions() {
      const cur = this.employee && this.employee.department;
      return cur && !DEPARTMENTS.includes(cur) ? DEPARTMENTS.concat([cur]) : DEPARTMENTS;
    },
    adminLocked() { return this.form.role === 'Admin' || this.isSelf; },
    adminOn() { return this.form.role === 'Admin' || this.form.isAdmin; },
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
      if (!f.department) errors.department = '请选择部门';
      if (!this.isEdit && f.password) { const pe = passwordError(f.password); if (pe) errors.password = pe; }
      this.errors = errors;
      return !Object.keys(errors).length;
    },
    onRoleChange() {
      this.clearError('role');
      if (!this.isEdit) this.form.department = DEFAULT_DEPT[this.form.role] || this.form.department;
    },
    clearError(k) { if (this.errors[k]) this.errors = { ...this.errors, [k]: '' }; this.serverError = ''; },
    async submit() {
      if (this.saving || !this.validate()) return;
      const f = this.form;
      const body = { name: f.name.trim(), phone: f.phone.trim(), position: f.role, department: f.department, isAdmin: this.adminOn };
      this.saving = true;
      this.serverError = '';
      try {
        let saved;
        if (this.isEdit) {
          saved = await api.admin.updateEmployee(this.employee.id, { ...body, enabled: f.enabled });
          toast('已保存', 'success');
        } else {
          saved = await api.admin.createEmployee(f.password ? { ...body, password: f.password } : body);
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
        <span v-else-if="phoneChanged" class="field-hint text-warn">修改手机号后，员工需用新手机号重新登录</span>
      </label>
      <label class="field">
        <span class="field-label">角色<span class="req">*</span></span>
        <select class="select" :class="{ invalid: errors.role }" v-model="form.role" :disabled="isSelf" @change="onRoleChange">
          <option v-for="o in roleOptions" :key="o.value" :value="o.value">{{ o.label }}</option>
        </select>
        <span v-if="errors.role" class="field-error">{{ errors.role }}</span>
        <span v-else-if="isSelf" class="field-hint">不能修改自己的角色</span>
        <span v-else-if="posHint[form.role]" class="field-hint">{{ posHint[form.role] }}</span>
      </label>
      <label class="field">
        <span class="field-label">部门<span class="req">*</span></span>
        <select class="select" :class="{ invalid: errors.department }" v-model="form.department" @change="clearError('department')">
          <option value="" disabled>请选择</option>
          <option v-for="d in deptOptions" :key="d" :value="d">{{ d }}</option>
        </select>
        <span v-if="errors.department" class="field-error">{{ errors.department }}</span>
      </label>
      <div class="field span-2">
        <span class="field-label">管理员权限</span>
        <div class="accounts-enable-row">
          <button type="button" class="btn btn-secondary switch-btn" role="switch" :aria-checked="adminOn ? 'true' : 'false'"
            :disabled="adminLocked" @click="form.isAdmin = !form.isAdmin; serverError = ''">
            <span class="switch" :class="{ on: adminOn }"></span>{{ adminOn ? '已开启' : '未开启' }}
          </button>
          <span class="field-hint">{{ form.role === 'Admin' ? '管理员岗位自带管理员权限' : (isSelf ? '不能取消自己的管理员权限' : '开启后可登录电脑后台、管理账号、审批注册，并拥有售后主管的全部操作权限') }}</span>
        </div>
      </div>
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
      <label v-if="!isEdit" class="field span-2">
        <span class="field-label">初始密码</span>
        <input class="input" :class="{ invalid: errors.password }" type="password" v-model="form.password" maxlength="64"
          autocomplete="new-password" placeholder="至少 8 位，包含字母和数字；可留空，之后再设置" @input="clearError('password')">
        <span v-if="errors.password" class="field-error">{{ errors.password }}</span>
        <span v-else class="field-hint">员工用手机号 + 密码登录小程序。没有密码的账号无法登录。</span>
      </label>
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
  template: `<Modal :title="employee.hasPassword ? '重置登录密码' : '设置登录密码'" :width="420" persistent @close="$emit('close')">
    <form class="form-stack" novalidate @submit.prevent="submit">
      <div class="accounts-pwd-who">
        <Avatar :user="employee" :size="32" />
        <div class="accounts-pwd-who-text">
          <div class="accounts-name">{{ employee.name }}</div>
          <div class="cell-sub num">{{ employee.phone }} · {{ employee.roleName || roleLabel(employee.role) }}</div>
        </div>
      </div>
      <p class="field-hint accounts-pwd-tip">员工用手机号 + 此密码登录小程序；售后主管还可以用它登录电脑后台。{{ employee.hasPassword ? '重置后原密码立即失效。' : '' }}</p>
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

// ---------------------------------------------------------------- 注册审批：通过 / 驳回弹窗

const RegistrationApproveModal = {
  name: 'RegistrationApproveModal',
  props: { item: { type: Object, required: true } },
  emits: ['close', 'done'],
  data() {
    return {
      form: { name: this.item.name || '', role: '', department: '', isAdmin: false },
      departments: DEPARTMENTS,
      posHint: POS_HINT,
      error: '',
      saving: false,
      roleOptions: ROLE_KEYS.map(k => ({ value: k, label: ROLE[k].label })),
    };
  },
  methods: {
    defaultDept(role) { return DEFAULT_DEPT[role] || ''; },
    async submit() {
      if (this.saving) return;
      const name = this.form.name.trim();
      if (!name) { this.error = '请填写姓名'; return; }
      if (!this.form.role) { this.error = '请选择角色'; return; }
      if (!this.form.department) { this.error = '请选择部门'; return; }
      this.saving = true;
      this.error = '';
      try {
        const dto = await api.admin.approveRegistration(this.item.id, { position: this.form.role, name, department: this.form.department, isAdmin: this.form.role === 'Admin' || this.form.isAdmin });
        toast(`已通过，「${name}」现在可以登录了`, 'success');
        this.$emit('done', dto);
      } catch (e) {
        if (e && e.code === 40100) return;
        this.error = (e && e.message) || '操作失败';
      } finally {
        this.saving = false;
      }
    },
  },
  template: `<Modal title="通过注册申请" :width="460" persistent @close="$emit('close')">
    <form class="form-stack" novalidate @submit.prevent="submit">
      <p class="field-hint accounts-pwd-tip">手机号 <span class="num">{{ item.phone }}</span>。通过后对方即可用注册时填的密码登录小程序。请确认是本公司员工。</p>
      <label class="field"><span class="field-label">姓名<span class="req">*</span></span>
        <input class="input" v-model="form.name" maxlength="20" autocomplete="off" @input="error = ''"></label>
      <label class="field"><span class="field-label">角色<span class="req">*</span></span>
        <select class="select" v-model="form.role" @change="error = ''; form.department = form.department || defaultDept(form.role)">
          <option value="" disabled>请选择</option>
          <option v-for="o in roleOptions" :key="o.value" :value="o.value">{{ o.label }}</option>
        </select>
        <span v-if="posHint[form.role]" class="field-hint" :class="{ 'text-warn': form.role === 'Admin' }">{{ posHint[form.role] }}</span>
      </label>
      <label class="field"><span class="field-label">部门<span class="req">*</span></span>
        <select class="select" v-model="form.department" @change="error = ''">
          <option value="" disabled>请选择</option>
          <option v-for="d in departments" :key="d" :value="d">{{ d }}</option>
        </select></label>
      <label v-if="form.role && form.role !== 'Admin'" class="accounts-admin-check">
        <input type="checkbox" v-model="form.isAdmin"> 同时给管理员权限（可登录电脑后台、管理账号）
      </label>
      <div v-if="error" class="field-error" role="alert">{{ error }}</div>
      <button type="submit" hidden></button>
    </form>
    <template #footer>
      <button type="button" class="btn btn-secondary" @click="$emit('close')">取消</button>
      <button type="button" class="btn btn-primary" :disabled="saving" @click="submit">{{ saving ? '处理中…' : '通过' }}</button>
    </template>
  </Modal>`,
};

const RegistrationRejectModal = {
  name: 'RegistrationRejectModal',
  props: { item: { type: Object, required: true } },
  emits: ['close', 'done'],
  data() { return { reason: '', error: '', saving: false }; },
  methods: {
    async submit() {
      if (this.saving) return;
      const reason = this.reason.trim();
      if (!reason) { this.error = '请填写驳回原因'; return; }
      this.saving = true;
      this.error = '';
      try {
        const dto = await api.admin.rejectRegistration(this.item.id, reason);
        toast('已驳回', 'success');
        this.$emit('done', dto);
      } catch (e) {
        if (e && e.code === 40100) return;
        this.error = (e && e.message) || '操作失败';
      } finally {
        this.saving = false;
      }
    },
  },
  template: `<Modal title="驳回注册申请" :width="440" persistent @close="$emit('close')">
    <form class="form-stack" novalidate @submit.prevent="submit">
      <p class="field-hint accounts-pwd-tip">「{{ item.name }}」<span class="num">{{ item.phone }}</span>。对方登录时会看到驳回原因，可以修改后重新提交。</p>
      <label class="field"><span class="field-label">驳回原因<span class="req">*</span></span>
        <input class="input" v-model="reason" maxlength="100" placeholder="如：请填写真实姓名" autocomplete="off" @input="error = ''"></label>
      <div v-if="error" class="field-error" role="alert">{{ error }}</div>
      <button type="submit" hidden></button>
    </form>
    <template #footer>
      <button type="button" class="btn btn-secondary" @click="$emit('close')">取消</button>
      <button type="button" class="btn btn-danger" :disabled="saving" @click="submit">{{ saving ? '处理中…' : '驳回' }}</button>
    </template>
  </Modal>`,
};

// ---------------------------------------------------------------- 页面

export default {
  name: 'AccountsView',
  components: { AccountsEditModal, AccountsPasswordModal, RegistrationApproveModal, RegistrationRejectModal },
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
      regs: [],            // 注册申请（待审批 + 最近处理）
      showHandled: false,
      approving: null,
      rejecting: null,
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
      const c = { '': this.searched.length };
      ROLE_KEYS.forEach(k => { c[k] = 0; });
      this.searched.forEach(e => { const k = e.position || e.role; if (c[k] != null) c[k]++; });
      return c;
    },
    /** 与后端一致：按角色（工程师 → 销售 → 主管）再按 id 排序，新增的行也落在对应位置 */
    rows() {
      const list = this.role ? this.searched.filter(e => (e.position || e.role) === this.role) : this.searched.slice();
      const rank = (r) => { const i = ROLE_KEYS.indexOf(r); return i < 0 ? ROLE_KEYS.length : i; };
      return list.sort((a, b) => rank(a.position || a.role) - rank(b.position || b.role) || a.id - b.id);
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
    pendingRegs() { return this.regs.filter(x => x.status === 'Pending'); },
    handledRegs() { return this.regs.filter(x => x.status !== 'Pending'); },
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
        const [list, regs] = await Promise.all([api.admin.employees(), api.admin.registrations().catch(() => null)]);
        this.items = Array.isArray(list) ? list : [];
        if (regs) {
          this.regs = Array.isArray(regs.items) ? regs.items : [];
          store.counts.registrations = regs.pendingCount ?? this.pendingRegs.length;
        }
        this.loaded = true;
      } catch (err) {
        this.error = (err && err.message) || '加载失败';
        if (err && err.code !== 40100 && this.loaded) toastError(err);
      } finally {
        this.loading = false;
      }
    },
    regTime(s) { return fmtMDHM(s); },
    /** 通过 / 驳回之后：关弹窗，重新拉两个列表（通过会多出一个员工） */
    onRegDone() {
      this.approving = null;
      this.rejecting = null;
      this.load();
      refreshCounts();
    },
    setRole(key) { updateQuery({ role: key }); },
    onSearch(v) { updateQuery({ keyword: (v || '').trim() }); },
    roleLabel(e) { return e.roleName || POS[e.position] || POS[e.role] || e.role; },
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
      if (wasCreate && dto && this.role && (dto.position || dto.role) !== this.role) updateQuery({ role: '' });
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
      const ok = await confirm('解绑微信', `确定解绑「${e.name}」的微信？解绑后该员工需重新登录，重新登录前收不到工单提醒。`, { okText: '解绑', danger: true });
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
          name: e.name, phone: e.phone, position: e.position || e.role, isAdmin: !!e.isAdmin, department: e.department || '', enabled: enable,
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

    <section v-if="loaded && regs.length" class="card accounts-regs">
      <div class="accounts-regs-head">
        <div class="accounts-regs-title">注册申请<span v-if="pendingRegs.length" class="accounts-regs-count num">{{ pendingRegs.length }}</span></div>
        <div class="page-sub">员工在小程序里提交的注册，通过并分配角色后才能登录</div>
      </div>
      <table v-if="pendingRegs.length" class="data-table accounts-table">
        <colgroup><col style="width: 200px"><col style="width: 150px"><col><col style="width: 180px"></colgroup>
        <thead><tr><th>姓名</th><th>手机号</th><th>提交时间</th><th class="text-right">操作</th></tr></thead>
        <tbody>
          <tr v-for="x in pendingRegs" :key="x.id">
            <td><span class="accounts-name ellipsis">{{ x.name }}</span></td>
            <td class="accounts-phone">{{ x.phone }}</td>
            <td class="accounts-phone">{{ regTime(x.submittedAt) }}</td>
            <td>
              <div class="accounts-actions">
                <button type="button" class="btn btn-primary btn-sm" @click="approving = x">通过</button>
                <button type="button" class="btn btn-ghost btn-sm accounts-danger" @click="rejecting = x">驳回</button>
              </div>
            </td>
          </tr>
        </tbody>
      </table>
      <div v-else class="accounts-regs-empty">没有待审批的注册申请</div>
      <template v-if="handledRegs.length">
        <button type="button" class="btn btn-ghost btn-sm accounts-regs-toggle" @click="showHandled = !showHandled">
          {{ showHandled ? '收起已处理' : '查看已处理（' + handledRegs.length + '）' }}
        </button>
        <table v-if="showHandled" class="data-table accounts-table">
          <colgroup><col style="width: 200px"><col style="width: 150px"><col style="width: 100px"><col><col style="width: 200px"></colgroup>
          <thead><tr><th>姓名</th><th>手机号</th><th>结果</th><th>驳回原因</th><th>处理人 / 时间</th></tr></thead>
          <tbody>
            <tr v-for="x in handledRegs" :key="x.id">
              <td><span class="ellipsis">{{ x.name }}</span></td>
              <td class="accounts-phone">{{ x.phone }}</td>
              <td><Tag :tone="x.status === 'Approved' ? 'success' : 'neutral'">{{ x.statusText }}</Tag></td>
              <td><span class="ellipsis accounts-dept" :class="{ 'text-faint': !x.rejectReason }">{{ x.rejectReason || '—' }}</span></td>
              <td class="accounts-phone">{{ x.reviewedByName || '—' }} · {{ regTime(x.reviewedAt) }}</td>
            </tr>
          </tbody>
        </table>
      </template>
    </section>

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
            <col style="width: 160px"><col style="width: 120px"><col style="width: 150px"><col style="min-width: 90px">
            <col style="width: 90px"><col style="width: 100px"><col style="width: 80px"><col style="width: 260px">
          </colgroup>
          <thead>
            <tr>
              <th>姓名</th><th>手机号</th><th>角色</th><th>部门</th>
              <th>微信绑定</th><th>登录密码</th><th>状态</th><th class="text-right">操作</th>
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
              <td>{{ roleLabel(e) }}<Tag v-if="e.isAdmin && e.position !== 'Admin'" tone="neutral" class="accounts-admin-tag">管理员</Tag></td>
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

    <p class="accounts-note"><Icon name="info" :size="14" />员工用手机号 + 密码登录小程序，登录后自动绑定微信用于接收工单提醒。忘记密码时在这里「重置密码」。</p>
  </div>

  <AccountsEditModal v-if="editing" :employee="editing.employee" @close="editing = null" @saved="onSaved" />
  <RegistrationApproveModal v-if="approving" :item="approving" @close="approving = null" @done="onRegDone" />
  <RegistrationRejectModal v-if="rejecting" :item="rejecting" @close="rejecting = null" @done="onRegDone" />
  <AccountsPasswordModal v-if="pwdTarget" :employee="pwdTarget" @close="pwdTarget = null" @saved="onPasswordSaved" />`,
};
