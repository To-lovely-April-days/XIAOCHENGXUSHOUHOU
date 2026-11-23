/**
 * 线性图标（24×24，stroke），生成 base64 SVG data URI，供 <image src> 使用。
 *   const icons = require('../../utils/icons.js');
 *   icons.get('camera', '#FFFFFF')            → 'data:image/svg+xml;base64,...'
 *   icons.get('home', '#1D1D1F', 2)           → 第三个参数为线宽（默认 1.8）
 * WXML 中推荐直接用全局组件 <ui-icon name="camera" color="#fff" size="24" />。
 */

const PATHS = {
  home: '<path d="M3 10.5L12 3l9 7.5V20a1 1 0 0 1-1 1h-5v-6h-6v6H4a1 1 0 0 1-1-1z"/>',
  orders: '<rect x="5" y="4" width="14" height="17" rx="2"/><path d="M9 9h6M9 13h6M9 17h3"/>',
  camera: '<path d="M4 8h3l2-3h6l2 3h3v11H4z"/><circle cx="12" cy="13" r="3.5"/>',
  clock: '<circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/>',
  user: '<circle cx="12" cy="8" r="4"/><path d="M4 21c0-4 3.6-6 8-6s8 2 8 6"/>',
  plus: '<path d="M12 5v14M5 12h14"/>',
  'plus-square': '<rect x="4" y="4" width="16" height="16" rx="4"/><path d="M12 8.5v7M8.5 12h7"/>',
  back: '<path d="M15 5l-7 7 7 7"/>',
  right: '<path d="M9 5l7 7-7 7"/>',
  down: '<path d="M6 9l6 6 6-6"/>',
  up: '<path d="M6 15l6-6 6 6"/>',
  sliders: '<path d="M4 7h16M7 12h10M10 17h4"/>',
  check: '<path d="M5 12.5l4.5 4.5L19 7.5"/>',
  close: '<path d="M6 6l12 12M18 6L6 18"/>',
  phone: '<path d="M5 4h3l2 5-2.5 1.5a11 11 0 0 0 6 6L15 14l5 2v3a2 2 0 0 1-2 2A16 16 0 0 1 3 6a2 2 0 0 1 2-2z"/>',
  location: '<path d="M12 21s-7-6.2-7-11.5A7 7 0 0 1 19 9.5C19 14.8 12 21 12 21z"/><circle cx="12" cy="9.5" r="2.5"/>',
  search: '<circle cx="11" cy="11" r="6.5"/><path d="M16 16l4.5 4.5"/>',
  image: '<rect x="3.5" y="4.5" width="17" height="15" rx="2"/><circle cx="9" cy="10" r="1.8"/><path d="M20.5 16l-5-5-8.5 8.5"/>',
  edit: '<path d="M4 20h4L19 9l-4-4L4 16z"/><path d="M13.5 6.5l4 4"/>',
  warning: '<path d="M12 4l9 16H3z"/><path d="M12 10v4.5M12 17.5v.5"/>',
  info: '<circle cx="12" cy="12" r="9"/><path d="M12 11v6M12 7.5v.5"/>',
  flash: '<path d="M13 3L5 14h6l-1 7 8-11h-6z"/>',
  switch: '<path d="M4 9a8 8 0 0 1 14-3l2 2M20 15a8 8 0 0 1-14 3l-2-2"/><path d="M20 4v4h-4M4 20v-4h4"/>',
  shield: '<path d="M12 3l8 3v6c0 4.5-3.4 8.2-8 9-4.6-.8-8-4.5-8-9V6z"/><path d="M8.5 12l2.5 2.5 4.5-5"/>',
  lock: '<rect x="5" y="11" width="14" height="10" rx="2"/><path d="M8 11V7.5a4 4 0 0 1 8 0V11"/>',
  more: '<path d="M5.5 12h.01M12 12h.01M18.5 12h.01"/>',
  bell: '<path d="M6 16.5V11a6 6 0 0 1 12 0v5.5l1.5 2h-15z"/><path d="M10 20.5a2 2 0 0 0 4 0"/>'
};

