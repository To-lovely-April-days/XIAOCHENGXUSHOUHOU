/**
 * 自定义 tabBar 辅助。每个 tab 页（home/orders/hours/me）在 onShow 里调用：
 *   tabbar.setTabSelected(this, 'home')
 * key：'home' | 'orders' | 'hours' | 'me'
 * 会同时按 globalData.user.role 刷新 tab 项（角色变化后自动更新）。
 */

function getBar(page) {
  if (!page || typeof page.getTabBar !== 'function') return null;
  return page.getTabBar() || null;
}

/**
 * 设置当前选中的 tab。
 * @param {Object} page 页面实例（this）
 * @param {'home'|'orders'|'hours'|'me'} key
 */
function setTabSelected(page, key) {
  const bar = getBar(page);
  if (!bar) return;
  if (typeof bar.refresh === 'function') bar.refresh();
  bar.setData({ selected: key });
}

/**
 * 隐藏 / 显示 tabBar（弹层打开时用，避免被 tabBar 遮挡）。
 * @param {Object} page
 * @param {boolean} hidden
 */
function setTabBarHidden(page, hidden) {
  const bar = getBar(page);
  if (bar) bar.setData({ hidden: !!hidden });
}

/** 刷新当前页面栈中所有 tabBar（登录 / 角色变化后） */
function refreshAll() {
  getCurrentPages().forEach((p) => {
    const bar = getBar(p);
    if (bar && typeof bar.refresh === 'function') bar.refresh();
  });
}

module.exports = {
  setTabSelected,
  setTabBarHidden,
  refreshAll
};
