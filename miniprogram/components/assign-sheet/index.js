/**
 * 指派 / 改派工程师、调整协助人、申请协助人、转交工单 底部弹层（原型 14，v1.1 多人协作，v1.3 转交）。
 *
 * <assign-sheet show="{{sheetShow}}" order="{{sheetOrder}}" mode="assign" bind:done="onAssigned" bind:close="onSheetClose" />
 *
 * 属性：
 *   show  {boolean}        是否显示；打开时自动 GET /api/engineers
 *   order {OrderListItem}  需要 id、customerName、urgency、deviceText、assignee、assistants
 *   mode  {string}
 *     assign（默认，主管）：单选负责人 + 每行可勾选「协助」（多选）；提交 assign(id, engineerId, assistantIds)；
 *                           改派时预选当前协助人，新负责人若原为协助人则自动从协助人中去掉
 *     assistants（主管）：  只多选协助人，负责人置灰标「负责人」；提交 setAssistants(id, engineerIds)
 *     request（负责人）：   单选 1 名工程师 + 说明；已是负责人 / 协助人的置灰；提交 requestAssistant(id, engineerId, note)
 *     transfer（负责人，v1.3）：单选 1 名接手工程师 + 转交原因（必填）；自己置灰标「负责人」，协助人可选标「协助人」；
 *                           提交 transfer(id, engineerId, reason)，不需要主管批准，对方确认接手后才换人
 * 事件：
 *   done  detail = OrderDetail（接口返回），组件随后自动触发 close
 *   close 点遮罩 / 取消 / 提交成功后；页面应把 show 置为 false
 * 协助人最多 5 人，超出 toast「协助人最多 5 人」。打开时会自动隐藏当前页的自定义 tabBar，关闭后恢复。
 */
const api = require('../../api/index.js');
const enums = require('../../utils/enums.js');
const tabbar = require('../../utils/tabbar.js');

const MAX_ASSISTANTS = 5;

const NOTES = {
  assign: '指派后工单进入“待启动”，被指派人收到钉钉群提醒；协助人可查看工单并在现场打卡，工时按本人计算。',
  assistants: '协助人可查看工单并在现场打卡，工时按本人计算；最多 5 人。正在现场计时的协助人需先离场打卡才能移除。',
  request: '提交后由主管审批，同意后对方成为本单协助人，可查看工单并在现场打卡。',
  transfer: '对方确认接手后成为本单负责人，你将退出本单（只能查看），已有的打卡和工时保留；对方确认前工单仍由你负责，可随时撤回。'
};

const NOTE_PLACEHOLDER = {
  request: '说明（选填），如：需要两人抬设备',
  transfer: '转交原因（必填），如：电气问题需要更熟悉的同事处理'
};

function currentPage() {
  const pages = getCurrentPages();
  return pages[pages.length - 1];
}

function sameSet(a, b) {
  if (a.length !== b.length) return false;
  return a.every((x) => b.indexOf(x) >= 0);
}

