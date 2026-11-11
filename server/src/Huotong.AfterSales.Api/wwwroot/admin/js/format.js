// 格式化工具。后端时间均为北京时间、无时区后缀：yyyy-MM-ddTHH:mm:ss / yyyy-MM-dd，按本地时间解析。

const pad2 = (n) => String(n).padStart(2, '0');
export { pad2 };

/**
 * 解析后端时间/日期字符串为本地 Date；Date 原样返回；无效返回 null。
 * @param {string|Date|null|undefined} s
 * @returns {Date|null}
 */
export function parseTime(s) {
  if (!s) return null;
  if (s instanceof Date) return isNaN(s.getTime()) ? null : s;
  const m = /^(\d{4})-(\d{1,2})(?:-(\d{1,2}))?(?:[T ](\d{1,2}):(\d{2})(?::(\d{2}))?)?/.exec(String(s));
  if (!m) return null;
  return new Date(+m[1], +m[2] - 1, m[3] ? +m[3] : 1, m[4] ? +m[4] : 0, m[5] ? +m[5] : 0, m[6] ? +m[6] : 0);
}

/** Date → 'yyyy-MM-dd'（接口日期参数） */
export function toApiDate(d) {
  d = parseTime(d);
  return d ? `${d.getFullYear()}-${pad2(d.getMonth() + 1)}-${pad2(d.getDate())}` : '';
}

/** Date → 'yyyy-MM-ddTHH:mm:ss'（接口时间参数） */
export function toApiDateTime(d) {
  d = parseTime(d);
  return d ? `${toApiDate(d)}T${pad2(d.getHours())}:${pad2(d.getMinutes())}:${pad2(d.getSeconds())}` : '';
}

/** 'HH:mm'（如 09:05） */
export function fmtTime(s) {
  const d = parseTime(s);
  return d ? `${pad2(d.getHours())}:${pad2(d.getMinutes())}` : '';
}

/** 'M月D日' */
export function fmtMD(s) {
  const d = parseTime(s);
  return d ? `${d.getMonth() + 1}月${d.getDate()}日` : '';
}

/** 'M月D日 HH:mm'（原型状态记录、打卡时间用） */
export function fmtMDHM(s) {
  const d = parseTime(s);
  return d ? `${fmtMD(d)} ${fmtTime(d)}` : '';
}

/** 'yyyy-MM-dd HH:mm' */
export function fmtDateTime(s) {
  const d = parseTime(s);
  return d ? `${toApiDate(d)} ${fmtTime(d)}` : '';
}

/** 星期：'周一'…'周日' */
export function fmtWeekday(s) {
  const d = parseTime(s);
  return d ? '周' + '日一二三四五六'[d.getDay()] : '';
}

/**
 * 分钟 → 'H:mm'（小时不封顶，如 795 → '13:15'）；null/undefined → '—'。
 * @param {number|null|undefined} minutes
 * @param {string} [empty='—']
 */
export function fmtHM(minutes, empty = '—') {
  if (minutes === null || minutes === undefined || isNaN(minutes)) return empty;
  const m = Math.max(0, Math.round(minutes));
  return `${Math.floor(m / 60)}:${pad2(m % 60)}`;
}

/** 分钟 → 小时 1 位小数字符串（如 5388 → '89.8'） */
export function fmtHours(minutes) {
  if (minutes === null || minutes === undefined || isNaN(minutes)) return '—';
  return (Math.round(minutes / 6) / 10).toFixed(1);
}

/** 千分位：1800 → '1,800'；保留至多 2 位小数 */
export function fmtNumber(n) {
  if (n === null || n === undefined || isNaN(n)) return '';
  const fixed = Math.round(Number(n) * 100) / 100;
  const [i, f] = String(fixed).split('.');
  return i.replace(/\B(?=(\d{3})+(?!\d))/g, ',') + (f ? '.' + f.padEnd(2, '0') : '');
}

/**
 * 价格：未定价（opts.priceSet === false）→ '未定价'；0 → '保内'（opts.free 时 '保内免费'）；其他 → '¥1,800'。
 * @param {number|null} price
 * @param {{free?: boolean, priceSet?: boolean|null}} [opts]
 */
export function fmtPrice(price, opts = {}) {
  if (opts.priceSet === false) return '未定价';
  if (price === null || price === undefined || price === '') return '—';
  if (Number(price) === 0) return opts.free ? '保内免费' : '保内';
  return '¥' + fmtNumber(price);
}

