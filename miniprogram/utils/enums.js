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
 * 工单状态。group：created/mine 接口的分组键。
 * step 仅作旧数据兜底（服务端未返回 progressStep 时）；v1.1 起进度一律用 order.progressStep。
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
  Avatar:       { label: '头像' },
  FactoryReceivePhoto: { label: '收货照片' },
  FactoryShipPhoto:    { label: '寄回照片' },
  FactorySignPhoto:    { label: '签收凭证' }
};

/** 暂停原因（v1.1） */
const HOLD_REASON = {
  WaitCustomer:   { label: '等客户' },
  WaitParts:      { label: '等备件' },
  WaitThirdParty: { label: '等第三方' },
  Other:          { label: '其他' }
};

/** 申请类型（工程师发起、主管审批） */
const REQUEST_TYPE = {
  Hold:         { label: '暂停申请' },
  AddAssistant: { label: '协助人申请' }
};

/** 申请状态 */
const REQUEST_STATUS = {
  Pending:   { label: '待审批' },
  Approved:  { label: '已同意' },
  Rejected:  { label: '已驳回' },
  Cancelled: { label: '已撤销' }
};

/**
 * 返厂节点。action：按钮文案；category：该节点照片的上传分类；
 * courier：需要快递公司 + 单号；photo：'required' | 'optional' | ''（不收照片）。
 */
const FACTORY_NODE = {
  CustomerShipped: { label: '客户寄出',  action: '记录客户寄出', category: '',                    courier: true,  photo: '' },
  FactoryReceived: { label: '工厂收货',  action: '确认工厂收货', category: 'FactoryReceivePhoto', courier: false, photo: 'required' },
  Repairing:       { label: '检测/维修', action: '',             category: '',                    courier: false, photo: '' },
  ShippedBack:     { label: '寄回客户',  action: '寄回客户',     category: 'FactoryShipPhoto',    courier: true,  photo: 'required' },
  CustomerSigned:  { label: '客户签收',  action: '记录客户签收', category: 'FactorySignPhoto',    courier: false, photo: 'optional' }
};

/** 参与人角色 */
const PARTICIPANT_ROLE = {
  Lead:      { label: '负责人' },
  Assistant: { label: '协助人', short: '协助' },
  Former:    { label: '已退出' }
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
  supplement: { label: '补录打卡' },
  factory:          { label: '返厂流程' },
  requestHold:      { label: '申请暂停' },
  requestAssistant: { label: '申请协助人' },
  assistants:       { label: '调整协助人' },
  setPrice:         { label: '定价' },
  suggestPrice:     { label: '价格建议' }
};

/** 主管首页待办类型 */
const TODO_TYPE = {
  Unassigned:       { label: '待指派' },
  Submitted:        { label: '待验收' },
  MissingPunch:     { label: '缺卡' },
  DistanceAbnormal: { label: '距离异常' },
  HoldRequest:      { label: '暂停申请' },
  AssistantRequest: { label: '协助人申请' },
  PriceSuggestion:  { label: '价格建议' }
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

/** 5 段进度的名称（与 progressStep 1-5 对应） */
const PROGRESS_LABELS = ['待认领', '待启动', '处理中', '待验收', '已闭环'];

/** 工单的进度段（1-5，Voided = 0）：用服务端 progressStep；旧服务端未返回时按状态兜底 */
function progressStepOf(order) {
  if (!order) return 0;
  if (typeof order.progressStep === 'number') return order.progressStep;
  if (order.status === 'OnHold') return order.holdFromStatus === 'Accepted' ? 2 : 3;
  return (STATUS[order.status] && STATUS[order.status].step) || 0;
}

/**
 * 进度条 5 段：[{on:boolean, paused:boolean}]。
 * OnHold 时第 progressStep 段为 paused（灰色暂停样式，on=false），之前的段正常点亮；Voided 全 false。
 * @param {Object} order 工单对象（用 progressStep、status）
 */
function progressSegments(order) {
  const step = progressStepOf(order);
  const hold = !!order && order.status === 'OnHold';
  return [1, 2, 3, 4, 5].map((i) => {
    const paused = hold && i === step;
    return { on: i <= step && !paused, paused: paused };
  });
}

/** 暂停文字：优先用服务端 holdText / hold.text；兜底用旧字段 holdReason */
function holdTextOf(order) {
  if (!order || order.status !== 'OnHold') return '';
  return order.holdText || (order.hold && order.hold.text) || order.holdReason || '';
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
  HOLD_REASON,
  REQUEST_TYPE,
  REQUEST_STATUS,
  FACTORY_NODE,
  PARTICIPANT_ROLE,
  PROGRESS_LABELS,
  HOLD_REASON_OPTIONS: toOptions(HOLD_REASON),
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
  progressStepOf,
  holdTextOf,
  toOptions
};
