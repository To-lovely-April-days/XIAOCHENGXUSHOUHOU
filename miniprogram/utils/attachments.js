/**
 * 工单附件（v1.2，docs/CHANGES-v1.2.md §6.3；v1.3 增加附件类别 type）：建单页与工单详情页共用。
 * - 选择：pickMedia（相册 / 相机，原图不压缩）、pickChatFiles（从微信聊天选择文档）
 * - 预检：类型 / 大小（与服务端同一张表，服务端校验是最终依据）
 * - 上传：逐个排队（同一时间只传 1 个），带进度；失败可重试
 * - 展示：fmtSize、badge（类型角标）
 * - 打开：图片 / 视频原文件预览；文档先经鉴权接口下载，再 wx.openDocument（txt / csv 改为转发到聊天）
 *
 * 统一条目：
 * { key, path, name, size, kind:'Image'|'Video'|'Document', ext, thumb, duration, status:'uploading'|'done'|'fail',
 *   fatal, error, percent, fileId, restricted, type:'Sat'|'RepairForm'|'Other', badgeText, badgeColor, sizeText }
 * type = 附件类别（v1.3，默认 'Other'）：只是记在条目上，挂载到工单时才提交，不影响上传。
 */
const api = require('../api/index.js');
const http = require('./request.js');

const MB = 1024 * 1024;
const MAX_COUNT = 50;
const MAX_IMAGE = 20 * MB;
const MAX_FILE = 100 * MB;
const DOC_EXTS = ['doc', 'docx', 'xls', 'xlsx', 'ppt', 'pptx', 'pdf', 'txt', 'csv'];
const OPEN_DOC_EXTS = ['doc', 'docx', 'xls', 'xlsx', 'ppt', 'pptx', 'pdf'];
const PICK_ONCE = 9;
const TRANSFER_TIMEOUT = 600000; // 上传 / 下载 10 分钟

const HINT = '文件需从微信聊天中选择：先把文件发到「文件传输助手」，再点「+」选「从微信聊天选择文件」。支持 Word / Excel / PPT / PDF / TXT / CSV，单个不超过 100MB；照片按原图上传。';
const HINT_RESTRICT = '长按附件可设为「仅销售和主管可见」（工程师看不到）。';
const PICK_ACTIONS = ['拍照或从相册选择', '从微信聊天选择文件'];
const RESTRICTED_LABEL = '仅销售和主管可见';

const GRAY = '#636366';

let seq = 0;

/* ---------------- 展示 ---------------- */

/** 字节数 → '512 B' / '36 KB' / '2.4 MB' / '1.20 GB' */
function fmtSize(bytes) {
  const n = Number(bytes);
  if (!isFinite(n) || n < 0) return '';
  if (n < 1024) return Math.round(n) + ' B';
  if (n < MB) return Math.round(n / 1024) + ' KB';
  if (n < 1024 * MB) return (n / MB).toFixed(1) + ' MB';
  return (n / (1024 * MB)).toFixed(2) + ' GB';
}

/** 取最后一个扩展名（小写、不带点），没有则 '' */
function extOf(name) {
  const m = String(name || '').match(/\.([A-Za-z0-9]{1,8})$/);
  return m ? m[1].toLowerCase() : '';
}

/**
 * 类型角标的文字与颜色。
 * @param {{kind?:string,ext?:string}} att AttachmentDto 或统一条目
 * @returns {{text:string,color:string}}
 */
function badge(att) {
  const ext = String((att && att.ext) || '').toLowerCase();
  const kind = att && att.kind;
  if (kind === 'Image') return { text: '图', color: GRAY };
  if (kind === 'Video') return { text: '视频', color: GRAY };
  if (ext === 'doc' || ext === 'docx') return { text: 'W', color: '#2B579A' };
  if (ext === 'xls' || ext === 'xlsx' || ext === 'csv') return { text: 'X', color: '#217346' };
  if (ext === 'ppt' || ext === 'pptx') return { text: 'P', color: '#D24726' };
  if (ext === 'pdf') return { text: 'PDF', color: '#C81E2B' };
  if (ext === 'txt') return { text: 'TXT', color: GRAY };
  return { text: '文件', color: GRAY };
}

