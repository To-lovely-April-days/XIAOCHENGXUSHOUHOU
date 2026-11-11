// 工单详情抽屉（原型 16 右侧）：进度条、基本信息与定价、暂停提示、待审批申请、异常警示框 + 补录、
// 处理人员（按人工时）、返厂流程时间线、附件（v1.2）、打卡记录、状态记录、底部操作。
// 所有可执行动作都由服务端计算：detail.actions、requests[].canDecide、factory.nodes[].canRecord / canEdit、
// canAddAttachment / canRestrictAttachment / attachments[].canDelete / canToggleRestricted，这里不推导规则。
import * as api from '../api.js';
import { toast, toastError, confirm, preview } from '../ui.js';
import { fmtHM, fmtPrice, fmtMD, fmtMDHM, fmtTime, fmtDistance, isWarranty, parseTime, toApiDate, pad2, fmtSize, fileBadge } from '../format.js';
import { ORDER_STATUS, SERVICE_TYPE, PUNCH_KIND, ORDER_ACTIONS, PROGRESS_STEPS, REQUEST_TYPE, FACTORY_NODE, PARTICIPANT_ROLE, label } from '../enums.js';
import OrdersAssignModal from './orders-assign.js';
import OrdersHoldModal from './orders-hold.js';
import OrdersPriceModal from './orders-price.js';
import OrdersFactoryModal from './orders-factory.js';
import AttachmentsUpload, { attUploading, attHasError, attDone } from './attachments-upload.js';
import { navigate } from '../router.js';

/**
 * 原型按状态的按钮映射。每项：
 *   action  需要出现在 detail.actions 里才显示（null = 不依赖 actions）
 *   kind    primary | secondary
 *   fallbackDisabled  action 不在 actions 中时仍显示为禁用按钮，tip 为提示
 */
const LAYOUT = {
  Pending: [{ action: 'void', kind: 'secondary' }, { action: 'factory', kind: 'secondary' }, { action: 'assign', kind: 'primary' }],
  Accepted: [
    { action: 'void', kind: 'secondary' },
    { action: 'hold', kind: 'secondary' },
    { action: 'factory', kind: 'secondary' },
    { action: 'reassign', kind: 'primary' },
  ],
  InProgress: [
    { action: 'hold', kind: 'secondary' },
    { action: 'reassign', kind: 'secondary' },
    { action: 'factory', kind: 'secondary' },
    { action: 'close', kind: 'primary', fallbackDisabled: '工程师提交完工后才能验收' },
  ],
  OnHold: [{ action: 'reassign', kind: 'secondary' }, { action: 'factory', kind: 'secondary' }, { action: 'resume', kind: 'primary' }],
  Submitted: [
    { action: 'reject', kind: 'secondary' },
    { action: 'factory', kind: 'secondary' },
    // 禁用时的提示取服务端的 closeBlockedReason（缺卡 / 未定价）
    { action: 'close', kind: 'primary', fallbackDisabled: '请先补录缺卡' },
  ],
  Closed: [{ action: 'export', kind: 'secondary', label: '导出本单', always: true }],
  Voided: [],
};

