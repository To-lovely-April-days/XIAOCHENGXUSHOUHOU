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
  { key: 'requests', label: '待审批申请', dot: '#D18A00', query: { pendingRequest: true } },
  { key: 'unpriced', label: '未定价', dot: '#C81E2B', query: { priceSet: false } },
];

/** 5 段进度条（v1.1 §4）。点亮段数用服务端的 progressStep，不做本地状态映射 */
export const PROGRESS_STEPS = ['待认领', '待启动', '处理中', '待验收', '已闭环'];

/** 暂停原因（v1.1） */
export const HOLD_REASON = {
  WaitCustomer: { label: '等客户' },
  WaitParts: { label: '等备件' },
  WaitThirdParty: { label: '等第三方' },
  Other: { label: '其他' },
};

/** 申请类型 / 状态（v1.1） */
export const REQUEST_TYPE = {
  Hold: { label: '暂停申请' },
  AddAssistant: { label: '协助人申请' },
};
export const REQUEST_STATUS = {
  Pending: { label: '待审批' },
  Approved: { label: '已同意' },
  Rejected: { label: '已驳回' },
  Cancelled: { label: '已撤销' },
};

/**
 * 返厂节点（v1.1）。action = 记录按钮文案；courier = 需要快递公司 + 单号；
 * photo = 'required' | 'optional' | ''；category = 该节点照片上传用的 FileCategory。
 */
export const FACTORY_NODE = {
  CustomerShipped: { label: '客户寄出', action: '记录客户寄出', courier: true, photo: '', category: '' },
  FactoryReceived: { label: '工厂收货', action: '确认工厂收货', courier: false, photo: 'required', category: 'FactoryReceivePhoto' },
  Repairing: { label: '检测/维修', action: '', courier: false, photo: '', category: '' },
  ShippedBack: { label: '寄回客户', action: '寄回客户', courier: true, photo: 'required', category: 'FactoryShipPhoto' },
  CustomerSigned: { label: '客户签收', action: '记录客户签收', courier: false, photo: 'optional', category: 'FactorySignPhoto' },
};

/** 工单参与人角色（v1.1） */
export const PARTICIPANT_ROLE = {
  Lead: { label: '负责人' },
  Assistant: { label: '协助人' },
  Former: { label: '已退出' },
};

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
  FactoryReceivePhoto: { label: '收货照片' },
  FactoryShipPhoto: { label: '寄回照片' },
  FactorySignPhoto: { label: '签收凭证' },
  Attachment: { label: '附件' },
};

/** 文件类型（v1.2，FileDto.kind；文件中心的 type 筛选值） */
export const FILE_KIND = {
  Image: { label: '图片' },
  Video: { label: '视频' },
  Document: { label: '文档' },
};

/** 文件来源（v1.2，文件中心的 source 筛选值；行上的具体文字用服务端的 sourceLabel） */
export const FILE_SOURCE = {
  Attachment: { label: '附件' },
  OrderPhoto: { label: '建单照片' },
  ProcessPhoto: { label: '完工照片' },
  Signature: { label: '签字件' },
  PunchPhoto: { label: '打卡照片' },
  Factory: { label: '返厂照片' },
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
  assistants: { label: '调整协助人', kind: 'secondary' },
  setPrice: { label: '定价', kind: 'secondary' },
  factory: { label: '返厂流程', kind: 'secondary' },
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
