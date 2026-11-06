/**
 * 首页 12 小时表盘（原型 00/01/02 左侧 150×150）：生成 SVG data URI。
 * 数字 12/3/6/9 由 WXML 叠加（SVG <text> 在部分机型渲染不稳定）。
 *
 * build({ now, arcs: [{start, end}], marker: {start, running}, plan: {start, end} })
 *   now     Date 当前（校准后）
 *   arcs    红色实线弧：今日到场—离场 / 到场—现在
 *   marker  start：首次到场（黑圈白点）；running：true 时在 now 位置画红圈
 *   plan    灰色虚线弧（今日预约）
 */
const C = 75;
const R = 63;

const B64 = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/';
function btoa(str) {
  let out = '';
  for (let i = 0; i < str.length; i += 3) {
    const a = str.charCodeAt(i);
    const b = i + 1 < str.length ? str.charCodeAt(i + 1) : NaN;
    const c = i + 2 < str.length ? str.charCodeAt(i + 2) : NaN;
    const n = (a << 16) | ((b || 0) << 8) | (c || 0);
    out += B64[(n >> 18) & 63] + B64[(n >> 12) & 63] +
      (isNaN(b) ? '=' : B64[(n >> 6) & 63]) + (isNaN(c) ? '=' : B64[n & 63]);
  }
  return out;
}

function f1(n) {
  return (Math.round(n * 10) / 10).toFixed(1);
}

/** 时间 → 表盘角度（度，12 点为 0，顺时针） */
function angleOf(d) {
  const h = d.getHours() % 12 + d.getMinutes() / 60 + d.getSeconds() / 3600;
  return h / 12 * 360;
}

function point(deg, r) {
  const a = deg * Math.PI / 180;
  return { x: C + r * Math.sin(a), y: C - r * Math.cos(a) };
}

/** 两个 Date 之间的弧（≥ 12 小时画整圈） */
function arcPath(start, end) {
  const spanH = (end.getTime() - start.getTime()) / 3600000;
  if (spanH <= 0) return '';
  if (spanH >= 12) {
    return 'M ' + f1(C) + ' ' + f1(C - R) + ' A ' + R + ' ' + R + ' 0 1 1 ' + f1(C - 0.01) + ' ' + f1(C - R);
  }
  const a1 = angleOf(start);
  const sweep = spanH * 30;
  const p1 = point(a1, R);
  const p2 = point(a1 + sweep, R);
  return 'M ' + f1(p1.x) + ' ' + f1(p1.y) + ' A ' + R + ' ' + R + ' 0 ' + (sweep > 180 ? 1 : 0) + ' 1 ' + f1(p2.x) + ' ' + f1(p2.y);
}

let tickCache = '';
function ticks() {
  if (tickCache) return tickCache;
  let minor = '';
  let major = '';
  for (let i = 0; i < 60; i++) {
    const deg = i * 6;
    if (i % 5 === 0) {
      const a = point(deg, 44);
      const b = point(deg, 53);
      major += 'M ' + f1(a.x) + ' ' + f1(a.y) + ' L ' + f1(b.x) + ' ' + f1(b.y) + ' ';
    } else {
      const a = point(deg, 49);
      const b = point(deg, 53);
      minor += 'M ' + f1(a.x) + ' ' + f1(a.y) + ' L ' + f1(b.x) + ' ' + f1(b.y) + ' ';
    }
  }
  tickCache = '<path d="' + minor.trim() + '" fill="none" stroke="#CFCFCD" stroke-width="1" stroke-linecap="round"/>' +
    '<path d="' + major.trim() + '" fill="none" stroke="#8E8E93" stroke-width="2" stroke-linecap="round"/>';
  return tickCache;
}

function circle(p, r, fill, stroke, sw) {
  return '<circle cx="' + f1(p.x) + '" cy="' + f1(p.y) + '" r="' + r + '" fill="' + fill + '" stroke="' + stroke + '" stroke-width="' + sw + '"/>';
}

function build(opts) {
  const o = opts || {};
  const now = o.now || new Date();
  let body = '<circle cx="75" cy="75" r="63" fill="none" stroke="#E8E8E6" stroke-width="8"/>' + ticks();

  if (o.plan && o.plan.start && o.plan.end) {
    const d = arcPath(o.plan.start, o.plan.end);
    if (d) body += '<path d="' + d + '" fill="none" stroke="#8E8E93" stroke-width="8" stroke-linecap="butt" stroke-dasharray="3 5"/>';
    body += circle(point(angleOf(o.plan.start), R), 6, '#FFFFFF', '#1D1D1F', 3);
  }

  (o.arcs || []).forEach((a) => {
    if (!a || !a.start) return;
    const d = arcPath(a.start, a.end || now);
    if (d) body += '<path d="' + d + '" fill="none" stroke="#C81E2B" stroke-width="8" stroke-linecap="round"/>';
  });

  const mk = o.marker || {};
  if (mk.start) body += circle(point(angleOf(mk.start), R), 5, '#FFFFFF', '#1D1D1F', 2.5);
  if (mk.running) body += circle(point(angleOf(now), R), 6.5, '#FFFFFF', '#C81E2B', 3);

  // 时针 / 分针
  const hp = point(angleOf(now), 29);
  const mp = point((now.getMinutes() + now.getSeconds() / 60) * 6, 44);
  body += '<path d="M 75 75 L ' + f1(hp.x) + ' ' + f1(hp.y) + '" stroke="#1D1D1F" stroke-width="4" stroke-linecap="round"/>';
  body += '<path d="M 75 75 L ' + f1(mp.x) + ' ' + f1(mp.y) + '" stroke="#6E6E73" stroke-width="2.5" stroke-linecap="round"/>';
  body += '<circle cx="75" cy="75" r="4.5" fill="#C81E2B" stroke="#FFFFFF" stroke-width="1.5"/>';

  const svg = '<svg xmlns="http://www.w3.org/2000/svg" width="300" height="300" viewBox="0 0 150 150">' + body + '</svg>';
  return 'data:image/svg+xml;base64,' + btoa(svg);
}

module.exports = { build };