/** 微信 / WeUI 风格实心面性图标（24×24，fill-rule="evenodd"） */
const FILLED = {
  wechat: 'M8.691 2.188C3.891 2.188 0 5.476 0 9.53c0 2.212 1.17 4.203 3.002 5.55a.59.59 0 0 1 .213.665l-.39 1.48c-.019.07-.048.141-.048.213 0 .163.13.295.29.295a.326.326 0 0 0 .167-.054l1.903-1.114a.864.864 0 0 1 .717-.098 10.16 10.16 0 0 0 2.837.403c.276 0 .543-.027.811-.05-.857-2.578.157-4.972 1.932-6.446 1.703-1.415 3.882-1.98 5.853-1.838-.576-3.583-4.196-6.348-8.596-6.348zM5.785 5.991c.642 0 1.162.529 1.162 1.18a1.17 1.17 0 0 1-1.162 1.178A1.17 1.17 0 0 1 4.623 7.17c0-.651.52-1.18 1.162-1.18zm5.813 0c.642 0 1.162.529 1.162 1.18a1.17 1.17 0 0 1-1.162 1.178 1.17 1.17 0 0 1-1.162-1.178c0-.651.52-1.18 1.162-1.18zm5.34 2.867c-1.797-.052-3.746.512-5.28 1.786-1.72 1.428-2.687 3.72-1.78 6.22.942 2.453 3.666 4.229 6.884 4.229.826 0 1.622-.12 2.361-.336a.722.722 0 0 1 .598.082l1.584.926a.272.272 0 0 0 .14.047c.134 0 .24-.111.24-.247 0-.06-.023-.12-.038-.177l-.327-1.233a.582.582 0 0 1-.023-.156.49.49 0 0 1 .201-.398C23.024 18.48 24 16.82 24 14.98c0-3.21-2.931-5.837-6.656-6.088V8.89c-.135-.01-.27-.027-.407-.03zm-2.53 3.274c.535 0 .969.44.969.982a.976.976 0 0 1-.969.983.976.976 0 0 1-.969-.983c0-.542.434-.982.97-.982zm4.844 0c.535 0 .969.44.969.982a.976.976 0 0 1-.969.983.976.976 0 0 1-.969-.983c0-.542.434-.982.97-.982z',
  'wx-info': 'M12 2C6.477 2 2 6.477 2 12s4.477 10 10 10 10-4.477 10-10S17.523 2 12 2zm0 4.5a1.15 1.15 0 1 1 0 2.3 1.15 1.15 0 0 1 0-2.3zm1 10.75a1 1 0 1 1-2 0v-6.25a1 1 0 1 1 2 0v6.25z',
  'wx-warn': 'M12 2C6.477 2 2 6.477 2 12s4.477 10 10 10 10-4.477 10-10S17.523 2 12 2zm-1 5.25a1 1 0 1 1 2 0v5.75a1 1 0 1 1-2 0V7.25zm1 10.25a1.2 1.2 0 1 1 0-2.4 1.2 1.2 0 0 1 0 2.4z',
  'wx-safe': 'M12 2.2L4 5.4v5.85c0 4.95 3.4 9.25 8 10.55 4.6-1.3 8-5.6 8-10.55V5.4L12 2.2zm4.25 7.45l-4.95 5a.9.9 0 0 1-1.28 0l-2.27-2.3a.9.9 0 1 1 1.28-1.27l1.63 1.65 4.31-4.35a.9.9 0 1 1 1.28 1.27z',
  'wx-lock': 'M12 2.5a4.75 4.75 0 0 0-4.75 4.75V10H6a2 2 0 0 0-2 2v7.5a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V12a2 2 0 0 0-2-2h-1.25V7.25A4.75 4.75 0 0 0 12 2.5zm-2.95 4.75a2.95 2.95 0 1 1 5.9 0V10h-5.9V7.25zM12 13.75a1.55 1.55 0 0 1 .85 2.84v1.66a.85.85 0 1 1-1.7 0v-1.66a1.55 1.55 0 0 1 .85-2.84z',
  'wx-camera': 'M9.2 4.5a1.5 1.5 0 0 0-1.28.72L6.85 7H4.5A2.5 2.5 0 0 0 2 9.5v8.5A2.5 2.5 0 0 0 4.5 20.5h15a2.5 2.5 0 0 0 2.5-2.5V9.5A2.5 2.5 0 0 0 19.5 7h-2.35l-1.07-1.78a1.5 1.5 0 0 0-1.28-.72H9.2zM12 9.75a4 4 0 1 1 0 8 4 4 0 0 1 0-8zm0 1.8a2.2 2.2 0 1 0 0 4.4 2.2 2.2 0 0 0 0-4.4z',
  'wx-album': 'M5 4h14a2.5 2.5 0 0 1 2.5 2.5v11A2.5 2.5 0 0 1 19 20H5a2.5 2.5 0 0 1-2.5-2.5v-11A2.5 2.5 0 0 1 5 4zm3.5 3.75a1.75 1.75 0 1 0 0 3.5 1.75 1.75 0 0 0 0-3.5zm-3.8 9.75h14.6l-4.55-5.6a.9.9 0 0 0-1.38-.03l-2.92 3.35-1.55-1.75a.9.9 0 0 0-1.36.02L4.7 17.5z'
};

