/**
 * 工单详情（原型 06 工程师 / 13 销售主管 / 16 主管操作）。
 * query.id：工单 id。页面按当前角色与 OrderDetail.actions 渲染按钮。
 */
const api = require('../../api/index.js');
const auth = require('../../utils/auth.js');
const format = require('../../utils/format.js');
const enums = require('../../utils/enums.js');

const ENGINEER_TABS = [{ key: 'info', label: '信息' }, { key: 'punch', label: '打卡记录' }, { key: 'files', label: '资料' }];
const VIEWER_TABS = [{ key: 'progress', label: '进度' }, { key: 'info', label: '信息' }, { key: 'files', label: '资料' }];

// 底部按钮顺序：越靠后越主要（最后一个可用按钮为主按钮）
const ENGINEER_ORDER = ['appoint', 'checkin', 'start', 'claim', 'complete'];
const SUPERVISOR_ORDER = ['void', 'hold', 'reject', 'reassign', 'resume', 'assign', 'close'];
const LABEL_OVERRIDE = { appoint: '设置预约', reject: '退回处理中', hold: '标为暂停搁置' };

const SALES_NOTE = '销售和主管在此只能查看；改派、作废、补录由主管操作。';

function timeMs(t) {
  const d = format.parseTime(t);
  return d ? d.getTime() : 0;
}

function monthDayHm(t) {
  return t ? format.formatMonthDay(t) + ' ' + format.formatHm(t) : '';
}

function minutesSince(t) {
  return Math.floor(format.secondsBetween(t) / 60);
}

function statusFg(status) {
  return (enums.STATUS[status] && enums.STATUS[status].fg) || enums.COLORS.text2;
}

/** 打卡距离 + 补录说明 */
function punchNote(p, showName) {
  const parts = [];
  if (showName && p.engineer && p.engineer.name) parts.push(p.engineer.name);
  if (p.isSupplement) {
    parts.push('主管补录' + (p.supplementBy && p.supplementBy.name ? ' · ' + p.supplementBy.name : ''));
    if (p.supplementReason) parts.push(p.supplementReason);
  } else if (p.distanceMeters !== null && p.distanceMeters !== undefined) {
    parts.push('距客户 ' + format.formatDistance(p.distanceMeters) + ' · ' + (p.outOfRange ? '超出范围' : '在范围内'));
  }
  return parts.join(' · ');
}

/**
 * 按「到场—离场」配对（按工程师），返回 { [arrivePunchId]: leavePunch|null }。
 */
function pairPunches(sorted) {
  const open = {};
  const pairs = {};
  sorted.forEach((p) => {
    const eng = (p.engineer && p.engineer.id) || 0;
    if (p.kind === 'Arrive') {
      pairs[p.id] = null;
      open[eng] = p;
    } else if (p.kind === 'Leave' && open[eng]) {
      pairs[open[eng].id] = p;
      delete open[eng];
    }
  });
  return pairs;
}

function sortPunches(punches) {
  return (punches || []).slice().sort((a, b) => timeMs(a.time) - timeMs(b.time) || a.id - b.id);
}

