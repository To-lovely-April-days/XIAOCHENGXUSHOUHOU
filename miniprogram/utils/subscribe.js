/**
 * 微信订阅消息「工单提醒」（v1.4）。模板由后端配置下发（GET /api/me/subscribe），后端未启用时这里的一切都静默不做事。
 *
 * 一次性订阅消息：用户点一次「允许」只能收到一条；在授权弹窗里勾选「总是保持以上选择，不再询问」之后，
 * 每次由点击触发的 wx.requestSubscribeMessage 都不再弹窗，并静默再攒一条额度。所以：
 *   - open()   ：开启入口（「我的 → 工单提醒」、首页提示条的「去开启」），会弹说明和授权弹窗；
 *   - topUp()  ：挂在高频点击上，只有缓存状态已是 'always'（不会弹窗）时才调用，用来攒额度；
 *   - refresh()：刷新配置与授权状态缓存。auth.ensureLogin() 成功后会自动调用，而每个页面的 onShow 都走 ensureLogin，
 *                所以页面不需要自己刷新；只有要把状态显示出来的页面（首页、我的）才直接用它的返回值。
 *
 * 授权状态：'always' 已勾选「总是保持」并允许 | 'rejected' 已勾选「总是保持」并拒绝 | 'off' 通知总开关已关 |
 *           'ask' 还没做过「总是」选择（每次调用都会弹窗）| '' 未知（未启用 / 还没查到 / 基础库不支持）
 *
 * wx.requestSubscribeMessage 只能在点击事件的同步调用栈（或 wx.showModal 的回调）里调用，
 * 所以 request() / topUp() / open() 都必须在点击处理函数里直接调用，前面不能有 await / 网络请求。
 */
const api = require('../api/index.js');

/** 配置缓存时间：到期后下一次 refresh 重新拉取（后台配好或更换模板后不必重启小程序） */
const CONFIG_TTL = 10 * 60 * 1000;
/** 拉取配置失败后，至少隔这么久才重试 */
const RETRY_GAP = 60 * 1000;
/** 两次静默攒额度之间的最小间隔 */
const TOP_UP_GAP = 1500;
/** 静默攒额度没攒上（调用失败 / 被拒收）之后，暂停这么久再试，免得每次点击都空调用 */
const TOP_UP_PAUSE = 60 * 1000;
/** wx.getSetting 超过这么久没回调就当作这次查询作废，允许重新查 */
const CHECK_TIMEOUT = 5000;
/** 开启入口的防连点间隔 */
const OPEN_GAP = 600;
/** 一次 wx.requestSubscribeMessage 最多带 3 个模板 */
const MAX_TMPL = 3;
/** 首页提示条关闭时间的 storage key；关闭后 7 天内不再显示 */
const TIP_KEY = 'subscribeTipClosedAt';
const TIP_SILENCE = 7 * 24 * 60 * 60 * 1000;

const INTRO = '开启后，工单指派、转交、审批结果会通过微信"服务通知"提醒你。请在接下来的弹窗中勾选"总是保持以上选择，不再询问"，再点"允许"。';

const state = {
  enabled: false,
  tmplIds: [],
  loadedAt: 0,   // 最近一次成功拉到配置的时间
  failedAt: 0,   // 最近一次拉取失败的时间
  loading: null, // 进行中的 load
  status: '',
  checking: null, // 进行中的 status
  checkingAt: 0,  // 进行中的 status 的发起时间
  checkSeq: 0,    // status 的序号：只有最新一次查询的结果才写进缓存
  requestingAt: 0, // 进行中的 wx.requestSubscribeMessage 的发起时间
  topUpAt: 0,
  topUpPauseUntil: 0,
  openAt: 0
};

function loggedIn() {
  try {
    const a = getApp();
    return !!(a && a.globalData && a.globalData.token && a.globalData.user);
  } catch (e) {
    return false;
  }
}

function snapshot() {
  return { enabled: state.enabled, templateIds: state.tmplIds.slice() };
}

