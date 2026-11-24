/** 修改密码（v1.5）。PUT /api/me/password；同一个密码用于小程序和电脑后台。 */
const api = require('../../api/index.js');
const auth = require('../../utils/auth.js');

/** 与后端一致：至少 8 位，同时含字母和数字 */
function passwordError(p) {
  if (!p) return '请输入新密码';
  if (p.length < 8 || !/[A-Za-z]/.test(p) || !/\d/.test(p)) return '新密码至少 8 位，且需同时包含字母和数字';
  if (p.length > 64) return '新密码不能超过 64 位';
  return '';
}

Page({
  data: { oldPassword: '', newPassword: '', confirm: '', show: false, busy: false, error: '' },

  onShow() {
    auth.ensureLogin().catch(() => {});
  },

  onInput(e) {
    const patch = { error: '' };
    patch[e.currentTarget.dataset.k] = e.detail.value;
    this.setData(patch);
  },

  toggleShow() {
    this.setData({ show: !this.data.show });
  },

  onSubmit() {
    const d = this.data;
    if (d.busy) return;
    if (!d.oldPassword) return this.setData({ error: '请输入原密码' });
    const pe = passwordError(d.newPassword);
    if (pe) return this.setData({ error: pe });
    if (d.newPassword !== d.confirm) return this.setData({ error: '两次输入的新密码不一致' });
    if (d.newPassword === d.oldPassword) return this.setData({ error: '新密码不能和原密码相同' });

    this.setData({ busy: true, error: '' });
    api.me.changePassword(d.oldPassword, d.newPassword, { silent: true })
      .then(() => {
        wx.showToast({ title: '密码已修改', icon: 'success' });
        setTimeout(() => wx.navigateBack(), 800);
      })
      .catch((err) => this.setData({ error: (err && err.message) || '修改失败，请重试' }))
      .then(() => this.setData({ busy: false }));
  }
});
