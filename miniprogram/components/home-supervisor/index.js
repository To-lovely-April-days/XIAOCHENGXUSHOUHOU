/**
 * 主管首页（原型 14）。数据：GET /api/home/supervisor；指派/改派用 <assign-sheet>。
 * 页面调用 refresh() → Promise；事件 meta：{ user }。
 */
const api = require('../../api/index.js');
const format = require('../../utils/format.js');

const START_H = 8;
const END_H = 20;
const SPAN = END_H - START_H;
const TICKS = [8, 10, 12, 14, 16, 18, 20].map((h) => ({ h: h, left: ((h - START_H) / SPAN * 100).toFixed(2) + '%' }));
const GRID = [10, 12, 14, 16, 18].map((h) => ({ h: h, left: ((h - START_H) / SPAN * 100).toFixed(2) + '%' }));

function hourOf(d) {
  return d.getHours() + d.getMinutes() / 60 + d.getSeconds() / 3600;
}

function pct(h) {
  return Math.min(100, Math.max(0, (h - START_H) / SPAN * 100));
}

function greetingOf(d) {
  const h = d.getHours();
  if (h < 5) return '夜深了';
  if (h < 9) return '早上好';
  if (h < 11) return '上午好';
  if (h < 13) return '中午好';
  if (h < 18) return '下午好';
  return '晚上好';
}

function buildRow(row, now, idx) {
  const segs = (row.segments || []).map((s, i) => {
    const a = format.parseTime(s.start);
    if (!a) return null;
    const b = s.end ? format.parseTime(s.end) : now;
    const l = pct(hourOf(a));
    const r = pct(b ? hourOf(b) : hourOf(now));
    return {
      i: i,
      orderId: s.orderId,
      left: l.toFixed(2) + '%',
      width: Math.max(r - l, 0.8).toFixed(2) + '%',
      open: !s.end,
      text: (s.customerName || '') + '，' + format.formatHm(a) + ' 到场' + (s.end ? '，' + format.formatHm(s.end) + ' 离场' : '，现场中')
    };
  }).filter(Boolean);
  const name = (row.engineer && row.engineer.name) || '';
  return {
    key: (row.engineer && row.engineer.id) || 'r' + idx,
    engineer: row.engineer,
    name: name,
    segs: segs,
    empty: segs.length ? '' : (row.note || '今日无打卡'),
    aria: name + '：' + (segs.length ? segs.map((s) => s.text).join('；') : (row.note || '今日无打卡'))
  };
}

function buildTodo(t, i) {
  const map = {
    // v1.1：审批 / 定价都在工单详情里完成
    HoldRequest: { btn: '审批', act: 'detail', tone: 'amber', suffix: '暂停申请', primary: true },
    AssistantRequest: { btn: '审批', act: 'detail', tone: 'amber', suffix: '协助人申请', primary: true },
    PriceSuggestion: { btn: '定价', act: 'detail', tone: 'amber', suffix: '价格建议' },
    Unassigned: { btn: '指派', act: 'assign', tone: 'amber' },
    Submitted: { btn: '查看', act: 'detail', tone: 'gray', suffix: '待验收' },
    MissingPunch: { btn: '去后台', act: 'admin', tone: 'amber' },
    DistanceAbnormal: { btn: '看照片', act: 'detail', tone: 'amber' }
  };
  const m = map[t.type] || { btn: '查看', act: 'detail', tone: 'gray' };
  return Object.assign({}, t, {
    key: t.type + '-' + t.orderId + '-' + (t.requestId || 0) + '-' + i,
    btn: m.btn,
    act: m.act,
    tone: m.tone,
    suffix: m.suffix || '',
    primary: m.act === 'assign' || !!m.primary
  });
}

