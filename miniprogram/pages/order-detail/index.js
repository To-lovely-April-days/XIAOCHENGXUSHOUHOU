/**
 * 工单详情（原型 06 工程师 / 13 销售主管 / 16 主管操作）。
 * query.id：工单 id。页面按当前角色与 OrderDetail.actions 渲染按钮。
 * v1.3（docs/CHANGES-v1.3.md §9.6）：多台设备与「修改 SAT 信息」、附件按三类分组（上传选类别 / 改类别，闭环后也能添加）、
 *   转交（发起 / 撤回 / 确认接手 / 拒绝）、已退出只读提示。按钮与入口一律以服务端的 actions / can* 为准，不在前端推导权限。
 */
const api = require('../../api/index.js');
const auth = require('../../utils/auth.js');
const format = require('../../utils/format.js');
const enums = require('../../utils/enums.js');
const location = require('../../utils/location.js');
const attachments = require('../../utils/attachments.js');
const subscribe = require('../../utils/subscribe.js');

const ENGINEER_TABS = [{ key: 'info', label: '信息' }, { key: 'punch', label: '打卡记录' }, { key: 'files', label: '附件资料' }];
// 返厂单（工程师）：「返厂流程」取代「打卡记录」
const ENGINEER_FACTORY_TABS = [{ key: 'info', label: '信息' }, { key: 'factory', label: '返厂流程' }, { key: 'files', label: '附件资料' }];
const VIEWER_TABS = [{ key: 'progress', label: '进度' }, { key: 'info', label: '信息' }, { key: 'files', label: '附件资料' }];

// 底部按钮顺序：越靠后越主要（最后一个可用按钮为主按钮）。
// setPrice / suggestPrice / assistants / requestAssistant 是页面内链接，不进底部栏；
// acceptTransfer / rejectTransfer / cancelTransfer 只在「转交待确认」卡片里。transfer 最不优先，按钮多时进「更多」。
const ENGINEER_ORDER = ['transfer', 'requestEscalate', 'requestHold', 'appoint', 'checkin', 'start', 'claim', 'factory', 'complete'];
const SUPERVISOR_ORDER = ['void', 'hold', 'reject', 'reassign', 'resume', 'assign', 'factory', 'close'];
const SALES_ORDER = ['factory'];
const MAX_BUTTONS = 3; // 超过时把顺序靠前的多余项收进「更多」
const LABEL_OVERRIDE = { appoint: '设置预约', reject: '退回处理中', hold: '标为暂停搁置' };

const SALES_NOTE = '销售可查看进度、提价格建议；改派、作废、定价、补录由主管操作。';
const HOLD_NOTE_PLACEHOLDER = '如：客户下周一才能安排停机';
const MAX_AMOUNT = 100000000;
const HOME = '/pages/home/index';
/** 附件类别的展示顺序：Sat → RepairForm → Other */
const ATT_TYPES = Object.keys(enums.ATTACHMENT_TYPE);
/** v2.1 两张维修登记表跟着服务方式走：只能在对应流程里上传，不出现在通用的「添加附件」类别里 */
const FORM_SERVICE = { OnsiteRepairForm: 'Onsite', FactoryRepairForm: 'Factory' };
const FORM_OF = FORM_SERVICE;
const GENERAL_ATT_OPTIONS = enums.ATTACHMENT_TYPE_OPTIONS.filter((o) => !FORM_SERVICE[o.value]);
const ISO_DATE = /^\d{4}-\d{2}-\d{2}$/;

function satDateLabelOf(satSigned) {
  return satSigned ? 'SAT 完成时间' : '发货时间';
}

function emptySatSheet() {
  return { show: false, satSigned: false, satDate: '', label: satDateLabelOf(false), legacy: '', pickerValue: '' };
}

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
    { key: 'processPhotos', title: d.serviceType === 'Remote' ? '沟通截图' : '处理照片',
      desc: d.serviceType === 'Onsite' ? '完工时上传，维修前、维修后各至少 1 张' : d.serviceType === 'Remote' ? '完工时上传，选填' : '完工时上传',
      list: f.processPhotos },
    // v2.0：上门的签字改为「现场维修登记表（签字版）」，在附件里；旧单已有的签字件照常显示
    { key: 'signatures', title: '客户签字件', desc: '现场维修记录表，签字后拍照', list: f.signatures, hide: !(f.signatures || []).length },
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

function attTypeOf(a) {
  return ATT_TYPES.indexOf(a && a.attachmentType) >= 0 ? a.attachmentType : 'Other';
}

/**
 * 附件列表（v1.2；v1.3 按类别分三组，空组也保留）。只展示服务端返回的内容与 can* 标记，不在前端推导权限；
 * 工程师拿不到「仅销售和主管可见」的附件。
 * @returns {Array<{type:string,label:string,count:number,items:Object[]}>} 顺序 Sat → RepairForm → Other，组内保持服务端顺序
 */