/* ---------------- 选择 ---------------- */

/** 预检：不通过的条目直接 fail + fatal，不上传 */
function precheck(item) {
  let error = '';
  if (item.kind === 'Document' && DOC_EXTS.indexOf(item.ext) < 0) error = '不支持的文件类型';
  else if (item.kind === 'Image' && item.size > MAX_IMAGE) error = '图片不能超过 20MB';
  else if (item.kind !== 'Image' && item.size > MAX_FILE) error = '文件不能超过 100MB';
  if (error) {
    item.status = 'fail';
    item.fatal = true;
    item.error = error;
  }
  return item;
}

function makeItem(o) {
  seq += 1;
  const item = {
    key: 'a' + Date.now() + '_' + seq,
    path: o.path,
    name: o.name || '',
    size: Number(o.size) || 0,
    kind: o.kind,
    ext: o.ext || '',
    thumb: o.thumb || '',
    duration: o.duration || 0,
    status: 'uploading',
    fatal: false,
    error: '',
    percent: 0,
    fileId: 0,
    restricted: false,
    type: o.type || 'Other'
  };
  const b = badge(item);
  item.badgeText = b.text;
  item.badgeColor = b.color;
  item.sizeText = fmtSize(item.size);
  return precheck(item);
}

function isCancel(err) {
  return /cancel/i.test(String((err && err.errMsg) || ''));
}

/**
 * 拍照或从相册选择图片 / 视频（原图，不压缩）。
 * @param {number} left 还能选几个
 * @param {string} [type='Other'] 附件类别，写进生成的每个条目
 * @returns {Promise<Object[]>} 统一条目；取消 / 失败时为 []
 */
function pickMedia(left, type) {
  const count = Math.min(PICK_ONCE, Math.floor(Number(left) || 0));
  if (count <= 0) return Promise.resolve([]);
  return new Promise((resolve) => {
    wx.chooseMedia({
      count: count,
      mediaType: ['mix'],
      sourceType: ['album', 'camera'],
      sizeType: ['original'],
      maxDuration: 60,
      success(res) {
        const files = (res.tempFiles || []).slice(0, count);
        resolve(files.map((f) => {
          const isVideo = f.fileType === 'video' || (!f.fileType && res.type === 'video');
          return makeItem({
            path: f.tempFilePath,
            name: isVideo ? '视频' : '图片',
            size: f.size,
            kind: isVideo ? 'Video' : 'Image',
            ext: extOf(f.tempFilePath),
            thumb: isVideo ? (f.thumbTempFilePath || '') : f.tempFilePath,
            duration: isVideo && f.duration ? Math.round(f.duration) : 0,
            type: type
          });
        }));
      },
      fail(err) {
        if (!isCancel(err)) wx.showToast({ title: '无法选择照片或视频', icon: 'none' });
        resolve([]);
      }
    });
  });
}

/**
 * 从微信聊天选择文档（小程序只能通过聊天文件选文档）。
 * @param {number} left 还能选几个
 * @param {string} [type='Other'] 附件类别，写进生成的每个条目
 * @returns {Promise<Object[]>} 统一条目（带 name、size）；取消 / 失败时为 []
 */