export default {
  name: 'OrdersDrawer',
  components: { OrdersAssignModal, OrdersHoldModal, OrdersPriceModal, OrdersFactoryModal, AttachmentsUpload },
  props: {
    orderId: { type: Number, required: true },
    serverOffset: { type: Number, default: 0 },
    nowTs: { type: Number, default: () => Date.now() },
  },
  emits: ['close', 'changed'],
  data() {
    return {
      detail: null,
      loading: false,
      error: '',
      seq: 0,
      busy: '', // 正在执行的 action
      forms: {}, // 补录表单：key = 到场打卡 id
      assignOpen: false,
      assignMode: 'assign', // 'assign' | 'assistants'
      holdOpen: false,
      priceOpen: false,
      closeAfterPrice: false, // 「定价并闭环」：定价成功后继续确认闭环
      priceInitial: null, // 打开定价弹窗时预填的金额（采用建议）
      factoryNode: null, // 正在记录 / 更正的返厂节点（FactoryDto.nodes 项）
      attOpen: false, // 「添加附件」面板是否展开
      attItems: [], // 待添加的附件条目（attachments-upload.js）
      attSaving: false,
      attBusy: 0, // 正在下载 / 删除 / 切换的附件 id
    };
  },
  computed: {
    d() { return this.detail; },
    statusMeta() { return (this.d && ORDER_STATUS[this.d.status]) || { label: '', dot: '#D4D4D8' }; },
    deviceLine() {
      const d = this.d;
      if (!d) return '';
      return [d.deviceModel, d.deviceSn, label(SERVICE_TYPE, d.serviceType)].filter(Boolean).join(' · ');
    },
    actions() { return (this.d && this.d.actions) || []; },
    onsite() { return !!this.d && this.d.serviceType === 'Onsite'; },
    /** 5 段进度条：点亮段数用服务端 progressStep；暂停时第 progressStep 段显示暂停样式 */
    segments() {
      const d = this.d;
      const step = d ? (Number(d.progressStep) || 0) : 0;
      const held = !!d && d.status === 'OnHold';
      return PROGRESS_STEPS.map((text, i) => {
        const n = i + 1;
        const paused = held && n === step;
        return { key: n, label: paused ? '暂停' : text, title: text, on: n <= step && !paused, paused, current: n === step && !held };
      });
    },
    progressLabel() {
      const d = this.d;
      if (!d) return '';
      return '进度：' + this.statusMeta.label + (this.holdText ? '，' + this.holdText : '');
    },
    holdText() {
      const d = this.d;
      if (!d || d.status !== 'OnHold') return '';
      return d.holdText || (d.hold && d.hold.text) || d.holdReason || '';
    },
    /** 暂停提示条：原因 + 暂停人、时间、恢复后回到的状态 */
    holdInfo() {
      const d = this.d;
      if (!d || d.status !== 'OnHold') return null;
      const h = d.hold || {};
      const from = label(ORDER_STATUS, h.fromStatus || d.holdFromStatus || 'InProgress');
      const who = [h.by && h.by.name, h.at ? fmtMDHM(h.at) : ''].filter(Boolean).join(' ');
      return { text: this.holdText || '其他', sub: (who ? who + ' 暂停，' : '') + `恢复后回到「${from}」`, from };
    },
    /** 各参与人今天未离场的到场时间：engineerId → 时间串 */
    onSiteMap() {
      const d = this.d;
      const map = {};
      if (!d) return map;
      (d.participants || []).forEach(p => { if (p.onSiteSince && p.user) map[p.user.id] = p.onSiteSince; });
      // 旧数据 / 无 participants 时退回到工单级的 onSiteSince（归负责人）
      if (!Object.keys(map).length && d.onSiteSince && d.assignee) map[d.assignee.id] = d.onSiteSince;
      return map;
    },
    /** 所有人正在计时的分钟数合计（人·分钟） */
    runningMinutes() {
      return Object.keys(this.onSiteMap).reduce((sum, k) => sum + this.minutesSince(this.onSiteMap[k]), 0);
    },
    hoursText() {
      const d = this.d;
      if (!d || d.serviceType !== 'Onsite') return '—';
      const m = (d.totalMinutes || 0) + this.runningMinutes;
      if (!m && !d.onSiteSince && !(d.punches || []).length) return '—';
      return fmtHM(m);
    },
    unpriced() { return !!this.d && this.d.priceSet === false; },
    /** 待验收工单只差定价就能闭环（缺卡优先，由服务端 closeBlockedReason 判定） */
    needPriceToClose() { const d = this.d; return !!d && d.status === 'Submitted' && d.priceSet === false && String(d.closeBlockedReason || '').includes('价格'); },
    priceText() { return this.d ? fmtPrice(this.d.price, { free: true, priceSet: this.d.priceSet }) : ''; },
    warranty() { return this.d ? isWarranty(this.d.price, this.d.priceSet) : false; },
    canSetPrice() { return this.actions.includes('setPrice'); },
    /** 销售最新的价格建议 */
    suggestion() {
      const s = this.d && this.d.priceSuggestion;
      if (!s) return null;
      return {
        amount: s.amount,
        who: (s.by && s.by.name) || '销售',
        money: fmtPrice(s.amount, { free: true }),
        note: s.note || '',
        time: fmtMDHM(s.createdAt),
        pending: !!s.pending,
      };
    },
    /** 待审批的申请（暂停 / 协助人） */
    pendingRequests() {
      return ((this.d && this.d.requests) || []).filter(r => r.status === 'Pending').map(r => ({
        r,
        key: r.id,
        title: label(REQUEST_TYPE, r.type, '申请'),
        who: (r.requester && r.requester.name) || '',
        text: r.text || '',
        time: fmtMDHM(r.createdAt),
        canDecide: !!r.canDecide,
        busy: this.busy === 'req' + r.id,
      }));
    },
    canAssistants() { return this.actions.includes('assistants'); },
    /** 处理人员：负责人 → 协助人 → 已退出，每人各自的工时 */
    people() {
      const d = this.d;
      if (!d) return [];
      return (d.participants || []).map((p, i) => {
        const running = p.onSiteSince ? this.minutesSince(p.onSiteSince) : 0;
        const m = (p.totalMinutes || 0) + running;
        return {
          key: (p.user && p.user.id) || 'p' + i,
          user: p.user,
          name: (p.user && p.user.name) || '—',
          role: p.role,
          roleLabel: label(PARTICIPANT_ROLE, p.role),
          roleTone: p.role === 'Lead' ? 'dark' : 'neutral',
          hours: this.onsite ? ((m || p.onSiteSince) ? fmtHM(m) : '—') : '—',
          onSite: !!p.onSiteSince,
          missing: !!p.missingPunch,
        };
      });
    },
    multiPeople() { return this.people.length > 1; },
    /** 返厂 5 节点时间线 */
    factoryNodes() {
      const f = this.d && this.d.factory;
      if (!f) return [];
      return (f.nodes || []).map(n => {
        const meta = FACTORY_NODE[n.node] || {};
        const implicit = n.node === 'Repairing';
        const who = [n.operator && n.operator.name, !implicit && n.time ? fmtMDHM(n.time) : ''].filter(Boolean).join(' · ');
        let sub = who;
        if (implicit) sub = n.state === 'current' ? '工厂检测/维修中，寄回后结束' : (n.state === 'done' ? '已完成' : '');
        else if (n.state === 'skipped') sub = '未记录，主管已直接记录工厂收货';
        return {
          n,
          key: n.node,
          label: n.label || meta.label || n.node,
          state: n.state || 'todo',
          skipped: n.state === 'skipped',
          sub,
          courier: n.courier || '',
          trackingNo: n.trackingNo || '',
          note: n.note || '',
          photos: (n.photos || []).filter(p => p && p.url),
          canRecord: !!n.canRecord,
          canEdit: !!n.canEdit,
          actionLabel: meta.action || '记录',
        };
      });
    },
    /** 下一个可记录的返厂节点（底部「返厂」按钮用） */
    nextFactory() { return this.factoryNodes.find(x => x.canRecord) || null; },
    /** 附件列表（服务端已按当前用户过滤，createdAt 正序） */
    attRows() {
      const list = (this.d && this.d.attachments) || [];
      return list.map(a => {
        const t = parseTime(a.createdAt);
        const meta = [fmtSize(a.size), a.uploader && a.uploader.name, t ? `${t.getMonth() + 1}-${t.getDate()} ${fmtTime(t)}` : ''].filter(Boolean).join(' · ');
        return {
          a,
          key: a.id,
          name: a.name || '未命名',
          badge: fileBadge(a),
          media: a.kind === 'Image' || a.kind === 'Video',
          meta,
          fromOrderPhoto: a.source === 'OrderPhoto',
          restricted: !!a.restricted,
          canDelete: !!a.canDelete,
          canToggle: !!a.canToggleRestricted,
          toggleLabel: a.restricted ? '设为所有人可见' : '设为仅销售和主管可见',
          busy: this.attBusy === a.id,
        };
      });
    },
    /** 本单全部图片 / 视频附件（预览时左右切换） */
    attMedia() {
      return ((this.d && this.d.attachments) || []).filter(a => (a.kind === 'Image' || a.kind === 'Video') && a.url)
        .map(a => ({ id: a.id, url: a.url, type: a.kind === 'Video' ? 'video' : 'image' }));
    },
    attRemaining() {
      const n = this.d ? Number(this.d.attachmentRemaining) : 0;
      return isFinite(n) ? Math.max(0, n) : 0;
    },
    attUploading() { return attUploading(this.attItems); },
    attReady() { return attDone(this.attItems); },
    creatorLabel() {
      const c = this.d && this.d.creator;
      return c && c.role && c.role !== 'Sales' ? '建单人' : '销售';
    },
    canSupplement() { return !!(this.d && (this.d.actions || []).includes('supplement')); },
    alerts() {
      return ((this.d && this.d.issueDetails) || []).map((it, i) => ({
        ...it,
        key: it.type + '-' + (it.punchId ?? i),
        missing: it.type === 'MissingPunch',
      }));
    },
    missingArriveIds() {
      return new Set(this.alerts.filter(a => a.missing && a.punchId != null).map(a => a.punchId));
    },
    photoUrls() {
      return ((this.d && this.d.punches) || []).filter(p => p.photo && p.photo.url).map(p => p.photo.url);
    },
    punchRows() {
      const d = this.d;
      if (!d) return [];
      const rows = [];
      const urls = this.photoUrls;
      (d.punches || []).forEach(p => {
        const url = p.photo && p.photo.url;
        let note = '';
        let tone = '';
        let dur = '';
        if (p.isSupplement) {
          note = ['主管补录', p.supplementBy && p.supplementBy.name, p.supplementReason].filter(Boolean).join(' · ');
          tone = 'warn';
        } else if (p.distanceMeters !== null && p.distanceMeters !== undefined) {
          note = '距客户 ' + fmtDistance(p.distanceMeters);
          if (p.outOfRange) { note += ' · 超出范围'; tone = 'warn'; }
        }
        if (p.kind === 'Leave' && p.pairMinutes !== null && p.pairMinutes !== undefined) dur = fmtHM(p.pairMinutes);
        const since = p.engineer ? this.onSiteMap[p.engineer.id] : null;
        const running = p.kind === 'Arrive' && !!since && p.time === since;
        if (running) {
          note = (note ? note + ' · ' : '') + '现场计时中';
          dur = fmtHM(this.minutesSince(since));
        }
        rows.push({
          key: 'p' + p.id,
          time: fmtMDHM(p.time),
          kind: label(PUNCH_KIND, p.kind),
          who: p.engineer ? p.engineer.name : '',
          note, tone, dur,
          url, index: url ? urls.indexOf(url) : -1,
          photoLabel: label(PUNCH_KIND, p.kind) + '打卡照片 ' + fmtMDHM(p.time),
        });
        if (p.kind === 'Arrive' && this.missingArriveIds.has(p.id)) {
          rows.push({
            key: 'm' + p.id, time: fmtMD(p.time) + ' —', kind: '离场', who: '',
            note: '缺卡，未计工时', tone: 'danger', dur: '—', missing: true,
            photoLabel: '缺少离场打卡照片',
          });
        }
      });
      return rows;
    },
    emptyPunchText() {
      const d = this.d;
      if (!d) return '';
      if (d.serviceType !== 'Onsite') return label(SERVICE_TYPE, d.serviceType) + '工单无需打卡';
      if (!d.assignee) return '暂无处理人，尚未打卡';
      if (d.appointmentTime) return `预约 ${fmtMDHM(d.appointmentTime)}，尚未到场`;
      return '尚未打卡';
    },
    logs() {
      return ((this.d && this.d.logs) || []).map(l => ({ key: l.id, text: l.text, time: fmtMDHM(l.time) }));
    },
    buttons() {
      const d = this.d;
      if (!d) return [];
      const actions = this.actions;
      const out = [];
      (LAYOUT[d.status] || []).forEach(b => {
        const meta = ORDER_ACTIONS[b.action] || {};
        let text = b.label || meta.label || b.action;
        if (b.action === 'factory') {
          // 文案 = 第一个可记录节点的动作；没有可记录节点（或 actions 不含 factory）时不显示
          if (!actions.includes('factory') || !this.nextFactory) return;
          text = this.nextFactory.actionLabel;
        }
        if (b.always || actions.includes(b.action)) {
          out.push({ key: b.action, label: text, kind: b.kind, disabled: false, tip: '' });
        } else if (b.action === 'close' && this.needPriceToClose && this.canSetPrice) {
          // 只差定价：按钮可点，先弹出定价，保存后接着确认闭环
          out.push({ key: 'close', label: '定价并闭环', kind: b.kind, disabled: false, tip: '' });
        } else if (b.fallbackDisabled) {
          const tip = b.action === 'close' && d.status === 'Submitted' ? (d.closeBlockedReason || b.fallbackDisabled) : b.fallbackDisabled;
          out.push({ key: b.action, label: text, kind: b.kind, disabled: true, tip });
        }
      });
      return out;
    },
  },
  watch: {
    orderId: { immediate: true, handler() { this.load(); } },
  },
  methods: {
    async load() {
      const seq = ++this.seq;
      this.loading = true;
      this.error = '';
      try {
        const d = await api.orders.detail(this.orderId);
        if (seq !== this.seq) return;
        this.setDetail(d);
      } catch (e) {
        if (seq !== this.seq) return;
        this.error = e.message || '加载失败';
      } finally {
        if (seq === this.seq) this.loading = false;
      }
    },
    setDetail(d) {
      this.detail = d;
      // 已不存在的缺卡对应表单清掉
      const keep = {};
      (d.issueDetails || []).forEach(it => {
        if (it.type === 'MissingPunch' && this.forms[it.punchId]) keep[it.punchId] = this.forms[it.punchId];
      });
      this.forms = keep;
    },
    /** 操作成功：用返回的 OrderDetail 刷新抽屉，并通知父级刷新列表/概况 */
    applied(d, msg) {
      if (d && d.id) this.setDetail(d); else this.load();
      toast(msg, 'success');
      this.$emit('changed');
    },
    openPreview(row) { if (row.url) preview(this.photoUrls, row.index); },
    /** 从某个时间到现在（服务器时间）的分钟数 */
    minutesSince(s) {
      const t = parseTime(s);
      if (!t) return 0;
      return Math.max(0, Math.floor((this.nowTs + this.serverOffset - t.getTime()) / 60000));
    },
    /** 操作失败：提示；状态冲突等刷新抽屉拿到最新状态与 actions */
    failed(e) {
      toastError(e);
      if (e && (e.code === 40900 || e.code === 40400)) { this.load(); this.$emit('changed'); }
    },

    // ------------------------------------------------ 定价
    openPrice(amount) {
      this.priceInitial = amount === null || amount === undefined ? null : Number(amount);
      this.priceOpen = true;
    },
    onPriced(res, price) {
      this.priceOpen = false;
      const thenClose = this.closeAfterPrice;
      this.closeAfterPrice = false;
      this.applied(res, '已定价 ' + fmtPrice(price, { free: true }));
      // 从「定价并闭环」进来：价格保存后接着走确认闭环
      if (thenClose) this.$nextTick(() => { if (this.actions.includes('close')) this.run({ key: 'close', disabled: false }); });
    },

    // ------------------------------------------------ 暂停
    onHeld(res) {
      this.holdOpen = false;
      this.applied(res, '已标为暂停搁置');
    },

    // ------------------------------------------------ 申请审批
    async decide(req, approve) {
      if (this.busy) return;
      const d = this.d;
      const r = req.r;
      const isHold = r.type === 'Hold';
      const line = (req.who ? req.who + '：' : '') + req.text;
      try {
        if (approve) {
          const effect = isHold ? '同意后工单进入「暂停搁置」，暂停原因取申请内容。'
            : `同意后 ${(r.engineer && r.engineer.name) || '该工程师'} 成为本单协助人。`;
          if (!(await confirm('同意' + req.title, line + '\n' + effect, { okText: '同意' }))) return;
          this.busy = 'req' + r.id;
          const res = await api.orders.approveRequest(d.id, r.id);
          this.applied(res, '已同意' + req.title);
        } else {
          const reply = await confirm('驳回' + req.title, line,
            { input: true, multiline: true, inputLabel: '回复', placeholder: '选填，会写入状态记录并通知申请人', okText: '驳回', maxLength: 200 });
          if (reply === null) return;
          this.busy = 'req' + r.id;
          const res = await api.orders.rejectRequest(d.id, r.id, reply);
          this.applied(res, '已驳回' + req.title);
        }
      } catch (e) {
        this.failed(e);
      } finally {
        this.busy = '';
      }
    },

    // ------------------------------------------------ 协助人
    openAssign(mode) {
      this.assignMode = mode;
      this.assignOpen = true;
    },

    // ------------------------------------------------ 返厂
    openFactory(row) { if (row && row.n) this.factoryNode = row.n; },
    onFactoryDone(res, nodeLabel, edited) {
      this.factoryNode = null;
      this.applied(res, (edited ? '已更正' : '已记录') + '「' + nodeLabel + '」');
    },
    previewNode(row, i) { preview(row.photos.map(p => p.url), i); },
    async copyNo(text) {
      if (!text) return;
      let ok = false;
      try {
        if (navigator.clipboard && navigator.clipboard.writeText) { await navigator.clipboard.writeText(text); ok = true; }
      } catch (e) { ok = false; }
      if (!ok) {
        // 非安全上下文（http）下的兜底
        try {
          const ta = document.createElement('textarea');
          ta.value = text;
          ta.setAttribute('readonly', '');
          ta.style.position = 'fixed';
          ta.style.opacity = '0';
          document.body.appendChild(ta);
          ta.select();
          ok = document.execCommand('copy');
          ta.remove();
        } catch (e) { ok = false; }
      }
      toast(ok ? '已复制单号 ' + text : '复制失败，请手动选择单号', ok ? 'success' : 'error');
    },

    // ------------------------------------------------ 附件（v1.2）
    /** 点名称：图片 / 视频 → 预览（同单全部图片视频可左右切换）；文档 → 下载 */
    openAtt(row) {
      if (row.media && row.a.url) {
        const i = this.attMedia.findIndex(m => m.id === row.a.id);
        preview(this.attMedia, Math.max(0, i));
      } else {
        this.downloadAtt(row);
      }
    },
    async downloadAtt(row) {
      if (this.attBusy) return;
      this.attBusy = row.a.id;
      try {
        await api.files.download(row.a.id, row.name);
      } catch (e) {
        this.failed(e);
      } finally {
        this.attBusy = 0;
      }
    },
    async toggleAtt(row) {
      if (this.attBusy) return;
      this.attBusy = row.a.id;
      try {
        const res = await api.orders.setAttachmentRestricted(this.d.id, row.a.id, !row.restricted);
        this.applied(res, '已更新');
      } catch (e) {
        this.failed(e);
      } finally {
        this.attBusy = 0;
      }
    },
    async deleteAtt(row) {
      if (this.attBusy) return;
      if (!(await confirm('删除附件', `确定删除「${row.name}」？删除后不可恢复。`, { danger: true, okText: '删除' }))) return;
      this.attBusy = row.a.id;
      try {
        const res = await api.orders.deleteAttachment(this.d.id, row.a.id);
        this.applied(res, '已删除');
      } catch (e) {
        this.failed(e);
      } finally {
        this.attBusy = 0;
      }
    },
    openAttAdd() {
      if (this.attOpen) return;
      if (this.attRemaining <= 0) { toast('附件最多 20 个，本单已达上限', 'error'); return; }
      this.attItems = [];
      this.attOpen = true;
    },
    cancelAttAdd() {
      if (this.attSaving) return;
      // 已上传未提交的文件直接丢弃（服务端留作未挂载文件）
      this.attOpen = false;
      this.attItems = [];
    },
    async submitAtt() {
      if (this.attSaving) return;
      if (this.attUploading) { toast('附件仍在上传，请稍候'); return; }
      if (attHasError(this.attItems)) { toast('有附件未上传成功，请重试或移除', 'error'); return; }
      const list = this.attReady;
      if (!list.length) { toast('请选择要添加的附件'); return; }
      this.attSaving = true;
      try {
        const res = await api.orders.addAttachments(this.d.id, list);
        this.attOpen = false;
        this.attItems = [];
        this.applied(res, `已添加 ${list.length} 个附件`);
      } catch (e) {
        this.failed(e);
      } finally {
        this.attSaving = false;
      }
    },
    openAllFiles() { navigate('/files', { orderId: this.d.id }); },

    // ------------------------------------------------ 补录
    formFor(a) { return this.forms[a.punchId]; },
    openForm(a) {
      this.forms = Object.assign({}, this.forms, { [a.punchId]: { time: '', reason: '', error: '', saving: false } });
      this.$nextTick(() => {
        const el = this.$refs['suppTime' + a.punchId];
        const input = Array.isArray(el) ? el[0] : el;
        if (input) input.focus();
      });
    },
    cancelForm(a) {
      const next = Object.assign({}, this.forms);
      delete next[a.punchId];
      this.forms = next;
    },
    async saveForm(a) {
      const f = this.forms[a.punchId];
      if (!f || f.saving) return;
      f.error = '';
      if (!/^\d{1,2}:\d{2}$/.test(f.time || '')) { f.error = '请填写离场时间'; return; }
      const reason = (f.reason || '').trim();
      if (!reason) { f.error = '请填写补录原因'; return; }
      const arrive = (this.d.punches || []).find(p => p.id === a.punchId);
      const date = a.date || (arrive ? toApiDate(arrive.time) : '');
      const [h, m] = f.time.split(':');
      const time = `${date}T${pad2(+h)}:${m}:00`;
      if (arrive && parseTime(time) <= parseTime(arrive.time)) {
        f.error = `离场时间必须晚于到场时间 ${fmtTime(arrive.time)}`;
        return;
      }
      f.saving = true;
      try {
        const d = await api.admin.supplement({ orderId: this.d.id, arrivePunchId: a.punchId, kind: 'Leave', time, reason });
        this.cancelForm(a);
        this.applied(d, `已补录离场 ${pad2(+h)}:${m}`);
      } catch (e) {
        f.error = e.message || '保存失败';
        toastError(e);
      } finally {
        f.saving = false;
      }
    },

    // ------------------------------------------------ 底部操作
    async run(b) {
      if (b.disabled) { if (b.tip) toast(b.tip); return; }
      if (this.busy) return;
      const d = this.d;
      const no = d.orderNo;
      try {
        if (b.key === 'assign' || b.key === 'reassign') { this.openAssign('assign'); return; }
        if (b.key === 'hold') { this.holdOpen = true; return; }
        if (b.key === 'close' && this.needPriceToClose) { this.closeAfterPrice = true; this.openPrice(null); return; }
        if (b.key === 'factory') { this.openFactory(this.nextFactory); return; }
        if (b.key === 'export') {
          this.busy = 'export';
          const day = toApiDate(d.createdAt);
          // 契约只有 from/to/status：按建单当天 + 当前状态缩小范围；keyword 为前向兼容参数（后端忽略时导出当天同状态的工单）
          await api.admin.exportOrders({ from: day, to: day, status: d.status, keyword: no }, `售后进度台账_${no}.xlsx`);
          toast('已开始下载', 'success');
          return;
        }
        let res;
        if (b.key === 'void') {
          const reason = await confirm('作废工单', `确定作废 ${no}（${d.customerName}）？作废后不能恢复。`,
            { input: true, multiline: true, inputLabel: '作废原因', placeholder: '选填，会写入状态记录', okText: '作废', danger: true });
          if (reason === null) return;
          this.busy = b.key;
          res = await api.orders.void(d.id, reason);
          this.applied(res, '工单已作废');
        } else if (b.key === 'resume') {
          const from = this.holdInfo ? this.holdInfo.from : '处理中';
          if (!(await confirm('恢复处理', `确定将 ${no} 恢复为「${from}」？`, { okText: '恢复处理' }))) return;
          this.busy = b.key;
          res = await api.orders.resume(d.id);
          this.applied(res, `已恢复为「${from}」`);
        } else if (b.key === 'reject') {
          const reason = await confirm('退回处理中', '退回后工单回到处理中，工程师需重新提交完工。',
            { input: true, multiline: true, inputLabel: '退回原因', placeholder: '选填，会写入状态记录并通知工程师', okText: '退回' });
          if (reason === null) return;
          this.busy = b.key;
          res = await api.orders.reject(d.id, reason);
          this.applied(res, '已退回处理中');
        } else if (b.key === 'close') {
          const hints = (d.closeHints || []).filter(Boolean);
          const msg = `确认 ${no}（${d.customerName}）已处理完成并闭环？` + (hints.length ? `\n提示：${hints.join('；')}` : '');
          if (!(await confirm('确认闭环', msg, { okText: '确认闭环' }))) return;
          this.busy = b.key;
          res = await api.orders.close(d.id);
          this.applied(res, '已确认闭环');
        }
      } catch (e) {
        this.failed(e);
      } finally {
        this.busy = '';
      }
    },
    onAssigned(res, name, mode) {
      const re = this.d && this.d.status !== 'Pending';
      this.assignOpen = false;
      if (mode === 'assistants') this.applied(res, '协助人已更新');
      else this.applied(res, (re ? '已改派给 ' : '已指派给 ') + name);
    },
  },
  template: `
  <Drawer :width="440" label="工单详情" @close="$emit('close')">
    <template #head>
      <template v-if="d">
        <span class="mono orders-dr-no">{{ d.orderNo }}</span>
        <span class="status"><span class="dot" :style="{ background: statusMeta.dot }"></span>{{ statusMeta.label }}</span>
      </template>
      <span v-else class="text-muted">工单详情</span>
    </template>

    <Loading v-if="loading && !d" />
    <Empty v-else-if="error && !d" :text="'详情加载失败：' + error">
      <button type="button" class="btn btn-secondary btn-sm" @click="load">重试</button>
    </Empty>
    <template v-else-if="d">
      <h2 class="drawer-title">{{ d.customerName }}
        <Tag v-if="d.urgency === 'Urgent'" tone="urgent" class="orders-dr-tag">紧急</Tag></h2>
      <div class="drawer-sub">{{ deviceLine }}<template v-if="d.factoryText"> · {{ d.factoryText }}</template></div>

      <div class="progress5" role="img" :aria-label="progressLabel">
        <div v-for="s in segments" :key="s.key" class="progress5-seg" :class="{ on: s.on, paused: s.paused, current: s.current }" :title="s.title">
          <span class="progress5-bar"><span v-if="s.paused" class="progress5-pause" aria-hidden="true"></span></span>
          <span class="progress5-label">{{ s.label }}</span>
        </div>
      </div>
      <div v-if="holdInfo" class="progress5-note" :title="holdInfo.text">暂停 · {{ holdInfo.text }}</div>

      <dl class="kv">
        <div><dt>服务价格</dt>
          <dd><span :class="{ 'text-success': warranty, 'text-danger': unpriced }">{{ priceText }}</span>
            <button v-if="canSetPrice" type="button" class="btn-link orders-dr-link" @click="openPrice(null)">{{ unpriced ? '定价' : '改价' }}</button></dd></div>
        <div><dt>累计现场工时<template v-if="multiPeople && onsite">（合计）</template></dt><dd>{{ hoursText }}</dd></div>
        <div><dt>负责人</dt><dd>{{ d.assignee ? d.assignee.name : '未分配' }}<span v-if="(d.assistants || []).length" class="orders-dr-plus"
              :title="'协助人：' + d.assistants.map(u => u.name).join('、')">+{{ d.assistants.length }} 协助</span></dd></div>
        <div><dt>{{ creatorLabel }}</dt><dd>{{ d.creator ? d.creator.name : '—' }}</dd></div>
        <div v-if="d.symptom" class="span-2"><dt>故障现象</dt><dd class="orders-dr-text">{{ d.symptom }}</dd></div>
      </dl>

      <div v-if="suggestion" class="orders-suggest orders-dr-suggest" :class="{ pending: suggestion.pending }" role="note">
        <div class="orders-suggest-main">
          <div class="orders-suggest-title">{{ suggestion.who }} 建议 <span class="num">{{ suggestion.money }}</span>
            <span v-if="suggestion.pending" class="tag tag-warn">待确认</span></div>
          <div class="orders-suggest-sub"><template v-if="suggestion.note">{{ suggestion.note }} · </template><span class="num">{{ suggestion.time }}</span></div>
        </div>
        <button v-if="suggestion.pending && canSetPrice" type="button" class="btn btn-secondary btn-sm" @click="openPrice(suggestion.amount)">采用</button>
      </div>

      <div v-if="holdInfo" class="alert alert-info orders-dr-hold" role="note">
        <div class="alert-row"><span class="orders-dr-pause" aria-hidden="true"></span><span>暂停搁置 · {{ holdInfo.text }}</span></div>
        <div class="alert-note">{{ holdInfo.sub }}</div>
      </div>

      <div v-for="q in pendingRequests" :key="'rq' + q.key" class="alert alert-warn orders-dr-req" role="note">
        <div class="orders-dr-req-head"><span class="orders-dr-req-title">{{ q.title }}</span><span class="tag tag-warn">待审批</span>
          <span class="orders-dr-req-time num">{{ q.time }}</span></div>
        <div class="orders-dr-req-text"><template v-if="q.who">{{ q.who }}：</template>{{ q.text }}</div>
        <div v-if="q.canDecide" class="alert-actions">
          <button type="button" class="btn btn-primary" :disabled="!!busy" @click="decide(q, true)">{{ q.busy ? '处理中…' : '同意' }}</button>
          <button type="button" class="btn btn-secondary" :disabled="!!busy" @click="decide(q, false)">驳回</button>
        </div>
      </div>

      <div v-for="a in alerts" :key="a.key" class="alert" :class="a.missing ? 'alert-danger' : 'alert-warn'" role="note">
        <div class="alert-row"><Icon name="alert" :size="15" :stroke="2" /><span>{{ a.text }}</span></div>
        <template v-if="a.missing && canSupplement && a.punchId != null">
          <button v-if="!formFor(a)" type="button" class="btn btn-primary orders-dr-supp-btn" @click="openForm(a)">补录离场时间</button>
          <template v-else>
            <div class="alert-form">
              <label class="field"><span class="field-label">离场时间</span>
                <input :ref="'suppTime' + a.punchId" class="input" type="time" v-model="formFor(a).time"
                       aria-label="补录离场时间" @input="formFor(a).error = ''"></label>
              <label class="field"><span class="field-label">补录原因（必填）</span>
                <input class="input" type="text" v-model="formFor(a).reason" maxlength="200" aria-label="补录原因"
                       placeholder="如：手机电量耗尽，工程师电话确认离场时间"
                       @input="formFor(a).error = ''" @keydown.enter.prevent="saveForm(a)"></label>
            </div>
            <div v-if="formFor(a).error" class="field-error orders-dr-err">{{ formFor(a).error }}</div>
            <div class="alert-actions orders-dr-supp-actions">
              <button type="button" class="btn btn-primary" :disabled="formFor(a).saving" @click="saveForm(a)">
                {{ formFor(a).saving ? '保存中…' : '保存补录' }}</button>
              <button type="button" class="btn btn-secondary" :disabled="formFor(a).saving" @click="cancelForm(a)">取消</button>
            </div>
            <div class="alert-note">保存后记录补录人和时间，导出时单独标注</div>
          </template>
        </template>
      </div>

      <template v-if="people.length || canAssistants">
        <h3 class="section-title orders-dr-sec">处理人员
          <button v-if="canAssistants" type="button" class="btn-link orders-dr-link" @click="openAssign('assistants')">调整协助人</button></h3>
        <div v-if="!people.length" class="orders-dr-empty">暂无处理人员</div>
        <div v-for="p in people" :key="p.key" class="orders-dr-person" :class="{ former: p.role === 'Former' }">
          <Avatar :user="p.user" :size="24" />
          <span class="orders-dr-person-name ellipsis">{{ p.name }}</span>
          <Tag :tone="p.roleTone">{{ p.roleLabel }}</Tag>
          <span v-if="p.onSite" class="hours-live"><i></i>现场中</span>
          <Tag v-if="p.missing" tone="missing">缺卡</Tag>
          <span class="orders-dr-person-hours num" :title="onsite ? '此人在本单的累计现场工时' : '非现场工单不计工时'">{{ p.hours }}</span>
        </div>
      </template>

      <template v-if="d.factory">
        <h3 class="section-title">返厂流程</h3>
        <ol class="factory-line">
          <li v-for="f in factoryNodes" :key="f.key" class="factory-node" :class="'is-' + f.state">
            <span class="factory-dot" aria-hidden="true"></span>
            <div class="factory-main">
              <div class="factory-head">
                <span class="factory-title">{{ f.label }}</span>
                <Tag v-if="f.skipped" tone="neutral">已跳过</Tag>
                <Tag v-else-if="f.state === 'current'" tone="urgent">当前</Tag>
                <button v-if="f.canEdit" type="button" class="btn-link orders-dr-link factory-edit" @click="openFactory(f)">更正</button>
              </div>
              <div v-if="f.sub" class="factory-sub">{{ f.sub }}</div>
              <div v-if="f.trackingNo || f.courier" class="factory-courier">
                <span>{{ f.courier }}</span>
                <button v-if="f.trackingNo" type="button" class="factory-no mono" :title="'点击复制单号 ' + f.trackingNo"
                        :aria-label="'复制快递单号 ' + f.trackingNo" @click="copyNo(f.trackingNo)">{{ f.trackingNo }}</button>
              </div>
              <div v-if="f.note" class="factory-note">{{ f.note }}</div>
              <div v-if="f.photos.length" class="factory-photos">
                <button v-for="(ph, i) in f.photos" :key="ph.id" type="button" class="thumb" :aria-label="'查看' + f.label + '照片 ' + (i + 1)"
                        @click="previewNode(f, i)"><img :src="ph.url" alt="" loading="lazy"></button>
              </div>
              <button v-if="f.canRecord" type="button" class="btn btn-primary btn-sm factory-act" @click="openFactory(f)">{{ f.actionLabel }}</button>
            </div>
          </li>
        </ol>
      </template>

      <h3 class="section-title orders-dr-sec"><span>附件<span class="att-count num">（{{ attRows.length }}）</span></span>
        <span class="att-head-links">
          <button v-if="d.canAddAttachment" type="button" class="btn-link orders-dr-link" @click="openAttAdd">添加附件</button>
          <button type="button" class="btn-link orders-dr-link" title="查看本单的全部文件（含完工照片、签字件等）" @click="openAllFiles">全部文件</button>
        </span></h3>
      <div v-if="attOpen" class="att-add">
        <AttachmentsUpload v-model="attItems" :limit="attRemaining" :can-restrict="!!d.canRestrictAttachment" :disabled="attSaving" />
        <div class="field-hint">支持图片、视频、Word、Excel、PPT、PDF、TXT、CSV；图片单个不超过 20MB，其他不超过 100MB。</div>
        <div class="att-add-actions">
          <button type="button" class="btn btn-primary" :disabled="attSaving || attUploading || !attReady.length" @click="submitAtt">
            {{ attSaving ? '提交中…' : (attUploading ? '上传中…' : '确定添加') }}</button>
          <button type="button" class="btn btn-secondary" :disabled="attSaving" @click="cancelAttAdd">取消</button>
        </div>
      </div>
      <div v-if="!attRows.length" class="orders-dr-empty">暂无附件</div>
      <ul v-else class="att-list">
        <li v-for="r in attRows" :key="r.key" class="att-row" :class="{ 'att-row-busy': r.busy }">
          <span class="att-badge" :style="{ background: r.badge.color }" aria-hidden="true">{{ r.badge.text }}</span>
          <div class="att-main">
            <div class="att-name-line">
              <button type="button" class="att-name ellipsis" :title="(r.media ? '预览 ' : '下载 ') + r.name" @click="openAtt(r)">{{ r.name }}</button>
              <Tag v-if="r.fromOrderPhoto" tone="neutral">建单照片</Tag>
              <Tag v-if="r.restricted" tone="warn">仅销售和主管可见</Tag>
            </div>
            <div class="att-meta num">{{ r.meta }}</div>
            <div class="att-actions">
              <button type="button" class="btn-link att-link" :disabled="!!attBusy" @click="downloadAtt(r)">下载</button>
              <button v-if="r.canToggle" type="button" class="btn-link att-link" :disabled="!!attBusy" @click="toggleAtt(r)">{{ r.toggleLabel }}</button>
              <button v-if="r.canDelete" type="button" class="btn-link att-link att-link-danger" :disabled="!!attBusy" @click="deleteAtt(r)">删除</button>
            </div>
          </div>
        </li>
      </ul>

      <h3 class="section-title">打卡记录</h3>
      <div v-if="!punchRows.length" class="orders-dr-empty">{{ emptyPunchText }}</div>
      <div v-for="p in punchRows" :key="p.key" class="punch-row">
        <span v-if="p.missing" class="thumb thumb-missing" role="img" :aria-label="p.photoLabel"></span>
        <button v-else-if="p.url" type="button" class="thumb" :aria-label="'查看' + p.photoLabel" @click="openPreview(p)">
          <img :src="p.url" alt="" loading="lazy"></button>
        <span v-else class="thumb orders-thumb-none" role="img" aria-label="无打卡照片"><Icon name="image" :size="16" /></span>
        <div class="punch-main">
          <div class="punch-title"><span class="num">{{ p.time }}</span> {{ p.kind }}<span v-if="p.who" class="text-muted"> · {{ p.who }}</span></div>
          <div class="punch-note" :class="p.tone">{{ p.note }}</div>
        </div>
        <span class="punch-dur">{{ p.dur }}</span>
      </div>

      <h3 class="section-title">状态记录</h3>
      <ol class="log-list">
        <li v-for="l in logs" :key="l.key" class="log-item">
          <span class="dot"></span><span class="log-text">{{ l.text }}</span><span class="log-time">{{ l.time }}</span>
        </li>
      </ol>
      <div v-if="!logs.length" class="orders-dr-empty">暂无状态记录</div>
    </template>

    <template v-if="d && buttons.length" #foot>
      <button v-for="b in buttons" :key="b.key" type="button" class="btn btn-lg"
              :class="b.disabled ? 'btn-disabled' : (b.kind === 'primary' ? 'btn-primary' : 'btn-secondary')"
              :aria-disabled="b.disabled ? 'true' : 'false'" :title="b.tip || null"
              :disabled="!!busy && busy === b.key" @click="run(b)">
        {{ busy === b.key ? '处理中…' : b.label }}</button>
    </template>
  </Drawer>

  <OrdersAssignModal v-if="assignOpen && d" :order="d" :mode="assignMode" @close="assignOpen = false" @done="onAssigned" />
  <OrdersHoldModal v-if="holdOpen && d" :order="d" @close="holdOpen = false" @done="onHeld" />
  <OrdersPriceModal v-if="priceOpen && d" :order="d" :initial="priceInitial" @close="priceOpen = false; closeAfterPrice = false" @done="onPriced" />
  <OrdersFactoryModal v-if="factoryNode && d" :key="factoryNode.node" :order="d" :node="factoryNode"
                      @close="factoryNode = null" @done="onFactoryDone" />
  `,
};