function applyConfig(d) {
  const ids = d && d.enabled && Array.isArray(d.templateIds)
    ? d.templateIds.filter((x) => typeof x === 'string' && x).slice(0, MAX_TMPL)
    : [];
  if (ids.join('|') !== state.tmplIds.join('|')) state.status = '';
  state.tmplIds = ids;
  state.enabled = ids.length > 0;
}

/** 后端是否已启用订阅消息（同步读缓存；未加载过时为 false） */
function isEnabled() {
  return state.enabled && state.tmplIds.length > 0;
}

/**
 * 拉取订阅消息配置并缓存在内存。未登录不请求；失败或未启用都按「未启用」处理，不提示。
 * @param {boolean} [force] 忽略缓存重新拉取
 * @returns {Promise<{enabled:boolean,templateIds:string[]}>} 不会 reject
 */
function load(force) {
  if (!loggedIn()) return Promise.resolve(snapshot());
  if (state.loading) return state.loading;
  const now = Date.now();
  if (!force) {
    if (state.loadedAt && now - state.loadedAt < CONFIG_TTL) return Promise.resolve(snapshot());
    if (state.failedAt && now - state.failedAt < RETRY_GAP) return Promise.resolve(snapshot());
  }
  state.loading = api.me.getSubscribeConfig({ silent: true }).then((d) => {
    applyConfig(d);
    state.loadedAt = Date.now();
    state.failedAt = 0;
  }, (err) => {
    // 网络异常 / 后端还没升级到 v1.4（404）：沿用已有配置（没有就是未启用），稍后再试。
    // 登录失效（401）不计入：重新登录后的第一次 refresh 要能立刻重新拉取
    if (!(err && err.code === 40100)) state.failedAt = Date.now();
  }).then(() => {
    state.loading = null;
    return snapshot();
  });
  return state.loading;
}

/** 由 wx.getSetting 的 subscriptionsSetting 算出授权状态 */
function parseSetting(sub, ids) {
  if (!sub || !ids.length) return '';
  if (sub.mainSwitch === false) return 'off';
  const items = sub.itemSettings || {};
  if (ids.every((id) => items[id] === 'accept')) return 'always';
  if (ids.some((id) => items[id] === 'reject' || items[id] === 'ban')) return 'rejected';
  return 'ask';
}

/**
 * 用 wx.getSetting({ withSubscriptions: true }) 查询当前授权状态，并更新缓存。
 * @returns {Promise<''|'always'|'rejected'|'off'|'ask'>} 不会 reject
 */
function status() {
  if (!isEnabled()) {
    state.status = '';
    return Promise.resolve('');
  }
  const startedAt = Date.now();
  if (state.checking && startedAt - state.checkingAt < CHECK_TIMEOUT) return state.checking;
  const ids = state.tmplIds.slice();
  const seq = ++state.checkSeq;
  state.checkingAt = startedAt;
  const p = new Promise((resolve) => {
    if (typeof wx.getSetting !== 'function') {
      resolve('');
      return;
    }
    try {
      wx.getSetting({
        withSubscriptions: true,
        success: (res) => resolve(parseSetting(res && res.subscriptionsSetting, ids)),
        fail: () => resolve('')
      });
    } catch (e) {
      resolve('');
    }
  }).then((st) => {
    // 已被更新的一次查询取代（这次超时未回调）：结果作废
    if (seq !== state.checkSeq) return cachedStatus();
    state.checking = null;
    // 查询期间模板换了：这次结果作废，等下一次 refresh
    const next = ids.join('|') === state.tmplIds.join('|') ? st : '';
    // 状态变了（如用户刚重新开启）：解除静默攒额度的暂停
    if (next !== state.status) state.topUpPauseUntil = 0;
    state.status = next;
    return state.status;
  });
  state.checking = p;
  return p;
}

/** 缓存的授权状态（同步；点击处理函数里用它做判断） */
function cachedStatus() {
  return isEnabled() ? state.status : '';
}

/**
 * 刷新配置与授权状态缓存。
 * @returns {Promise<string>} 最新授权状态，不会 reject
 */
function refresh() {
  return load().then(() => status());
}