Component({
  options: { addGlobalClass: true },
  properties: {
    show: { type: Boolean, value: false },
    order: { type: Object, value: null },
    mode: { type: String, value: 'assign' }
  },
  data: {
    loading: false,
    submitting: false,
    error: '',
    engineers: [],
    kind: 'assign',     // 打开时锁定的 mode
    pickId: 0,
    pickName: '',
    currentId: 0,
    assistIds: [],
    assistCount: 0,
    note: '',
    notePlaceholder: NOTE_PLACEHOLDER.request,
    title: '指派工程师',
    subtitle: '',
    footNote: NOTES.assign,
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
      const kind = NOTES[this.data.mode] ? this.data.mode : 'assign';
      const currentId = (o.assignee && o.assignee.id) || 0;
      const initAssist = (o.assistants || []).map((u) => u.id).filter((id) => id !== currentId);
      this._initAssist = initAssist.slice();
      this._raw = [];
      // 设备一律用服务端的汇总文字 deviceText（v1.3），不自己拼
      const sub = [o.customerName, o.urgency && o.urgency !== 'Normal' ? enums.label(enums.URGENCY, o.urgency) : '', o.deviceText]
        .filter(Boolean).join(' · ');
      let title = currentId ? '改派工程师' : '指派工程师';
      if (kind === 'assistants') title = '调整协助人';
      if (kind === 'request') title = '申请协助人';
      if (kind === 'transfer') title = '转交工单';
      this.setData({
        kind: kind,
        title: title,
        subtitle: sub,
        footNote: NOTES[kind],
        currentId: currentId,
        pickId: 0,
        pickName: '',
        assistIds: kind === 'request' || kind === 'transfer' ? [] : initAssist,
        note: '',
        notePlaceholder: NOTE_PLACEHOLDER[kind] || NOTE_PLACEHOLDER.request,
        error: '',
        engineers: []
      });
      this.updateConfirm();
      this.load();
    },

    load() {
      this.setData({ loading: true, error: '' });
      api.engineers.list({ silent: true }).then((list) => {
        this._raw = (list || []).map((e) => ({
          id: e.user.id,
          name: e.user.name,
          loadText: e.loadText || '',
          onSiteNow: !!e.onSiteNow
        }));
        const patch = { loading: false };
        // 已停用 / 已不是工程师的原协助人不在列表里，无法取消勾选；服务端会因「所选协助人不存在或已停用」拒绝整个提交。
        // 这里把他们从待提交集合中去掉，并提示保存后会从本单移除。
        if (this.data.kind !== 'request' && this.data.kind !== 'transfer') {
          const known = {};
          this._raw.forEach((e) => { known[e.id] = true; });
          const gone = this.data.assistIds.filter((id) => !known[id]);
          if (gone.length) {
            const o = this.data.order || {};
            const names = (o.assistants || []).filter((u) => gone.indexOf(u.id) >= 0).map((u) => u.name).filter(Boolean).join('、');
            patch.assistIds = this.data.assistIds.filter((id) => known[id]);
            patch.footNote = '原协助人' + (names ? ' ' + names + ' ' : '') + '已停用，保存后将从本单移除。' + NOTES[this.data.kind];
          }
        }
        this.setData(patch);
        this.render();
      }).catch((err) => {
        this.setData({ loading: false, error: (err && err.message) || '加载失败' });
      });
    },

    /** 按当前选择重算每行的展示状态 */
    render() {
      const d = this.data;
      const kind = d.kind;
      const o = d.order || {};
      const existAssist = (o.assistants || []).map((u) => u.id);
      const engineers = (this._raw || []).map((e) => {
        const isLead = e.id === d.currentId;
        const checked = d.assistIds.indexOf(e.id) >= 0;
        const row = Object.assign({}, e, { tag: '', disabled: false, radio: false, radioOn: false, check: false, checkOn: false, chip: false, chipOn: false });
        if (kind === 'assign') {
          row.tag = isLead ? '当前处理人' : '';
          row.radio = true;
          row.radioOn = d.pickId === e.id;
          // 被选为负责人的不能再是协助人
          row.chip = d.pickId !== e.id;
          row.chipOn = checked;
        } else if (kind === 'assistants') {
          row.tag = isLead ? '负责人' : '';
          row.disabled = isLead;
          row.check = !isLead;
          row.checkOn = checked;
        } else if (kind === 'transfer') {
          // 转交：自己（当前负责人）置灰；本单协助人可选
          const isAssist = existAssist.indexOf(e.id) >= 0;
          row.tag = isLead ? '负责人' : (isAssist ? '协助人' : '');
          row.disabled = isLead;
          row.radio = !row.disabled;
          row.radioOn = d.pickId === e.id;
        } else {
          const isAssist = existAssist.indexOf(e.id) >= 0;
          row.tag = isLead ? '负责人' : (isAssist ? '已是协助人' : '');
          row.disabled = isLead || isAssist;
          row.radio = !row.disabled;
          row.radioOn = d.pickId === e.id;
        }
        return row;
      });
      this.setData({ engineers: engineers, assistCount: d.assistIds.length });
      this.updateConfirm();
    },

    /** 点整行：assign / request / transfer = 选负责人或目标；assistants = 勾选协助人 */
    onPick(e) {
      const id = Number(e.currentTarget.dataset.id);
      const eng = this.data.engineers.find((x) => x.id === id);
      if (!eng || eng.disabled) return;
      if (this.data.kind === 'assistants') {
        this.toggleAssist(id);
        return;
      }
      const patch = { pickId: id, pickName: eng.name };
      // 新负责人从协助人中去掉（协助人不能是负责人）
      if (this.data.kind === 'assign') patch.assistIds = this.data.assistIds.filter((x) => x !== id);
      this.setData(patch);
      this.render();
    },

    /** assign 模式行内「协助」开关 */
    onToggleAssist(e) {
      this.toggleAssist(Number(e.currentTarget.dataset.id));
    },

    toggleAssist(id) {
      const ids = this.data.assistIds.slice();
      const i = ids.indexOf(id);
      if (i >= 0) {
        ids.splice(i, 1);
      } else {
        if (ids.length >= MAX_ASSISTANTS) {
          wx.showToast({ title: '协助人最多 5 人', icon: 'none' });
          return;
        }
        ids.push(id);
      }
      this.setData({ assistIds: ids });
      this.render();
    },

    onNote(e) {
      this.setData({ note: e.detail.value });
    },

    updateConfirm() {
      const d = this.data;
      let label = '请选择工程师';
      let can = false;
      if (d.kind === 'assistants') {
        const changed = !sameSet(d.assistIds, this._initAssist || []);
        label = d.assistIds.length ? '保存协助人（' + d.assistIds.length + ' 人）' : ((this._initAssist || []).length ? '清空协助人' : '请选择协助人');
        can = changed;
        if (!changed && d.assistIds.length) label = '协助人未变化';
      } else if (d.kind === 'request') {
        if (d.pickId) {
          label = '申请添加 ' + d.pickName + ' 为协助人';
          can = true;
        }
      } else if (d.kind === 'transfer') {
        label = '请选择接手工程师';
        if (d.pickId) {
          label = '转交给 ' + d.pickName;
          can = true;
        }
      } else if (d.pickId && d.pickId === d.currentId) {
        label = d.pickName + ' 是当前处理人';
      } else if (d.pickId) {
        label = '指派给 ' + d.pickName + (d.assistIds.length ? '（协助 ' + d.assistIds.length + ' 人）' : '');
        can = true;
      }
      this.setData({ confirmLabel: label, canConfirm: can });
    },

    onConfirm() {
      const d = this.data;
      if (!d.canConfirm || d.submitting || !d.order) return;
      let promise;
      let okText;
      if (d.kind === 'assistants') {
        promise = api.orders.setAssistants(d.order.id, d.assistIds, { loading: '保存中' });
        okText = '协助人已更新';
      } else if (d.kind === 'request') {
        const note = (d.note || '').trim();
        if (note.length > 200) {
          wx.showToast({ title: '说明不能超过 200 个字', icon: 'none' });
          return;
        }
        promise = api.orders.requestAssistant(d.order.id, d.pickId, note, { loading: '提交中' });
        okText = '已提交申请，等待主管审批';
      } else if (d.kind === 'transfer') {
        const reason = (d.note || '').trim();
        if (!reason) {
          wx.showToast({ title: '请填写转交原因', icon: 'none' });
          return;
        }
        if (reason.length > 200) {
          wx.showToast({ title: '转交原因不能超过 200 个字', icon: 'none' });
          return;
        }
        promise = api.orders.transfer(d.order.id, d.pickId, reason, { loading: '提交中' });
        okText = '已发起转交，等待 ' + d.pickName + ' 确认';
      } else {
        promise = api.orders.assign(d.order.id, d.pickId, d.assistIds.filter((x) => x !== d.pickId), { loading: '指派中' });
        okText = '已指派给 ' + d.pickName;
      }
      this.setData({ submitting: true });
      promise.then((detail) => {
        this.setData({ submitting: false });
        wx.showToast({ title: okText, icon: 'none' });
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