Component({
  options: { addGlobalClass: true },

  data: {
    status: 'loading',
    errorText: '',
    greeting: '',
    onSiteCount: 0,
    todoCount: 0,
    nowText: '',
    nowLeft: '',
    showNow: false,
    rows: [],
    ticks: TICKS,
    grid: GRID,
    todos: [],
    sheetShow: false,
    sheetOrder: null
  },

  lifetimes: {
    attached() { this._assigned = {}; }
  },

  methods: {
    /** keepAssigned：指派成功后刷新时保留「已指派」行 */
    refresh(keepAssigned) {
      if (!keepAssigned) this._assigned = {};
      const first = this.data.status !== 'ok';
      if (first) this.setData({ status: 'loading', errorText: '' });
      return api.home.supervisor({ silent: true }).then((d) => {
        this.apply(d || {});
      }).catch((err) => {
        const msg = (err && err.message) || '加载失败，请稍后重试';
        if (first) this.setData({ status: 'error', errorText: msg });
        else wx.showToast({ title: msg, icon: 'none' });
      });
    },

    apply(d) {
      if (d.now) format.syncServerTime(d.now);
      const now = format.parseTime(d.now) || format.now();
      const nowH = hourOf(now);
      const todos = (d.todos || []).map(buildTodo);

      // 本次会话里刚指派、已不在待办中的工单，保留一行「已指派」
      const assigned = this._assigned || {};
      Object.keys(assigned).forEach((id) => {
        const a = assigned[id];
        const idx = todos.findIndex((t) => t.type === 'Unassigned' && String(t.orderId) === id);
        const row = Object.assign({}, a.todo, {
          assigned: true,
          subtitle: '已指派给 ' + a.name + ' · 待启动',
          btn: '改派',
          primary: false,
          tone: 'gray',
          key: 'assigned-' + id
        });
        if (idx >= 0) todos[idx] = row;
        else todos.splice(Math.min(a.pos, todos.length), 0, row);
      });

      this.setData({
        status: 'ok',
        greeting: greetingOf(now) + '，' + ((d.user && d.user.name) || ''),
        onSiteCount: d.onSiteCount || 0,
        todoCount: todos.filter((t) => !t.assigned).length,
        nowText: format.formatHm(now),
        nowLeft: pct(nowH).toFixed(2) + '%',
        showNow: nowH >= START_H && nowH <= END_H,
        rows: (d.timeline || []).map((r, i) => buildRow(r, now, i)),
        todos: todos
      });
      this.triggerEvent('meta', { user: d.user || null });
    },

    onRetry() { this.refresh(); },

    toDetail(e) {
      const id = e.currentTarget.dataset.id;
      if (id) wx.navigateTo({ url: '/pages/order-detail/index?id=' + id });
    },

    onTodo(e) {
      const t = this.data.todos[e.currentTarget.dataset.index];
      if (!t) return;
      if (t.act === 'assign') return this.openAssign(t);
      if (t.act === 'admin') {
        wx.showModal({
          title: '请在电脑后台补录',
          content: '补录打卡需在电脑端主管后台操作（需填写补录原因）。可先查看工单详情与打卡记录。',
          confirmText: '查看详情',
          cancelText: '知道了',
          confirmColor: '#1D1D1F',
          success: (r) => {
            if (r.confirm) wx.navigateTo({ url: '/pages/order-detail/index?id=' + t.orderId });
          }
        });
        return;
      }
      wx.navigateTo({ url: '/pages/order-detail/index?id=' + t.orderId });
    },

    /** 取工单详情（assign-sheet 需要 assignee / deviceText / urgency） */
    openAssign(t) {
      if (this._opening) return;
      this._opening = true;
      api.orders.detail(t.orderId, { loading: true }).then((o) => {
        this._sheetTodo = t;
        this.setData({ sheetOrder: o, sheetShow: true });
      }).catch(() => {}).then(() => { this._opening = false; });
    },

    onSheetClose() { this.setData({ sheetShow: false }); },

    onAssigned(e) {
      const o = e.detail || {};
      const t = this._sheetTodo;
      if (t && o.id) {
        const pos = this.data.todos.findIndex((x) => x.key === t.key);
        this._assigned[String(o.id)] = {
          name: (o.assignee && o.assignee.name) || '',
          todo: t,
          pos: pos < 0 ? 0 : pos
        };
      }
      // 「已指派给 X」提示由 assign-sheet 弹出，这里不再覆盖
      this.refresh(true);
    }
  }
});
