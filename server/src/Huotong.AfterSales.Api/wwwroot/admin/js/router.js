// 极简 hash 路由：#/orders?quick=missing
const { reactive } = window.Vue;

/**
 * 当前路由（响应式）。
 * @type {{ path: string, query: Record<string,string>, fullPath: string }}
 */
export const route = reactive({ path: '/', query: {}, fullPath: '/' });

/** 解析 location.hash → { path, query, fullPath } */
export function parseHash(hash = location.hash) {
  let s = (hash || '').replace(/^#/, '');
  if (!s.startsWith('/')) s = '/' + s;
  const qi = s.indexOf('?');
  const path = (qi >= 0 ? s.slice(0, qi) : s).replace(/\/+$/, '') || '/';
  const query = {};
  if (qi >= 0) new URLSearchParams(s.slice(qi + 1)).forEach((v, k) => { query[k] = v; });
  return { path, query, fullPath: s };
}

/** 组装 hash 字符串（不含 #），跳过空值 */
export function buildPath(path, query) {
  const qs = new URLSearchParams();
  Object.keys(query || {}).forEach(k => {
    const v = query[k];
    if (v !== undefined && v !== null && v !== '') qs.set(k, String(v));
  });
  const q = qs.toString();
  return path + (q ? '?' + q : '');
}

/**
 * 跳转。
 * @param {string} path 如 '/orders'
 * @param {Record<string,any>} [query]
 * @param {{replace?: boolean}} [opts] replace=true 时不产生历史记录
 */
export function navigate(path, query, opts = {}) {
  const target = '#' + buildPath(path, query);
  if (location.hash === target) { sync(); return; }
  if (opts.replace) {
    history.replaceState(null, '', location.pathname + location.search + target);
    sync();
  } else {
    location.hash = target;
  }
}

/** 在当前页合并修改查询参数（值为 ''/null 即删除），默认 replace */
export function updateQuery(patch, opts = { replace: true }) {
  navigate(route.path, Object.assign({}, route.query, patch), opts);
}

const listeners = [];
/** 路由变化回调（在 route 已更新后调用） */
export function onRouteChange(fn) { listeners.push(fn); }

function sync() {
  const r = parseHash();
  route.path = r.path;
  route.query = r.query;
  route.fullPath = r.fullPath;
  listeners.forEach(fn => { try { fn(route); } catch (e) { console.error(e); } });
}

window.addEventListener('hashchange', sync);
sync();
