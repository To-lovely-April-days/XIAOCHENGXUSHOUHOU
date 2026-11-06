/**
 * 头像：有 avatarUrl 显示图片，否则显示姓氏单字（浅灰圆底）。
 * 属性：
 *   user  {UserBrief} { name, surname, avatarUrl }
 *   size  {number}    直径，设计稿 px（内部 ×2 转 rpx），默认 40
 *   src   {string}    直接指定图片（优先于 user.avatarUrl）
 *   text  {string}    直接指定文字（优先于 user.surname）
 *   bg / color {string} 文字头像底色/字色，默认 #ECECEA / #3A3A3C
 */
Component({
  options: { addGlobalClass: true },
  properties: {
    user: { type: Object, value: null },
    size: { type: Number, value: 40 },
    src: { type: String, value: '' },
    text: { type: String, value: '' },
    bg: { type: String, value: '#ECECEA' },
    color: { type: String, value: '#3A3A3C' }
  },
  data: { url: '', letter: '', failed: false },
  observers: {
    'user, src, text': function (user, src, text) {
      const u = user || {};
      const letter = text || u.surname || (u.name ? String(u.name).charAt(0) : '');
      this.setData({ url: src || u.avatarUrl || '', letter: letter, failed: false });
    }
  },
  methods: {
    onError() {
      this.setData({ failed: true });
    }
  }
});
