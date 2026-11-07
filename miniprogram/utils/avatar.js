/**
 * 头像：上传并保存。
 * - fromChooseAvatar(e)：<button open-type="chooseAvatar"> 的回调
 * - pickFromAlbumOrCamera()：wx.chooseMedia 从相册选择或拍照（chooseAvatar 不可用时的兜底）
 * 都返回 Promise<User|null>（用户取消返回 null）。
 */
const api = require('../api/index.js');
const auth = require('./auth.js');

function save(path) {
  return api.files.upload(path, 'Avatar', { loading: '上传中' })
    .then((file) => api.me.setAvatar(file.id))
    .then((user) => {
      auth.setUser(user);
      wx.showToast({ title: '头像已更新', icon: 'none' });
      return user;
    });
}

function fromChooseAvatar(e) {
  const d = (e && e.detail) || {};
  if (!d.avatarUrl) {
    console.warn('[avatar] chooseAvatar 未返回头像', d);
    if (d.errMsg && !/cancel/.test(d.errMsg)) {
      wx.showToast({ title: '获取微信头像失败，可改用「从相册选择或拍照」', icon: 'none', duration: 3000 });
    }
    return Promise.resolve(null);
  }
  return save(d.avatarUrl);
}

function pickFromAlbumOrCamera() {
  return new Promise((resolve, reject) => {
    wx.chooseMedia({
      count: 1,
      mediaType: ['image'],
      sourceType: ['album', 'camera'],
      sizeType: ['compressed'],
      success: (res) => {
        const f = res.tempFiles && res.tempFiles[0];
        if (!f) return resolve(null);
        save(f.tempFilePath).then(resolve, reject);
      },
      fail: (err) => {
        const msg = (err && err.errMsg) || '';
        console.warn('[avatar] chooseMedia fail', msg);
        if (/cancel/.test(msg)) return resolve(null);
        wx.showToast({ title: /auth|privacy|scope/i.test(msg) ? '没有相册/相机权限，请在设置中开启' : '选择图片失败', icon: 'none' });
        resolve(null);
      }
    });
  });
}

module.exports = { fromChooseAvatar, pickFromAlbumOrCamera };
