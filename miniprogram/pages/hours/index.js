/**
 * 我的工时（原型 04，tab，仅工程师）。
 * 按天：GET /api/hours/today；按月：GET /api/hours/month。计时中每秒刷新（时间以服务器为准）。
 */
const api = require('../../api/index.js');
const auth = require('../../utils/auth.js');
const tabbar = require('../../utils/tabbar.js');
const format = require('../../utils/format.js');
const system = require('../../utils/system.js');

const WEEK_HEAD = ['一', '二', '三', '四', '五', '六', '日'];

/** 热力图底色：按分钟数线性加深（原型：2:25→0.40，7:10→0.82） */
function heat(minutes) {
  const a = Math.min(0.92, Math.max(0.26, 0.19 + minutes * 0.00147));
  return { bg: 'rgba(200, 30, 43, ' + a.toFixed(2) + ')', fg: a >= 0.5 ? '#FFFFFF' : '#1D1D1F' };
}

/** 时刻 → 12 小时表盘角度（弧度，12 点为 -π/2） */
function dialAngle(d) {
  const h = (d.getHours() % 12) + d.getMinutes() / 60 + d.getSeconds() / 3600;
  return (h / 12) * Math.PI * 2 - Math.PI / 2;
}

function punchSide(p, kind) {
  if (!p) return null;
  const label = kind === 'Arrive' ? '到场' : '离场';
  const hasDist = p.distanceMeters !== null && p.distanceMeters !== undefined;
  let distText = '';
  if (hasDist) distText = '距客户 ' + format.formatDistance(p.distanceMeters) + (p.outOfRange ? ' · 距离异常' : ' · 在范围内');
  return {
    title: label + ' ' + format.formatHm(p.time),
    photo: (p.photo && p.photo.url) || '',
    distText: p.isSupplement ? '主管补录' + (p.supplementReason ? ' · ' + p.supplementReason : '') : distText,
    distClass: p.isSupplement ? 'amber' : (p.outOfRange ? 'red' : ''),
    isSupplement: !!p.isSupplement
  };
}