function buildAttachments(d) {
  const all = d.attachments || [];
  return ATT_TYPES.map((type) => {
    const items = all.filter((a) => attTypeOf(a) === type).map((a, i) => {
      const b = attachments.badge(a);
      const sub = [attachments.fmtSize(a.size), (a.uploader && a.uploader.name) || '', monthDayHm(a.createdAt)].filter(Boolean).join(' · ');
      return {
        id: a.id,
        name: a.name || '',
        badgeText: b.text,
        badgeColor: b.color,
        sub: sub,
        restricted: !!a.restricted,
        first: i === 0,
        hasMenu: !!(a.canDelete || a.canToggleRestricted || a.canChangeType)
      };
    });
    return { type: type, label: enums.ATTACHMENT_TYPE[type].label, count: items.length, items: items, isForm: !!FORM_OF[type] };
  }).filter((g) => !g.isForm || g.count > 0 || FORM_SERVICE[g.type] === d.serviceType);
}

function emptyAttachSheet() {
  // fixed：从流程里点「上传登记表」进来，类别固定，不显示类别选择
  return { show: false, type: 'Other', fixed: false, title: '添加附件', canRestrict: false, restricted: false, items: [], uploading: false, doneCount: 0 };
}

/** v2.1 流程里的登记表卡片：上门 = 现场维修登记表，返厂 = 返厂维修登记表；远程没有 */
function buildFormCard(d) {
  const type = d.serviceType === 'Onsite' ? 'OnsiteRepairForm' : d.serviceType === 'Factory' ? 'FactoryRepairForm' : '';
  if (!type) return null;
  const items = (d.attachments || []).filter((a) => a.attachmentType === type);
  return {
    type: type,
    title: enums.ATTACHMENT_TYPE[type].label,
    count: items.length,
    text: items.length ? '已上传 ' + items.length + ' 份' : '未上传',
    tip: d.serviceType === 'Onsite' ? '客户签字后拍照上传；提交完工前必须有' : '维修完成后上传；提交完工前必须有',
    canAdd: !!d.canAddAttachment
  };
}

/** v2.1 每日工作内容：按天分组，新的在前 */
function buildWorkDays(d) {
  const days = [];
  (d.workLogs || []).forEach((l) => {
    let day = days.filter((x) => x.date === l.date)[0];
    if (!day) { day = { date: l.date, dateText: format.formatDateDot ? format.formatDateDot(l.date) : l.date, items: [] }; days.push(day); }
    day.items.push({ id: l.id, name: (l.engineer && l.engineer.name) || '', content: l.content, canEdit: !!l.canEdit });
  });
  return days;
}

/** 「设备资料」卡片：项目编号、逐台设备（只列填了的项）、SAT 信息 */
function buildDeviceCard(d) {
  const devices = (d.devices || []).map((x, i) => {
    const seq = x.seq || i + 1;
    const rows = [
      { label: '名称', value: x.name },
      { label: '编号', value: x.sn },
      { label: '型号', value: x.model }
    ].filter((r) => r.value);
    rows.forEach((r, j) => { r.last = j === rows.length - 1; });
    return { key: 'dv' + seq, title: '设备 ' + seq, rows: rows };
  });
  return {
    projectNo: d.projectNo || '—',
    devices: devices,
    satText: d.satText || (d.satSigned ? '已完成' : '未完成'),
    satDateLabel: d.satDateLabel || satDateLabelOf(d.satSigned),
    satDate: d.satDate || '未填写',
    satDateEmpty: !d.satDate
  };
}

/**
 * 转交（v1.3）：待确认的转交卡片（所有能看到本单的人可见，按钮只看 actions）；
 * 负责工程师另显示本人最近一条被拒绝的转交（服务端文字）。
 */
