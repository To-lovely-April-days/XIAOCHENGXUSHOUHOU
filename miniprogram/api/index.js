/**
 * 接口层：与 docs/API.md 一一对应。
 * 所有函数返回 Promise<data>（已解包 {code,message,data}），出错 reject ApiError {code,message,httpStatus,data}。
 * 最后一个参数 opts 可选，透传给 request：{ silent?:boolean, loading?:boolean|string }。
 *
 * 常用类型（详见 API.md「公共 DTO」）：
 * @typedef {{id:number,name:string,surname:string,avatarUrl:?string,role:'Engineer'|'Sales'|'Supervisor'}} UserBrief
 * @typedef {UserBrief & {roleName:string,department:string,phoneMasked:string,phone:string}} User
 * @typedef {{id:number,url:string,category:string,mimeType:string,createdAt:string,name:string,size:number,kind:'Image'|'Video'|'Document',ext:string}} FileDto
 * @typedef {FileDto & {source:'Attachment'|'OrderPhoto',sourceLabel:string,restricted:boolean,uploader:?UserBrief,canDelete:boolean,canToggleRestricted:boolean,
 *   attachmentType:'Sat'|'RepairForm'|'Other',attachmentTypeLabel:string,attachmentTypeShort:string,canChangeType:boolean}} AttachmentDto
 *   v1.2 工单附件；restricted = 仅销售和主管可见（工程师从任何接口都拿不到）；v1.3 attachmentType = 附件类别
 * @typedef {{seq:number,name:string,sn:string,model:string,title:string}} DeviceDto  v1.3 工单的一台设备；三项没填的为 ''
 * @typedef {{id:number,orderId:number,status:'Pending'|'Accepted'|'Rejected'|'Cancelled'|'Expired',statusText:string,from:UserBrief,to:UserBrief,
 *   reason:string,createdAt:string,decidedAt:?string,decidedBy:?UserBrief,reply:?string,text:string}} TransferDto
 *   v1.3 转交；text 为可直接展示的一句话；能不能接手 / 拒绝 / 撤回只看 OrderDetail.actions
 * @typedef {Object} OrderListItem  id, orderNo, status, urgency, serviceType, problemType, customerName, city, region,
 *   projectNo:?string（旧单为 null）, deviceText（设备汇总文字，永不为空；列表、标题一律显示它）,
 *   deviceModel, deviceSn（仅保留给旧客户端，v1.3 不再读）, symptom, price:?number, priceSet:?boolean（工程师恒为 null）, assignee:?UserBrief（负责人）,
 *   assistants:UserBrief[], myRole:?('Lead'|'Assistant'|'Former'), myExitText:?('已转交'|'已改派')（仅 Former）,
 *   pendingTransfer:?TransferDto（本单待确认的转交）, myMinutes:?number, myOnSiteSince:?string, creator:UserBrief,
 *   createdAt, claimedAt, appointmentTime, submittedAt, closedAt, totalMinutes（所有人合计）, onSiteSince, lastArriveTime,
 *   issues:string[], progressStep:number(0-5), holdReasonType, holdText, holdFromStatus, pendingRequestCount, factoryText
 * @typedef {Object} OrderDetail  OrderListItem + source, contactName, contactPhone, address, latitude, longitude,
 *   devices:DeviceDto[], satSigned（是否已完成 SAT）, satDate（'yyyy-MM-dd' | 旧数据 'yyyy-MM' | ''）, satText, satDateLabel,
 *   canEditSat:boolean, transfers:TransferDto[], readOnlyText:?string（当前用户已退出本单时非 null）,
 *   productStage, faultTime, customerTried, siteEnvironment, customerRequest, holdReason(=holdText),
 *   priceSetAt, priceSuggestion:?{id,amount,note,by:UserBrief,createdAt,pending}, hold:?{reasonType,reasonText,note,text,fromStatus,at,by},
 *   requests:RequestDto[], participants:ParticipantDto[], factory:?{nodes:FactoryNodeDto[]}, closeBlockedReason:?string,
 *   closeHints:string[], completion, files{orderPhotos,punchPhotos,processPhotos,signatures,factoryPhotos},
 *   punches:PunchDto[], logs:StatusLogDto[]（含 kind）, issueDetails[]（含 engineer）, actions:string[],
 *   v1.2：attachments:AttachmentDto[]（当前用户可见的附件）, canAddAttachment:boolean, canRestrictAttachment:boolean,
 *   attachmentLimit:number(50), attachmentRemaining:number（files.orderPhotos 仅保留给 v1.1 客户端，v1.2 不再读）
 * @typedef {Object} RequestDto  id, orderId, type:'Hold'|'AddAssistant', status, requester:UserBrief, createdAt, reasonType,
 *   reasonText, note, text, engineer:?UserBrief, decider, decidedAt, reply, canDecide:boolean, canCancel:boolean
 * @typedef {{user:UserBrief,role:'Lead'|'Assistant'|'Former',totalMinutes:number,onSiteSince:?string,missingPunch:boolean,
 *   exitText:?('已转交'|'已改派'),exitedAt:?string}} ParticipantDto  exitText / exitedAt 只有带退出记录的 Former 有值
 * @typedef {Object} FactoryNodeDto  node, label, state:'done'|'current'|'todo'|'skipped', recorded, time, operator:?UserBrief,
 *   courier, trackingNo, note, photos:FileDto[], canRecord:boolean, canEdit:boolean
 * @typedef {Object} PunchDto  id, orderId, engineer, kind, time, latitude, longitude, address, distanceMeters, outOfRange,
 *   photo:?FileDto, isSupplement, supplementBy, supplementReason, supplementAt, pairMinutes
 * @typedef {{token:?string,user:?User,needBind:boolean,bindTicket?:string}} LoginResult
 */
