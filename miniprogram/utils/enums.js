/**
 * API.md 枚举的中文与配色（DESIGN §8）。
 * 每个 map：{ 枚举值: { label, bg?, fg?, ... } }；每个 *_OPTIONS：[{ value, label }]（picker / 胶囊用）。
 * 取文案：label(STATUS, 'Pending') → '待认领'。
 */

/** 设计令牌（JS 中需要颜色时用；WXSS 里用 var(--xxx)） */
const COLORS = {
  bg: '#F6F6F4',
  card: '#FFFFFF',
  text: '#1D1D1F',
  text2: '#6E6E73',
  text3: '#AEAEB2',
  textBody: '#3A3A3C',
  divider: '#ECECEA',
  border: '#E3E3E1',
  soft: '#F0F0EE',
  red: '#C81E2B',
  redSoft: '#FBEDEE',
  green: '#2F7A4B',
  amber: '#9A5B00',
  gray: '#636366',
  disabled: '#C7C7C5',
  track: '#E5E5E3'
};

/**
 * 工单状态。step：销售首页 5 段进度（0 = 不显示）；group：created/mine 接口的分组键。
 */
const STATUS = {
  Pending:    { label: '待认领',   bg: '#F0F0EE', fg: '#9A5B00', step: 1, group: 'wait' },
  Accepted:   { label: '待启动',   bg: '#F0F0EE', fg: '#636366', step: 2, group: 'doing' },
  InProgress: { label: '处理中',   bg: '#FBEDEE', fg: '#C81E2B', step: 3, group: 'doing' },
  OnHold:     { label: '暂停搁置', bg: '#ECECEA', fg: '#636366', step: 3, group: 'doing' },
  Submitted:  { label: '待验收',   bg: '#F0F0EE', fg: '#1D1D1F', step: 4, group: 'check' },
  Closed:     { label: '已闭环',   bg: '#F0F0EE', fg: '#2F7A4B', step: 5, group: 'closed' },
  Voided:     { label: '已作废',   bg: '#F0F0EE', fg: '#AEAEB2', step: 0, group: 'voided' }
};

/** 紧急程度 */
const URGENCY = {
  Urgent:  { label: '紧急', bg: '#FBEDEE', fg: '#C81E2B' },
  Normal:  { label: '一般', bg: '#F0F0EE', fg: '#3A3A3C' },
  Consult: { label: '咨询', bg: '#F0F0EE', fg: '#636366' }
};

/** 维修类型；needPunch：是否需要打卡 */
const SERVICE_TYPE = {
  Onsite:  { label: '现场', needPunch: true },
  Remote:  { label: '远程', needPunch: false },
  Factory: { label: '返厂', needPunch: false }
};

/** 工单来源 */
const SOURCE = {
  SalesReferral:   { label: '销售转报' },
  CustomerDirect:  { label: '客户直联' },
  Inspection:      { label: '现场巡检' },
  ReturnLogistics: { label: '退货物流' }
};

/** 问题类型 */
const PROBLEM_TYPE = {
  Fault:     { label: '设备故障' },
  Complaint: { label: '客诉投诉' },
  Return:    { label: '退回返修' },
  Consult:   { label: '咨询指导' }
};

/** 设备运行状况（完工） */
const RUN_STATUS = {
  Normal:       { label: '正常',   fg: '#2F7A4B' },
  Observe:      { label: '待观察', fg: '#9A5B00' },
  NotRecovered: { label: '未恢复', fg: '#C81E2B' }
};

/** 角色 */
const ROLE = {
  Engineer:   { label: '售后工程师' },
  Sales:      { label: '销售' },
  Supervisor: { label: '售后主管' }
};

/** 异常 */
const ISSUE = {
  MissingPunch:     { label: '缺卡',     bg: '#FBEDEE', fg: '#C81E2B' },
  DistanceAbnormal: { label: '距离异常', bg: '#F0F0EE', fg: '#9A5B00' }
};

/** 打卡类型 */
const PUNCH_KIND = {
  Arrive: { label: '到场' },
  Leave:  { label: '离场' }
};

/** 文件分类 */
const FILE_CATEGORY = {
  OrderPhoto:   { label: '建单照片' },
  PunchPhoto:   { label: '打卡照片' },
  ProcessPhoto: { label: '处理照片' },
  Signature:    { label: '客户签字件' },
  Avatar:       { label: '头像' }
};

/** OrderDetail.actions 的按钮文案 */
const ACTION = {
  claim:      { label: '认领' },
  appoint:    { label: '预约时间' },
  checkin:    { label: '打卡' },
  start:      { label: '开始处理' },
  complete:   { label: '提交完工' },
  assign:     { label: '指派' },
  reassign:   { label: '改派' },
  void:       { label: '作废' },
  hold:       { label: '暂停搁置' },
  resume:     { label: '恢复处理' },
  reject:     { label: '退回' },
  close:      { label: '确认闭环' },
  supplement: { label: '补录打卡' }
};

/** 主管首页待办类型 */
const TODO_TYPE = {
  Unassigned:       { label: '待指派' },
  Submitted:        { label: '待验收' },
  MissingPunch:     { label: '缺卡' },
  DistanceAbnormal: { label: '距离异常' }
};

function toOptions(map) {
  return Object.keys(map).map((k) => ({ value: k, label: map[k].label }));
}

/**
 * 取中文文案。
 * @param {Object} map 如 STATUS
 * @param {string} value 枚举值
 * @param {string} [fallback=value]
 */
function label(map, value, fallback) {
  const v = map && map[value];
  return v ? v.label : (fallback !== undefined ? fallback : (value || ''));
}

/** 进度条 5 段：[{on:boolean}]，Voided 返回全 false */
function progressSegments(status) {
  const step = (STATUS[status] && STATUS[status].step) || 0;
  return [1, 2, 3, 4, 5].map((i) => ({ on: i <= step }));
}

module.exports = {
  COLORS,
  STATUS,
  URGENCY,
  SERVICE_TYPE,
  SOURCE,
  PROBLEM_TYPE,
  RUN_STATUS,
  ROLE,
  ISSUE,
  PUNCH_KIND,
  FILE_CATEGORY,
  ACTION,
  TODO_TYPE,
  STATUS_OPTIONS: toOptions(STATUS),
  URGENCY_OPTIONS: toOptions(URGENCY),
  SERVICE_TYPE_OPTIONS: toOptions(SERVICE_TYPE),
  SOURCE_OPTIONS: toOptions(SOURCE),
  PROBLEM_TYPE_OPTIONS: toOptions(PROBLEM_TYPE),
  RUN_STATUS_OPTIONS: toOptions(RUN_STATUS),
  ROLE_OPTIONS: toOptions(ROLE),
  ISSUE_OPTIONS: toOptions(ISSUE),
  label,
  progressSegments,
  toOptions
};