/**
 * 调起订阅授权。必须在点击事件的同步调用栈（或 wx.showModal 的回调）里调用。
 * 这里不弹任何提示：未启用 / 基础库不支持 → resolve(null)；调用失败 → resolve({ ok:false, errCode, errMsg })。
 * @returns {Promise<?{ok:boolean,accepted:boolean,result?:Object,errCode?:number,errMsg?:string}>} 不会 reject；
 *   accepted = 所有模板这一次都被允许
 */
function request() {
  return new Promise((resolve) => {
    if (!isEnabled() || typeof wx.requestSubscribeMessage !== 'function') {
      resolve(null);
      return;
    }
    const ids = state.tmplIds.slice();
    state.requestingAt = Date.now();
    try {
      wx.requestSubscribeMessage({
        tmplIds: ids,
        success: (res) => {
          const result = {};
          ids.forEach((id) => { result[id] = (res && res[id]) || ''; });
          resolve({ ok: true, accepted: ids.every((id) => result[id] === 'accept'), result: result });
        },
        fail: (err) => {
          resolve({ ok: false, accepted: false, errCode: Number(err && err.errCode) || 0, errMsg: String((err && err.errMsg) || '') });
        },
        complete: () => { state.requestingAt = 0; }
      });
    } catch (e) {
      state.requestingAt = 0;
      resolve(null);
    }
  });
}

/**
 * 静默攒一条额度：只有缓存状态已是 'always'（用户勾过「总是保持以上选择」并允许，此时不会弹窗）才调用 request()。
 * 在点击处理函数里同步调用；不等待结果、不抛错、不影响后面的逻辑。
 * @returns {boolean} 这次是否发起了调用
 */
function topUp() {
  try {
    if (cachedStatus() !== 'always') return false;
    // 授权状态正在重查（页面刚 onShow，用户可能刚从设置页回来）：缓存可能已过期，这次不调用
    if (state.checking) return false;
    const now = Date.now();
    if (now < state.topUpPauseUntil) return false;
    if (state.requestingAt && now - state.requestingAt < 5000) return false;
    if (now - state.topUpAt < TOP_UP_GAP) return false;
    state.topUpAt = now;
    request().then((r) => {
      // 没攒上（用户在设置里改了选择、模板被停用、网络异常等）：暂停一会儿并重新查一次状态，免得之后每次点击都空调用
      if (r && !r.accepted) {
        state.topUpPauseUntil = Date.now() + TOP_UP_PAUSE;
        status();
      }
    });
    return true;
  } catch (e) {
    return false;
  }
}

/**
 * 给页面 / 组件定义里的点击处理函数挂上 topUp()：先同步调 topUp()，再原样执行原函数（this、参数、返回值都不变）。
 *   Page(subscribe.bindTaps({ ... }, ['goDetail', 'onClaim']));
 *   Component(subscribe.bindTaps({ ..., methods: { ... } }, ['toDetail']));
 * 只挂由 bindtap 直接触发的函数。
 * @param {Object} def Page / Component 的定义对象
 * @param {string[]} names 函数名（Page 在定义对象上找，Component 在 methods 里找）
 * @returns {Object} def 本身
 */
function bindTaps(def, names) {
  const hosts = [def, def && def.methods].filter((h) => h && typeof h === 'object');
  (names || []).forEach((name) => {
    const host = hosts.find((h) => typeof h[name] === 'function');
    if (!host) {
      console.warn('[subscribe] bindTaps 未找到点击处理函数：' + name);
      return;
    }
    const fn = host[name];
    host[name] = function () {
      topUp();
      return fn.apply(this, arguments);
    };
  });
  return def;
}

function toast(title, icon) {
  wx.showToast({ title: title, icon: icon || 'none', duration: 2000 });
}

/** 已拒绝 / 总开关已关：引导去设置页打开 */
function guideSetting(st) {
  return new Promise((resolve) => {
    wx.showModal({
      title: '工单提醒已关闭',
      content: st === 'off'
        ? '你已关闭本小程序的通知。请在设置页进入「通知管理」，打开「接收通知」并允许工单提醒。'
        : '你之前选择了不接收工单提醒。请在设置页进入「通知管理」，把工单提醒改为接收。',
      confirmText: '去设置',
      cancelText: '取消',
      confirmColor: '#1D1D1F',
      success: (r) => {
        if (!r.confirm || typeof wx.openSetting !== 'function') {
          resolve(cachedStatus());
          return;
        }
        wx.openSetting({
          withSubscriptions: true,
          fail: () => toast('请点右上角「···」进入「设置」打开通知'),
          complete: () => { status().then(resolve); }
        });
      },
      fail: () => resolve(cachedStatus())
    });
  });
}

