/**
 * 工程师首页（原型 00 处理中 / 01 待启动 / 02 空闲）。数据：GET /api/home/engineer。
 * 页面调用 refresh() → Promise；事件 meta：{ user, tripDay }（更新页头）。
 */
const api = require('../../api/index.js');
const format = require('../../utils/format.js');
const enums = require('../../utils/enums.js');
const clock = require('./clock.js');

/** 城市徽标文字（最多 2 字） */
function badgeOf(o) {
  const s = (o && (o.city || o.region)) || '';
  if (s) return s.slice(0, 2);
  return enums.label(enums.SERVICE_TYPE, o && o.serviceType, '工单');
}

function deviceLine(o) {
  return [o.deviceModel, o.symptom].filter(Boolean).join(' · ');
}

function decorate(o) {
  return Object.assign({}, o, {
    badge: badgeOf(o),
    deviceLine: deviceLine(o),
    priceText: format.formatPrice(o.price),
    free: Number(o.price) === 0
  });
}

/** 当前工单 4 段进度 */
function buildSteps(o) {
  const onsite = o.serviceType === 'Onsite';
  const arrived = onsite ? !!o.lastArriveTime : true;
  const hold = o.status === 'OnHold';
  return [
    { key: 'claim', label: '已认领', sub: format.formatMonthDay(o.claimedAt), state: 'done' },
    {
      key: 'arrive',
      label: onsite ? '已到场' : '已开始',
      sub: onsite ? (o.lastArriveTime ? (format.isToday(o.lastArriveTime) ? format.formatHm(o.lastArriveTime) : format.formatMonthDay(o.lastArriveTime)) : '待打卡') : '',
      state: arrived ? 'done' : 'todo'
    },
    { key: 'doing', label: hold ? '暂停搁置' : '处理中', sub: enums.label(enums.SERVICE_TYPE, o.serviceType), state: 'current' },
    { key: 'check', label: '待验收', sub: '提交完工后', state: 'todo' }
  ].map((s, i, arr) => Object.assign(s, {
    // 连接线：本段与下一段都已完成为黑色
    line: i < arr.length - 1 ? (s.state === 'done' && arr[i + 1].state === 'done' ? 'on' : 'off') : ''
  }));
}

function upcomingItem(o) {
  const t = format.parseTime(o.appointmentTime);
  return Object.assign(decorate(o), {
    weekText: t ? format.weekday(t) : '待约',
    dayText: t ? String(t.getDate()) : '—',
    apptText: t ? format.formatHm(t) + ' 预约' : '未预约'
  });
}

function hallItem(o) {
  return Object.assign(decorate(o), {
    meta: [o.symptom, o.region, format.timeAgo(o.createdAt)].filter(Boolean).join(' · ')
  });
}

function doneItem(o) {
  const closed = o.status === 'Closed';
  const when = closed ? format.formatMonthDay(o.closedAt) + '闭环' : format.formatMonthDay(o.submittedAt) + '提交';
  const parts = [when];
  if (o.serviceType === 'Onsite') parts.push('现场 ' + format.formatDuration(o.totalMinutes || 0));
  else parts.push(enums.label(enums.SERVICE_TYPE, o.serviceType));
  return Object.assign({}, o, { meta: parts.join(' · ') });
}

