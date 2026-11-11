/**
 * 工单详情（原型 06 工程师 / 13 销售主管 / 16 主管操作）。
 * query.id：工单 id。页面按当前角色与 OrderDetail.actions 渲染按钮。
 */
const api = require('../../api/index.js');
const auth = require('../../utils/auth.js');
const format = require('../../utils/format.js');
const enums = require('../../utils/enums.js');
const attachments = require('../../utils/attachments.js');

const ENGINEER_TABS = [{ key: 'info', label: '信息' }, { key: 'punch', label: '打卡记录' }, { key: 'files', label: '附件资料' }];
// 返厂单（工程师）：「返厂流程」取代「打卡记录」
const ENGINEER_FACTORY_TABS = [{ key: 'info', label: '信息' }, { key: 'factory', label: '返厂流程' }, { key: 'files', label: '附件资料' }];
const VIEWER_TABS = [{ key: 'progress', label: '进度' }, { key: 'info', label: '信息' }, { key: 'files', label: '附件资料' }];

// 底部按钮顺序：越靠后越主要（最后一个可用按钮为主按钮）。
// setPrice / suggestPrice / assistants / requestAssistant 是页面内链接，不进底部栏。
const ENGINEER_ORDER = ['requestHold', 'appoint', 'checkin', 'start', 'claim', 'factory', 'complete'];
const SUPERVISOR_ORDER = ['void', 'hold', 'reject', 'reassign', 'resume', 'assign', 'factory', 'close'];
const SALES_ORDER = ['factory'];
const MAX_BUTTONS = 3; // 超过时把顺序靠前的多余项收进「更多」
const LABEL_OVERRIDE = { appoint: '设置预约', reject: '退回处理中', hold: '标为暂停搁置' };

const SALES_NOTE = '销售可查看进度、提价格建议；改派、作废、定价、补录由主管操作。';
const HOLD_NOTE_PLACEHOLDER = '如：客户下周一才能安排停机';
const MAX_AMOUNT = 100000000;

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

/** 第一个当前用户可记录的返厂节点（factory 按钮的目标） */
function nextFactoryNode(d) {
  const nodes = (d && d.factory && d.factory.nodes) || [];
  return nodes.filter((n) => n.canRecord && enums.FACTORY_NODE[n.node] && enums.FACTORY_NODE[n.node].action)[0] || null;
}

/** 金额输入：只保留数字和一个小数点，最多 2 位小数 */
function cleanAmount(value) {
  let v = String(value || '').replace(/[^\d.]/g, '');
  const dot = v.indexOf('.');
  if (dot >= 0) v = v.slice(0, dot + 1) + v.slice(dot + 1).replace(/\./g, '').slice(0, 2);
  if (/^0\d/.test(v)) v = v.replace(/^0+(?=\d)/, '');
  return v;
}

/**
 * 状态记录 → 时间线标题 / 标签。
 * 「… → 处理中」「… → 暂停搁置（等备件）」这类以状态名结尾的拆成 标题 + 状态标签；
 * 价格 / 人员等不改变状态的记录（如「改价 保内免费 → ¥1,800」）整句作标题、不带标签。
 */