/** 打卡记录按天分组（新的在前，组内时间正序） */
function buildDays(punches, expanded) {
  const sorted = sortPunches(punches);
  const pairs = pairPunches(sorted);
  const engIds = {};
  sorted.forEach((p) => { engIds[(p.engineer && p.engineer.id) || 0] = 1; });
  const multiEng = Object.keys(engIds).length > 1;

  const map = {};
  const order = [];
  sorted.forEach((p) => {
    const key = format.formatDate(p.time);
    if (!map[key]) {
      map[key] = { date: key, punches: [] };
      order.push(key);
    }
    map[key].punches.push(p);
  });

  let runningMinutes = 0;
  const days = order.reverse().map((key, idx) => {
    const g = map[key];
    const today = format.isToday(key);
    let minutes = 0;
    let running = false;
    let missing = false;
    let firstArrive = '';
    let lastLeave = '';
    const entries = [];

    g.punches.forEach((p) => {
      const isArrive = p.kind === 'Arrive';
      if (isArrive && !firstArrive) firstArrive = format.formatHm(p.time);
      if (!isArrive) {
        lastLeave = format.formatHm(p.time);
        if (p.pairMinutes) minutes += p.pairMinutes;
      }
      entries.push({
        key: 'p' + p.id,
        kind: enums.label(enums.PUNCH_KIND, p.kind),
        time: format.formatHm(p.time),
        note: punchNote(p, multiEng),
        noteClass: p.isSupplement ? 'amber' : (p.outOfRange ? 'red' : ''),
        dur: !isArrive && p.pairMinutes !== null && p.pairMinutes !== undefined ? format.formatDuration(p.pairMinutes) : '',
        durClass: '',
        dot: p.isSupplement ? 'amber' : '',
        photo: p.photo && p.photo.url ? p.photo.url : ''
      });
      // 未配对到场：今天 = 计时中；更早 = 缺卡
      if (isArrive && pairs[p.id] === null) {
        if (today) {
          running = true;
          const m = minutesSince(p.time);
          runningMinutes += m;
          entries.push({
            key: 'w' + p.id, kind: '离场', time: '待打卡',
            note: '离开现场时打卡，中途离开也需打卡', noteClass: '',
            dur: '计时中', durClass: 'red', dot: 'gray', photo: ''
          });
          minutes += m;
        } else {
          missing = true;
          entries.push({
            key: 'm' + p.id, kind: '离场', time: '缺卡',
            note: '未打离场卡，当天未计工时，需主管补录', noteClass: 'red',
            dur: '—', durClass: 'muted-3', dot: 'missing', photo: ''
          });
        }
      }
    });
    entries.forEach((e, i) => { e.last = i === entries.length - 1; });

    const subParts = [];
    if (firstArrive) subParts.push('到场 ' + firstArrive);
    if (missing) subParts.push('离场 缺卡');
    else if (lastLeave) subParts.push('离场 ' + lastLeave);

    return {
      date: key,
      title: format.formatMonthDay(key) + ' ' + format.weekday(key),
      running: running,
      runningText: '现场 ' + format.formatDuration(minutes) + ' · 计时中',
      sub: subParts.join(' · '),
      dur: missing && !minutes ? '—' : format.formatDuration(minutes),
      missing: missing,
      expanded: expanded[key] !== undefined ? expanded[key] : (idx === 0 || today),
      entries: entries
    };
  });
  return { days: days, runningMinutes: runningMinutes };
}