function pickChatFiles(left, type) {
  const count = Math.min(PICK_ONCE, Math.floor(Number(left) || 0));
  if (count <= 0) return Promise.resolve([]);
  if (!wx.chooseMessageFile) {
    wx.showToast({ title: '当前微信版本不支持从聊天选择文件', icon: 'none' });
    return Promise.resolve([]);
  }
  return new Promise((resolve) => {
    wx.chooseMessageFile({
      count: count,
      type: 'file',
      extension: DOC_EXTS,
      success(res) {
        const files = (res.tempFiles || []).slice(0, count);
        resolve(files.map((f) => makeItem({
          path: f.path,
          name: f.name || '',
          size: f.size,
          kind: 'Document',
          ext: extOf(f.name || f.path),
          type: type
        })));
      },
      fail(err) {
        if (!isCancel(err)) {
          const msg = String((err && err.errMsg) || '');
          console.error('[chooseMessageFile fail]', msg);
          if (/privacy|scope is not declared|112/i.test(msg)) {
            // 隐私指引里「选中的文件」尚未审核通过 / 未声明
            wx.showModal({
              title: '暂时不能选择文件',
              content: '小程序的《用户隐私保护指引》正在审核「选中的文件」权限，审核通过后即可使用。照片和视频不受影响。',
              showCancel: false, confirmColor: '#1D1D1F'
            });
          } else {
            wx.showModal({ title: '无法选择文件', content: msg || '请稍后重试', showCancel: false, confirmColor: '#1D1D1F' });
          }
        }
        resolve([]);
      }
    });
  });
}

/**
 * 弹出「拍照或从相册选择 / 从微信聊天选择文件」并选择。
 * @param {number} left 还能选几个
 * @param {string} [type='Other'] 附件类别，写进生成的每个条目
 * @returns {Promise<Object[]>}
 */
function pick(left, type) {
  return new Promise((resolve) => {
    wx.showActionSheet({
      itemList: PICK_ACTIONS,
      success(res) {
        (res.tapIndex === 0 ? pickMedia(left, type) : pickChatFiles(left, type)).then(resolve);
      },
      fail() { resolve([]); }
    });
  });
}

/* ---------------- 上传（排队，同一时间只传 1 个） ---------------- */

const queue = [];
let running = null;

function emit(job, patch) {
  Object.assign(job.item, patch);
  if (job.canceled) return;
  try {
    job.onChange(job.item, patch);
  } catch (e) {
    console.error('[attachments] onChange', e);
  }
}

function pump() {
  if (running) return;
  const job = queue.shift();
  if (!job) return;
  running = job;
  const item = job.item;
  api.files.upload(item.path, 'Attachment', {
    silent: true,
    timeout: TRANSFER_TIMEOUT,
    // 只有文档有真实文件名；相册 / 相机选的图片视频不传
    fileName: item.kind === 'Document' ? item.name : undefined,
    onProgress(p) {
      // 100% 只表示请求体发完，服务端还要校验落盘，完成以接口返回为准
      emit(job, { percent: Math.min(p, 99) });
    },
    onTask(task) { job.task = task; }
  }).then((file) => {
    emit(job, { status: 'done', percent: 100, fileId: file.id, error: '' });
  }).catch((err) => {
    emit(job, { status: 'fail', fatal: false, error: (err && err.message) || '上传失败', percent: 0 });
  }).then(() => {
    running = null;
    job.resolve(item);
    pump();
  });
}

/**
 * 上传一个条目（进入队列）。预检未通过（fatal）的条目不上传。
 * @param {Object} item 统一条目（本函数会直接修改它）
 * @param {function(Object, Object):void} onChange (item, patch) 状态 / 进度变化时回调，patch 为本次变化的字段
 * @returns {Promise<Object>} 结束（成功 / 失败 / 取消）后 resolve(item)，不会 reject
 */
function upload(item, onChange) {
  if (!item || item.fatal) return Promise.resolve(item);
  return new Promise((resolve) => {
    const job = { item: item, onChange: onChange || function () {}, resolve: resolve, canceled: false, task: null };
    emit(job, { status: 'uploading', error: '', percent: 0, fileId: 0 });
    queue.push(job);
    pump();
  });
}

/** 取消某个条目的上传（排队中的直接移除，上传中的中断）；条目被删除或弹层关闭时调用 */
function cancel(key) {
  for (let i = queue.length - 1; i >= 0; i--) {
    if (queue[i].item.key === key) {
      const job = queue.splice(i, 1)[0];
      job.canceled = true;
      job.resolve(job.item);
    }
  }
  if (running && running.item.key === key) {
    running.canceled = true;
    try {
      if (running.task && running.task.abort) running.task.abort();
    } catch (e) { /* ignore */ }
  }
}