const http = require('../utils/request.js');

const get = http.get;
const post = http.post;
const put = http.put;

/* ---------------- 1. 认证 ---------------- */
const auth = {
  /**
   * POST /api/auth/login
   * @param {string} code wx.login 返回的 code
   * @returns {Promise<LoginResult>} 已绑定 {token,user,needBind:false}；未绑定 {needBind:true,bindTicket}
   */
  login(code, opts) {
    return post('/api/auth/login', { code: code }, Object.assign({ auth: false }, opts));
  },
  /**
   * POST /api/auth/bind 手机号快速验证后绑定
   * @param {string} bindTicket login 返回的票据（10 分钟有效）
   * @param {string} phoneCode getPhoneNumber 回调 e.detail.code
   * @returns {Promise<LoginResult>} 错误：40401 未找到账号；40900 已绑定其他微信
   */
  bind(bindTicket, phoneCode, opts) {
    return post('/api/auth/bind', { bindTicket: bindTicket, phoneCode: phoneCode }, Object.assign({ auth: false }, opts));
  },
  /**
   * POST /api/auth/account-login（v1.5）手机号 + 密码登录
   * @param {string} phone
   * @param {string} password
   * @param {string} code wx.login 的 code，可为空串；有则顺带把当前微信绑到账号上（订阅消息用）
   * @returns {Promise<LoginResult>} 错误：40310 账号审核中；40311 注册被驳回（message 含原因）；40300 已停用；40000 手机号或密码错误 / 尝试过多
   */
  accountLogin(phone, password, code, opts) {
    return post('/api/auth/account-login', { phone: phone, password: password, code: code || '' }, Object.assign({ auth: false }, opts));
  },
  /**
   * POST /api/auth/register（v1.5）提交注册申请，等主管在电脑后台审批
   * @param {{name:string,phone:string,password:string,code:string}} body code 为 wx.login 的 code（必填）
   * @returns {Promise<{status:'Pending',message:string}>} 错误：40900 手机号已有账号 / 已在审批中；40000 校验不通过或提交过于频繁
   */
  register(body, opts) {
    return post('/api/auth/register', body, Object.assign({ auth: false }, opts));
  },
  /**
   * GET /api/auth/subscribe（v1.8，无需登录）注册页要申请订阅的「审批结果通知」模板
   * @returns {Promise<{enabled:boolean,templateIds:string[]}>} 后端没配模板时 enabled=false
   */
  subscribeConfig(opts) {
    return get('/api/auth/subscribe', null, Object.assign({ auth: false }, opts));
  }
};