/** 是否保内（price === 0），用于绿色字。未定价（priceSet === false）不算保内 */
export const isWarranty = (price, priceSet) => priceSet !== false && price !== null && price !== undefined && Number(price) === 0;

/**
 * 相对时间：刚刚 / N 分钟前 / N 小时前 / 昨天 HH:mm / M月D日 / yyyy年M月D日
 * @param {string|Date} s
 * @param {Date} [now]
 */
export function fmtRelative(s, now = new Date()) {
  const d = parseTime(s);
  if (!d) return '';
  const diff = (now.getTime() - d.getTime()) / 1000;
  if (diff < 0) return fmtMDHM(d);
  if (diff < 60) return '刚刚';
  if (diff < 3600) return `${Math.floor(diff / 60)} 分钟前`;
  const startToday = new Date(now.getFullYear(), now.getMonth(), now.getDate());
  if (d >= startToday) return `${Math.floor(diff / 3600)} 小时前`;
  const startYesterday = new Date(startToday.getTime() - 86400000);
  if (d >= startYesterday) return `昨天 ${fmtTime(d)}`;
  if (d.getFullYear() === now.getFullYear()) return fmtMD(d);
  return `${d.getFullYear()}年${fmtMD(d)}`;
}

/**
 * 距离：< 1000 m → '86 m'；否则 '2.4 km'；空 → ''。
 * @param {number|null} meters
 */
export function fmtDistance(meters) {
  if (meters === null || meters === undefined || isNaN(meters)) return '';
  if (meters < 1000) return `${Math.round(meters)} m`;
  return `${(meters / 1000).toFixed(1)} km`;
}

/** 手机号脱敏：13900000001 → 139****0001 */
export function maskPhone(phone) {
  if (!phone) return '';
  const s = String(phone).replace(/\s+/g, '');
  if (s.length < 7) return s;
  return s.slice(0, 3) + '****' + s.slice(-4);
}

/** 本周一 ~ 今天：{ from, to }（yyyy-MM-dd） */
export function thisWeekRange(now = new Date()) {
  const day = now.getDay() || 7;
  const monday = new Date(now.getFullYear(), now.getMonth(), now.getDate() - (day - 1));
  return { from: toApiDate(monday), to: toApiDate(now) };
}

/** 最近 N 天（含今天）：{ from, to } */
export function lastDaysRange(n, now = new Date()) {
  const start = new Date(now.getFullYear(), now.getMonth(), now.getDate() - (n - 1));
  return { from: toApiDate(start), to: toApiDate(now) };
}

/** 本月 1 日 ~ 月末：{ from, to } */
export function monthRange(year, month) {
  return { from: toApiDate(new Date(year, month - 1, 1)), to: toApiDate(new Date(year, month, 0)) };
}

/**
 * 文件大小：< 1024 → 'N B'；< 1 MB → 'N KB'（取整）；< 1 GB → 'N.N MB'；否则 'N.NN GB'。空值 → ''。
 * @param {number|null|undefined} bytes
 */
export function fmtSize(bytes) {
  if (bytes === null || bytes === undefined || bytes === '' || isNaN(bytes)) return '';
  const n = Math.max(0, Number(bytes));
  if (n < 1024) return `${Math.round(n)} B`;
  if (n < 1048576) return `${Math.round(n / 1024)} KB`;
  if (n < 1073741824) return `${(n / 1048576).toFixed(1)} MB`;
  return `${(n / 1073741824).toFixed(2)} GB`;
}

const BADGE_GRAY = '#636366';
const BADGE_BY_EXT = {
  doc: { text: 'W', color: '#2B579A' }, docx: { text: 'W', color: '#2B579A' },
  xls: { text: 'X', color: '#217346' }, xlsx: { text: 'X', color: '#217346' }, csv: { text: 'X', color: '#217346' },
  ppt: { text: 'P', color: '#D24726' }, pptx: { text: 'P', color: '#D24726' },
  pdf: { text: 'PDF', color: '#C81E2B' },
  txt: { text: 'TXT', color: BADGE_GRAY },
};

/**
 * 文件类型角标：{ text, color }。按 kind（Image / Video / Document）与 ext（小写、不带点）。
 * @param {{kind?:string, ext?:string}} item FileDto / 上传条目
 * @returns {{text:string,color:string}}
 */
export function fileBadge(item) {
  const it = item || {};
  if (it.kind === 'Image') return { text: '图', color: BADGE_GRAY };
  if (it.kind === 'Video') return { text: '视频', color: BADGE_GRAY };
  return BADGE_BY_EXT[String(it.ext || '').toLowerCase()] || { text: '文件', color: BADGE_GRAY };
}