/* ---------------- 打开 ---------------- */

const docCache = {}; // fileId → 本地路径
let opening = false;

function fsm() {
  return wx.getFileSystemManager ? wx.getFileSystemManager() : null;
}

function exists(path) {
  const fs = fsm();
  if (!fs || !path) return false;
  try {
    fs.accessSync(path);
    return true;
  } catch (e) {
    return false;
  }
}

function utf8Len(str) {
  let n = 0;
  for (let i = 0; i < str.length; i++) {
    const c = str.charCodeAt(i);
    n += c < 0x80 ? 1 : (c < 0x800 ? 2 : 3);
  }
  return n;
}

/** 本地保存用的文件名：去掉路径 / 非法字符，并把 UTF-8 长度压到 180 字节内（文件系统单个文件名上限 255 字节） */
function localName(att) {
  const ext = att.ext ? '.' + String(att.ext).toLowerCase() : '';
  const name = String(att.name || '').replace(/[\\/:*?"<>|\u0000-\u001f]/g, '_');
  if (!name) return 'file' + ext;
  const dot = name.lastIndexOf('.');
  let base = dot > 0 && name.length - dot <= 10 ? name.slice(0, dot) : name;
  const tail = dot > 0 && name.length - dot <= 10 ? name.slice(dot) : ext;
  while (base.length > 1 && utf8Len(base + tail) > 180) base = base.slice(0, -1);
  return base + tail;
}

/** 准备下载目标路径：清空 att/ 目录后重建（不占满 200MB 用户目录）。失败返回 ''（退回默认临时路径） */
function prepareDocPath(att) {
  const fs = fsm();
  const base = wx.env && wx.env.USER_DATA_PATH;
  if (!fs || !base) return '';
  const root = base + '/att';
  try {
    try { fs.rmdirSync(root, true); } catch (e) { /* 目录不存在 */ }
    const dir = root + '/' + att.id;
    fs.mkdirSync(dir, true);
    return dir + '/' + localName(att);
  } catch (e) {
    console.error('[attachments] prepare dir', e);
    return '';
  }
}

function downloadDoc(att, filePath) {
  let loadingOn = true;
  wx.showLoading({ title: '下载中', mask: true });
  const done = () => {
    loadingOn = false;
    wx.hideLoading();
  };
  return http.download(api.files.downloadPath(att.id), null, {
    filePath: filePath || undefined,
    timeout: TRANSFER_TIMEOUT,
    silent: true,
    onProgress(p) {
      if (loadingOn) wx.showLoading({ title: '下载中 ' + p + '%', mask: true });
    }
  }).then((path) => {
    done();
    return path;
  }, (err) => {
    done();
    return Promise.reject(err);
  });
}

/** 下载文档到本地（同一页面生命周期内按 id 缓存） */
function fetchDoc(att) {
  const cached = docCache[att.id];
  if (cached && exists(cached)) return Promise.resolve(cached);
  delete docCache[att.id];
  const target = prepareDocPath(att);
  return downloadDoc(att, target).catch((err) => {
    // 指定路径写入失败（非 HTTP 错误、非超时）→ 退回默认临时路径再试一次
    const msg = String((err && err.data && err.data.errMsg) || '');
    if (target && err && err.code === -1 && !/timeout|超时|abort|domain/i.test(msg)) return downloadDoc(att, '');
    return Promise.reject(err);
  }).then((path) => {
    docCache[att.id] = path;
    return path;
  });
}

function showDoc(att, filePath) {
  const ext = String(att.ext || '').toLowerCase();
  if (OPEN_DOC_EXTS.indexOf(ext) >= 0) {
    wx.openDocument({
      filePath: filePath,
      fileType: ext,
      showMenu: true, // 右上角菜单可转发 / 保存
      fail() {
        wx.showToast({ title: '无法打开该文件', icon: 'none' });
      }
    });
    return;
  }
  // txt / csv：openDocument 不支持，转发到聊天后查看
  wx.showModal({
    title: att.name || '附件',
    content: '该类型不支持在小程序内预览，可转发到微信聊天后查看。',
    confirmText: '转发',
    success(res) {
      if (!res.confirm) return;
      if (!wx.shareFileMessage) {
        wx.showToast({ title: '当前微信版本不支持转发文件', icon: 'none' });
        return;
      }
      wx.shareFileMessage({
        filePath: filePath,
        fileName: att.name || ('file' + (ext ? '.' + ext : '')),
        fail(err) {
          if (!isCancel(err)) wx.showToast({ title: '转发失败', icon: 'none' });
        }
      });
    }
  });
}

/**
 * 打开附件。
 * @param {Object} att AttachmentDto（id, url, name, kind, ext）
 * @param {Object[]} [list] 同一工单的全部附件（图片 / 视频可左右切换）
 * @returns {Promise<void>} 不会 reject（错误已提示）
 */
function open(att, list) {
  if (!att) return Promise.resolve();
  if (att.kind === 'Image' || att.kind === 'Video') {
    const media = (list && list.length ? list : [att]).filter((a) => (a.kind === 'Image' || a.kind === 'Video') && a.url);
    const cur = Math.max(0, media.findIndex((a) => a.id === att.id));
    if (!media.length) return Promise.resolve();
    if (wx.previewMedia) {
      wx.previewMedia({
        sources: media.map((a) => ({ url: a.url, type: a.kind === 'Video' ? 'video' : 'image' })),
        current: cur
      });
    } else if (att.kind === 'Image') {
      wx.previewImage({ current: att.url, urls: media.filter((a) => a.kind === 'Image').map((a) => a.url) });
    } else {
      wx.showToast({ title: '当前微信版本不支持预览视频', icon: 'none' });
    }
    return Promise.resolve();
  }
  if (opening) return Promise.resolve();
  opening = true;
  return fetchDoc(att).then((path) => {
    opening = false;
    showDoc(att, path);
  }).catch((err) => {
    opening = false;
    wx.showToast({ title: (err && err.message) || '下载失败，请检查网络后重试', icon: 'none', duration: 2500 });
  });
}

/**
 * 预览本地（尚未提交）的条目：图片 / 视频用本地路径预览，文档用 openDocument 直接打开。
 * @param {Object} item 统一条目
 * @param {Object[]} [list] 同一批条目
 */
function openLocal(item, list) {
  if (!item) return;
  if (item.kind === 'Document') {
    if (OPEN_DOC_EXTS.indexOf(item.ext) < 0) {
      wx.showToast({ title: '该类型不支持在小程序内预览', icon: 'none' });
      return;
    }
    wx.openDocument({
      filePath: item.path,
      fileType: item.ext,
      showMenu: false,
      fail() {
        wx.showToast({ title: '无法打开该文件', icon: 'none' });
      }
    });
    return;
  }
  const media = (list && list.length ? list : [item]).filter((m) => m.kind !== 'Document' && !m.fatal);
  const cur = Math.max(0, media.findIndex((m) => m.key === item.key));
  if (wx.previewMedia) {
    wx.previewMedia({
      sources: media.map((m) => ({ url: m.path, type: m.kind === 'Video' ? 'video' : 'image', poster: m.kind === 'Video' ? m.thumb : undefined })),
      current: cur
    });
  } else if (item.kind === 'Image') {
    wx.previewImage({ current: item.path, urls: media.filter((m) => m.kind === 'Image').map((m) => m.path) });
  }
}

/** 页面卸载时调用：清掉已下载文档的缓存记录 */
function resetCache() {
  Object.keys(docCache).forEach((k) => { delete docCache[k]; });
}

module.exports = {
  MAX_COUNT,
  MAX_IMAGE,
  MAX_FILE,
  DOC_EXTS,
  OPEN_DOC_EXTS,
  HINT,
  HINT_RESTRICT,
  PICK_ACTIONS,
  RESTRICTED_LABEL,
  pickMedia,
  pickChatFiles,
  pick,
  upload,
  cancel,
  fmtSize,
  badge,
  extOf,
  open,
  openLocal,
  resetCache
};