/* ---------------- 2. 我的 ---------------- */
const me = {
  /**
   * GET /api/me
   * @returns {Promise<{user:User,monthMinutes:number,activeOrderCount:number,monthTripDays:number}>}
   */
  get(opts) {
    return get('/api/me', null, opts);
  },
  /**
   * PUT /api/me/avatar
   * @param {number} fileId 先用 files.upload(path,'Avatar') 上传得到
   * @returns {Promise<User>}
   */
  setAvatar(fileId, opts) {
    return put('/api/me/avatar', { fileId: fileId }, opts);
  },
  /**
   * GET /api/me/subscribe（v1.4）微信订阅消息「工单提醒」配置；任何已登录角色可调
   * @returns {Promise<{enabled:boolean,templateIds:string[]}>} 后端未配置模板时 enabled=false、templateIds=[]
   *   页面不要直接调，统一走 utils/subscribe.js
   */
  getSubscribeConfig(opts) {
    return get('/api/me/subscribe', null, opts);
  },
  /**
   * PUT /api/me/password 修改自己的密码
   * @param {string} oldPassword 原密码（账号还没设过密码时可空）
   * @param {string} newPassword 至少 8 位，同时含字母和数字
   * @returns {Promise<User>}
   */
  changePassword(oldPassword, newPassword, opts) {
    return put('/api/me/password', { oldPassword: oldPassword, newPassword: newPassword }, opts);
  },
  /** POST /api/me/logout（v1.5）退出登录：解除微信绑定，不再往这个微信发提醒 */
  logout(opts) {
    return post('/api/me/logout', {}, opts);
  }
};

/* ---------------- 3. 文件 ---------------- */
const files = {
  /**
   * POST /api/files（multipart）
   * @param {string} filePath 本地临时文件
   * @param {'OrderPhoto'|'PunchPhoto'|'ProcessPhoto'|'Signature'|'Avatar'|'FactoryReceivePhoto'|'FactoryShipPhoto'|'FactorySignPhoto'|'Attachment'} category
   *   工单附件（建单时与建单后）一律传 'Attachment'
   * @param {{silent?:boolean,loading?:boolean|string,fileName?:string,timeout?:number,onProgress?:function(number):void,onTask?:function(Object):void}} [opts]
   *   v1.2：fileName 原始文件名（wx.chooseMessageFile 的 name；相册 / 相机选的不传）、timeout（默认 60000）、onProgress(percent)
   * @returns {Promise<FileDto>}
   */
  upload(filePath, category, opts) {
    return http.uploadFile(filePath, category, opts);
  },
  /**
   * GET /api/files/{id}/download 的路径（需 Authorization 头；配合 http.download 使用）
   * @param {number} id 文件 id
   * @returns {string}
   */
  downloadPath(id) {
    return '/api/files/' + id + '/download';
  }
};

