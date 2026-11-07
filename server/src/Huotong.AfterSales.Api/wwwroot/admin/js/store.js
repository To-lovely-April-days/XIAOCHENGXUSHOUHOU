// 全局状态：当前用户 + 侧栏计数。视图在增删改后调用 store.refreshCounts() 刷新侧栏数字。
import * as api from './api.js';

const { reactive } = window.Vue;

export const store = reactive({
  /** 当前登录用户（User DTO），未登录为 null */
  user: null,
  /** 启动校验失败时的错误文案（非 40100） */
  bootError: '',
  /**
   * 侧栏计数。
   * pending = 待处理（待认领 + 待验收），用于「工单」菜单角标
   * all = 全部工单数（不含已作废）；missing / distance / submitted = 快捷筛选数量
   */
  counts: { pending: null, all: null, missing: null, distance: null, submitted: null },
});

/**
 * 拉取 /api/me 并校验角色（仅 Supervisor）。
 * @returns {Promise<object>} user
 */
export async function loadMe() {
  const r = await api.me.get();
  if (!r || !r.user || r.user.role !== 'Supervisor') {
    api.clearToken();
    store.user = null;
    const err = new api.ApiError('仅售后主管可登录电脑后台', 40300, 403);
    throw err;
  }
  store.user = r.user;
  store.bootError = '';
  refreshCounts();
  return r.user;
}

let pendingRefresh = null;
/** 刷新侧栏计数（失败静默） */
export function refreshCounts() {
  if (pendingRefresh) return pendingRefresh;
  pendingRefresh = Promise.all([
    api.orders.list({ page: 1, pageSize: 1 }),
    api.orders.list({ issue: 'MissingPunch', page: 1, pageSize: 1 }),
    api.orders.list({ issue: 'DistanceAbnormal', page: 1, pageSize: 1 }),
  ]).then(([all, missing, distance]) => {
    const c = (all && all.counts) || {};
    store.counts.all = c.all ?? (all ? all.total : null);
    store.counts.submitted = c.Submitted ?? 0;
    store.counts.pending = (c.Pending ?? 0) + (c.Submitted ?? 0);
    store.counts.missing = missing ? missing.total : null;
    store.counts.distance = distance ? distance.total : null;
  }).catch(() => { /* 计数失败不打扰 */ }).finally(() => { pendingRefresh = null; });
  return pendingRefresh;
}

/** 退出登录 */
export function logout() {
  api.clearToken();
  store.user = null;
  store.counts = { pending: null, all: null, missing: null, distance: null, submitted: null };
}

store.refreshCounts = refreshCounts;
