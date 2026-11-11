/**
 * 格式化工具。接口时间均为北京时间 'yyyy-MM-ddTHH:mm:ss'（无时区），一律按本地时间解析。
 * 所有接收时间的函数都同时接受 Date / 时间戳 / 字符串。
 */

const WEEK = ['周日', '周一', '周二', '周三', '周四', '周五', '周六'];

function pad2(n) {
  return n < 10 ? '0' + n : '' + n;
}

/**
 * 安全解析时间（兼容 iOS）。支持 'yyyy-MM-ddTHH:mm[:ss]'、'yyyy-MM-dd HH:mm:ss'、'yyyy-MM-dd'、'yyyy-MM'、Date、时间戳。
 * @param {string|Date|number} v
 * @returns {?Date} 无法解析返回 null
 */
function parseTime(v) {
  if (v === null || v === undefined || v === '') return null;
  if (v instanceof Date) return isNaN(v.getTime()) ? null : v;
  if (typeof v === 'number') return new Date(v);
  const m = String(v).match(/^(\d{4})-(\d{1,2})(?:-(\d{1,2}))?(?:[T ](\d{1,2}):(\d{1,2})(?::(\d{1,2}))?)?/);
  if (!m) return null;
  return new Date(+m[1], +m[2] - 1, m[3] ? +m[3] : 1, m[4] ? +m[4] : 0, m[5] ? +m[5] : 0, m[6] ? +m[6] : 0);
}

/* ---------- 服务器时间对齐（时间以服务器为准） ---------- */
let serverOffset = 0;

/**
 * 用接口返回的 serverTime 校准本地时钟偏差。
 * @param {string} serverTime 'yyyy-MM-ddTHH:mm:ss'
 */
function syncServerTime(serverTime) {
  const d = parseTime(serverTime);
  if (d) serverOffset = d.getTime() - Date.now();
}

/** 校准后的“现在” @returns {Date} */
function now() {
  return new Date(Date.now() + serverOffset);
}

/* ---------- 时长 ---------- */

/**
 * 分钟 → 'H:mm'。
 * @param {?number} min
 * @returns {string} 如 217 → '3:37'；null/undefined → '—'
 */
function formatDuration(min) {
  if (min === null || min === undefined || isNaN(min)) return '—';
  const m = Math.max(0, Math.floor(min));
  return Math.floor(m / 60) + ':' + pad2(m % 60);
}

/**
 * 秒 → 计时显示拆分（大字 H:mm + 小字 :ss）。
 * @param {number} seconds
 * @returns {{hm:string, ss:string}} 如 13038 → {hm:'3:37', ss:':18'}
 */
function splitClock(seconds) {
  const s = Math.max(0, Math.floor(seconds || 0));
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  return { hm: h + ':' + pad2(m), ss: ':' + pad2(s % 60) };
}

/**
 * 两个时间之间的秒数（end 缺省为校准后的 now）。
 * @returns {number}
 */
function secondsBetween(start, end) {
  const a = parseTime(start);
  const b = end ? parseTime(end) : now();
  if (!a || !b) return 0;
  return Math.max(0, Math.floor((b.getTime() - a.getTime()) / 1000));
}

/* ---------- 价格 ---------- */

/**
 * 价格显示。
 * @param {?number} price 0 表示保内
 * @param {boolean} [short=false] true 时 0 显示 '保内'
 * @param {?boolean} [priceSet] 是否已定价（v1.1）；false，或 price 为 null 且 priceSet 不为 true → '未定价'
 * @returns {string} '¥1,800' / '¥1,800.50' / '保内免费' / '保内' / '未定价'
 */
function formatPrice(price, short, priceSet) {
  const empty = price === null || price === undefined || price === '' || isNaN(price);
  if (priceSet === false || (empty && priceSet !== true)) return '未定价';
  if (empty) return '—';
  const p = Number(price);
  if (p === 0) return short ? '保内' : '保内免费';
  const fixed = Math.round(p * 100) % 100 === 0 ? String(Math.round(p)) : p.toFixed(2);
  const parts = fixed.split('.');
  parts[0] = parts[0].replace(/\B(?=(\d{3})+(?!\d))/g, ',');
  return '¥' + parts.join('.');
}

/* ---------- 日期 ---------- */