/* ---------------- 4. 工单 ---------------- */
const orders = {
  /**
   * GET /api/orders/hall 工单大厅（Pending）
   * @param {'all'|'urgent'|'onsite'|'remote'} [filter='all']
   * @returns {Promise<{items:OrderListItem[],counts:{all:number,urgent:number,onsite:number,remote:number}}>}
   */
  hall(filter, opts) {
    return get('/api/orders/hall', { filter: filter || 'all' }, opts);
  },
  /**
   * GET /api/orders/mine 工程师本人负责、协助或已退出（已转交 / 已改派）的工单（每项带 myRole）
   * @param {'doing'|'check'|'closed'|'former'|'all'} [group='doing'] former = 本人已退出的工单（v1.3）
   * @returns {Promise<{items:OrderListItem[],counts:{doing:number,check:number,closed:number,former:number,all:number}}>}
   */
  mine(group, opts) {
    return get('/api/orders/mine', { group: group || 'doing' }, opts);
  },
  /**
   * GET /api/orders/created 本人创建的工单（不含作废）
   * @param {'all'|'wait'|'doing'|'check'|'closed'} [group='all']
   * @returns {Promise<{items:OrderListItem[],counts:{all:number,wait:number,doing:number,check:number,closed:number}}>}
   */
  created(group, opts) {
    return get('/api/orders/created', { group: group || 'all' }, opts);
  },
  /**
   * GET /api/orders 全部工单（Sales/Supervisor）
   * @param {{status?:string,keyword?:string,issue?:string,assigneeId?:number,serviceType?:string,from?:string,to?:string,
   *   priceSet?:boolean,pendingRequest?:boolean,page?:number,pageSize?:number}} [params]
   *   status 为空 = 全部（不含 Voided）；from/to 为 yyyy-MM-dd；priceSet 按是否已定价过滤；pendingRequest=true 只看有待审批申请的
   * @returns {Promise<{items:OrderListItem[],total:number,page:number,pageSize:number,counts:Object<string,number>}>}
   *   counts 键：all, Pending, Accepted, InProgress, OnHold, Submitted, Closed（只受 keyword 影响）
   */
  list(params, opts) {
    return get('/api/orders', Object.assign({ page: 1, pageSize: 20 }, params), opts);
  },
  /**
   * GET /api/orders/recent-customers 最近客户（最多 5 个）
   * @returns {Promise<Array<{customerName,contactName,contactPhone,address,latitude,longitude,devices:Array<{name,sn,model}>,
   *   deviceModel,deviceSn,satDate,satSigned,projectNo}>>}  v1.3：devices = 该客户最近一张工单的设备清单
   */
  recentCustomers(opts) {
    return get('/api/orders/recent-customers', null, opts);
  },
  /**
   * GET /api/orders/{id}
   * @param {number} id
   * @returns {Promise<OrderDetail>} actions 决定可显示的按钮
   */
  detail(id, opts) {
    return get('/api/orders/' + id, null, opts);
  },
  /**
   * POST /api/orders 新建工单
   * @param {Object} body 字段见 API.md：source, problemType, urgency, serviceType, customerName, contactName, contactPhone,
   *   address, latitude, longitude, projectNo（必填）, devices:[{name,sn,model}]（选填，可为 []）, satSigned（必填：是否已完成 SAT）,
   *   satDate('yyyy-MM-dd' | ''，选填：SAT 完成时间 / 发货时间）, faultTime, symptom,
   *   customerTried, siteEnvironment, customerRequest, attachments:[{fileId:number,restricted?:boolean,type?:'Sat'|'RepairForm'|'Other'}]
   *   （v1.3 起不再传 deviceModel / deviceSn；v1.2 起用 attachments，不再传 photoFileIds；v1.1 起不含 price，定价由主管在详情操作）
   * @returns {Promise<OrderDetail>}
   */
  create(body, opts) {
    return post('/api/orders', body, opts);
  },
  /**
   * POST /api/orders/{id}/claim 认领（Engineer）；已被认领 → 40900
   * @returns {Promise<OrderDetail>}
   */
  claim(id, opts) {
    return post('/api/orders/' + id + '/claim', {}, opts);
  },
  /**
   * POST /api/orders/{id}/appointment 预约上门时间
   * @param {number} id
   * @param {string} appointmentTime 'yyyy-MM-ddTHH:mm:ss'（可用 format.toApiTime(date)）
   * @returns {Promise<OrderDetail>}
   */
  appoint(id, appointmentTime, opts) {
    return post('/api/orders/' + id + '/appointment', { appointmentTime: appointmentTime }, opts);
  },
  /**
   * POST /api/orders/{id}/start 开始处理（远程单，Accepted → InProgress；返厂单改为记录「工厂收货」）
   * @returns {Promise<OrderDetail>}
   */
  start(id, opts) {
    return post('/api/orders/' + id + '/start', {}, opts);
  },
  /**
   * POST /api/orders/{id}/complete 提交完工
   * @param {number} id
   * @param {{faultCause:string,solution:string,partsReplaced:string,runStatus:'Normal'|'Observe'|'NotRecovered',processPhotoIds:number[],signaturePhotoIds:number[]}} body
   * @returns {Promise<OrderDetail>}
   */
  complete(id, body, opts) {
    return post('/api/orders/' + id + '/complete', body, opts);
  },
  /**
   * POST /api/orders/{id}/assign 指派 / 改派（Supervisor）
   * @param {number} id
   * @param {number} engineerId 负责人
   * @param {?number[]} [assistantIds] 协助人最终全集；省略 / null = 不调整协助人；[] = 清空
   * @returns {Promise<OrderDetail>}
   */
  assign(id, engineerId, assistantIds, opts) {
    // 兼容旧调用 assign(id, engineerId, opts)
    if (assistantIds && !Array.isArray(assistantIds) && opts === undefined) {
      opts = assistantIds;
      assistantIds = null;
    }
    const body = { engineerId: engineerId };
    if (Array.isArray(assistantIds)) body.assistantIds = assistantIds;
    return post('/api/orders/' + id + '/assign', body, opts);
  },
  /**
   * POST /api/orders/{id}/assistants 调整协助人（Supervisor；Accepted / InProgress / OnHold）
   * @param {number} id
   * @param {number[]} engineerIds 协助人最终全集，[] = 清空，最多 5 人
   * @returns {Promise<OrderDetail>}
   */
  setAssistants(id, engineerIds, opts) {
    return post('/api/orders/' + id + '/assistants', { engineerIds: engineerIds || [] }, opts);
  },
  /**
   * POST /api/orders/{id}/assistant-requests 负责人申请添加协助人（Engineer）
   * @param {number} id
   * @param {number} engineerId 目标工程师
   * @param {string} [note] 说明，≤ 200 字
   * @returns {Promise<OrderDetail>}
   */
  requestAssistant(id, engineerId, note, opts) {
    return post('/api/orders/' + id + '/assistant-requests', { engineerId: engineerId, note: note || null }, opts);
  },
  /**
   * POST /api/orders/{id}/void 作废（Supervisor，Pending/Accepted）
   * @param {number} id
   * @param {string} reason
   * @returns {Promise<OrderDetail>}
   */
  void(id, reason, opts) {
    return post('/api/orders/' + id + '/void', { reason: reason || '' }, opts);
  },
  /**
   * POST /api/orders/{id}/hold 暂停搁置（Supervisor，Accepted / InProgress）
   * @param {number} id
   * @param {'WaitCustomer'|'WaitParts'|'WaitThirdParty'|'Other'} reasonType
   * @param {string} [note] 说明，Other 时必填，≤ 200 字
   * @returns {Promise<OrderDetail>}
   */
  hold(id, reasonType, note, opts) {
    return post('/api/orders/' + id + '/hold', { reasonType: reasonType, note: note || null }, opts);
  },
  /**
   * POST /api/orders/{id}/hold-requests 负责人申请暂停（Engineer）；已有待审批申请 → 40900
   * @param {number} id
   * @param {'WaitCustomer'|'WaitParts'|'WaitThirdParty'|'Other'} reasonType
   * @param {string} [note]
   * @returns {Promise<OrderDetail>}
   */
  /**
   * POST /api/orders/{id}/escalate-requests（v2.0）负责工程师申请升级服务方式，需主管同意。
   * 远程 → 上门 / 返厂；上门 → 返厂。同意后工单改服务方式并回到「待启动」，原有记录、打卡、工时保留。
   * @param {'Onsite'|'Factory'} target @param {string} reason 升级原因（必填） @param {string} findings 已排查情况（必填）
   * @returns {Promise<OrderDetail>} requests 里多一条 type='Escalate' 的申请（targetServiceType、text）
   */
  requestEscalate(id, target, reason, findings, extra, opts) {
    // extra：转上门而工单还没有地址时带 { address, latitude, longitude }，否则传 null
    return post('/api/orders/' + id + '/escalate-requests',
      Object.assign({ targetServiceType: target, reason: reason, findings: findings }, extra || {}), opts);
  },
  requestHold(id, reasonType, note, opts) {
    return post('/api/orders/' + id + '/hold-requests', { reasonType: reasonType, note: note || null }, opts);
  },
  /**
   * POST /api/orders/{id}/requests/{requestId}/approve 同意申请（Supervisor；暂停 / 协助人共用）
   * @returns {Promise<OrderDetail>}
   */
  approveRequest(id, requestId, opts) {
    return post('/api/orders/' + id + '/requests/' + requestId + '/approve', {}, opts);
  },
  /**
   * POST /api/orders/{id}/requests/{requestId}/reject 驳回申请（Supervisor）
   * @param {number} id
   * @param {number} requestId
   * @param {string} [reply] 回复，选填，≤ 200 字
   * @returns {Promise<OrderDetail>}
   */
  rejectRequest(id, requestId, reply, opts) {
    return post('/api/orders/' + id + '/requests/' + requestId + '/reject', { reply: reply || null }, opts);
  },
  /**
   * POST /api/orders/{id}/requests/{requestId}/cancel 申请人撤回（Engineer）
   * @returns {Promise<OrderDetail>}
   */
  cancelRequest(id, requestId, opts) {
    return post('/api/orders/' + id + '/requests/' + requestId + '/cancel', {}, opts);
  },
  /**
   * POST /api/orders/{id}/price 定价 / 改价（Supervisor）
   * @param {number} id
   * @param {number} price 0 = 保内免费
   * @returns {Promise<OrderDetail>}
   */
  setPrice(id, price, opts) {
    return post('/api/orders/' + id + '/price', { price: price }, opts);
  },
  /**
   * POST /api/orders/{id}/price-suggestions 价格建议（Sales）
   * @param {number} id
   * @param {number} amount
   * @param {string} [note] 备注，≤ 200 字
   * @returns {Promise<OrderDetail>}
   */
  suggestPrice(id, amount, note, opts) {
    return post('/api/orders/' + id + '/price-suggestions', { amount: amount, note: note || null }, opts);
  },
  /**
   * POST /api/orders/{id}/factory-nodes 记录 / 更正返厂节点（服务端按角色判权）
   * @param {number} id
   * @param {{node:'CustomerShipped'|'FactoryReceived'|'ShippedBack'|'CustomerSigned',courier?:string,trackingNo?:string,note?:string,photoIds?:number[]}} body
   *   photoIds 为该节点照片的最终全集
   * @returns {Promise<OrderDetail>}
   */
  factoryNode(id, body, opts) {
    return post('/api/orders/' + id + '/factory-nodes', body, opts);
  },
  /**
   * POST /api/orders/{id}/resume 恢复处理（Supervisor，OnHold → 暂停前的状态）
   * @returns {Promise<OrderDetail>}
   */
  resume(id, opts) {
    return post('/api/orders/' + id + '/resume', {}, opts);
  },
  /**
   * POST /api/orders/{id}/reject 退回处理中（Supervisor，Submitted → InProgress）
   * @param {number} id
   * @param {string} reason
   * @returns {Promise<OrderDetail>}
   */
  reject(id, reason, opts) {
    return post('/api/orders/' + id + '/reject', { reason: reason || '' }, opts);
  },
  /**
   * POST /api/orders/{id}/close 确认闭环（Supervisor）；note = 备注（选填，≤200 字，写进状态记录）；有缺卡 → 40900「请先补录缺卡」；未定价 → 40900「请先设置服务价格（保内填 0）」
   * @returns {Promise<OrderDetail>}
   */
  close(id, note, opts) {
    return post('/api/orders/' + id + '/close', { note: note || '' }, opts);
  },
  /**
   * POST /api/orders/{id}/attachments 添加附件（v1.2；服务端按建单人 / 负责人 / 协助人 / 主管判权；v1.3 起已闭环也能添加）
   * @param {number} id
   * @param {Array<{fileId:number,restricted?:boolean,type?:'Sat'|'RepairForm'|'Other'}>} attachments
   *   先用 files.upload(path,'Attachment') 上传得到 fileId；type = 附件类别，省略 = Other
   * @returns {Promise<OrderDetail>}
   */
  addAttachments(id, attachments, opts) {
    return post('/api/orders/' + id + '/attachments', { attachments: attachments || [] }, opts);
  },
  /**
   * POST /api/orders/{id}/attachments/{fileId}/delete 删除附件（v1.2；上传人本人或主管）
   * @returns {Promise<OrderDetail>}
   */
  deleteAttachment(id, fileId, opts) {
    return post('/api/orders/' + id + '/attachments/' + fileId + '/delete', {}, opts);
  },
  /**
   * POST /api/orders/{id}/attachments/{fileId}/restricted 切换「仅销售和主管可见」（v1.2；Supervisor）
   * @param {number} id
   * @param {number} fileId
   * @param {boolean} restricted
   * @returns {Promise<OrderDetail>}
   */
  setAttachmentRestricted(id, fileId, restricted, opts) {
    return post('/api/orders/' + id + '/attachments/' + fileId + '/restricted', { restricted: !!restricted }, opts);
  },
  /**
   * POST /api/orders/{id}/attachments/{fileId}/type 修改附件类别（v1.3；上传人本人或主管）
   * @param {number} id
   * @param {number} fileId
   * @param {'Sat'|'RepairForm'|'Other'} type
   * @returns {Promise<OrderDetail>}
   */
  setAttachmentType(id, fileId, type, opts) {
    return post('/api/orders/' + id + '/attachments/' + fileId + '/type', { type: type }, opts);
  },
  /**
   * POST /api/orders/{id}/sat 修改「是否已完成 SAT」与对应时间（v1.3；建单人 / 负责人 / 主管，两项整体覆盖）
   * @param {number} id
   * @param {boolean} satSigned 是否已完成 SAT
   * @param {string} [satDate] 'yyyy-MM-dd'；'' = 清空
   * @returns {Promise<OrderDetail>}
   */
  setSat(id, satSigned, satDate, opts) {
    return post('/api/orders/' + id + '/sat', { satSigned: satSigned, satDate: satDate || '' }, opts);
  },
  /**
   * POST /api/orders/{id}/transfers 发起转交（v1.3；负责工程师，Accepted / InProgress / OnHold）
   * @param {number} id
   * @param {number} engineerId 接手人（启用中的工程师，不能是自己）
   * @param {string} reason 转交原因，必填，≤ 200 字
   * @returns {Promise<OrderDetail>}
   */
  transfer(id, engineerId, reason, opts) {
    return post('/api/orders/' + id + '/transfers', { engineerId: engineerId, reason: reason || '' }, opts);
  },
  /**
   * POST /api/orders/{id}/transfers/{transferId}/accept 确认接手（v1.3；接手人）
   * @returns {Promise<OrderDetail>} 此时当前用户已是负责人
   */
  acceptTransfer(id, transferId, opts) {
    return post('/api/orders/' + id + '/transfers/' + transferId + '/accept', {}, opts);
  },
  /**
   * POST /api/orders/{id}/transfers/{transferId}/reject 拒绝接手（v1.3；接手人）
   * @param {number} id
   * @param {number} transferId
   * @param {string} [reply] 拒绝理由，选填，≤ 200 字
   * @returns {Promise<?OrderDetail>} 拒绝后仍能查看该工单 → 最新详情；否则 **null**（已无查看权，要离开详情页）
   */
  rejectTransfer(id, transferId, reply, opts) {
    return post('/api/orders/' + id + '/transfers/' + transferId + '/reject', { reply: reply || null }, opts);
  },
  /**
   * POST /api/orders/{id}/transfers/{transferId}/cancel 撤回（发起人）/ 撤销（主管）待确认的转交（v1.3）
   * @returns {Promise<OrderDetail>}
   */
  cancelTransfer(id, transferId, opts) {
    return post('/api/orders/' + id + '/transfers/' + transferId + '/cancel', {}, opts);
  }
};
/** orders.void 的别名（避免部分编辑器对保留字属性报警） */
orders.voidOrder = orders.void;