function buildTransfer(d, role) {
  const actions = d.actions || [];
  const t = d.pendingTransfer;
  let card = null;
  if (t) {
    const from = (t.from && t.from.name) || '';
    const to = (t.to && t.to.name) || '';
    const canAccept = actions.indexOf('acceptTransfer') >= 0;
    const canReject = actions.indexOf('rejectTransfer') >= 0;
    const canCancel = actions.indexOf('cancelTransfer') >= 0;
    let text = from + ' 已发起转交，等待 ' + to + ' 确认';
    if (canAccept) text = from + ' 请你接手本单';
    else if (canCancel && role === 'Engineer') text = '已发起转交，等待 ' + to + ' 确认';
    card = {
      id: t.id,
      time: format.formatDateTimeShort(t.createdAt),
      text: text,
      reason: t.reason || '',
      canAccept: canAccept,
      canReject: canReject,
      canCancel: canCancel,
      cancelLabel: role === 'Supervisor' ? '撤销转交' : '撤回转交'
    };
  }
  let rejected = '';
  // 与「申请被驳回」的提示一致：只在还能再转交的状态（待启动 / 处理中 / 暂停搁置）显示，待验收 / 闭环后不再挂着
  if (!t && role === 'Engineer' && d.myRole === 'Lead' && ['Accepted', 'InProgress', 'OnHold'].indexOf(d.status) >= 0) {
    const all = d.transfers || [];
    const last = all[all.length - 1];
    if (last && last.status === 'Rejected' && last.from && d.assignee && last.from.id === d.assignee.id) rejected = last.text || '';
  }
  return { card: card, rejected: rejected };
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
      // 带退出记录的「已退出」：已转交 / 已改派（由打卡推导出来的没有）
      exitText: p.role === 'Former' && p.exitText ? p.exitText : '',
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

/**
 * v2.2 收费确认卡片。没发起过、当前用户也不用判断时不显示（返回 null）。
 * 工程师看不到金额（服务端不返回），只看到收不收费和走到哪一步。
 */
function buildCharge(d, isEngineerView) {
  const c = d.charge;
  if (!c) return null;
  if (c.status === 'None' && !c.canDecide) return null;
  const tone = c.status === 'Confirmed' ? 'green' : c.status === 'Free' ? 'gray' : c.status === 'None' ? 'gray' : 'amber';
  const steps = (c.steps || []).map((s, i) => ({
    key: i,
    title: s.title,
    state: s.done ? 'done' : s.current ? 'cur' : '',
    sub: s.done
      ? [s.who, s.time ? format.formatDateTimeShort(s.time) : '', s.note].filter(Boolean).join(' · ')
      : s.current ? '等待 ' + (s.who || '') + ' 处理' : ''
  }));
  const facts = [];
  if (!isEngineerView && c.amount !== null && c.amount !== undefined) facts.push({ label: '收费金额', value: format.formatPrice(c.amount, false, true) });
  if (c.paidText) facts.push({ label: '付款情况', value: c.paidText, green: c.paid === true });
  if (c.sales && c.status !== 'Free') facts.push({ label: '报价人', value: c.sales.name });
  const action = c.canDecide ? (c.status === 'Free' ? '改为收费' : '去判断')
    : c.canQuote ? '去报价' : c.canReview ? '去审核' : c.canConfirm ? '去确认' : '';
  return {
    status: c.status,
    statusText: c.status === 'None' ? '待判断' : c.status === 'Confirmed' && isEngineerView ? '已确认，可以继续' : c.statusText,
    tone: tone,
    intro: c.status === 'None' ? '认领后先判断这张单要不要收费。要收费的交给销售报价，审核通过后再回到你手上。' : '',
    reason: c.reason && c.status !== 'None' ? c.reason : '',
    quoteNote: c.quoteNote || '',
    directorNote: c.directorNote || '',
    returnNote: c.returnNote || '',
    steps: steps,
    facts: facts,
    action: action,
    actionMinor: c.canDecide && c.status === 'Free',
    blockedHint: isEngineerView && c.pending ? '确认之前暂时不能到场打卡、开始处理和提交完工。' : '',
    // 面板里用
    customer: d.customerName + ' · ' + d.orderNo,
    amountText: c.amount !== null && c.amount !== undefined ? format.formatPrice(c.amount, false, true) : '',
    paidText: c.paidText || '',
    salesName: (c.sales && c.sales.name) || '',
    directorName: (c.director && c.director.name) || '',
    requesterName: (c.requester && c.requester.name) || '',
    assigneeName: (d.assignee && d.assignee.name) || '',
    fixedSalesName: (c.fixedSales && c.fixedSales.name) || ''
  };
}

Page(subscribe.bindTaps({
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
    transferCard: null,       // v1.3 待确认的转交（见 buildTransfer）
    transferRejected: '',     // 负责工程师：本人最近一条转交被拒绝的提示
    deviceCard: null,         // v1.3「设备资料」卡片（见 buildDeviceCard）
    satSheet: emptySatSheet(),
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
    attachGroups: [],         // 附件按类别分三组（见 buildAttachments）
    attachTotal: 0,
    attachTypes: GENERAL_ATT_OPTIONS,
    formCard: null,           // v2.1 流程里的登记表卡片（见 buildFormCard）
    workDays: [],             // v2.1 每日工作内容，按天分组
    canWriteWorkLog: false,
    myTodayWorkLog: '',
    workSheet: { show: false, text: '' },
    // v2.2 收费确认：卡片 + 底部面板（mode: decide 工程师判断 / quote 销售报价 / review 销售总监审核 / confirm 售后主管确认）
    chargeCard: null,
    chargeSheet: { show: false, mode: '', charge: true, reason: '', salesList: [], salesIdx: -1, amount: '', note: '', paid: '' },
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
    // v1.9 确认闭环的底部面板：关键信息 + 备注（选填）+ 常用语
    closeSheet: { show: false, sub: '', worker: '', hints: '', note: '' },
    // v2.0 申请升级服务方式：targets = 可选的目标（远程可选上门 / 返厂，上门只能选返厂）
    escalateSheet: { show: false, from: '', targets: [], target: '', reason: '', findings: '', noAddress: false, address: '', latitude: null, longitude: null },
    closePhrases: ['资料齐全', '客户已签字确认', '已电话回访'],
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

  /**
   * @param {boolean} [afterActionError] 某个操作失败后的自动重新加载：那次失败的原因已经 toast 出来了，
   *   这里即使发现已无查看权也不再弹「无权查看该工单」把它盖掉，直接带离页面
   */
  load(afterActionError) {
    const first = !this.detail;
    if (first) this.setData({ loading: true, error: '' });
    return api.orders.detail(this.orderId, { silent: true }).then((d) => {
      this.applyDetail(d);
    }).catch((err) => {
      const msg = (err && err.message) || '加载失败';
      if (first) this.setData({ loading: false, error: msg });
      // 停在详情页期间失去了查看权（接手人：对方撤回了转交 / 主管改派了；大厅工单被别人认领）→ 带离页面
      else if (err && err.code === 40300) this.leavePage(afterActionError === true ? '' : '无权查看该工单', afterActionError === true ? 1500 : 700);
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
    const transfer = buildTransfer(d, role);
    const attachGroups = buildAttachments(d);

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
      transferCard: transfer.card,
      transferRejected: transfer.rejected,
      chargeCard: buildCharge(d, isEngineerView),
      deviceCard: buildDeviceCard(d),
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
      attachGroups: attachGroups,
      formCard: buildFormCard(d),
      workDays: buildWorkDays(d),
      canWriteWorkLog: !!d.canWriteWorkLog,
      myTodayWorkLog: d.myTodayWorkLog || '',
      attachTotal: attachGroups.reduce((s, g) => s + g.count, 0),
      canAddAttachment: !!d.canAddAttachment,
      issues: this.buildIssues(d),
      buttons: this.buildButtons(d, role),
      progressNote: this.progressNote(d, isSupervisor)
    }, this.priceView(d, role), this.computeLive(d)));

    // 添加附件弹层开着时权限没了（如工单已作废 / 本人已退出）→ 关闭并丢弃未提交的文件
    if (this.data.attachSheet.show && !d.canAddAttachment) this.closeAttachSheet();
    if (this.data.satSheet.show && !d.canEditSat) this.closeSatSheet();
    // 转交弹层开着时已不能转交（如已有待确认的转交 / 已不是负责人）→ 关闭
    if (this.data.sheetShow && this.data.sheetMode === 'transfer' && actions.indexOf('transfer') < 0) this.setData({ sheetShow: false });

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
    const hit = logs.find((l) => {
      const text = l.text || '';
      // 跳过「转交已失效（…）：主管已改派」：文字里也有人名和「改派」，会被误当成改派记录；
      // 发起 / 拒绝 / 撤回转交的记录带用户填写的原因，同样不参与匹配
      if (text.indexOf('转交已失效') === 0 || /发起转交给|拒绝接手|撤回转交|撤销转交/.test(text)) return false;
      return text.indexOf(a.name) >= 0 && /认领|指派|改派|确认接手/.test(text);
    });
    if (hit) {
      verb = /确认接手/.test(hit.text) ? '接手' : (/改派/.test(hit.text) ? '改派' : (/指派/.test(hit.text) ? '指派' : '认领'));
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

  /** 「工单信息」卡片；项目编号、SAT 信息已移到「设备资料」卡片（v1.3） */
  orderRows(d) {
    const rows = [
      { label: '工单来源', value: enums.label(enums.SOURCE, d.source) },
      { label: '问题类型', value: enums.label(enums.PROBLEM_TYPE, d.problemType) },
      { label: '维修类型', value: enums.label(enums.SERVICE_TYPE, d.serviceType) },
      { label: '紧急程度', value: enums.label(enums.URGENCY, d.urgency) },
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
      // v2.0：远程 = 客户确认方式；返厂 = 寄回前测试结果（没有就不显示）
      { label: '客户确认', value: c.customerConfirm, hide: !c.customerConfirm },
      { label: '测试结果', value: c.testResult, hide: !c.testResult },
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
        // v2.2 认领之后接着判断要不要收费（可以点「稍后再说」）
        this.run(api.orders.claim(id, { loading: '认领中' }), '认领成功', true).then((r) => {
          if (r && r.charge && r.charge.canDecide && r.charge.status === 'None') setTimeout(() => this.openChargeSheet(), 600);
        });
        break;
      case 'appoint':
        this.openAppt();
        break;
      case 'checkin':
        wx.navigateTo({ url: '/pages/checkin/index?orderId=' + id });
        break;
      case 'start':
        if (this.chargeBlocked()) break;
        this.confirm('开始处理', '确认开始处理此工单？状态将变为「处理中」。', '开始').then(() => {
          this.run(api.orders.start(id, { loading: '提交中' }), '已开始处理', true);
        });
        break;
      case 'complete':
        if (this.chargeBlocked()) break;
        // 还没判断过要不要收费：先判断，再提交完工
        if (d.charge && d.charge.status === 'None' && d.charge.canDecide) {
          wx.showToast({ title: '请先判断这张单要不要收费', icon: 'none' });
          setTimeout(() => this.openChargeSheet(), 500);
          break;
        }
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
      case 'transfer':
        this.setData({ sheetMode: 'transfer', sheetShow: true });
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
      case 'requestEscalate': {
        const targets = d.serviceType === 'Remote'
          ? [{ value: 'Onsite', label: '上门服务' }, { value: 'Factory', label: '返厂维修' }]
          : [{ value: 'Factory', label: '返厂维修' }];
        this.setData({
          escalateSheet: {
            show: true,
            from: enums.label(enums.SERVICE_TYPE, d.serviceType),
            targets: targets,
            target: targets.length === 1 ? targets[0].value : '',
            reason: '',
            findings: '',
            // 远程单建单时可以不填地址：要转上门就得在这里补上（打卡要算距离）
            noAddress: !d.address || d.latitude === null || d.latitude === undefined || d.longitude === null || d.longitude === undefined,
            address: '',
            latitude: null,
            longitude: null
          }
        });
        break;
      }
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
        // v1.9 底部面板：先看一眼价格 / 工时 / 处理人和提示，备注选填，会写进进度里
        this.setData({
          closeSheet: {
            show: true,
            sub: [d.customerName, d.orderNo].filter(Boolean).join(' · '),
            worker: (d.assignee && d.assignee.name) || '—',
            hints: hints.join('；'),
            note: ''
          }
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
      if (reloadOnError || (err && err.code === 40900)) this.load(true).catch(() => {});
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

  /**
   * 弹出带输入框的确认框。
   * @param {boolean} [optional] true 时允许空内容（resolve ''）
   */
  askReason(title, placeholder, confirmText, optional) {
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
          if (!r && !optional) {
            wx.showToast({ title: '请填写原因', icon: 'none' });
            return;
          }
          resolve(r);
        }
      });
    });
  },

  /**
   * 已无权查看本单（拒绝接手后 / 停在详情页时转交被撤回、失效）：离开详情页，没有上一页则回首页。
   * @param {string} [toastText] 离开前的提示（不传 = 不另外提示，保留已经弹出的那条）
   * @param {number} [delay=700] 多久之后离开（毫秒）
   */
  leavePage(toastText, delay) {
    if (this.leaving) return;
    this.leaving = true;
    this.stopTimer();
    this.setData({ busy: true, sheetShow: false });
    if (toastText) wx.showToast({ title: toastText, icon: 'none' });
    setTimeout(() => {
      if (this.unloaded) return;
      const pages = getCurrentPages();
      // 这期间用户已经从本页进了别的页面：不能把那个页面退掉；等回到本页时 onShow 会重新加载并再次走到这里
      const top = pages[pages.length - 1];
      if (top && top !== this && top.route && this.route && top.route !== this.route) {
        this.leaving = false;
        this.setData({ busy: false });
        return;
      }
      if (pages.length > 1) wx.navigateBack({ fail: () => wx.switchTab({ url: HOME }) });
      else wx.switchTab({ url: HOME });
    }, delay || 700);
  },

  /* ---------------- 转交（v1.3）：确认接手 / 拒绝 / 撤回（撤销） ---------------- */
  /** 本单待确认的转交（TransferDto），没有为 null */
  pendingTransfer() {
    return (this.detail && this.detail.pendingTransfer) || null;
  },

  onAcceptTransfer() {
    const t = this.pendingTransfer();
    if (!t || this.data.busy) return;
    const from = (t.from && t.from.name) || '';
    this.confirm('确认接手', '接手后你将成为本单负责人，' + from + ' 退出本单（其打卡和工时记录保留）。', '确认接手').then(() => {
      this.run(api.orders.acceptTransfer(this.orderId, t.id, { loading: '提交中' }), '已接手', true);
    });
  },

  onRejectTransfer() {
    const t = this.pendingTransfer();
    if (!t || this.data.busy) return;
    this.askReason('拒绝接手', '拒绝理由（选填）', '拒绝', true).then((reply) => {
      if (reply.length > 200) {
        wx.showToast({ title: '拒绝理由不能超过 200 个字', icon: 'none' });
        return;
      }
      this.run(api.orders.rejectTransfer(this.orderId, t.id, reply, { loading: '提交中' }), '已拒绝', true).then((d) => {
        // 拒绝成功且已无查看权 → 接口返回 null（失败时 run 给的是 undefined，不能用 !d 判断）
        if (d === null) this.leavePage();
      });
    });
  },

  /** 发起人「撤回转交」/ 主管「撤销转交」（同一个接口） */
  onCancelTransfer() {
    const t = this.pendingTransfer();
    if (!t || this.data.busy) return;
    const from = (t.from && t.from.name) || '';
    const to = (t.to && t.to.name) || '';
    const id = this.orderId;
    if (this.data.role === 'Supervisor') {
      this.confirm('撤销转交', '确定撤销 ' + from + ' 转交给 ' + to + ' 的请求？工单仍由 ' + from + ' 负责。', '撤销').then(() => {
        this.run(api.orders.cancelTransfer(id, t.id, { loading: '提交中' }), '已撤销转交', true);
      });
      return;
    }
    this.confirm('撤回转交', '确定撤回转交给 ' + to + ' 的请求？', '撤回').then(() => {
      this.run(api.orders.cancelTransfer(id, t.id, { loading: '提交中' }), '已撤回', true);
    });
  },

  /* ---------------- 修改 SAT 信息（v1.3；入口按 canEditSat） ---------------- */
  openSatSheet() {
    const d = this.detail;
    if (!d || !d.canEditSat) return;
    const raw = String(d.satDate || '');
    const iso = ISO_DATE.test(raw);
    this.satTouched = false;
    this.setData({
      satSheet: {
        show: true,
        satSigned: !!d.satSigned,
        satDate: iso ? raw : '',
        label: satDateLabelOf(!!d.satSigned),
        legacy: !iso && raw ? raw : '', // 旧数据的 yyyy-MM 不带入，只做提示
        pickerValue: format.today()
      }
    });
  },

  closeSatSheet() {
    this.setData({ 'satSheet.show': false });
  },

  /** 切换「已完成 / 未完成」时清空时间（两个时间含义不同） */
  onSatChoose(e) {
    const v = String(e.currentTarget.dataset.v) === '1';
    if (this.data.satSheet.satSigned === v) return;
    this.satTouched = true;
    this.setData({ 'satSheet.satSigned': v, 'satSheet.satDate': '', 'satSheet.label': satDateLabelOf(v) });
  },

  onSatDate(e) {
    this.satTouched = true;
    this.setData({ 'satSheet.satDate': e.detail.value });
  },

  onSatClear() {
    this.satTouched = true;
    this.setData({ 'satSheet.satDate': '' });
  },

  saveSat() {
    const s = this.data.satSheet;
    if (this.data.busy || !s.show) return;
    // 旧数据的 yyyy-MM 没有带进日期框：什么都没改就点「保存」时不提交，否则会把原记录的时间清空
    if (s.legacy && !this.satTouched) {
      this.closeSatSheet();
      return;
    }
    this.run(api.orders.setSat(this.orderId, !!s.satSigned, s.satDate || '', { loading: '保存中' }), '已保存').then((d) => {
      if (d) this.setData({ 'satSheet.show': false });
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
    this.confirm('同意' + r.title, r.text + (r.type === 'Hold' ? '\n同意后工单进入「暂停搁置」。'
      : r.type === 'Escalate' ? '\n同意后工单改为新的服务方式并回到「待启动」，原有记录和工时保留。'
        : '\n同意后对方成为本单协助人。'), '同意').then(() => {
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

  /* ---------------- 收费确认（v2.2） ---------------- */

  /** 收费确认没走完：不能开始处理 / 提交完工（服务端同样会拦） */
  chargeBlocked() {
    const c = (this.detail || {}).charge;
    if (!c || !c.pending) return false;
    wx.showToast({ title: '收费确认还没走完（' + c.statusText + '）', icon: 'none' });
    return true;
  },

  /** 卡片上的按钮：按当前用户能做的那一步打开面板 */
  openChargeSheet() {
    const c = (this.detail || {}).charge;
    if (!c || this.data.busy) return;
    const mode = c.canDecide ? 'decide' : c.canQuote ? 'quote' : c.canReview ? 'review' : c.canConfirm ? 'confirm' : '';
    if (!mode) return;
    this.setData({
      chargeSheet: {
        show: true, mode: mode, charge: true, reason: '', salesList: this.data.chargeSheet.salesList || [], salesIdx: -1,
        amount: mode === 'quote' && c.amount ? String(c.amount) : '', note: '', paid: ''
      }
    });
    // 不是销售提的单：拉一次销售名单给工程师选
    if (mode === 'decide' && !c.fixedSales && !(this.data.chargeSheet.salesList || []).length) {
      api.sales.list({ silent: true }).then((list) => {
        this.setData({ 'chargeSheet.salesList': (list || []).map((u) => ({ id: u.id, name: u.name })) });
      }).catch(() => {});
    }
  },

  closeChargeSheet() {
    if (this.data.busy) return;
    this.setData({ 'chargeSheet.show': false });
  },

  onChargeChoice(e) {
    this.setData({ 'chargeSheet.charge': e.currentTarget.dataset.value === 'yes' });
  },

  onChargePaid(e) {
    this.setData({ 'chargeSheet.paid': e.currentTarget.dataset.value });
  },

  onChargeSales(e) {
    this.setData({ 'chargeSheet.salesIdx': Number(e.detail.value) });
  },

  onChargeInput(e) {
    this.setData({ ['chargeSheet.' + e.currentTarget.dataset.field]: e.detail.value });
  },

  /** 面板右边的主按钮 */
  saveCharge() {
    if (this.data.busy) return;
    const s = this.data.chargeSheet;
    const c = (this.detail || {}).charge || {};
    const id = this.orderId;
    const note = String(s.note || '').trim();
    const tip = (t) => wx.showToast({ title: t, icon: 'none' });
    let p;
    let ok;
    if (s.mode === 'decide') {
      const reason = String(s.reason || '').trim();
      if (!s.charge) {
        p = api.orders.decideCharge(id, false, reason, null, { loading: '提交中' });
        ok = '已记为不收费';
      } else {
        const picked = s.salesList[s.salesIdx];
        if (!c.fixedSales && !picked) return tip('请选择交给哪位销售');
        if (!reason) return tip('请填写收费原因');
        p = api.orders.decideCharge(id, true, reason, c.fixedSales ? null : picked.id, { loading: '提交中' });
        ok = '已交给销售报价';
      }
    } else if (s.mode === 'quote') {
      const amount = Number(String(s.amount || '').replace(/,/g, ''));
      if (!s.amount || isNaN(amount) || amount <= 0) return tip('请填写收费金额');
      p = api.orders.quoteCharge(id, amount, note, false, { loading: '提交中' });
      ok = '已提交审核';
    } else if (s.mode === 'review') {
      if (!s.paid) return tip('请选择客户是否已付款');
      p = api.orders.reviewCharge(id, true, s.paid === 'yes', note, { loading: '提交中' });
      ok = '已转售后主管';
    } else {
      p = api.orders.confirmCharge(id, true, note, { loading: '提交中' });
      ok = '已确认';
    }
    this.run(p, ok).then((d) => {
      if (d) this.setData({ 'chargeSheet.show': false });
    });
  },

  /** 面板左边的次按钮：报价 = 不收费；审核 / 确认 = 退回（要写原因） */
  backCharge() {
    if (this.data.busy) return;
    const s = this.data.chargeSheet;
    const id = this.orderId;
    const note = String(s.note || '').trim();
    if (s.mode === 'decide') return this.closeChargeSheet();
    const done = (d) => { if (d) this.setData({ 'chargeSheet.show': false }); };
    if (s.mode === 'quote') {
      // 先问一句再提交（点了取消什么都不发生）
      return this.confirm('不收费', '确认这张单不收费？工程师会收到提醒并继续处理。', '不收费').then(() => {
        this.run(api.orders.quoteCharge(id, null, note, true, { loading: '提交中' }), '已记为不收费').then(done);
      });
    }
    if (!note) return wx.showToast({ title: '退回请写明原因', icon: 'none' });
    const p = s.mode === 'review'
      ? api.orders.reviewCharge(id, false, null, note, { loading: '提交中' })
      : api.orders.confirmCharge(id, false, note, { loading: '提交中' });
    this.run(p, '已退回').then(done);
  },

  /* ---------------- 每日工作内容（v2.1） ---------------- */
  openWorkSheet() {
    if (!this.data.canWriteWorkLog) return;
    this.setData({ workSheet: { show: true, text: this.data.myTodayWorkLog || '' } });
  },

  closeWorkSheet() {
    if (this.data.busy) return;
    this.setData({ 'workSheet.show': false });
  },

  onWorkInput(e) {
    this.setData({ 'workSheet.text': e.detail.value });
  },

  saveWork() {
    if (this.data.busy) return;
    const text = String(this.data.workSheet.text || '').trim();
    if (!text) return wx.showToast({ title: '请填写今天的工作内容', icon: 'none' });
    this.run(api.orders.saveWorkLog(this.orderId, text, { loading: '保存中' }), '已保存').then((d) => {
      if (d) this.setData({ 'workSheet.show': false });
    });
  },

  /** 流程里的登记表卡片：有文件时点「查看」跳到附件资料 */
  goFiles() {
    this.setData({ tab: 'files' });
  },

  /* ---------------- 申请升级服务方式（v2.0） ---------------- */
  closeEscalateSheet() {
    if (this.data.busy) return;
    this.setData({ 'escalateSheet.show': false });
  },

  onEscalateTarget(e) {
    this.setData({ 'escalateSheet.target': e.currentTarget.dataset.value });
  },

  onEscalateAddress() {
    location.chooseLocation(null).then((r) => {
      const name = r.name || '';
      const addr = r.address || '';
      const full = !name ? addr : (addr.indexOf(name) >= 0 ? addr : (addr ? addr + ' ' + name : name));
      this.setData({ 'escalateSheet.address': full, 'escalateSheet.latitude': r.latitude, 'escalateSheet.longitude': r.longitude });
    }).catch(() => {});
  },

  onEscalateInput(e) {
    this.setData({ ['escalateSheet.' + e.currentTarget.dataset.field]: e.detail.value });
  },

  saveEscalate() {
    if (this.data.busy) return;
    const s = this.data.escalateSheet;
    const reason = String(s.reason || '').trim();
    const findings = String(s.findings || '').trim();
    if (!s.target) return wx.showToast({ title: '请选择要升级成哪种服务方式', icon: 'none' });
    if (!reason) return wx.showToast({ title: '请填写升级原因', icon: 'none' });
    if (!findings) return wx.showToast({ title: '请填写已经排查了什么', icon: 'none' });
    const needAddress = s.target === 'Onsite' && s.noAddress;
    if (needAddress && !s.address) return wx.showToast({ title: '转为上门服务请在地图上选择客户地址', icon: 'none' });
    const extra = needAddress ? { address: s.address, latitude: s.latitude, longitude: s.longitude } : null;
    this.run(api.orders.requestEscalate(this.orderId, s.target, reason, findings, extra, { loading: '提交中' }), '已提交，等主管同意').then((d) => {
      if (d) this.setData({ 'escalateSheet.show': false });
    });
  },

  /* ---------------- 确认闭环面板（v1.9） ---------------- */
  closeCloseSheet() {
    if (this.data.busy) return;
    this.setData({ 'closeSheet.show': false });
  },

  onCloseNote(e) {
    this.setData({ 'closeSheet.note': e.detail.value });
  },

  /** 点常用语：接到备注后面（已有内容时用逗号隔开；同一句不重复加） */
  onClosePhrase(e) {
    const text = e.currentTarget.dataset.text || '';
    const cur = String(this.data.closeSheet.note || '').trim();
    if (!text || cur.indexOf(text) >= 0) return;
    const next = (cur ? cur + '，' + text : text).slice(0, 200);
    this.setData({ 'closeSheet.note': next });
  },

  saveClose() {
    if (this.data.busy) return;
    const id = this.data.order && this.data.order.id;
    if (!id) return;
    const note = String(this.data.closeSheet.note || '').trim();
    this.run(api.orders.close(id, note, { loading: '提交中' }), '已闭环').then((d) => {
      if (d) this.setData({ 'closeSheet.show': false });
    });
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
    if (!att) return;
    // 预览时左右滑动的顺序与页面上按类别分组后的顺序一致
    const all = this.detail.attachments || [];
    const ordered = ATT_TYPES.reduce((list, t) => list.concat(all.filter((a) => attTypeOf(a) === t)), []);
    attachments.open(att, ordered);
  },

  /** 点「…」：改类别 / 切换可见范围 / 删除（按服务端的 canChangeType / canToggleRestricted / canDelete） */
  onAttachmentMore(e) {
    const att = this.findAttachment(e);
    if (!att || this.data.busy) return;
    const items = [];
    if (att.canChangeType) {
      const cur = attTypeOf(att);
      // v2.1：登记表类别只有本单服务方式对应的那一张可选
      const svc = (this.detail && this.detail.serviceType) || '';
      ATT_TYPES.filter((t) => t !== cur && (!FORM_SERVICE[t] || FORM_SERVICE[t] === svc)).forEach((t) => {
        items.push({ key: 'type', type: t, label: '归为「' + enums.ATTACHMENT_TYPE[t].short + '」' });
      });
    }
    if (att.canToggleRestricted) items.push({ key: 'toggle', label: att.restricted ? '设为所有人可见' : '设为仅销售和主管可见' });
    if (att.canDelete) items.push({ key: 'delete', label: '删除' });
    if (!items.length) return;
    const id = this.orderId;
    wx.showActionSheet({
      itemList: items.map((x) => x.label),
      success: (res) => {
        const pick = items[res.tapIndex];
        if (!pick) return;
        if (pick.key === 'type') {
          this.run(api.orders.setAttachmentType(id, att.id, pick.type, { loading: '提交中' }), '已更新', true);
        } else if (pick.key === 'toggle') {
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
    return remaining > 0 ? '附件最多 50 个，本单还可添加 ' + remaining + ' 个' : '附件最多 50 个，本单已达上限';
  },

  /** 点某一组的「添加」：弹层带上该组的类别（data-att-type；弹层里可再切换） */
  openAttachSheet(e) {
    const d = this.detail;
    if (!d || !d.canAddAttachment) return;
    const t = e && e.currentTarget && e.currentTarget.dataset.attType;
    const type = ATT_TYPES.indexOf(t) >= 0 ? t : 'Other';
    // 登记表：只能从对应流程里的卡片进来，类别固定
    const fixed = !!FORM_SERVICE[type];
    if (fixed && FORM_SERVICE[type] !== d.serviceType) return;
    if (this.attachRemaining() === 0) {
      wx.showToast({ title: this.attachLimitText(0), icon: 'none' });
      return;
    }
    this.attachSeq = (this.attachSeq || 0) + 1;
    this.setData({
      attachSheet: Object.assign(emptyAttachSheet(), {
        show: true, type: type, fixed: fixed, title: fixed ? '上传' + enums.ATTACHMENT_TYPE[type].short : '添加附件',
        canRestrict: !fixed && !!d.canRestrictAttachment
      })
    });
  },

  /** 弹层里切换类别（data-att-type）：只改本次添加的归类，不影响已选的文件 */
  onAttachType(e) {
    const t = e.currentTarget.dataset.attType;
    if (!this.data.attachSheet.show || this.data.attachSheet.fixed || ATT_TYPES.indexOf(t) < 0 || FORM_SERVICE[t]) return;
    this.setData({ 'attachSheet.type': t });
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

  /** 弹层里的两个选择按钮：data-type = media | chat（附件类别用的是 data-att-type，两者不要混） */
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
    // 本次添加的附件整批归入弹层里选的类别
    const type = ATT_TYPES.indexOf(sheet.type) >= 0 ? sheet.type : 'Other';
    const body = done.map((x) => ({ fileId: x.fileId, restricted: restricted, type: type }));
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
  // v1.4：底部操作按钮、转交确认 / 拒绝 / 撤回、暂停、申请审批、定价并闭环的点击顺带静默攒一条订阅消息额度（utils/subscribe.js topUp）
}, ['onAction', 'onAcceptTransfer', 'onRejectTransfer', 'onCancelTransfer', 'saveHold', 'onApproveRequest', 'onRejectRequest', 'savePrice']));