Component({
  options: { addGlobalClass: true },

  data: {
    status: 'loading', // loading | ok | error
    errorText: '',
    state: '',
    clockSrc: '',
    clockHm: '0:00',
    clockSs: '',
    running: false,
    heroLines: [],
    current: null,
    steps: [],
    currentOnsite: false,
    currentOnSite: false,
    next: null,
    upcoming: [],
    hall: { count: 0, urgentCount: 0, items: [] },
    idleDesc: '',
    recentDone: [],
    monthText: '0:00'
  },

  lifetimes: {
    detached() { this.stopTimer(); }
  },

  pageLifetimes: {
    show() { if (this._today) this.startTimer(); },
    hide() { this.stopTimer(); }
  },

  methods: {
    /** 页面 onShow / 下拉刷新调用 */
    refresh() {
      const first = this.data.status !== 'ok';
      if (first) this.setData({ status: 'loading', errorText: '' });
      return api.home.engineer({ silent: true }).then((d) => {
        this.apply(d || {});
      }).catch((err) => {
        const msg = (err && err.message) || '加载失败，请稍后重试';
        if (first) this.setData({ status: 'error', errorText: msg });
        else wx.showToast({ title: msg, icon: 'none' });
      });
    },

    apply(d) {
      const today = d.today || {};
      if (today.serverTime) format.syncServerTime(today.serverTime);
      this._today = today;

      const state = d.state || 'idle';
      const hall = d.hall || { count: 0, urgentCount: 0, items: [] };
      const current = d.current ? decorate(d.current) : null;
      const next = d.next ? Object.assign(decorate(d.next), {
        apptText: d.next.appointmentTime ? format.formatDateTimeShort(d.next.appointmentTime) : '未预约',
        phoneText: format.formatPhone(d.next.contactPhone),
        hasLocation: d.next.latitude !== null && d.next.latitude !== undefined && d.next.longitude !== null && d.next.longitude !== undefined && !(d.next.latitude === 0 && d.next.longitude === 0),
        onsite: d.next.serviceType === 'Onsite'
      }) : null;

      // 后续安排：working 时含 next；再加 upcoming
      let upcoming = (d.upcoming || []).slice();
      if (state === 'working' && d.next) upcoming.unshift(d.next);
      upcoming = upcoming.map(upcomingItem);

      let idleDesc = '';
      if (hall.count > 0) {
        idleDesc = '工单大厅有 ' + hall.count + ' 张待认领工单' + (hall.urgentCount > 0 ? '，其中 ' + hall.urgentCount + ' 张紧急' : '') + '。客户直接联系的需求，可自行新建工单。';
      } else {
        idleDesc = '工单大厅暂无待认领工单。客户直接联系的需求，可自行新建工单。';
      }

      this._todayAppts = this.countTodayAppointments(d);
      this.setData({
        status: 'ok',
        state: state,
        current: current,
        steps: current ? buildSteps(current) : [],
        currentOnsite: !!current && current.serviceType === 'Onsite',
        currentOnSite: !!current && !!current.onSiteSince,
        next: next,
        upcoming: upcoming,
        hall: { count: hall.count || 0, urgentCount: hall.urgentCount || 0, items: (hall.items || []).map(hallItem) },
        idleDesc: idleDesc,
        recentDone: (d.recentDone || []).map(doneItem),
        monthText: format.formatDuration(d.monthMinutes || 0)
      });

      this.triggerEvent('meta', { user: d.user || null, tripDay: today.tripDay || 0 });
      this._lastMinute = -1;
      this.tick();
      this.startTimer();
    },

    countTodayAppointments(d) {
      const list = [];
      if (d.next) list.push(d.next);
      (d.upcoming || []).forEach((o) => list.push(o));
      return list.filter((o) => o.appointmentTime && format.isToday(o.appointmentTime))
        .sort((a, b) => format.parseTime(a.appointmentTime) - format.parseTime(b.appointmentTime));
    },

    /* ---------- 计时 ---------- */
    startTimer() {
      this.stopTimer();
      this._timer = setInterval(() => this.tick(), 1000);
    },

    stopTimer() {
      if (this._timer) clearInterval(this._timer);
      this._timer = null;
    },

    tick() {
      const today = this._today;
      if (!today) return;
      const now = format.now();
      const running = !!today.running && !!today.runningSince;
      const seconds = (today.closedMinutes || 0) * 60 + (running ? format.secondsBetween(today.runningSince) : 0);
      const c = format.splitClock(seconds);
      const patch = {};
      if (c.hm !== this.data.clockHm) patch.clockHm = c.hm;
      const ss = running ? c.ss : '';
      if (ss !== this.data.clockSs) patch.clockSs = ss;
      if (running !== this.data.running) patch.running = running;

      const minute = now.getHours() * 60 + now.getMinutes();
      if (minute !== this._lastMinute) {
        this._lastMinute = minute;
        patch.clockSrc = this.buildClock(now, running);
        patch.heroLines = this.buildHero(running);
      }
      if (Object.keys(patch).length) this.setData(patch);
    },

    buildClock(now, running) {
      const today = this._today || {};
      const arcs = (today.entries || []).map((e) => ({
        start: format.parseTime(e.arrive && e.arrive.time),
        end: e.leave ? format.parseTime(e.leave.time) : null
      })).filter((a) => a.start && (a.end || running));
      let plan = null;
      if (!arcs.length && this.data.state === 'next' && this._todayAppts && this._todayAppts.length) {
        const s = format.parseTime(this._todayAppts[0].appointmentTime);
        if (s && s.getTime() > now.getTime() - 2 * 3600000) plan = { start: s, end: new Date(s.getTime() + 2 * 3600000) };
      }
      return clock.build({
        now: now,
        arcs: arcs,
        plan: plan,
        marker: { start: format.parseTime(today.firstArriveTime), running: running }
      });
    },

    /** 大计时下方两行说明（dot: ring 黑圈 / red 红点 / gray 灰点 / ''） */
    buildHero(running) {
      const today = this._today || {};
      const state = this.data.state;
      const lines = [];
      if (today.firstArriveTime) {
        lines.push({ dot: 'ring', text: format.formatHm(today.firstArriveTime) + ' 到场' });
        if (running) lines.push({ dot: 'red', text: '现场计时中' });
        else lines.push({ dot: '', text: '已离场，当前未计时', muted: true });
        return lines;
      }
      if (state === 'working') {
        const c = this.data.current;
        lines.push({ dot: 'gray', text: c && c.serviceType !== 'Onsite' ? enums.label(enums.SERVICE_TYPE, c.serviceType) + '处理中' : '今日尚未到场' });
        lines.push({ dot: '', text: '本月已计 ' + this.data.monthText, muted: true });
        return lines;
      }
      if (state === 'next') {
        const appts = this._todayAppts || [];
        if (appts.length) lines.push({ dot: 'gray', text: format.formatHm(appts[0].appointmentTime) + ' 预约 ' + appts.length + ' 单' });
        else lines.push({ dot: 'gray', text: '今日暂无预约' });
        lines.push({ dot: '', text: '到场打卡后开始计时', muted: true });
        return lines;
      }
      lines.push({ dot: '', text: '今日暂无预约工单' });
      lines.push({ dot: '', text: '本月已计 ', strong: this.data.monthText, muted: true });
      return lines;
    },

    /* ---------- 交互 ---------- */
    onRetry() { this.refresh(); },

    toDetail(e) {
      const id = e.currentTarget.dataset.id;
      if (id) wx.navigateTo({ url: '/pages/order-detail/index?id=' + id });
    },

    toCheckin(e) {
      const id = e.currentTarget.dataset.id;
      wx.navigateTo({ url: '/pages/checkin/index' + (id ? '?orderId=' + id : '') });
    },

    toComplete(e) {
      const id = e.currentTarget.dataset.id;
      if (id) wx.navigateTo({ url: '/pages/complete/index?id=' + id });
    },

    toHall() { wx.switchTab({ url: '/pages/orders/index' }); },

    toNew() { wx.navigateTo({ url: '/pages/order-new/index' }); },

    toMyOrders() { wx.navigateTo({ url: '/pages/my-orders/index' }); },

    onCall() {
      const n = this.data.next;
      const phone = n && String(n.contactPhone || '').replace(/\s/g, '');
      if (!phone) return;
      wx.makePhoneCall({ phoneNumber: phone, fail: () => {} });
    },

    onNavigate() {
      const n = this.data.next;
      if (!n || !n.hasLocation) {
        wx.showToast({ title: '该工单没有客户坐标', icon: 'none' });
        return;
      }
      wx.openLocation({
        latitude: Number(n.latitude),
        longitude: Number(n.longitude),
        name: n.customerName || '',
        address: n.address || '',
        scale: 16,
        fail: (err) => {
          if (err && /cancel/.test(err.errMsg || '')) return;
          wx.showToast({ title: '无法打开地图', icon: 'none' });
        }
      });
    }
  }
});