Page({
  data: {
    statusBarHeight: 20,
    navBarHeight: 44,
    sideWidth: 94,
    role: '',
    loginError: '',
    isEngineer: false,
    tab: 'day',

    // 按天
    todayLoading: true,
    todayError: '',
    todayLabel: '',
    todayHm: '0:00',
    todaySs: '',
    running: false,
    runningOrderId: 0,
    entries: [],

    // 按月
    year: 0,
    month: 0,
    monthLabel: '',
    canNext: false,
    monthLoading: true,
    monthError: '',
    monthTotal: '0:00',
    tripDays: 0,
    weekHead: WEEK_HEAD,
    cells: [],
    days: []
  },

  onLoad() {
    const m = system.getNavMetrics();
    const n = format.now();
    this.setData({
      statusBarHeight: m.statusBarHeight,
      navBarHeight: m.navBarHeight,
      sideWidth: m.sideWidth,
      year: n.getFullYear(),
      month: n.getMonth() + 1,
      monthLabel: n.getFullYear() + '年' + (n.getMonth() + 1) + '月'
    });
    this.today = null;
    this.monthData = null;
  },

  onShow() {
    tabbar.setTabSelected(this, 'hours');
    return this.login();
  },

  /** ensureLogin 后加载；网络/服务端异常时展示错误态（可重试） */
  login() {
    return auth.ensureLogin().then((user) => {
      tabbar.setTabSelected(this, 'hours');
      const isEngineer = user.role === 'Engineer';
      this.setData({ role: user.role, isEngineer: isEngineer, loginError: '' });
      if (isEngineer) return this.loadAll();
    }).catch((err) => {
      if (auth.isLoadFailed(err) && !this.data.role) this.setData({ loginError: err.message || '网络异常，请重试' });
    });
  },

  onLoginRetry() {
    this.setData({ loginError: '' });
    this.login();
  },

  onHide() {
    this.stopTimer();
  },

  onUnload() {
    this.stopTimer();
  },

  onPullDownRefresh() {
    if (!this.data.role) {
      this.login().then(() => wx.stopPullDownRefresh(), () => wx.stopPullDownRefresh());
      return;
    }
    if (!this.data.isEngineer) {
      wx.stopPullDownRefresh();
      return;
    }
    this.loadAll().then(() => wx.stopPullDownRefresh());
  },

  loadAll() {
    return Promise.all([this.loadToday(), this.loadMonth()]);
  },

  /* ---------- 按天 ---------- */

  loadToday() {
    if (!this.today) this.setData({ todayLoading: true });
    return api.hours.today(null, { silent: true }).then((d) => {
      this.today = d || {};
      if (d && d.serverTime) format.syncServerTime(d.serverTime);
      this.setData({ todayError: '', todayLoading: false }, () => this.buildToday());
      this.syncTimer();
    }).catch((err) => {
      this.setData({ todayLoading: false, todayError: (err && err.message) || '加载失败' });
    });
  },

  buildToday() {
    const d = this.today || {};
    const list = d.entries || [];
    let runningOrderId = 0;
    const entries = list.map((e, i) => {
      const isRunning = !!(d.running && !e.leave);
      if (isRunning) runningOrderId = e.orderId;
      return {
        key: (e.arrive && e.arrive.id) || ('e' + i),
        orderId: e.orderId,
        orderNo: e.orderNo || '',
        customerName: e.customerName || '',
        running: isRunning,
        minutes: e.minutes,
        dur: isRunning ? format.formatDuration(format.secondsBetween(e.arrive && e.arrive.time) / 60) : format.formatDuration(e.minutes),
        isSupplement: !!e.isSupplement,
        arrive: punchSide(e.arrive, 'Arrive'),
        leave: punchSide(e.leave, 'Leave')
      };
    });
    const date = d.date || format.today();
    this.setData({
      todayLabel: format.formatMonthDay(date) + ' ' + format.weekday(date),
      running: !!d.running,
      runningOrderId: runningOrderId,
      entries: entries
    }, () => {
      this.lastDrawMinute = null; // 数据变化后强制重画表盘
      this.tickToday();
    });
  },

  /** 今日合计 = 已配对 + 正在计时 */
  tickToday() {
    const d = this.today;
    if (!d) return;
    let sec = (d.closedMinutes || 0) * 60;
    if (d.running && d.runningSince) sec += format.secondsBetween(d.runningSince);
    const clock = format.splitClock(sec);
    const patch = { todayHm: clock.hm, todaySs: d.running ? clock.ss : '' };
    if (d.running) {
      this.data.entries.forEach((e, i) => {
        if (!e.running) return;
        const src = (d.entries || [])[i];
        patch['entries[' + i + '].dur'] = format.formatDuration(format.secondsBetween(src && src.arrive && src.arrive.time) / 60);
      });
    }
    this.setData(patch);
    const minuteKey = Math.floor(format.now().getTime() / 60000);
    if (minuteKey !== this.lastDrawMinute) this.drawClock();
  },

  /* ---------- 表盘（canvas 2d） ---------- */

  drawClock() {
    if (this.data.tab !== 'day' || !this.data.isEngineer) return;
    const q = wx.createSelectorQuery().in(this);
    q.select('#clock').fields({ node: true, size: true }).exec((res) => {
      const r = res && res[0];
      if (!r || !r.node || !r.width) return;
      this.lastDrawMinute = Math.floor(format.now().getTime() / 60000);
      let dpr = 2;
      try { dpr = (wx.getWindowInfo ? wx.getWindowInfo() : wx.getSystemInfoSync()).pixelRatio || 2; } catch (e) { /* ignore */ }
      const canvas = r.node;
      canvas.width = r.width * dpr;
      canvas.height = r.height * dpr;
      const ctx = canvas.getContext('2d');
      const s = (r.width / 140) * dpr; // 以原型 140×140 为坐标系
      ctx.setTransform(s, 0, 0, s, 0, 0);
      this.paintClock(ctx);
    });
  },

  paintClock(ctx) {
    const C = 70;
    const R = 58;
    ctx.clearRect(0, 0, 140, 140);
    ctx.lineCap = 'round';

    // 轨道
    ctx.beginPath();
    ctx.arc(C, C, R, 0, Math.PI * 2);
    ctx.strokeStyle = '#E8E8E6';
    ctx.lineWidth = 8;
    ctx.stroke();

    // 刻度
    for (let i = 0; i < 60; i++) {
      const a = (i / 60) * Math.PI * 2 - Math.PI / 2;
      const major = i % 5 === 0;
      const r1 = major ? 39 : 44;
      const r2 = 48;
      ctx.beginPath();
      ctx.moveTo(C + Math.cos(a) * r1, C + Math.sin(a) * r1);
      ctx.lineTo(C + Math.cos(a) * r2, C + Math.sin(a) * r2);
      ctx.strokeStyle = major ? '#8E8E93' : '#CFCFCD';
      ctx.lineWidth = major ? 2 : 1;
      ctx.stroke();
    }

    // 数字
    ctx.fillStyle = '#6E6E73';
    ctx.font = '600 12px sans-serif';
    ctx.textAlign = 'center';
    ctx.textBaseline = 'alphabetic';
    ctx.fillText('12', C, 46);
    ctx.fillText('3', 98, 74);
    ctx.fillText('6', C, 102);
    ctx.fillText('9', 42, 74);

    // 今日在现场的弧（超过 12 小时按 12 小时画）
    const now = format.now();
    const ends = [];
    ((this.today && this.today.entries) || []).forEach((e) => {
      const a = format.parseTime(e.arrive && e.arrive.time);
      if (!a) return;
      const l = e.leave ? format.parseTime(e.leave.time) : now;
      if (!l || l <= a) return;
      const start = dialAngle(a);
      const span = Math.min((l - a) / 43200000, 0.999) * Math.PI * 2;
      ctx.beginPath();
      ctx.arc(C, C, R, start, start + span);
      ctx.strokeStyle = '#C81E2B';
      ctx.lineWidth = 8;
      ctx.stroke();
      ends.push({ start: start, end: start + span, open: !e.leave });
    });
    ends.forEach((p) => {
      ctx.beginPath();
      ctx.arc(C + Math.cos(p.start) * R, C + Math.sin(p.start) * R, 5, 0, Math.PI * 2);
      ctx.fillStyle = '#FFFFFF';
      ctx.fill();
      ctx.strokeStyle = '#1D1D1F';
      ctx.lineWidth = 2.5;
      ctx.stroke();
      if (p.open) {
        ctx.beginPath();
        ctx.arc(C + Math.cos(p.end) * R, C + Math.sin(p.end) * R, 6.5, 0, Math.PI * 2);
        ctx.fillStyle = '#FFFFFF';
        ctx.fill();
        ctx.strokeStyle = '#C81E2B';
        ctx.lineWidth = 3;
        ctx.stroke();
      }
    });

    // 指针
    const ha = dialAngle(now);
    const ma = ((now.getMinutes() + now.getSeconds() / 60) / 60) * Math.PI * 2 - Math.PI / 2;
    ctx.beginPath();
    ctx.moveTo(C, C);
    ctx.lineTo(C + Math.cos(ha) * 26.7, C + Math.sin(ha) * 26.7);
    ctx.strokeStyle = '#1D1D1F';
    ctx.lineWidth = 4;
    ctx.stroke();
    ctx.beginPath();
    ctx.moveTo(C, C);
    ctx.lineTo(C + Math.cos(ma) * 40.6, C + Math.sin(ma) * 40.6);
    ctx.strokeStyle = '#6E6E73';
    ctx.lineWidth = 2.5;
    ctx.stroke();
    ctx.beginPath();
    ctx.arc(C, C, 4.5, 0, Math.PI * 2);
    ctx.fillStyle = '#C81E2B';
    ctx.fill();
    ctx.strokeStyle = '#FFFFFF';
    ctx.lineWidth = 1.5;
    ctx.stroke();
  },

  /* ---------- 按月 ---------- */

  loadMonth() {
    const y = this.data.year;
    const m = this.data.month;
    if (!this.monthData || this.monthData.year !== y || this.monthData.month !== m) {
      this.setData({ monthLoading: true });
    }
    return api.hours.month(y, m, null, { silent: true }).then((d) => {
      if (this.data.year !== y || this.data.month !== m) return; // 已切换到别的月份
      this.monthData = d || { year: y, month: m, days: [] };
      this.buildMonth();
      this.setData({ monthLoading: false, monthError: '' });
      this.syncTimer();
    }).catch((err) => {
      if (this.data.year !== y || this.data.month !== m) return;
      this.monthData = null;
      this.setData({ monthLoading: false, monthError: (err && err.message) || '加载失败', cells: [], days: [] });
    });
  },

  /** 计时中的秒数（当天未离场且非缺卡的到场） */
  runningSecondsOf(day) {
    if (!day || !day.running) return 0;
    let sec = 0;
    (day.entries || []).forEach((e) => {
      if (!e.leaveTime && !e.missing && e.arriveTime) sec += format.secondsBetween(e.arriveTime);
    });
    return sec;
  },

  buildMonth() {
    const d = this.monthData || {};
    const y = this.data.year;
    const m = this.data.month;
    const byDate = {};
    (d.days || []).forEach((day) => { byDate[day.date] = day; });

    const todayStr = format.today();
    const first = new Date(y, m - 1, 1);
    const daysInMonth = new Date(y, m, 0).getDate();
    const lead = (first.getDay() + 6) % 7; // 周一为第一列
    const cells = [];
    for (let i = 0; i < lead; i++) cells.push({ key: 'b' + i, blank: true });
    for (let n = 1; n <= daysInMonth; n++) {
      const ds = y + '-' + format.pad2(m) + '-' + format.pad2(n);
      const day = byDate[ds];
      const minutes = day ? (day.minutes || 0) + Math.floor(this.runningSecondsOf(day) / 60) : 0;
      let bg = 'transparent';
      let fg = '#AEAEB2';
      if (minutes > 0) {
        const h = heat(minutes);
        bg = h.bg;
        fg = h.fg;
      } else if (ds <= todayStr) {
        bg = '#EFEFED';
        fg = '#6E6E73';
      }
      cells.push({ key: ds, day: n, bg: bg, fg: fg, today: ds === todayStr, dot: !!(day && day.hasSupplement) });
    }

    const days = (d.days || []).map((day) => this.buildDayRow(day));
    let total = (d.totalMinutes || 0) * 60;
    (d.days || []).forEach((day) => { total += this.runningSecondsOf(day); });

    this.setData({
      monthLabel: y + '年' + m + '月',
      canNext: this.canGoNext(),
      monthTotal: format.formatDuration(total / 60),
      tripDays: d.tripDays || 0,
      cells: cells,
      days: days
    });
  },

  buildDayRow(day) {
    const entries = day.entries || [];
    const names = [];
    entries.forEach((e) => { if (e.customerName && names.indexOf(e.customerName) < 0) names.push(e.customerName); });
    const missing = entries.filter((e) => e.missing);
    const sup = entries.filter((e) => e.isSupplement);
    const open = entries.filter((e) => !e.leaveTime && !e.missing);
    let note;
    let noteClass = '';
    if (missing.length) {
      note = format.formatHm(missing[0].arriveTime) + ' 到场 · 缺卡 · 未计工时';
      noteClass = 'red';
    } else if (sup.length) {
      note = '离场时间由主管补录' + (sup[0].supplementReason ? ' · 原因：' + sup[0].supplementReason : '');
      noteClass = 'amber';
    } else if (day.running && open.length) {
      note = format.formatHm(open[0].arriveTime) + ' 到场 · 计时中';
    } else {
      note = entries.map((e) => format.formatHm(e.arriveTime) + ' 到场 – ' + format.formatHm(e.leaveTime) + ' 离场').join('；');
    }
    const live = (day.minutes || 0) + this.runningSecondsOf(day) / 60;
    return {
      date: day.date,
      week: format.weekday(day.date),
      day: format.parseTime(day.date).getDate(),
      customer: names.join('、') || '—',
      note: note,
      noteClass: noteClass,
      running: !!day.running,
      dur: format.formatDuration(live),
      orderId: entries.length ? entries[0].orderId : 0
    };
  },

  /** 计时中时刷新月视图的合计与当天行 */
  tickMonth() {
    const d = this.monthData;
    if (!d || !(d.days || []).some((x) => x.running)) return;
    let total = (d.totalMinutes || 0) * 60;
    const patch = {};
    (d.days || []).forEach((day, i) => {
      const run = this.runningSecondsOf(day);
      total += run;
      if (day.running) patch['days[' + i + '].dur'] = format.formatDuration((day.minutes || 0) + run / 60);
    });
    patch.monthTotal = format.formatDuration(total / 60);
    this.setData(patch);
  },

  canGoNext() {
    const n = format.now();
    return this.data.year * 12 + this.data.month < n.getFullYear() * 12 + n.getMonth() + 1;
  },

  prevMonth() {
    let y = this.data.year;
    let m = this.data.month - 1;
    if (m < 1) { m = 12; y -= 1; }
    this.changeMonth(y, m);
  },

  nextMonth() {
    if (!this.canGoNext()) return;
    let y = this.data.year;
    let m = this.data.month + 1;
    if (m > 12) { m = 1; y += 1; }
    this.changeMonth(y, m);
  },

  changeMonth(y, m) {
    this.monthData = null;
    this.setData({ year: y, month: m, monthLabel: y + '年' + m + '月', cells: [], days: [], monthLoading: true, monthError: '' });
    this.setData({ canNext: this.canGoNext() });
    this.loadMonth();
  },

  /* ---------- 计时器 ---------- */

  syncTimer() {
    const needDay = !!(this.today && this.today.running);
    const needMonth = !!(this.monthData && (this.monthData.days || []).some((x) => x.running));
    if (needDay || needMonth) this.startTimer();
    else this.stopTimer();
  },

  startTimer() {
    if (this.timer) return;
    this.timer = setInterval(() => {
      if (this.data.tab === 'day') this.tickToday();
      else this.tickMonth();
    }, 1000);
  },

  stopTimer() {
    if (this.timer) {
      clearInterval(this.timer);
      this.timer = null;
    }
  },

  /* ---------- 交互 ---------- */

  onTab(e) {
    const tab = e.currentTarget.dataset.tab;
    if (tab === this.data.tab) return;
    this.setData({ tab: tab }, () => {
      if (tab === 'day') {
        this.lastDrawMinute = null;
        this.tickToday();
      } else {
        this.buildMonth();
      }
    });
  },

  onEntryTap(e) {
    const id = e.currentTarget.dataset.id;
    if (id) wx.navigateTo({ url: '/pages/order-detail/index?id=' + id });
  },

  onPhotoTap(e) {
    const url = e.currentTarget.dataset.url;
    if (!url) return;
    const urls = [];
    this.data.entries.forEach((x) => {
      if (x.arrive && x.arrive.photo) urls.push(x.arrive.photo);
      if (x.leave && x.leave.photo) urls.push(x.leave.photo);
    });
    wx.previewImage({ current: url, urls: urls.length ? urls : [url] });
  },

  goLeave() {
    const id = this.data.runningOrderId;
    wx.navigateTo({ url: '/pages/checkin/index' + (id ? '?orderId=' + id : '') });
  },

  retryToday() {
    this.loadToday();
  },

  retryMonth() {
    this.loadMonth();
  },

  goHome() {
    wx.switchTab({ url: '/pages/home/index' });
  }
});
