/**
 * 状态栏 / 胶囊按钮尺寸（自定义导航用，结果缓存）。
 * @returns {{statusBarHeight:number, navBarHeight:number, totalHeight:number, windowWidth:number,
 *   menuWidth:number, menuHeight:number, menuRightGap:number, sideWidth:number}} 单位 px
 *   navBarHeight：状态栏以下、与胶囊垂直居中对齐的导航高度；sideWidth：左右对称预留宽度（保证标题居中且不压胶囊）
 */
let cached = null;

function getNavMetrics() {
  if (cached) return cached;
  let win = {};
  try {
    win = wx.getWindowInfo ? wx.getWindowInfo() : wx.getSystemInfoSync();
  } catch (e) { /* ignore */ }
  const statusBarHeight = win.statusBarHeight || 20;
  const windowWidth = win.windowWidth || 375;
  let menu = null;
  try { menu = wx.getMenuButtonBoundingClientRect(); } catch (e) { /* ignore */ }
  let navBarHeight = 44;
  let menuWidth = 87;
  let menuHeight = 32;
  let menuRightGap = 7;
  if (menu && menu.top && menu.height) {
    navBarHeight = (menu.top - statusBarHeight) * 2 + menu.height;
    menuWidth = menu.width;
    menuHeight = menu.height;
    menuRightGap = windowWidth - menu.right;
  }
  cached = {
    statusBarHeight,
    navBarHeight,
    totalHeight: statusBarHeight + navBarHeight,
    windowWidth,
    menuWidth,
    menuHeight,
    menuRightGap,
    sideWidth: menuWidth + menuRightGap * 2
  };
  return cached;
}

module.exports = { getNavMetrics };
