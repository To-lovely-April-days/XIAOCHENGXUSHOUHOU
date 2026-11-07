// 登录页：手机号 + 密码（仅售后主管）。
import * as api from '../api.js';
import { route, navigate } from '../router.js';
import { store, loadMe } from '../store.js';

export default {
  name: 'LoginView',
  data() { return { phone: '', password: '', error: '', submitting: false }; },
  mounted() {
    try { this.phone = localStorage.getItem('admin_last_phone') || ''; } catch (e) { /* ignore */ }
    this.$nextTick(() => { (this.phone ? this.$refs.pwd : this.$refs.phone).focus(); });
  },
  methods: {
    async submit() {
      if (this.submitting) return;
      this.error = '';
      const phone = this.phone.replace(/\s+/g, '');
      if (!phone) { this.error = '请输入手机号'; this.$refs.phone.focus(); return; }
      if (!/^1\d{10}$/.test(phone)) { this.error = '请输入 11 位手机号'; this.$refs.phone.focus(); return; }
      if (!this.password) { this.error = '请输入密码'; this.$refs.pwd.focus(); return; }
      this.submitting = true;
      try {
        const r = await api.auth.passwordLogin(phone, this.password);
        api.setToken(r.token);
        try { localStorage.setItem('admin_last_phone', phone); } catch (e) { /* ignore */ }
        store.user = null;
        await loadMe(); // 校验角色
        const redirect = route.query.redirect;
        if (redirect && redirect.startsWith('/') && !redirect.startsWith('/login')) {
          location.hash = '#' + redirect;
        } else {
          navigate('/orders');
        }
      } catch (e) {
        this.error = e.message || '登录失败';
        this.password = '';
        this.$nextTick(() => this.$refs.pwd && this.$refs.pwd.focus());
      } finally {
        this.submitting = false;
      }
    },
  },
  template: `<div class="login-page">
    <form class="login-card" @submit.prevent="submit" novalidate>
      <div class="login-brand">
        <img class="brand-logo-lg" src="img/logo.png" alt="霍桐仪器 HTLAB">
        <div><div class="login-title">霍桐售后</div><div class="login-sub">主管后台</div></div>
      </div>
      <label class="field"><span class="field-label">手机号</span>
        <input ref="phone" class="input input-lg" type="tel" inputmode="numeric" maxlength="13" autocomplete="username"
               v-model="phone" placeholder="请输入手机号" @input="error = ''"></label>
      <label class="field"><span class="field-label">密码</span>
        <input ref="pwd" class="input input-lg" type="password" autocomplete="current-password"
               v-model="password" placeholder="请输入密码" @input="error = ''"></label>
      <div class="login-error" role="alert">{{ error }}</div>
      <button type="submit" class="btn btn-primary btn-block btn-lg" :disabled="submitting">{{ submitting ? '登录中…' : '登录' }}</button>
      <div class="login-tip">仅售后主管可登录。忘记密码请联系其他主管在「账号管理」中重置。</div>
    </form>
  </div>`,
};