/* ---------------- 5. 打卡 ---------------- */
const punches = {
  /**
   * GET /api/punches/context 打卡上下文
   * @param {number} [orderId] 省略时服务端自动选择工单
   * @returns {Promise<null|{orderId:number,orderNo:string,customerName:string,address:string,latitude:number,longitude:number,
   *   nextKind:'Arrive'|'Leave',openArriveTime:?string,blockedReason:?string,rangeMeters:number,serverTime:string,engineerName:string,
   *   myRole:'Lead'|'Assistant'}>}
   *   无可打卡工单时为 null
   */
  context(orderId, opts) {
    return get('/api/punches/context', orderId ? { orderId: orderId } : null, opts);
  },
  /**
   * POST /api/punches 打卡
   * @param {{orderId:number,kind:'Arrive'|'Leave',latitude:number,longitude:number,address:string,photoFileId:number}} body
   * @returns {Promise<{punch:PunchDto,order:OrderListItem}>} 类型已变化 → 40900「打卡状态已变化，请刷新」
   */
  create(body, opts) {
    return post('/api/punches', body, opts);
  }
};

/* ---------------- 6. 工时 ---------------- */
const hours = {
  /**
   * GET /api/hours/today
   * @param {number} [engineerId] 仅 Supervisor 查看他人时传
   * @returns {Promise<{date:string,closedMinutes:number,running:boolean,runningSince:?string,serverTime:string,
   *   firstArriveTime:?string,tripDay:number,entries:Array<{orderId,orderNo,customerName,arrive:PunchDto,leave:?PunchDto,minutes:number,isSupplement:boolean,role:'Lead'|'Assistant'|'Former'}>}>}
   */
  today(engineerId, opts) {
    return get('/api/hours/today', engineerId ? { engineerId: engineerId } : null, opts);
  },
  /**
   * GET /api/hours/month
   * @param {number} year
   * @param {number} month 1-12
   * @param {number} [engineerId]
   * @returns {Promise<{year:number,month:number,totalMinutes:number,tripDays:number,
   *   days:Array<{date:string,minutes:number,running:boolean,hasSupplement:boolean,
   *   entries:Array<{orderId,orderNo,customerName,arriveTime,leaveTime,minutes,isSupplement,supplementReason,missing,role}>}>}>}
   */
  month(year, month, engineerId, opts) {
    const q = { year: year, month: month };
    if (engineerId) q.engineerId = engineerId;
    return get('/api/hours/month', q, opts);
  }
};