function parseLog(log) {
  const text = log.text || '';
  const arrow = text.indexOf(' → ');
  if (arrow >= 0) {
    const rest = text.slice(arrow + 3);
    const st = Object.keys(enums.STATUS).filter((k) => rest.indexOf(enums.STATUS[k].label) === 0)[0];
    if (st) {
      const extra = rest.slice(enums.STATUS[st].label.length).replace(/^（/, '').replace(/）$/, '');
      return { title: text.slice(0, arrow), tag: enums.STATUS[st].label, tagStatus: st, extra: extra };
    }
  }
  if (!log.kind || log.kind === 'Status') {
    return {
      title: arrow >= 0 ? text.slice(0, arrow) : text,
      tag: log.toStatus ? enums.label(enums.STATUS, log.toStatus) : '',
      tagStatus: log.toStatus || '',
      extra: ''
    };
  }
  return { title: text, tag: '', tagStatus: '', extra: '' };
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
    const parsed = parseLog(log);
    const when = monthDayHm(log.time);
    // 「首次到场」与到场打卡合并为一条
    if (text.indexOf('首次到场') >= 0 && arriveTimes[when] !== undefined) {
      const it = items[arriveTimes[when]];
      it.tag = parsed.tag;
      it.tagColor = statusFg(parsed.tagStatus);
      return;
    }
    let sub = i === 0 && d.source ? when + ' · ' + enums.label(enums.SOURCE, d.source) : when;
    if (parsed.extra) sub += ' · ' + parsed.extra;
    items.push({
      ms: timeMs(log.time),
      title: parsed.title,
      sub: sub,
      subClass: '',
      tag: parsed.tag,
      tagColor: parsed.tagStatus ? statusFg(parsed.tagStatus) : '',
      photo: ''
    });
  });

  items.sort((a, b) => a.ms - b.ms);
  const steps = items.map((it) => Object.assign(it, { state: 'done' }));

  const onsite = d.serviceType === 'Onsite';
  const factory = d.serviceType === 'Factory';
  let current = null;
  switch (d.status) {
    case 'Pending':
      current = { title: '等待认领', sub: '已发布到工单大厅，可由主管指派' };
      break;
    case 'Accepted':
      if (factory) current = { title: '待启动', sub: '等待设备到厂，工厂收货后进入处理中' };
      else current = { title: '待启动', sub: d.appointmentTime ? '预约 ' + format.formatDateTimeShort(d.appointmentTime) : (onsite ? '等待工程师上门，首次到场打卡后进入处理中' : '等待工程师开始处理') };
      break;
    case 'InProgress':
      if (running) current = { title: '现场处理中', sub: '今日已计 ' + format.formatDuration(todayMinutes) + '，离场打卡后计入' };
      else current = { title: onsite ? '处理中' : enums.label(enums.SERVICE_TYPE, d.serviceType) + '处理中', sub: onsite ? '当前不在现场' : (factory && d.factoryText ? d.factoryText : '工程师处理中') };
      break;
    case 'OnHold':
      // 暂停：灰色标记，不用红色
      current = { title: '暂停搁置', sub: enums.holdTextOf(d) || '等待客户 / 备件 / 外部', state: 'paused', tag: '暂停', tagColor: enums.COLORS.gray };
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
    future.push({ title: '提交完工', sub: factory ? '寄回客户后，工程师上传处理照片并提交' : '工程师上传处理照片和客户签字件', tag: '待验收' });
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

/** 流程资料行（v1.2：建单照片已并入「附件」，不再单列） */
function buildFiles(d, isEngineerView) {
  const f = d.files || {};
  const rows = [
    { key: 'punchPhotos', title: '打卡照片', desc: '每次到场、离场各 1 张，带水印', list: f.punchPhotos, hide: d.serviceType !== 'Onsite' && !(f.punchPhotos || []).length },
    { key: 'processPhotos', title: '处理照片', desc: '完工时上传，需包含维修前原状', list: f.processPhotos },
    { key: 'signatures', title: '客户签字件', desc: '现场维修记录表，签字后拍照', list: f.signatures, warn: d.serviceType === 'Onsite' },
    { key: 'factoryPhotos', title: '返厂照片', desc: '工厂收货、寄回客户、客户签收时上传', list: f.factoryPhotos, hide: d.serviceType !== 'Factory' }
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

/**
 * 附件列表（v1.2）。只展示服务端返回的内容与 can* 标记，不在前端推导权限；
 * 工程师拿不到「仅销售和主管可见」的附件。
 */
function buildAttachments(d) {
  return (d.attachments || []).map((a) => {
    const b = attachments.badge(a);
    const sub = [attachments.fmtSize(a.size), (a.uploader && a.uploader.name) || '', monthDayHm(a.createdAt)].filter(Boolean).join(' · ');
    return {
      id: a.id,
      name: a.name || '',
      badgeText: b.text,
      badgeColor: b.color,
      sub: sub,
      restricted: !!a.restricted,
      hasMenu: !!(a.canDelete || a.canToggleRestricted)
    };
  });
}

function emptyAttachSheet() {
  return { show: false, canRestrict: false, restricted: false, items: [], uploading: false, doneCount: 0 };
}

/** 待审批的申请卡片（所有角色可见）；工程师另显示本人最近一条被驳回的申请 */
function buildRequests(d, isEngineerView) {
  const all = d.requests || [];
  const list = all.filter((r) => r.status === 'Pending').map((r) => ({
    id: r.id,
    type: r.type,
    title: enums.label(enums.REQUEST_TYPE, r.type),
    text: ((r.requester && r.requester.name) || '') + '：' + (r.text || ''),
    time: format.formatDateTimeShort(r.createdAt),
    canDecide: !!r.canDecide,
    canCancel: !!r.canCancel
  }));
  let rejected = '';
  if (isEngineerView && all.length && ['Accepted', 'InProgress', 'OnHold'].indexOf(d.status) >= 0) {
    const last = all[all.length - 1];
    if (last.status === 'Rejected') {
      rejected = enums.label(enums.REQUEST_TYPE, last.type) + '已被' + ((last.decider && last.decider.name) || '主管') + '驳回' +
        (last.reply ? '：' + last.reply : '') + '（' + (last.text || '') + '）';
    }
  }
  return { list: list, rejected: rejected };
}

/** 暂停提示条 */
function buildHoldBar(d) {
  if (d.status !== 'OnHold') return null;
  const h = d.hold || {};
  const text = h.text || enums.holdTextOf(d);
  const from = h.fromStatus || d.holdFromStatus || 'InProgress';
  const who = [(h.by && h.by.name) || '', h.at ? format.formatDateTimeShort(h.at) : ''].filter(Boolean).join(' ');
  return {
    text: '暂停搁置' + (text ? ' · ' + text : ''),
    sub: (who ? who + ' 暂停，' : '') + '恢复后回到「' + enums.label(enums.STATUS, from) + '」'
  };
}

/** 处理人员：负责人 / 协助人 / 已退出，每人累计工时（含计时中） */
function buildParticipants(d) {
  const onsite = d.serviceType === 'Onsite';
  return (d.participants || []).map((p, i) => {
    const live = p.onSiteSince ? minutesSince(p.onSiteSince) : 0;
    const role = enums.PARTICIPANT_ROLE[p.role] || {};
    return {
      key: 'pt' + ((p.user && p.user.id) || i) + '-' + p.role,
      user: p.user,
      name: (p.user && p.user.name) || '',
      roleLabel: role.short || role.label || '',
      roleClass: p.role === 'Lead' ? 'lead' : (p.role === 'Former' ? 'former' : ''),
      hours: onsite ? format.formatDuration((p.totalMinutes || 0) + live) : '',
      onSite: !!p.onSiteSince,
      missing: !!p.missingPunch
    };
  });
}

/** 返厂流程 5 节点时间线 */
function buildFactory(d) {
  const nodes = (d.factory && d.factory.nodes) || [];
  const shippedBack = nodes.some((n) => n.node === 'ShippedBack' && n.recorded);
  const rows = nodes.map((n) => {
    const def = enums.FACTORY_NODE[n.node] || {};
    const skipped = n.state === 'skipped';
    let sub = '';
    let subClass = '';
    if (n.recorded) {
      sub = [(n.operator && n.operator.name) || '', n.time ? format.formatDateTimeShort(n.time) : ''].filter(Boolean).join(' · ');
    } else if (n.node === 'Repairing') {
      if (n.state === 'current') sub = '检测 / 维修中' + (n.time ? ' · ' + format.formatMonthDay(n.time) + '到厂' : '');
      else if (n.state === 'done') sub = '已完成，维修结果见完工信息';
    } else if (skipped) {
      sub = '未记录，主管已直接确认工厂收货';
    } else if (n.node === 'CustomerSigned' && shippedBack && d.status !== 'Closed' && d.status !== 'Voided') {
      sub = '尚未记录客户签收';
      subClass = 'amber';
    } else if (n.state === 'current') {
      sub = '待记录';
    }
    return {
      node: n.node,
      label: n.label || def.label || n.node,
      state: n.state || 'todo',
      skipped: skipped,
      sub: sub,
      subClass: subClass,
      courier: n.courier || '',
      trackingNo: n.trackingNo || '',
      note: n.note || '',
      photos: (n.photos || []).map((f) => ({ id: f.id, url: f.url })),
      canRecord: !!n.canRecord && !!def.action,
      actionLabel: def.action || '',
      canEdit: !!n.canEdit
    };
  });
  rows.forEach((r, i) => {
    const next = rows[i + 1];
    r.last = !next;
    r.lineOn = !!next && r.state === 'done' && (next.state === 'done' || next.state === 'current');
  });
  return rows;
}

/** 待验收工单是否只差定价就能闭环（缺卡优先，由服务端的 closeBlockedReason 判定） */
function needPriceToClose(d) {
  return !!d && d.status === 'Submitted' && d.priceSet === false && String(d.closeBlockedReason || '').indexOf('价格') >= 0;
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
    priceClass: '',     // green（保内）/ red（主管未定价）/ muted（销售未定价）
    priceLink: '',      // 定价 / 改价 / 提建议
    typeText: '',       // 工程师头部第 1 格：维修类型（工程师不显示价格）
    suggestion: null,   // { text, tag, tone:'amber'|'plain', tappable }
    requests: [],
    requestRejected: '',
    holdBar: null,
    participants: [],
    canAssistants: false,
    canRequestAssistant: false,
    factoryNodes: [],
    closeHintText: '',
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
    attachments: [],          // v1.2 附件列表（见 buildAttachments）
    canAddAttachment: false,
    attachHint: attachments.HINT,
    attachSheet: emptyAttachSheet(),
    issues: [],
    buttons: [],

    sheetShow: false,
    sheetMode: 'assign',
    holdReasons: enums.HOLD_REASON_OPTIONS,
    holdPlaceholder: HOLD_NOTE_PLACEHOLDER,
    holdSheet: { show: false, mode: 'hold', title: '', reasonType: '', note: '' },
    priceSheet: { show: false, mode: 'set', title: '', amount: '', note: '', hint: '', suggestText: '', suggestAmount: '' },
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
    this.unloaded = true;
    this.data.attachSheet.items.forEach((x) => attachments.cancel(x.key));
    attachments.resetCache();
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
    const isFactory = d.serviceType === 'Factory';
    const tabs = isEngineerView ? (isFactory ? ENGINEER_FACTORY_TABS : ENGINEER_TABS) : VIEWER_TABS;
    let tab = this.data.tab;
    if (!this.tabTouched || !tabs.some((t) => t.key === tab)) {
      // 工程师看自己参与的返厂单：默认「返厂流程」
      tab = isEngineerView && isFactory && (d.myRole || d.status !== 'Pending') ? 'factory' : tabs[0].key;
    }
    const actions = d.actions || [];
    const reqs = buildRequests(d, isEngineerView);

    this.setData(Object.assign({
      loading: false,
      error: '',
      order: d,
      role: role,
      isEngineerView: isEngineerView,
      isSupervisor: isSupervisor,
      tabs: tabs,
      tab: tab,
      requests: reqs.list,
      requestRejected: reqs.rejected,
      holdBar: buildHoldBar(d),
      canAssistants: actions.indexOf('assistants') >= 0,
      canRequestAssistant: actions.indexOf('requestAssistant') >= 0,
      factoryNodes: buildFactory(d),
      closeHintText: isSupervisor && (d.closeHints || []).length ? '闭环提示：' + d.closeHints.join('；') : '',
      typeText: enums.label(enums.SERVICE_TYPE, d.serviceType),
      creatorLabel: d.creator && d.creator.role === 'Sales' ? '销售' : '建单人',
      assigneeSub: this.assigneeSub(d),
      infoRows: this.infoRows(d),
      orderRows: this.orderRows(d),
      completionRows: this.completionRows(d),
      canNav: typeof d.latitude === 'number' && typeof d.longitude === 'number' && !(d.latitude === 0 && d.longitude === 0),
      contactPhoneText: format.formatPhone(d.contactPhone),
      files: buildFiles(d, isEngineerView),
      attachments: buildAttachments(d),
      canAddAttachment: !!d.canAddAttachment,
      issues: this.buildIssues(d),
      buttons: this.buildButtons(d, role),
      progressNote: this.progressNote(d, isSupervisor)
    }, this.priceView(d, role), this.computeLive(d)));

    // 添加附件弹层开着时权限没了（如工单已闭环 / 作废）→ 关闭并丢弃未提交的文件
    if (this.data.attachSheet.show && !d.canAddAttachment) this.closeAttachSheet();

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
    // 计时中的分钟：有 participants 时按人累加，否则用工单级 onSiteSince
    const parts = d.participants || [];
    const extra = parts.length
      ? parts.reduce((s, p) => s + (p.onSiteSince ? minutesSince(p.onSiteSince) : 0), 0)
      : (d.onSiteSince ? minutesSince(d.onSiteSince) : 0);
    this.running = running || parts.some((p) => !!p.onSiteSince);
    return {
      days: built.days,
      participants: buildParticipants(d),
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

  /** 头部价格格与价格建议行。工程师：不返回任何价格内容（接口也不会给） */
  priceView(d, role) {
    if (role === 'Engineer') return { priceText: '', priceClass: '', priceLink: '', suggestion: null };
    const actions = d.actions || [];
    const isSupervisor = role === 'Supervisor';
    const unpriced = d.priceSet === false || d.price === null || d.price === undefined;
    let link = '';
    if (isSupervisor && actions.indexOf('setPrice') >= 0) link = unpriced ? '定价' : '改价';
    else if (role === 'Sales' && actions.indexOf('suggestPrice') >= 0) link = '提建议';

    let suggestion = null;
    const ps = d.priceSuggestion;
    if (ps) {
      const money = format.formatPrice(ps.amount, false, true);
      if (isSupervisor) {
        if (ps.pending) {
          suggestion = { text: ((ps.by && ps.by.name) || '销售') + ' 建议价格 ' + money, note: ps.note || '', tag: '', tone: 'amber', tappable: actions.indexOf('setPrice') >= 0 };
        }
      } else {
        suggestion = { text: '我方建议 ' + money + (ps.note ? ' · ' + ps.note : ''), note: '', tag: ps.pending ? '待主管确认' : '', tone: 'plain', tappable: false };
      }
    }
    return {
      priceText: format.formatPrice(d.price, false, d.priceSet),
      priceClass: unpriced ? (isSupervisor ? 'red unpriced' : 'muted-3 unpriced') : (Number(d.price) === 0 ? 'green' : ''),
      priceLink: link,
      suggestion: suggestion
    };
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
    const order = role === 'Supervisor' ? SUPERVISOR_ORDER : (role === 'Engineer' ? ENGINEER_ORDER : SALES_ORDER);
    const next = nextFactoryNode(d);
    let list = order.filter((a) => actions.indexOf(a) >= 0 && (a !== 'factory' || next)).map((a) => ({
      key: a,
      // factory 按钮文案 = 第一个可记录节点的动作文案
      label: a === 'factory' ? enums.FACTORY_NODE[next.node].action : (LABEL_OVERRIDE[a] || enums.label(enums.ACTION, a)),
      kind: 'secondary',
      disabled: false,
      tip: ''
    }));
    if (role === 'Supervisor' && d.status === 'Submitted' && actions.indexOf('close') < 0 && needPriceToClose(d) && actions.indexOf('setPrice') >= 0) {
      // 只差定价：按钮可点，先弹出定价，保存后接着确认闭环
      list.push({ key: 'close', label: '定价并闭环', kind: 'secondary', disabled: false, tip: '' });
    } else if (role === 'Supervisor' && d.status === 'Submitted' && actions.indexOf('close') < 0) {
      list.push({ key: 'close', label: enums.label(enums.ACTION, 'close'), kind: 'disabled', disabled: true, tip: d.closeBlockedReason || '请先补录缺卡' });
    }
    // 最多 3 个按钮：顺序靠前的多余项收进「更多」
    this.moreActions = [];
    if (list.length > MAX_BUTTONS) {
      this.moreActions = list.slice(0, list.length - (MAX_BUTTONS - 1));
      list = [{ key: 'more', label: '更多', kind: 'secondary', disabled: false, tip: '' }].concat(list.slice(list.length - (MAX_BUTTONS - 1)));
    }
    for (let i = list.length - 1; i >= 0; i--) {
      if (!list[i].disabled && list[i].key !== 'more') {
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
    const actions = d.actions || [];
    const next = nextFactoryNode(d);
    const acts = SUPERVISOR_ORDER.filter((a) => actions.indexOf(a) >= 0 && (a !== 'factory' || next))
      .map((a) => (a === 'factory' ? enums.FACTORY_NODE[next.node].action : (LABEL_OVERRIDE[a] || enums.label(enums.ACTION, a))));
    const inPage = [];
    if (actions.indexOf('setPrice') >= 0) inPage.push('定价（头部「服务价格」）');
    if (actions.indexOf('assistants') >= 0) inPage.push('调整协助人（信息 →「处理人员」）');
    const parts = [];
    if (acts.length) parts.push('主管可在底部操作：' + acts.join('、'));
    if (inPage.length) parts.push('页面内可' + inPage.join('、'));
    if (actions.indexOf('supplement') >= 0) parts.push('缺卡请在上方警示框中补录离场时间');
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
    if (key === 'more') {
      const more = this.moreActions || [];
      if (!more.length) return;
      wx.showActionSheet({
        itemList: more.map((m) => m.label),
        success: (res) => {
          const m = more[res.tapIndex];
          if (m) this.doAction(m.key);
        },
        fail() {}
      });
      return;
    }
    this.doAction(key);
  },

  doAction(key) {
    if (this.data.busy) return;
    const id = this.orderId;
    const d = this.detail || {};
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
      case 'factory': {
        const next = nextFactoryNode(d);
        if (next) this.goFactoryNode(next.node);
        break;
      }
      case 'assign':
      case 'reassign':
        this.setData({ sheetMode: 'assign', sheetShow: true });
        break;
      case 'void':
        this.askReason('作废工单', '请输入作废原因', '作废').then((r) => {
          this.run(api.orders.void(id, r, { loading: '提交中' }), '已作废');
        });
        break;
      case 'hold':
        this.openHoldSheet('hold');
        break;
      case 'requestHold':
        this.openHoldSheet('request');
        break;
      case 'reject':
        this.askReason('退回处理中', '请输入退回原因', '退回').then((r) => {
          this.run(api.orders.reject(id, r, { loading: '提交中' }), '已退回处理中');
        });
        break;
      case 'resume': {
        const from = (d.hold && d.hold.fromStatus) || d.holdFromStatus || 'InProgress';
        this.confirm('恢复处理', '确认恢复处理？工单将回到「' + enums.label(enums.STATUS, from) + '」。', '恢复').then(() => {
          this.run(api.orders.resume(id, { loading: '提交中' }), '已恢复处理');
        });
        break;
      }
      case 'close': {
        if (needPriceToClose(d)) {
          this.closeAfterPrice = true;
          this.openPriceSheet('set');
          break;
        }
        const hints = d.closeHints || [];
        const content = '确认资料齐全并闭环此工单？闭环后不可再修改。' + (hints.length ? '\n提示：' + hints.join('；') : '');
        this.confirm('确认闭环', content, '确认闭环').then(() => {
          this.run(api.orders.close(id, { loading: '提交中' }), '已闭环');
        });
        break;
      }
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

  /* ---------------- 暂停 / 申请暂停（共用弹层） ---------------- */
  openHoldSheet(mode) {
    this.setData({
      holdSheet: { show: true, mode: mode, title: mode === 'request' ? '申请暂停' : '标为暂停搁置', reasonType: '', note: '' }
    });
  },

  closeHoldSheet() {
    this.setData({ 'holdSheet.show': false });
  },

  onHoldReason(e) {
    this.setData({ 'holdSheet.reasonType': e.currentTarget.dataset.value });
  },

  onHoldNote(e) {
    this.setData({ 'holdSheet.note': e.detail.value });
  },

  saveHold() {
    const h = this.data.holdSheet;
    if (this.data.busy) return;
    const note = (h.note || '').trim();
    if (!h.reasonType) {
      wx.showToast({ title: '请选择暂停原因', icon: 'none' });
      return;
    }
    if (h.reasonType === 'Other' && !note) {
      wx.showToast({ title: '请填写暂停说明', icon: 'none' });
      return;
    }
    if (note.length > 200) {
      wx.showToast({ title: '暂停说明不能超过 200 个字', icon: 'none' });
      return;
    }
    const id = this.orderId;
    const request = h.mode === 'request';
    const p = request
      ? api.orders.requestHold(id, h.reasonType, note, { loading: '提交中' })
      : api.orders.hold(id, h.reasonType, note, { loading: '提交中' });
    this.run(p, request ? '已提交申请' : '已暂停搁置').then((d) => {
      if (d) this.setData({ 'holdSheet.show': false });
    });
  },

  /* ---------------- 申请审批（暂停 / 协助人） ---------------- */
  findRequest(e) {
    const id = Number(e.currentTarget.dataset.id);
    return this.data.requests.find((r) => r.id === id) || null;
  },

  onApproveRequest(e) {
    const r = this.findRequest(e);
    if (!r || this.data.busy) return;
    this.confirm('同意' + r.title, r.text + (r.type === 'Hold' ? '\n同意后工单进入「暂停搁置」。' : '\n同意后对方成为本单协助人。'), '同意').then(() => {
      this.run(api.orders.approveRequest(this.orderId, r.id, { loading: '提交中' }), '已同意');
    });
  },

  onRejectRequest(e) {
    const r = this.findRequest(e);
    if (!r || this.data.busy) return;
    wx.showModal({
      title: '驳回' + r.title,
      editable: true,
      placeholderText: '回复（选填），如：备件明天到，先别停',
      confirmText: '驳回',
      confirmColor: enums.COLORS.red,
      success: (res) => {
        if (!res.confirm) return;
        const reply = (res.content || '').trim();
        if (reply.length > 200) {
          wx.showToast({ title: '回复不能超过 200 个字', icon: 'none' });
          return;
        }
        this.run(api.orders.rejectRequest(this.orderId, r.id, reply, { loading: '提交中' }), '已驳回');
      }
    });
  },

  onCancelRequest(e) {
    const r = this.findRequest(e);
    if (!r || this.data.busy) return;
    this.confirm('撤回' + r.title, '确认撤回这条申请？', '撤回').then(() => {
      this.run(api.orders.cancelRequest(this.orderId, r.id, { loading: '提交中' }), '已撤回');
    });
  },

  /* ---------------- 定价（主管） / 价格建议（销售） ---------------- */
  onPriceLink() {
    const d = this.detail;
    if (!d || !this.data.priceLink) return;
    const actions = d.actions || [];
    if (this.data.role === 'Supervisor' && actions.indexOf('setPrice') >= 0) this.openPriceSheet('set');
    else if (this.data.role === 'Sales' && actions.indexOf('suggestPrice') >= 0) this.openPriceSheet('suggest');
  },

  onSuggestionTap() {
    const s = this.data.suggestion;
    if (s && s.tappable) this.openPriceSheet('set');
  },

  openPriceSheet(mode) {
    const d = this.detail || {};
    const ps = d.priceSuggestion;
    const priced = d.priceSet !== false && d.price !== null && d.price !== undefined;
    const sheet = { show: true, mode: mode, title: '', amount: '', note: '', hint: '', suggestText: '', suggestAmount: '' };
    if (mode === 'set') {
      sheet.title = priced ? '改价' : '定价';
      sheet.amount = priced ? String(d.price) : '';
      sheet.hint = (this.closeAfterPrice ? '闭环前需要先确定服务价格。' : '') + '保内免费填 0。价格仅销售和主管可见，工程师看不到。';
      if (ps && ps.pending) {
        sheet.suggestText = ((ps.by && ps.by.name) || '销售') + ' 建议 ' + format.formatPrice(ps.amount, false, true) + (ps.note ? ' · ' + ps.note : '');
        sheet.suggestAmount = String(ps.amount);
      }
    } else {
      sheet.title = '价格建议';
      sheet.hint = '建议仅供主管定价参考，最终价格由主管确定。保内免费填 0。';
      if (ps) {
        sheet.amount = String(ps.amount);
        sheet.note = ps.note || '';
      }
    }
    this.setData({ priceSheet: sheet });
  },

  closePriceSheet() {
    this.closeAfterPrice = false;
    this.setData({ 'priceSheet.show': false });
  },

  onPriceAmount(e) {
    const v = cleanAmount(e.detail.value);
    this.setData({ 'priceSheet.amount': v });
    return v;
  },

  onPriceNote(e) {
    this.setData({ 'priceSheet.note': e.detail.value });
  },

  onAdoptSuggestion() {
    const s = this.data.priceSheet;
    if (s.suggestAmount !== '') this.setData({ 'priceSheet.amount': s.suggestAmount });
  },

  savePrice() {
    const s = this.data.priceSheet;
    if (this.data.busy) return;
    const suggest = s.mode === 'suggest';
    const raw = String(s.amount || '').trim();
    if (!raw) {
      wx.showToast({ title: suggest ? '请填写建议价格' : '请填写服务价格（保内填 0）', icon: 'none' });
      return;
    }
    if (!/^\d+(\.\d{1,2})?$/.test(raw)) {
      wx.showToast({ title: (suggest ? '建议价格' : '服务价格') + '格式不正确', icon: 'none' });
      return;
    }
    const amount = Number(raw);
    if (amount > MAX_AMOUNT) {
      wx.showToast({ title: (suggest ? '建议价格' : '服务价格') + '过大', icon: 'none' });
      return;
    }
    const note = (s.note || '').trim();
    if (suggest && note.length > 200) {
      wx.showToast({ title: '备注不能超过 200 个字', icon: 'none' });
      return;
    }
    const id = this.orderId;
    const p = suggest
      ? api.orders.suggestPrice(id, amount, note, { loading: '提交中' })
      : api.orders.setPrice(id, amount, { loading: '保存中' });
    const thenClose = !suggest && this.closeAfterPrice;
    this.closeAfterPrice = false;
    this.run(p, suggest ? '已提交价格建议' : '价格已保存').then((d) => {
      if (!d) return;
      this.setData({ 'priceSheet.show': false });
      // 从「定价并闭环」进来：价格保存成功后接着走确认闭环
      if (thenClose && (d.actions || []).indexOf('close') >= 0) this.doAction('close');
    });
  },

  /* ---------------- 附件（v1.2） ---------------- */
  findAttachment(e) {
    const id = Number(e.currentTarget.dataset.id);
    return ((this.detail && this.detail.attachments) || []).find((a) => a.id === id) || null;
  },

  /** 点附件行：图片 / 视频原文件预览；文档下载后打开 */
  onAttachmentTap(e) {
    const att = this.findAttachment(e);
    if (att) attachments.open(att, this.detail.attachments);
  },

  /** 点「…」：切换可见范围 / 删除（按服务端的 canToggleRestricted / canDelete） */
  onAttachmentMore(e) {
    const att = this.findAttachment(e);
    if (!att || this.data.busy) return;
    const items = [];
    if (att.canToggleRestricted) items.push({ key: 'toggle', label: att.restricted ? '设为所有人可见' : '设为仅销售和主管可见' });
    if (att.canDelete) items.push({ key: 'delete', label: '删除' });
    if (!items.length) return;
    const id = this.orderId;
    wx.showActionSheet({
      itemList: items.map((x) => x.label),
      success: (res) => {
        const pick = items[res.tapIndex];
        if (!pick) return;
        if (pick.key === 'toggle') {
          this.run(api.orders.setAttachmentRestricted(id, att.id, !att.restricted, { loading: '提交中' }), '已更新', true);
        } else {
          this.confirm('删除附件', '确定删除「' + att.name + '」？删除后不可恢复。', '删除').then(() => {
            this.run(api.orders.deleteAttachment(id, att.id, { loading: '提交中' }), '已删除', true);
          });
        }
      },
      fail() {}
    });
  },

  /** 本单还能再加几个附件（服务端给出，含当前用户看不到的受限附件） */
  attachRemaining() {
    const d = this.detail || {};
    const n = Number(d.attachmentRemaining);
    return isNaN(n) ? Math.max(0, attachments.MAX_COUNT - (d.attachments || []).length) : Math.max(0, n);
  },

  attachLimitText(remaining) {
    return remaining > 0 ? '附件最多 20 个，本单还可添加 ' + remaining + ' 个' : '附件最多 20 个，本单已达上限';
  },

  openAttachSheet() {
    const d = this.detail;
    if (!d || !d.canAddAttachment) return;
    if (this.attachRemaining() === 0) {
      wx.showToast({ title: this.attachLimitText(0), icon: 'none' });
      return;
    }
    this.attachSeq = (this.attachSeq || 0) + 1;
    this.setData({ attachSheet: Object.assign(emptyAttachSheet(), { show: true, canRestrict: !!d.canRestrictAttachment }) });
  },

  /** 关闭弹层：未提交的文件直接丢弃（服务端留作未挂载文件，无需清理） */
  closeAttachSheet() {
    this.data.attachSheet.items.forEach((x) => attachments.cancel(x.key));
    this.attachSeq = (this.attachSeq || 0) + 1;
    this.setData({ attachSheet: emptyAttachSheet() });
  },

  onAttachRestricted(e) {
    if (!this.data.attachSheet.canRestrict) return;
    this.setData({ 'attachSheet.restricted': !!e.detail.value });
  },

  /** 已选列表变化后重算「上传中 / 已上传数」 */
  attachSummary(items) {
    return {
      'attachSheet.uploading': items.some((x) => x.status === 'uploading'),
      'attachSheet.doneCount': items.filter((x) => x.status === 'done' && x.fileId).length
    };
  },

  patchAttachItem(key, patch, seq) {
    if (this.unloaded || seq !== this.attachSeq) return;
    const items = this.data.attachSheet.items;
    const i = items.findIndex((x) => x.key === key);
    if (i < 0) return;
    const p = {};
    Object.keys(patch).forEach((k) => { p['attachSheet.items[' + i + '].' + k] = patch[k]; });
    if (patch.status !== undefined || patch.fileId !== undefined) {
      const next = items.map((x, j) => (j === i ? Object.assign({}, x, patch) : x));
      Object.assign(p, this.attachSummary(next));
    }
    this.setData(p);
  },

  uploadAttachItem(item) {
    const seq = this.attachSeq;
    // 传副本：上传模块会直接修改条目，页面数据只通过 setData 更新
    attachments.upload(Object.assign({}, item), (it, patch) => this.patchAttachItem(it.key, patch, seq));
  },

  /** 弹层里的两个选择按钮：data-type = media | chat */
  onAttachPick(e) {
    const type = e.currentTarget.dataset.type;
    const sheet = this.data.attachSheet;
    if (!sheet.show) return;
    const remaining = this.attachRemaining();
    const left = remaining - sheet.items.length;
    if (left <= 0) {
      wx.showToast({ title: this.attachLimitText(remaining), icon: 'none' });
      return;
    }
    const seq = this.attachSeq;
    (type === 'chat' ? attachments.pickChatFiles(left) : attachments.pickMedia(left)).then((picked) => {
      if (this.unloaded || seq !== this.attachSeq || !picked.length) return;
      const cur = this.data.attachSheet.items;
      const add = picked.slice(0, Math.max(0, this.attachRemaining() - cur.length));
      if (!add.length) return;
      const items = cur.concat(add);
      this.setData(Object.assign({ 'attachSheet.items': items }, this.attachSummary(items)));
      add.filter((x) => !x.fatal).forEach((x) => this.uploadAttachItem(x));
    });
  },

  onAttachRetry(e) {
    const key = e.currentTarget.dataset.key;
    const item = this.data.attachSheet.items.find((x) => x.key === key);
    if (!item || item.fatal || item.status !== 'fail') return;
    this.uploadAttachItem(item);
  },

  onAttachRemove(e) {
    const key = e.currentTarget.dataset.key;
    attachments.cancel(key);
    const items = this.data.attachSheet.items.filter((x) => x.key !== key);
    this.setData(Object.assign({ 'attachSheet.items': items }, this.attachSummary(items)));
  },

  onAttachPreview(e) {
    const key = e.currentTarget.dataset.key;
    const items = this.data.attachSheet.items;
    const item = items.find((x) => x.key === key);
    if (item && !item.fatal) attachments.openLocal(item, items);
  },

  saveAttachments() {
    const sheet = this.data.attachSheet;
    if (this.data.busy || !sheet.show) return;
    if (sheet.uploading) {
      wx.showToast({ title: '附件还在上传，请稍候', icon: 'none' });
      return;
    }
    if (sheet.items.some((x) => x.status === 'fail')) {
      wx.showToast({ title: '有附件上传失败，请重试或移除', icon: 'none' });
      return;
    }
    const done = sheet.items.filter((x) => x.status === 'done' && x.fileId);
    if (!done.length) {
      wx.showToast({ title: '请选择要添加的附件', icon: 'none' });
      return;
    }
    const restricted = sheet.canRestrict && !!sheet.restricted;
    const body = done.map((x) => ({ fileId: x.fileId, restricted: restricted }));
    this.run(api.orders.addAttachments(this.orderId, body, { loading: '提交中' }), '已添加').then((d) => {
      if (!d) return;
      this.attachSeq = (this.attachSeq || 0) + 1;
      this.setData({ attachSheet: emptyAttachSheet() });
    });
  },

  /* ---------------- 协助人 ---------------- */
  onEditAssistants() {
    if (this.data.canAssistants) this.setData({ sheetMode: 'assistants', sheetShow: true });
  },

  onRequestAssistant() {
    if (this.data.canRequestAssistant) this.setData({ sheetMode: 'request', sheetShow: true });
  },

  /* ---------------- 返厂流程 ---------------- */
  goFactoryNode(node) {
    wx.navigateTo({ url: '/pages/factory-node/index?id=' + this.orderId + '&node=' + node });
  },

  onFactoryNode(e) {
    const node = e.currentTarget.dataset.node;
    if (node) this.goFactoryNode(node);
  },

  /** 点击复制快递单号 */
  onCopyTracking(e) {
    const no = e.currentTarget.dataset.no;
    if (!no) return;
    wx.setClipboardData({
      data: String(no),
      success: () => wx.showToast({ title: '单号已复制', icon: 'none' }),
      fail() {}
    });
  },

  onPreviewFactoryPhoto(e) {
    const node = e.currentTarget.dataset.node;
    const url = e.currentTarget.dataset.url;
    const row = this.data.factoryNodes.find((n) => n.node === node);
    const urls = row ? row.photos.map((p) => p.url) : [];
    if (!url) return;
    wx.previewImage({ urls: urls.indexOf(url) >= 0 ? urls : [url], current: url });
  },

  /* ---------------- 指派 / 协助人弹层 ---------------- */
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