/** 授权弹窗关闭之后：按最新状态给出反馈 */
function afterRequest(res) {
  return status().then((st) => {
    if (!res) {
      toast('当前微信版本暂不支持，请升级微信后再试');
      return st;
    }
    if (st === 'always') {
      toast('提醒已开启', 'success');
      return st;
    }
    // 20004：用户关闭了通知总开关
    if (st === 'off' || res.errCode === 20004) return guideSetting('off');
    if (!res.ok) {
      console.warn('[subscribe] requestSubscribeMessage 失败', res.errCode, res.errMsg);
      toast('暂时无法开启提醒，请稍后再试');
      return st;
    }
    if (res.accepted) {
      wx.showModal({
        title: '已允许本次提醒',
        content: '这次只能收到一条提醒。请再开启一次，在弹窗中勾选"总是保持以上选择，不再询问"后再点"允许"，之后才能持续收到。',
        showCancel: false,
        confirmText: '知道了',
        confirmColor: '#1D1D1F'
      });
      return st;
    }
    toast('未开启工单提醒');
    return st;
  });
}

/**
 * 「工单提醒」开启入口。必须在点击处理函数里直接调用。
 *   'ask' / 未知 → 先弹说明，确认后在该回调里调起授权弹窗；
 *   'rejected' / 'off' → 引导去设置页打开（wx.openSetting）；
 *   'always' → 提示「提醒已开启」，顺便攒一条额度。
 * @returns {Promise<string>} 流程结束后的最新授权状态，不会 reject；后端未启用时直接 resolve('')
 */
function open() {
  if (!isEnabled()) return Promise.resolve('');
  const st = cachedStatus();
  // 防连点：避免叠出两个说明弹窗、连续调起两次授权
  const now = Date.now();
  if (now - state.openAt < OPEN_GAP) return Promise.resolve(st);
  state.openAt = now;
  if (st === 'always') {
    topUp();
    toast('提醒已开启', 'success');
    return Promise.resolve(st);
  }
  if (st === 'rejected' || st === 'off') return guideSetting(st);
  return new Promise((resolve) => {
    wx.showModal({
      title: '开启工单提醒',
      content: INTRO,
      confirmText: '去开启',
      cancelText: '暂不',
      confirmColor: '#1D1D1F',
      success: (r) => {
        if (!r.confirm) {
          resolve(cachedStatus());
          return;
        }
        // 仍在点击回调的同步调用栈里，可以调起授权弹窗
        request().then(afterRequest).then(resolve, () => resolve(cachedStatus()));
      },
      fail: () => resolve(cachedStatus())
    });
  });
}

/** 「我的 → 工单提醒」右侧的状态文字 */
function statusText() {
  const st = cachedStatus();
  if (st === 'always') return '已开启';
  if (st === 'rejected' || st === 'off') return '已关闭';
  return '未开启';
}

function tipClosedRecently() {
  try {
    const at = Number(wx.getStorageSync(TIP_KEY)) || 0;
    return at > 0 && Date.now() - at < TIP_SILENCE;
  } catch (e) {
    return false;
  }
}

/** 首页提示条是否显示：已启用、从未做过「总是」选择（'ask'）、且 7 天内没点过关闭 */
function tipVisible() {
  return cachedStatus() === 'ask' && !tipClosedRecently();
}

/** 关闭首页提示条：记住 7 天 */
function closeTip() {
  try { wx.setStorageSync(TIP_KEY, Date.now()); } catch (e) { /* ignore */ }
}

module.exports = {
  load,
  status,
  refresh,
  request,
  topUp,
  bindTaps,
  open,
  isEnabled,
  cachedStatus,
  statusText,
  tipVisible,
  closeTip
};