/* ---------------- 7. 首页聚合 ---------------- */
const home = {
  /**
   * GET /api/home/engineer
   * @returns {Promise<{user:User,today:Object,state:'working'|'next'|'idle',current:?OrderListItem,
   *   next:?(OrderListItem & {address,contactName,contactPhone,latitude,longitude}),upcoming:OrderListItem[],
   *   hall:{count:number,urgentCount:number,items:OrderListItem[]},recentDone:OrderListItem[],onHold:OrderListItem[],monthMinutes:number,
   *   transfersToMe:OrderListItem[]}>}
   *   today 结构同 hours.today()；v1.3 transfersToMe = 待我确认的转交（每项带 pendingTransfer）
   */
  engineer(opts) {
    return get('/api/home/engineer', null, opts);
  },
  /**
   * GET /api/home/supervisor
   * @returns {Promise<{user:User,now:string,onSiteCount:number,
   *   timeline:Array<{engineer:UserBrief,segments:Array<{start:string,end:?string,customerName:string,orderId:number}>,note:string}>,
   *   todos:Array<{type:'HoldRequest'|'AssistantRequest'|'Unassigned'|'Submitted'|'PriceSuggestion'|'MissingPunch'|'DistanceAbnormal',
   *   orderId:number,requestId:?number,title:string,subtitle:string,urgent?:boolean}>}>}
   */
  supervisor(opts) {
    return get('/api/home/supervisor', null, opts);
  }
};