const B64 = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/';

/** 仅 ASCII 的 base64 编码 */
function btoa(str) {
  let out = '';
  for (let i = 0; i < str.length; i += 3) {
    const a = str.charCodeAt(i);
    const b = i + 1 < str.length ? str.charCodeAt(i + 1) : NaN;
    const c = i + 2 < str.length ? str.charCodeAt(i + 2) : NaN;
    const n = (a << 16) | ((b || 0) << 8) | (c || 0);
    out += B64[(n >> 18) & 63] + B64[(n >> 12) & 63] +
      (isNaN(b) ? '=' : B64[(n >> 6) & 63]) +
      (isNaN(c) ? '=' : B64[n & 63]);
  }
  return out;
}

const cache = {};

/**
 * 取图标 data URI。
 * @param {string} name PATHS 中的名字（home/orders/camera/clock/user/plus/plus-square/back/right/down/check/close/phone/location/search/image/edit/warning/info/flash/switch/shield/wechat/lock/more）
 * @param {string} [color='#1D1D1F']
 * @param {number} [strokeWidth=1.8]
 * @returns {string}
 */
function get(name, color, strokeWidth) {
  color = color || '#1D1D1F';
  if (FILLED[name]) {
    const fkey = 'f|' + name + '|' + color;
    if (cache[fkey]) return cache[fkey];
    const fsvg = '<svg xmlns="http://www.w3.org/2000/svg" width="48" height="48" viewBox="0 0 24 24" fill="' + color +
      '" fill-rule="evenodd" clip-rule="evenodd"><path d="' + FILLED[name] + '"/></svg>';
    cache[fkey] = 'data:image/svg+xml;base64,' + btoa(fsvg);
    return cache[fkey];
  }
  strokeWidth = strokeWidth || 1.8;
  const key = name + '|' + color + '|' + strokeWidth;
  if (cache[key]) return cache[key];
  const inner = PATHS[name];
  if (!inner) return '';
  const svg = '<svg xmlns="http://www.w3.org/2000/svg" width="48" height="48" viewBox="0 0 24 24" fill="none" stroke="' + color +
    '" stroke-width="' + strokeWidth + '" stroke-linecap="round" stroke-linejoin="round">' + inner + '</svg>';
  cache[key] = 'data:image/svg+xml;base64,' + btoa(svg);
  return cache[key];
}

module.exports = {
  get,
  names: Object.keys(PATHS).concat(Object.keys(FILLED))
};