/** 进度时间线：合并状态记录与打卡，末尾补当前/未到达阶段 */
function buildSteps(d, todayMinutes, running) {
  const punches = sortPunches(d.punches);
  const pairs = pairPunches(punches);
  const items = [];
  const arriveTimes = {};

  punches.forEach((p) => {
    const isArrive = p.kind === 'Arrive';
    const parts = [monthDayHm(p.time)];
    if (!isArrive && p.pairMinutes !== null && p.pairMinutes !== undefined) parts.push('当天现场 ' + format.formatDuration(p.pairMinutes));
    const note = punchNote(p, false);
    if (note) parts.push(note);
    if (isArrive && pairs[p.id] === null && !format.isToday(p.time)) parts.push('缺离场卡');
    if (p.photo && p.photo.url) parts.push('查看照片');
    if (isArrive) arriveTimes[monthDayHm(p.time)] = items.length;
    items.push({
      ms: timeMs(p.time),
      title: ((p.engineer && p.engineer.name) || '') + ' ' + (isArrive ? '到场打卡' : (p.isSupplement ? '离场（主管补录）' : '离场打卡')),
      sub: parts.join(' · '),
      subClass: p.isSupplement ? 'amber' : (p.outOfRange ? 'red' : ''),
      tag: '',
      tagColor: '',
      photo: p.photo && p.photo.url ? p.photo.url : ''
    });
  });

  (d.logs || []).forEach((log, i) => {
    const text = log.text || '';
    const arrow = text.indexOf(' → ');
    const title = arrow >= 0 ? text.slice(0, arrow) : text;
    const tag = log.toStatus ? enums.label(enums.STATUS, log.toStatus) : '';
    const when = monthDayHm(log.time);
    // 「首次到场」与到场打卡合并为一条
    if (text.indexOf('首次到场') >= 0 && arriveTimes[when] !== undefined) {
      const it = items[arriveTimes[when]];
      it.tag = tag;
      it.tagColor = statusFg(log.toStatus);
      return;
    }
    items.push({
      ms: timeMs(log.time),
      title: title,
      sub: i === 0 && d.source ? when + ' · ' + enums.label(enums.SOURCE, d.source) : when,
      subClass: '',
      tag: tag,
      tagColor: log.toStatus ? statusFg(log.toStatus) : '',
      photo: ''
    });
  });

  items.sort((a, b) => a.ms - b.ms);
  const steps = items.map((it) => Object.assign(it, { state: 'done' }));

  const onsite = d.serviceType === 'Onsite';
  let current = null;
  switch (d.status) {
    case 'Pending':
      current = { title: '等待认领', sub: '已发布到工单大厅，可由主管指派' };
      break;
    case 'Accepted':
      current = { title: '待启动', sub: d.appointmentTime ? '预约 ' + format.formatDateTimeShort(d.appointmentTime) : (onsite ? '等待工程师上门，首次到场打卡后进入处理中' : '等待工程师开始处理') };
      break;
    case 'InProgress':
      if (running) current = { title: '现场处理中', sub: '今日已计 ' + format.formatDuration(todayMinutes) + '，离场打卡后计入' };
      else current = { title: onsite ? '处理中' : enums.label(enums.SERVICE_TYPE, d.serviceType) + '处理中', sub: onsite ? '当前不在现场' : '工程师处理中' };
      break;
    case 'OnHold':
      current = { title: '暂停搁置', sub: d.holdReason || '等待客户 / 备件 / 外部' };
      break;
    case 'Submitted':
      current = { title: '主管验收', sub: '资料齐全后确认闭环' };
      break;
    default:
      break;
  }
  if (current) {
    steps.push(Object.assign({ tag: '当前', tagColor: enums.COLORS.red, subClass: '', photo: '', state: 'current' }, current));
  }
  const future = [];
  if (['Pending', 'Accepted', 'InProgress', 'OnHold'].indexOf(d.status) >= 0) {
    future.push({ title: '提交完工', sub: '工程师上传处理照片和客户签字件', tag: '待验收' });
    future.push({ title: '主管验收', sub: '资料齐全后确认闭环', tag: '已闭环' });
  }
  future.forEach((f) => steps.push(Object.assign({ tagColor: enums.COLORS.text3, subClass: '', photo: '', state: 'future' }, f)));

  steps.forEach((s, i) => {
    s.key = 's' + i;
    const next = steps[i + 1];
    s.last = !next;
    s.lineOn = !!next && s.state === 'done' && next.state !== 'future';
  });
  return steps;
}

function buildFiles(d, isEngineerView) {
  const f = d.files || {};
  const creatorSales = d.creator && d.creator.role === 'Sales';
  const rows = [
    { key: 'orderPhotos', title: '建单照片', desc: creatorSales ? '销售上传' : '建单时上传', list: f.orderPhotos },
    { key: 'punchPhotos', title: '打卡照片', desc: '每次到场、离场各 1 张，带水印', list: f.punchPhotos, hide: d.serviceType !== 'Onsite' && !(f.punchPhotos || []).length },
    { key: 'processPhotos', title: '处理照片', desc: '完工时上传，需包含维修前原状', list: f.processPhotos },
    { key: 'signatures', title: '客户签字件', desc: '现场维修记录表，签字后拍照', list: f.signatures, warn: d.serviceType === 'Onsite' }
  ];
  return rows.filter((r) => !r.hide).map((r) => {
    const n = (r.list || []).length;
    return {
      key: r.key,
      title: r.title,
      desc: r.desc,
      count: n,
      text: n ? n + ' 张' : '未上传',
      warn: !n && !!r.warn
    };
  });
}