/* ---------------- 指派面板 ---------------- */
const engineers = {
  /**
   * GET /api/engineers（Supervisor + Engineer：负责人申请协助人时选人）
   * @returns {Promise<Array<{user:UserBrief,doingCount:number,acceptedCount:number,submittedCount:number,assistCount:number,onSiteNow:boolean,loadText:string}>>}
   */
  list(opts) {
    return get('/api/engineers', null, opts);
  }
};

/* ---------------- 8. 主管后台 ---------------- */
const admin = {
  /**
   * GET /api/admin/overview
   * @returns {Promise<{pending,urgentPending,inProgress,onSiteEngineers,submitted,closedThisWeek,weekMinutes,updatedAt,
   *   onHold,pendingRequests,unpriced,priceSuggestions}>}
   */
  overview(opts) {
    return get('/api/admin/overview', null, opts);
  },
  /**
   * POST /api/admin/punches/supplement 补录缺卡
   * @param {{orderId:number,arrivePunchId:number,kind:'Leave'|'Arrive',time:string,reason:string}} body
   * @returns {Promise<OrderDetail>}
   */
  supplement(body, opts) {
    return post('/api/admin/punches/supplement', body, opts);
  },
  /**
   * GET /api/admin/export/orders → xlsx，下载到本地
   * @param {{from?:string,to?:string,status?:string}} [params]
   * @returns {Promise<string>} tempFilePath（可用 wx.openDocument({filePath, fileType:'xlsx'}) 打开）
   */
  exportOrders(params) {
    return http.download('/api/admin/export/orders', params);
  },
  /**
   * GET /api/admin/export/hours → xlsx
   * @param {number} year
   * @param {number} month
   * @returns {Promise<string>} tempFilePath
   */
  exportHours(year, month) {
    return http.download('/api/admin/export/hours', { year: year, month: month });
  },
  /**
   * GET /api/admin/employees
   * @returns {Promise<Array<{id,name,phone,role,roleName,department,enabled,bound,avatarUrl}>>}
   */
  employees(opts) {
    return get('/api/admin/employees', null, opts);
  },
  /**
   * POST /api/admin/employees
   * @param {{name:string,phone:string,role:string,department:string}} body
   */
  createEmployee(body, opts) {
    return post('/api/admin/employees', body, opts);
  },
  /**
   * PUT /api/admin/employees/{id}
   * @param {number} id
   * @param {{name:string,phone:string,role:string,department:string,enabled:boolean}} body
   */
  updateEmployee(id, body, opts) {
    return put('/api/admin/employees/' + id, body, opts);
  },
  /**
   * POST /api/admin/employees/{id}/unbind 解绑微信
   * @param {number} id
   */
  unbindEmployee(id, opts) {
    return post('/api/admin/employees/' + id + '/unbind', {}, opts);
  }
};

/* ---------------- 注册审批（v1.8，小程序端） ---------------- */
const registrations = {
  /**
   * GET /api/registrations 管理员或有「注册审批权限」的人可用（user.canApprove）
   * @returns {Promise<{pendingCount:number,items:{id:number,name:string,phone:string,status:'Pending'|'Approved'|'Rejected',
   *   statusText:string,rejectReason:?string,submittedAt:string,reviewedAt:?string,reviewedByName:?string}[]}>}
   */
  list(opts) {
    return get('/api/registrations', null, opts);
  },
  /**
   * POST /api/registrations/{id}/approve
   * @param {{name?:string,position:'Engineer'|'Sales'|'SalesManager'|'Supervisor',department:string}} body
   *   这里不能传管理员岗位 / isAdmin（40000「管理员权限请到电脑后台设置」）
   */
  approve(id, body, opts) {
    return post('/api/registrations/' + id + '/approve', body, opts);
  },
  /** POST /api/registrations/{id}/reject */
  reject(id, reason, opts) {
    return post('/api/registrations/' + id + '/reject', { reason: reason }, opts);
  }
};

module.exports = {
  registrations,
  auth,
  me,
  files,
  orders,
  punches,
  hours,
  home,
  engineers,
  admin
};
