/**
 * 自定义 tabBar，按 globalData.user.role 显示：
 *   Engineer：首页 / 工单 / 打卡(中间红色相机，navigateTo 打卡页) / 工时 / 我的
 *   Sales、Supervisor：首页 / 工单 / 新建(中间黑色加号，navigateTo 新建工单) / 我的
 * 选中项由页面 onShow 调 utils/tabbar.js 的 setTabSelected(this, key) 设置。
 */
const icons = require('../utils/icons.js');

const ON = '#1D1D1F';
const OFF = '#AEAEB2';

const TABS = {
  home: { key: 'home', text: '首页', icon: 'home', path: '/pages/home/index' },
  orders: { key: 'orders', text: '工单', icon: 'orders', path: '/pages/orders/index' },
  hours: { key: 'hours', text: '工时', icon: 'clock', path: '/pages/hours/index' },
  me: { key: 'me', text: '我的', icon: 'user', path: '/pages/me/index' }
};

const CENTER = {
  checkin: { key: 'checkin', text: '打卡', center: true, icon: 'camera', path: '/pages/checkin/index', btnBg: '#C81E2B', labelColor: '#C81E2B', shadow: 'rgba(200, 30, 43, 0.30)' },
  create: { key: 'create', text: '新建', center: true, icon: 'plus', path: '/pages/order-new/index', btnBg: '#1D1D1F', labelColor: '#1D1D1F', shadow: 'rgba(0, 0, 0, 0.22)' }
};

function decorate(t) {
  if (t.center) return Object.assign({}, t, { iconSrc: icons.get(t.icon, '#FFFFFF', 2.2) });
  return Object.assign({}, t, { iconOn: icons.get(t.icon, ON, 2), iconOff: icons.get(t.icon, OFF, 1.8) });
}

function buildItems(role) {
  if (!role) return [];
  const list = role === 'Engineer'
    ? [TABS.home, TABS.orders, CENTER.checkin, TABS.hours, TABS.me]
    // 4 项时「我的」占两格，让中间的新建按钮正好落在屏幕正中
    : [TABS.home, TABS.orders, CENTER.create, Object.assign({ wide: true }, TABS.me)];
  return list.map(decorate);
}

Component({
  data: {
    role: '',
    items: [],
    selected: '',
    hidden: false
  },
  lifetimes: {
    attached() {
      this.refresh();
    }
  },
  pageLifetimes: {
    show() {
      this.refresh();
    }
  },
  methods: {
    /** 根据当前角色重建 tab 项（角色未变化时不 setData） */
    refresh() {
      const app = getApp();
      const user = app && app.globalData && app.globalData.user;
      const role = (user && user.role) || '';
      if (role === this.data.role && this.data.items.length) return;
      this.setData({ role: role, items: buildItems(role) });
    },

    onTap(e) {
      const item = this.data.items[e.currentTarget.dataset.index];
      if (!item) return;
      if (item.center) {
        wx.navigateTo({ url: item.path });
        return;
      }
      if (item.key === this.data.selected) return;
      wx.switchTab({ url: item.path });
    }
  }
});
