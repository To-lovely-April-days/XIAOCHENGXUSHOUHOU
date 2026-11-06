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
  wechat: '<path d="M9.5 4.5C5.4 4.5 2.5 7.1 2.5 10.2c0 1.8 1 3.3 2.5 4.4L4.5 17l2.8-1.4c.7.2 1.4.3 2.2.3"/><path d="M15 9c3.6 0 6.5 2.3 6.5 5.2 0 1.5-.8 2.9-2.1 3.8l.4 2-2.4-1.2c-.8.2-1.6.4-2.4.4-3.6 0-6.5-2.3-6.5-5.1S11.4 9 15 9z"/>'
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
 * @param {string} name PATHS 中的名字（home/orders/camera/clock/user/plus/plus-square/back/right/down/check/close/phone/location/search/image/edit/warning/info/flash/switch/shield/wechat）
 * @param {string} [color='#1D1D1F']
 * @param {number} [strokeWidth=1.8]
 * @returns {string}
 */
function get(name, color, strokeWidth) {
  color = color || '#1D1D1F';
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
  names: Object.keys(PATHS)
};