Page({
  data: {
    loading: true,
    error: '',
    order: null,
    role: '',
    isEngineerView: true,
    isSupervisor: false,
    tabs: ENGINEER_TABS,
    tab: 'info',

    priceText: '',
    hoursText: '',
    creatorLabel: '销售',
    assigneeSub: '',
    infoRows: [],
    orderRows: [],
    completionRows: [],
    canNav: false,
    contactPhoneText: '',

    days: [],
    steps: [],
    progressNote: SALES_NOTE,
    files: [],
    issues: [],
    buttons: [],

    sheetShow: false,
    apptShow: false,
    apptDate: '',
    apptTime: '',
    apptStart: '',

    supp: { open: false, punchId: 0, date: '', minTime: '00:00', arriveText: '', time: '', reason: '' },
    busy: false
  },

  onLoad(query) {
    this.orderId = Number(query && query.id) || 0;
    this.expanded = {};
    this.tabTouched = false;
    if (!this.orderId) this.setData({ loading: false, error: '缺少工单参数' });
  },

  onShow() {
    if (!this.orderId) return;
    auth.ensureLogin().then(() => this.load()).catch(() => {});
  },

  onHide() {
    this.stopTimer();
  },

  onUnload() {
    this.stopTimer();
  },

  onPullDownRefresh() {
    if (!this.orderId) {
      wx.stopPullDownRefresh();
      return;
    }
    this.load().then(() => wx.stopPullDownRefresh(), () => wx.stopPullDownRefresh());
  },

  load() {
    const first = !this.detail;
    if (first) this.setData({ loading: true, error: '' });
    return api.orders.detail(this.orderId, { silent: true }).then((d) => {
      this.applyDetail(d);
    }).catch((err) => {
      const msg = (err && err.message) || '加载失败';
      if (first) this.setData({ loading: false, error: msg });
      else wx.showToast({ title: msg, icon: 'none' });
      return Promise.reject(err);
    });
  },

  /** 用接口返回的 OrderDetail 刷新页面 */
  applyDetail(d) {
    if (!d) return;
    this.detail = d;
    const role = auth.getRole();
    const isEngineerView = role === 'Engineer';
    const isSupervisor = role === 'Supervisor';
    const tabs = isEngineerView ? ENGINEER_TABS : VIEWER_TABS;
    let tab = this.data.tab;
    if (!this.tabTouched || !tabs.some((t) => t.key === tab)) tab = tabs[0].key;

    this.setData(Object.assign({
      loading: false,
      error: '',
      order: d,
      role: role,
      isEngineerView: isEngineerView,
      isSupervisor: isSupervisor,
      tabs: tabs,
      tab: tab,
      priceText: format.formatPrice(d.price),
      creatorLabel: d.creator && d.creator.role === 'Sales' ? '销售' : '建单人',
      assigneeSub: this.assigneeSub(d),
      infoRows: this.infoRows(d),
      orderRows: this.orderRows(d),
      completionRows: this.completionRows(d),
      canNav: typeof d.latitude === 'number' && typeof d.longitude === 'number' && !(d.latitude === 0 && d.longitude === 0),
      contactPhoneText: format.formatPhone(d.contactPhone),
      files: buildFiles(d, isEngineerView),
      issues: this.buildIssues(d),
      buttons: this.buildButtons(d, role),
      progressNote: this.progressNote(d, isSupervisor)
    }, this.computeLive(d)));

    // 页面数据变化后，补录表单若对应的缺卡已消失则收起
    const s = this.data.supp;
    if (s.open && !(d.issueDetails || []).some((i) => i.type === 'MissingPunch' && i.punchId === s.punchId)) {
      this.setData({ 'supp.open': false });
    }
    this.startTimer();
  },

  /** 随时间变化的部分（计时中） */
  computeLive(d) {
    const built = buildDays(d.punches, this.expanded);
    const running = built.days.some((x) => x.running);
    // 今日已配对 + 计时中
    const todayPaired = (d.punches || []).filter((p) => p.kind === 'Leave' && format.isToday(p.time))
      .reduce((s, p) => s + (p.pairMinutes || 0), 0);
    const extra = d.onSiteSince ? minutesSince(d.onSiteSince) : 0;
    this.running = running;
    return {
      days: built.days,
      hoursText: format.formatDuration((d.totalMinutes || 0) + extra),
      steps: auth.getRole() === 'Engineer' ? [] : buildSteps(d, todayPaired + built.runningMinutes, running)
    };
  },

  startTimer() {
    this.stopTimer();
    if (!this.running) return;
    this.timer = setInterval(() => {
      if (this.detail) this.setData(this.computeLive(this.detail));
    }, 30000);
  },

  stopTimer() {
    if (this.timer) clearInterval(this.timer);
    this.timer = null;
  },

  assigneeSub(d) {
    const a = d.assignee;
    if (!a) return '';
    let verb = '认领';
    let time = d.claimedAt;
    const logs = (d.logs || []).slice().reverse();
    const hit = logs.find((l) => (l.text || '').indexOf(a.name) >= 0 && /认领|指派|改派/.test(l.text || ''));
    if (hit) {
      verb = /改派/.test(hit.text) ? '改派' : (/指派/.test(hit.text) ? '指派' : '认领');
      time = hit.time;
    }
    const roleName = enums.label(enums.ROLE, a.role);
    return roleName + (time ? ' · ' + monthDayHm(time) + ' ' + verb : '');
  },

  infoRows(d) {
    const rows = [
      { label: '发生时间', value: d.faultTime },
      { label: '客户已尝试', value: d.customerTried },
      { label: '现场环境', value: d.siteEnvironment },
      { label: '客户诉求', value: d.customerRequest }
    ];
    return rows.filter((r) => r.value).map((r, i) => Object.assign({ key: 'i' + i }, r));
  },

  orderRows(d) {
    const sat = d.satDate ? d.satDate.replace(/^(\d{4})-(\d{1,2}).*$/, (m, y, mo) => y + '年' + Number(mo) + '月') : '';
    const rows = [
      { label: '工单来源', value: enums.label(enums.SOURCE, d.source) },
      { label: '问题类型', value: enums.label(enums.PROBLEM_TYPE, d.problemType) },
      { label: '维修类型', value: enums.label(enums.SERVICE_TYPE, d.serviceType) },
      { label: '紧急程度', value: enums.label(enums.URGENCY, d.urgency) },
      { label: '购买/SAT', value: sat ? sat + ' · ' + (d.satSigned ? 'SAT 已签字' : 'SAT 未签字') : '' },
      { label: '项目编号', value: d.projectNo },
      { label: '预约时间', value: d.appointmentTime ? format.formatDateTimeShort(d.appointmentTime) : '' },
      { label: '建单时间', value: d.createdAt ? format.formatDateTimeShort(d.createdAt) + (d.creator ? ' · ' + d.creator.name : '') : '' }
    ];
    return rows.filter((r) => r.value).map((r, i) => Object.assign({ key: 'o' + i }, r));
  },

  completionRows(d) {
    const c = d.completion;
    if (!c) return [];
    const run = enums.RUN_STATUS[c.runStatus];
    const rows = [
      { label: '故障原因', value: c.faultCause },
      { label: '解决措施', value: c.solution },
      { label: '更换备件', value: c.partsReplaced || '无' },
      { label: '运行状况', value: run ? run.label : c.runStatus, color: run ? run.fg : '' },
      { label: '提交时间', value: c.submittedAt ? format.formatDateTimeShort(c.submittedAt) : '' }
    ];
    return rows.filter((r) => r.value).map((r, i) => Object.assign({ key: 'c' + i, color: '' }, r));
  },

  buildIssues(d) {
    const canSupp = (d.actions || []).indexOf('supplement') >= 0;
    return (d.issueDetails || []).map((it, i) => ({
      key: 'x' + i,
      type: it.type,
      tone: it.type === 'MissingPunch' ? 'red' : 'amber',
      title: enums.label(enums.ISSUE, it.type),
      text: it.text,
      punchId: it.punchId || 0,
      date: it.date || '',
      canSupplement: canSupp && it.type === 'MissingPunch' && !!it.punchId
    }));
  },

  buildButtons(d, role) {
    const actions = d.actions || [];
    const order = role === 'Supervisor' ? SUPERVISOR_ORDER : (role === 'Engineer' ? ENGINEER_ORDER : []);
    const list = order.filter((a) => actions.indexOf(a) >= 0).map((a) => ({
      key: a,
      label: LABEL_OVERRIDE[a] || enums.label(enums.ACTION, a),
      kind: 'secondary',
      disabled: false,
      tip: ''
    }));
    if (role === 'Supervisor' && d.status === 'Submitted' && actions.indexOf('close') < 0) {
      list.push({ key: 'close', label: enums.label(enums.ACTION, 'close'), kind: 'disabled', disabled: true, tip: '请先补录缺卡' });
    }
    for (let i = list.length - 1; i >= 0; i--) {
      if (!list[i].disabled) {
        list[i].kind = 'primary';
        break;
      }
    }
    const wide = list.length > 1;
    list.forEach((b) => { b.wide = wide && b.kind !== 'secondary'; });
    return list;
  },

  progressNote(d, isSupervisor) {
    if (!isSupervisor) return SALES_NOTE;
    const acts = (d.actions || []).filter((a) => a !== 'supplement').map((a) => LABEL_OVERRIDE[a] || enums.label(enums.ACTION, a));
    const parts = [];
    if (acts.length) parts.push('主管可在底部操作：' + acts.join('、'));
    if ((d.actions || []).indexOf('supplement') >= 0) parts.push('缺卡请在上方警示框中补录离场时间');
    if (!parts.length) return '当前状态无可执行的主管操作。';
    return parts.join('；') + '。';
  },

  /* ---------------- 交互 ---------------- */

  onTab(e) {
    this.tabTouched = true;
    this.setData({ tab: e.currentTarget.dataset.key });
  },

  onToggleDay(e) {
    const key = e.currentTarget.dataset.date;
    const day = this.data.days.find((x) => x.date === key);
    if (!day) return;
    this.expanded[key] = !day.expanded;
    const idx = this.data.days.indexOf(day);
    this.setData({ ['days[' + idx + '].expanded']: !day.expanded });
  },

  onCall(e) {
    const phone = e.currentTarget.dataset.phone;
    if (!phone) return;
    wx.makePhoneCall({ phoneNumber: String(phone).replace(/\s/g, ''), fail() {} });
  },

  onNav() {
    const d = this.detail;
    if (!d || !this.data.canNav) return;
    wx.openLocation({
      latitude: Number(d.latitude),
      longitude: Number(d.longitude),
      name: d.customerName || '',
      address: d.address || '',
      scale: 16,
      fail(err) {
        if (err && /cancel/.test(err.errMsg || '')) return;
        wx.showToast({ title: '无法打开地图', icon: 'none' });
      }
    });
  },

  onPreviewPhoto(e) {
    const url = e.currentTarget.dataset.url;
    if (!url) return;
    const all = ((this.detail && this.detail.punches) || []).map((p) => p.photo && p.photo.url).filter(Boolean);
    wx.previewImage({ urls: all.indexOf(url) >= 0 ? all : [url], current: url });
  },

  onPreviewFiles(e) {
    const key = e.currentTarget.dataset.key;
    const list = ((this.detail && this.detail.files) || {})[key] || [];
    if (!list.length) {
      wx.showToast({ title: '暂未上传', icon: 'none' });
      return;
    }
    const hasVideo = list.some((f) => /^video\//.test(f.mimeType || ''));
    if (hasVideo && wx.previewMedia) {
      wx.previewMedia({
        sources: list.map((f) => ({ url: f.url, type: /^video\//.test(f.mimeType || '') ? 'video' : 'image' })),
        current: 0
      });
    } else {
      wx.previewImage({ urls: list.map((f) => f.url), current: list[0].url });
    }
  },

  onAction(e) {
    const key = e.currentTarget.dataset.key;
    const btn = this.data.buttons.find((b) => b.key === key);
    if (!btn) return;
    if (btn.disabled) {
      wx.showToast({ title: btn.tip || '暂不可操作', icon: 'none' });
      return;
    }
    if (this.data.busy) return;
    const id = this.orderId;
    switch (key) {
      case 'claim':
        this.run(api.orders.claim(id, { loading: '认领中' }), '认领成功', true);
        break;
      case 'appoint':
        this.openAppt();
        break;
      case 'checkin':
        wx.navigateTo({ url: '/pages/checkin/index?orderId=' + id });
        break;
      case 'start':
        this.confirm('开始处理', '确认开始处理此工单？状态将变为「处理中」。', '开始').then(() => {
          this.run(api.orders.start(id, { loading: '提交中' }), '已开始处理', true);
        });
        break;
      case 'complete':
        wx.navigateTo({ url: '/pages/complete/index?id=' + id });
        break;
      case 'assign':
      case 'reassign':
        this.setData({ sheetShow: true });
        break;
      case 'void':
        this.askReason('作废工单', '请输入作废原因', '作废').then((r) => {
          this.run(api.orders.void(id, r, { loading: '提交中' }), '已作废');
        });
        break;
      case 'hold':
        this.askReason('暂停搁置', '原因，如：等待备件 / 等待客户', '确定').then((r) => {
          this.run(api.orders.hold(id, r, { loading: '提交中' }), '已暂停搁置');
        });
        break;
      case 'reject':
        this.askReason('退回处理中', '请输入退回原因', '退回').then((r) => {
          this.run(api.orders.reject(id, r, { loading: '提交中' }), '已退回处理中');
        });
        break;
      case 'resume':
        this.confirm('恢复处理', '确认恢复处理？工单将回到「处理中」。', '恢复').then(() => {
          this.run(api.orders.resume(id, { loading: '提交中' }), '已恢复处理');
        });
        break;
      case 'close':
        this.confirm('确认闭环', '确认资料齐全并闭环此工单？闭环后不可再修改。', '确认闭环').then(() => {
          this.run(api.orders.close(id, { loading: '提交中' }), '已闭环');
        });
        break;
      default:
        break;
    }
  },

  /** 执行操作：成功用返回的 OrderDetail 刷新；冲突类错误后重新拉取 */
  run(promise, okText, reloadOnError) {
    this.setData({ busy: true });
    return promise.then((d) => {
      this.setData({ busy: false });
      this.applyDetail(d);
      wx.showToast({ title: okText, icon: 'success' });
      return d;
    }).catch((err) => {
      this.setData({ busy: false });
      if (reloadOnError || (err && err.code === 40900)) this.load().catch(() => {});
      return Promise.reject(err);
    }).catch(() => {});
  },

  confirm(title, content, confirmText) {
    return new Promise((resolve) => {
      wx.showModal({
        title: title,
        content: content,
        confirmText: confirmText || '确定',
        confirmColor: enums.COLORS.text,
        success: (res) => { if (res.confirm) resolve(); }
      });
    });
  },

  askReason(title, placeholder, confirmText) {
    return new Promise((resolve) => {
      wx.showModal({
        title: title,
        editable: true,
        placeholderText: placeholder,
        confirmText: confirmText || '确定',
        confirmColor: enums.COLORS.red,
        success: (res) => {
          if (!res.confirm) return;
          const r = (res.content || '').trim();
          if (!r) {
            wx.showToast({ title: '请填写原因', icon: 'none' });
            return;
          }
          resolve(r);
        }
      });
    });
  },

  /* ---------------- 指派 ---------------- */
  onSheetClose() {
    this.setData({ sheetShow: false });
  },

  onAssigned(e) {
    if (e.detail && e.detail.id) this.applyDetail(e.detail);
    else this.load().catch(() => {});
  },

  /* ---------------- 预约 ---------------- */
  openAppt() {
    const d = this.detail || {};
    const n = format.now();
    let date;
    let time = '10:00';
    if (d.appointmentTime) {
      date = format.formatDate(d.appointmentTime);
      time = format.formatHm(d.appointmentTime);
    } else {
      date = format.formatDate(new Date(n.getFullYear(), n.getMonth(), n.getDate() + 1));
    }
    this.setData({ apptShow: true, apptDate: date, apptTime: time, apptStart: format.today() });
  },

  onApptDate(e) {
    this.setData({ apptDate: e.detail.value });
  },

  onApptTime(e) {
    this.setData({ apptTime: e.detail.value });
  },

  closeAppt() {
    this.setData({ apptShow: false });
  },

  saveAppt() {
    const { apptDate, apptTime, busy } = this.data;
    if (busy) return;
    if (!apptDate || !apptTime) {
      wx.showToast({ title: '请选择预约日期和时间', icon: 'none' });
      return;
    }
    const t = format.parseTime(apptDate + 'T' + apptTime + ':00');
    if (!t || t.getTime() < format.now().getTime()) {
      wx.showToast({ title: '预约时间不能早于当前时间', icon: 'none' });
      return;
    }
    this.run(api.orders.appoint(this.orderId, format.toApiTime(t), { loading: '提交中' }), '预约已设置').then((d) => {
      if (d) this.setData({ apptShow: false });
    });
  },

  /* ---------------- 补录 ---------------- */
  openSupp(e) {
    const punchId = Number(e.currentTarget.dataset.id);
    const issue = this.data.issues.find((x) => x.punchId === punchId);
    const arrive = ((this.detail && this.detail.punches) || []).find((p) => p.id === punchId);
    if (!issue) return;
    this.setData({
      supp: {
        open: true,
        punchId: punchId,
        date: issue.date || (arrive ? format.formatDate(arrive.time) : ''),
        minTime: arrive ? format.formatHm(arrive.time) : '00:00',
        arriveText: arrive ? monthDayHm(arrive.time) + ' 到场' : '',
        time: '',
        reason: ''
      }
    });
  },

  cancelSupp() {
    this.setData({ 'supp.open': false });
  },

  onSuppTime(e) {
    this.setData({ 'supp.time': e.detail.value });
  },

  onSuppReason(e) {
    this.setData({ 'supp.reason': e.detail.value });
  },

  saveSupp() {
    const s = this.data.supp;
    if (this.data.busy) return;
    if (!s.time) {
      wx.showToast({ title: '请选择离场时间', icon: 'none' });
      return;
    }
    if (s.time <= s.minTime) {
      wx.showToast({ title: '离场时间须晚于到场时间 ' + s.minTime, icon: 'none' });
      return;
    }
    const reason = (s.reason || '').trim();
    if (!reason) {
      wx.showToast({ title: '请填写补录原因', icon: 'none' });
      return;
    }
    if (!s.date) {
      wx.showToast({ title: '缺少缺卡日期', icon: 'none' });
      return;
    }
    const body = {
      orderId: this.orderId,
      arrivePunchId: s.punchId,
      kind: 'Leave',
      time: s.date + 'T' + s.time + ':00',
      reason: reason
    };
    this.run(api.admin.supplement(body, { loading: '保存中' }), '补录成功').then((d) => {
      if (d) this.setData({ 'supp.open': false });
    });
  },

  onRetry() {
    this.load().catch(() => {});
  },

  noop() {}
});
