/**
 * 指派 / 改派工程师底部弹层（原型 14）。
 *
 * <assign-sheet show="{{sheetShow}}" order="{{sheetOrder}}" bind:done="onAssigned" bind:close="onSheetClose" />
 *
 * 属性：
 *   show  {boolean}        是否显示；打开时自动 GET /api/engineers
 *   order {OrderListItem}  需要 id、customerName、urgency、deviceModel、assignee
 * 事件：
 *   done  detail = OrderDetail（POST /api/orders/{id}/assign 的返回），组件随后自动触发 close
 *   close 点遮罩 / 取消 / 指派成功后；页面应把 show 置为 false
 * 打开时会自动隐藏当前页的自定义 tabBar，关闭后恢复。
 */
const api = require('../../api/index.js');
const enums = require('../../utils/enums.js');
const tabbar = require('../../utils/tabbar.js');

function currentPage() {
  const pages = getCurrentPages();
  return pages[pages.length - 1];
}

Component({
  options: { addGlobalClass: true },
  properties: {
    show: { type: Boolean, value: false },
    order: { type: Object, value: null }
  },
  data: {
    loading: false,
    submitting: false,
    error: '',
    engineers: [],
    pickId: 0,
    pickName: '',
    currentId: 0,
    title: '指派工程师',
    subtitle: '',
    confirmLabel: '请选择工程师',
    canConfirm: false
  },
  observers: {
    // 用数据观察器：保证与 order 同一次 setData 时能读到新 order
    show(show) {
      show = !!show;
      if (show === this._opened) return;
      this._opened = show;
      tabbar.setTabBarHidden(currentPage(), show);
      if (show) this.open();
    }
  },
  methods: {

    open() {
      const o = this.data.order || {};
      const currentId = (o.assignee && o.assignee.id) || 0;
      const sub = [o.customerName, o.urgency && o.urgency !== 'Normal' ? enums.label(enums.URGENCY, o.urgency) : '', o.deviceModel]
        .filter(Boolean).join(' · ');
      this.setData({
        title: currentId ? '改派工程师' : '指派工程师',
        subtitle: sub,
        currentId: currentId,
        pickId: 0,
        pickName: '',
        error: '',
        engineers: []
      });
      this.updateConfirm();
      this.load();
    },

    load() {
      this.setData({ loading: true, error: '' });
      api.engineers.list({ silent: true }).then((list) => {
        const engineers = (list || []).map((e) => ({
          id: e.user.id,
          name: e.user.name,
          loadText: e.loadText || '',
          onSiteNow: !!e.onSiteNow,
          current: e.user.id === this.data.currentId
        }));
        this.setData({ engineers: engineers, loading: false });
      }).catch((err) => {
        this.setData({ loading: false, error: (err && err.message) || '加载失败' });
      });
    },

    onPick(e) {
      const id = Number(e.currentTarget.dataset.id);
      const eng = this.data.engineers.find((x) => x.id === id);
      if (!eng) return;
      this.setData({ pickId: id, pickName: eng.name });
      this.updateConfirm();
    },

    updateConfirm() {
      const d = this.data;
      let label = '请选择工程师';
      let can = false;
      if (d.pickId && d.pickId === d.currentId) {
        label = d.pickName + ' 是当前处理人';
      } else if (d.pickId) {
        label = '指派给 ' + d.pickName;
        can = true;
      }
      this.setData({ confirmLabel: label, canConfirm: can });
    },

    onConfirm() {
      const d = this.data;
      if (!d.canConfirm || d.submitting || !d.order) return;
      this.setData({ submitting: true });
      api.orders.assign(d.order.id, d.pickId, { loading: '指派中' }).then((detail) => {
        this.setData({ submitting: false });
        wx.showToast({ title: '已指派给 ' + d.pickName, icon: 'none' });
        this.triggerEvent('done', detail);
        this.close();
      }).catch(() => {
        this.setData({ submitting: false });
      });
    },

    close() {
      this.triggerEvent('close');
    },

    noop() {}
  },
  pageLifetimes: {
    show() {
      if (this.data.show) tabbar.setTabBarHidden(currentPage(), true);
    }
  },
  lifetimes: {
    detached() {
      if (this.data.show) tabbar.setTabBarHidden(currentPage(), false);
    }
  }
});
