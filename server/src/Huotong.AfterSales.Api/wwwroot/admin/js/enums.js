// 枚举中文与原型 16 配色。值与 docs/API.md 一致。

/** 角色 */
export const ROLE = {
  Engineer: { label: '售后工程师' },
  Sales: { label: '销售' },
  Supervisor: { label: '售后主管' },
};

/**
 * 工单状态。dot = PC 后台状态圆点色；bg/fg = 标签配色（DESIGN §8）。
 */
export const ORDER_STATUS = {
  Pending: { label: '待认领', dot: '#D18A00', bg: '#F0F0EE', fg: '#9A5B00' },
  Accepted: { label: '待启动', dot: '#A1A1AA', bg: '#F0F0EE', fg: '#636366' },
  InProgress: { label: '处理中', dot: '#C81E2B', bg: '#FBEDEE', fg: '#C81E2B' },
  OnHold: { label: '暂停搁置', dot: '#D4D4D8', bg: '#ECECEA', fg: '#636366' },
  Submitted: { label: '待验收', dot: '#18181B', bg: '#F0F0EE', fg: '#1D1D1F' },
  Closed: { label: '已闭环', dot: '#2F7A4B', bg: '#F0F0EE', fg: '#2F7A4B' },
  Voided: { label: '已作废', dot: '#E4E4E7', bg: '#F0F0EE', fg: '#AEAEB2' },
};

/** 工单列表状态 tab（原型顺序）；key '' = 全部 */
export const STATUS_TABS = [
  { key: '', label: '全部', countKey: 'all' },
  { key: 'Pending', label: '待认领', countKey: 'Pending' },
  { key: 'Accepted', label: '待启动', countKey: 'Accepted' },
  { key: 'InProgress', label: '处理中', countKey: 'InProgress' },
  { key: 'Submitted', label: '待验收', countKey: 'Submitted' },
  { key: 'Closed', label: '已闭环', countKey: 'Closed' },
  { key: 'OnHold', label: '暂停搁置', countKey: 'OnHold' },
];

/**
 * 侧栏「待处理」快捷筛选。路由 #/orders?quick=<key>，query 为对应的 orders.list 参数。
 */
export const QUICK_FILTERS = [
  { key: 'missing', label: '缺卡待补录', dot: '#C81E2B', query: { issue: 'MissingPunch' } },
  { key: 'distance', label: '打卡距离异常', dot: '#D18A00', query: { issue: 'DistanceAbnormal' } },
  { key: 'submitted', label: '待验收', dot: '#18181B', query: { status: 'Submitted' } },
];

export const ORDER_SOURCE = {
  SalesReferral: { label: '销售转报' },
  CustomerDirect: { label: '客户直联' },
  Inspection: { label: '现场巡检' },
  ReturnLogistics: { label: '退货物流' },
};

export const PROBLEM_TYPE = {
  Fault: { label: '设备故障' },
  Complaint: { label: '客诉投诉' },
  Return: { label: '退回返修' },
  Consult: { label: '咨询指导' },
};

/** 紧急程度。PC 端「紧急」标签 #FEF2F2/#B91C1C */
export const URGENCY = {
  Urgent: { label: '紧急', bg: '#FEF2F2', fg: '#B91C1C' },
  Normal: { label: '一般', bg: '#F4F4F5', fg: '#3F3F46' },
  Consult: { label: '咨询', bg: '#F4F4F5', fg: '#71717A' },
};

/** 维修类型（列表「类型」列显示 short） */
export const SERVICE_TYPE = {
  Onsite: { label: '现场' },
  Remote: { label: '远程' },
  Factory: { label: '返厂' },
};

export const PUNCH_KIND = {
  Arrive: { label: '到场' },
  Leave: { label: '离场' },
};

export const RUN_STATUS = {
  Normal: { label: '正常', fg: '#2F7A4B' },
  Observe: { label: '待观察', fg: '#B45309' },
  NotRecovered: { label: '未恢复', fg: '#B91C1C' },
};

/** 异常标签配色 */
export const ISSUE = {
  MissingPunch: { label: '缺卡', bg: '#FEF2F2', fg: '#B91C1C', alertBg: '#FEF2F2', alertBorder: '#FECACA', alertFg: '#991B1B' },
  DistanceAbnormal: { label: '距离异常', bg: '#FFFBEB', fg: '#B45309', alertBg: '#FFFBEB', alertBorder: '#FDE68A', alertFg: '#92400E' },
};

export const FILE_CATEGORY = {
  OrderPhoto: { label: '建单照片' },
  PunchPhoto: { label: '打卡照片' },
  ProcessPhoto: { label: '处理照片' },
  Signature: { label: '客户签字件' },
  Avatar: { label: '头像' },
};

/**
 * 详情 actions → 按钮文案与默认样式（原型 16）。
 * kind: primary | secondary
 */
export const ORDER_ACTIONS = {
  assign: { label: '指派', kind: 'primary' },
  reassign: { label: '改派', kind: 'secondary' },
  void: { label: '作废', kind: 'secondary' },
  hold: { label: '标为暂停搁置', kind: 'secondary' },
  resume: { label: '恢复处理', kind: 'primary' },
  reject: { label: '退回处理中', kind: 'secondary' },
  close: { label: '确认闭环', kind: 'primary' },
  supplement: { label: '补录离场时间', kind: 'primary' },
};

/**
 * 取枚举中文。
 * @param {Record<string,{label:string}>} map
 * @param {string} value
 * @param {string} [fallback]
 */
export function label(map, value, fallback = '') {
  return (map && value && map[value] && map[value].label) || fallback || value || '';
}

/** 生成 <select> 选项：[{ value, label }] */
export function options(map) {
  return Object.keys(map).map(k => ({ value: k, label: map[k].label }));
}