/** '11月6日' */
function formatMonthDay(d) {
  const t = parseTime(d);
  return t ? (t.getMonth() + 1) + '月' + t.getDate() + '日' : '';
}

/** '周五' */
function weekday(d) {
  const t = parseTime(d);
  return t ? WEEK[t.getDay()] : '';
}

/** '10:05' */
function formatHm(d) {
  const t = parseTime(d);
  return t ? pad2(t.getHours()) + ':' + pad2(t.getMinutes()) : '';
}

/** 'yyyy-MM-dd' */
function formatDate(d) {
  const t = parseTime(d);
  return t ? t.getFullYear() + '-' + pad2(t.getMonth() + 1) + '-' + pad2(t.getDate()) : '';
}

/** '2026.11.06'（水印用） */
function formatDateDot(d) {
  const t = parseTime(d);
  return t ? t.getFullYear() + '.' + pad2(t.getMonth() + 1) + '.' + pad2(t.getDate()) : '';
}

function sameDay(a, b) {
  return a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate();
}

/** 是否为今天（按校准后的 now） */
function isToday(d) {
  const t = parseTime(d);
  return !!t && sameDay(t, now());
}

/**
 * 短日期时间：今天 → '今天 10:00'，否则 '11月9日 10:00'（跨年带年份）。
 * @param {string|Date} str
 */
function formatDateTimeShort(str) {
  const t = parseTime(str);
  if (!t) return '';
  const n = now();
  if (sameDay(t, n)) return '今天 ' + formatHm(t);
  const prefix = t.getFullYear() === n.getFullYear() ? '' : t.getFullYear() + '年';
  return prefix + formatMonthDay(t) + ' ' + formatHm(t);
}

/**
 * 相对时间：'刚刚' / '10 分钟前' / '3 小时前'（同一天）/ '昨天' / '11月4日'（跨年 '2025年11月4日'）。
 * @param {string|Date} str
 */
function timeAgo(str) {
  const t = parseTime(str);
  if (!t) return '';
  const n = now();
  const diff = (n.getTime() - t.getTime()) / 1000;
  if (sameDay(t, n)) {
    if (diff < 60) return '刚刚';
    if (diff < 3600) return Math.floor(diff / 60) + ' 分钟前';
    return Math.floor(diff / 3600) + ' 小时前';
  }
  const y = new Date(n.getFullYear(), n.getMonth(), n.getDate() - 1);
  if (sameDay(t, y)) return '昨天';
  return (t.getFullYear() === n.getFullYear() ? '' : t.getFullYear() + '年') + formatMonthDay(t);
}

/**
 * 距离：<1000 → '86 m'，否则 '2.4 km'；null → ''。
 * @param {?number} m 米
 */
function formatDistance(m) {
  if (m === null || m === undefined || isNaN(m)) return '';
  if (m < 1000) return Math.round(m) + ' m';
  return (m / 1000).toFixed(1) + ' km';
}

/** '13900000000' → '139****0000' */
function maskPhone(phone) {
  const s = String(phone || '').replace(/\s/g, '');
  if (s.length < 7) return s;
  return s.slice(0, 3) + '****' + s.slice(-4);
}

/** '13800000000' → '138 0000 0000'（非 11 位原样返回） */
function formatPhone(phone) {
  const s = String(phone || '').replace(/\s/g, '');
  if (!/^\d{11}$/.test(s)) return s;
  return s.slice(0, 3) + ' ' + s.slice(3, 7) + ' ' + s.slice(7);
}

/**
 * Date → 接口时间字符串 'yyyy-MM-ddTHH:mm:ss'
 * @param {Date|string} d
 */
function toApiTime(d) {
  const t = parseTime(d);
  if (!t) return '';
  return formatDate(t) + 'T' + pad2(t.getHours()) + ':' + pad2(t.getMinutes()) + ':' + pad2(t.getSeconds());
}

/** 今天 'yyyy-MM-dd'（按校准后的 now） */
function today() {
  return formatDate(now());
}

module.exports = {
  pad2,
  parseTime,
  syncServerTime,
  now,
  formatDuration,
  splitClock,
  secondsBetween,
  formatPrice,
  formatMonthDay,
  weekday,
  formatHm,
  formatDate,
  formatDateDot,
  isToday,
  formatDateTimeShort,
  timeAgo,
  formatDistance,
  maskPhone,
  formatPhone,
  toApiTime,
  today
};
