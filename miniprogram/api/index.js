/**
 * 接口层：与 docs/API.md 一一对应。
 * 所有函数返回 Promise<data>（已解包 {code,message,data}），出错 reject ApiError {code,message,httpStatus,data}。
 * 最后一个参数 opts 可选，透传给 request：{ silent?:boolean, loading?:boolean|string }。
 *
 * 常用类型（详见 API.md「公共 DTO」）：
 * @typedef {{id:number,name:string,surname:string,avatarUrl:?string,role:'Engineer'|'Sales'|'Supervisor'}} UserBrief
 * @typedef {UserBrief & {roleName:string,department:string,phoneMasked:string,phone:string}} User
 * @typedef {{id:number,url:string,category:string,mimeType:string,createdAt:string}} FileDto
 * @typedef {Object} OrderListItem  id, orderNo, status, urgency, serviceType, problemType, customerName, city, region,
 *   deviceModel, deviceSn, symptom, price:?number, priceSet:?boolean（工程师恒为 null）, assignee:?UserBrief（负责人）,
 *   assistants:UserBrief[], myRole:?('Lead'|'Assistant'), myMinutes:?number, myOnSiteSince:?string, creator:UserBrief,
 *   createdAt, claimedAt, appointmentTime, submittedAt, closedAt, totalMinutes（所有人合计）, onSiteSince, lastArriveTime,
 *   issues:string[], progressStep:number(0-5), holdReasonType, holdText, holdFromStatus, pendingRequestCount, factoryText
 * @typedef {Object} OrderDetail  OrderListItem + source, contactName, contactPhone, address, latitude, longitude, satDate,
 *   satSigned, productStage, projectNo, faultTime, customerTried, siteEnvironment, customerRequest, holdReason(=holdText),
 *   priceSetAt, priceSuggestion:?{id,amount,note,by:UserBrief,createdAt,pending}, hold:?{reasonType,reasonText,note,text,fromStatus,at,by},
 *   requests:RequestDto[], participants:ParticipantDto[], factory:?{nodes:FactoryNodeDto[]}, closeBlockedReason:?string,
 *   closeHints:string[], completion, files{orderPhotos,punchPhotos,processPhotos,signatures,factoryPhotos},
 *   punches:PunchDto[], logs:StatusLogDto[]（含 kind）, issueDetails[]（含 engineer）, actions:string[]
 * @typedef {Object} RequestDto  id, orderId, type:'Hold'|'AddAssistant', status, requester:UserBrief, createdAt, reasonType,
 *   reasonText, note, text, engineer:?UserBrief, decider, decidedAt, reply, canDecide:boolean, canCancel:boolean
 * @typedef {{user:UserBrief,role:'Lead'|'Assistant'|'Former',totalMinutes:number,onSiteSince:?string,missingPunch:boolean}} ParticipantDto
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
   * POST /api/auth/dev-login（仅后端 MockMode）
   * @param {string} phone 员工手机号
   * @returns {Promise<LoginResult>}
   */
  devLogin(phone, opts) {
    return post('/api/auth/dev-login', { phone: phone }, Object.assign({ auth: false }, opts));
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
  }
};

/* ---------------- 3. 文件 ---------------- */
const files = {
  /**
   * POST /api/files（multipart）
   * @param {string} filePath 本地临时文件
   * @param {'OrderPhoto'|'PunchPhoto'|'ProcessPhoto'|'Signature'|'Avatar'|'FactoryReceivePhoto'|'FactoryShipPhoto'|'FactorySignPhoto'} category
   * @returns {Promise<FileDto>}
   */
  upload(filePath, category, opts) {
    return http.uploadFile(filePath, category, opts);
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
   * GET /api/orders/mine 工程师本人负责或协助的工单（每项带 myRole）
   * @param {'doing'|'check'|'closed'|'all'} [group='doing']
   * @returns {Promise<{items:OrderListItem[],counts:{doing:number,check:number,closed:number,all:number}}>}
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
   * @returns {Promise<Array<{customerName,contactName,contactPhone,address,latitude,longitude,deviceModel,deviceSn,satDate,satSigned,projectNo}>>}
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
   *   address, latitude, longitude, deviceModel, deviceSn, satDate('yyyy-MM'), satSigned, projectNo, faultTime, symptom,
   *   customerTried, siteEnvironment, customerRequest, photoFileIds:number[]（v1.1 起不含 price，定价由主管在详情操作）
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
   * POST /api/orders/{id}/close 确认闭环（Supervisor）；有缺卡 → 40900「请先补录缺卡」；未定价 → 40900「请先设置服务价格（保内填 0）」
   * @returns {Promise<OrderDetail>}
   */
  close(id, opts) {
    return post('/api/orders/' + id + '/close', {}, opts);
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
   *   hall:{count:number,urgentCount:number,items:OrderListItem[]},recentDone:OrderListItem[],onHold:OrderListItem[],monthMinutes:number}>}
   *   today 结构同 hours.today()
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

module.exports = {
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
