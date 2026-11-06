/**
 * 自定义导航栏（页面 json 需设 "navigationStyle": "custom"）。
 * 状态栏高度 + 与胶囊按钮垂直居中对齐，默认 fixed 并自带等高占位。
 *
 * 属性：
 *   title       {string}  标题
 *   back        {boolean} 是否显示返回按钮，默认 true（页面栈只有 1 层时显示“首页”图标）
 *   background  {string}  背景色，默认 #F6F6F4（传 transparent 可透明）
 *   color       {string}  文字/图标颜色，默认 #1D1D1F
 *   fixed       {boolean} 是否固定在顶部，默认 true
 *   placeholder {boolean} fixed 时是否渲染等高占位，默认 true
 *   customBack  {boolean} true 时点返回只触发 bind:back，不自动跳转
 * 事件：bind:back
 * 插槽：left（返回按钮右侧）、right（胶囊左侧，宽度有限）、默认插槽（标题位置，title 为空时使用）
 */
const system = require('../../utils/system.js');
const icons = require('../../utils/icons.js');

const HOME = '/pages/home/index';

Component({
  options: { multipleSlots: true, addGlobalClass: true },
  properties: {
    title: { type: String, value: '' },
    back: { type: Boolean, value: true },
    background: { type: String, value: '#F6F6F4' },
    color: { type: String, value: '#1D1D1F', observer: 'updateIcon' },
    fixed: { type: Boolean, value: true },
    placeholder: { type: Boolean, value: true },
    customBack: { type: Boolean, value: false }
  },
  data: {
    statusBarHeight: 20,
    navBarHeight: 44,
    totalHeight: 64,
    sideWidth: 94,
    isRoot: false,
    backIcon: '',
    homeIcon: ''
  },
  lifetimes: {
    attached() {
      const m = system.getNavMetrics();
      this.setData({
        statusBarHeight: m.statusBarHeight,
        navBarHeight: m.navBarHeight,
        totalHeight: m.totalHeight,
        sideWidth: m.sideWidth,
        isRoot: getCurrentPages().length <= 1
      });
      this.updateIcon();
    }
  },
  methods: {
    updateIcon() {
      const c = this.data.color || '#1D1D1F';
      this.setData({ backIcon: icons.get('back', c, 2.2), homeIcon: icons.get('home', c, 2) });
    },
    onBack() {
      this.triggerEvent('back');
      if (this.data.customBack) return;
      if (getCurrentPages().length > 1) {
        wx.navigateBack({ fail: () => wx.reLaunch({ url: HOME }) });
      } else {
        wx.reLaunch({ url: HOME });
      }
    }
  }
});
