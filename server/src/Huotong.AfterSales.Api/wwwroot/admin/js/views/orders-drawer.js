// 工单详情抽屉（原型 16 右侧）：基本信息、异常警示框 + 补录、打卡记录、状态记录、底部操作。
// 底部按钮由 detail.actions 驱动（服务端计算），按原型映射排列。
import * as api from '../api.js';
import { toast, toastError, confirm, preview } from '../ui.js';
import { fmtHM, fmtPrice, fmtMD, fmtMDHM, fmtTime, fmtDistance, isWarranty, parseTime, toApiDate, pad2 } from '../format.js';
import { ORDER_STATUS, SERVICE_TYPE, PUNCH_KIND, ORDER_ACTIONS, label } from '../enums.js';
import OrdersAssignModal from './orders-assign.js';

/**
 * 原型按状态的按钮映射。每项：
 *   action  需要出现在 detail.actions 里才显示（null = 不依赖 actions）
 *   kind    primary | secondary
 *   fallbackDisabled  action 不在 actions 中时仍显示为禁用按钮，tip 为提示
 */
const LAYOUT = {
  Pending: [{ action: 'void', kind: 'secondary' }, { action: 'assign', kind: 'primary' }],
  Accepted: [{ action: 'void', kind: 'secondary' }, { action: 'reassign', kind: 'primary' }],
  InProgress: [
    { action: 'hold', kind: 'secondary' },
    { action: 'reassign', kind: 'secondary' },
    { action: 'close', kind: 'primary', fallbackDisabled: '工程师提交完工后才能验收' },
  ],
  OnHold: [{ action: 'resume', kind: 'primary' }, { action: 'reassign', kind: 'secondary' }],
  Submitted: [
    { action: 'reject', kind: 'secondary' },
    { action: 'close', kind: 'primary', fallbackDisabled: '请先补录缺卡' },
  ],
  Closed: [{ action: 'export', kind: 'secondary', label: '导出本单', always: true }],
  Voided: [],
};

export default {
  name: 'OrdersDrawer',
  components: { OrdersAssignModal },
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
    runningMinutes() {
      const since = this.d && parseTime(this.d.onSiteSince);
      if (!since) return 0;
      return Math.max(0, Math.floor((this.nowTs + this.serverOffset - since.getTime()) / 60000));
    },
    hoursText() {
      const d = this.d;
      if (!d || d.serviceType !== 'Onsite') return '—';
      const m = (d.totalMinutes || 0) + this.runningMinutes;
      if (!m && !d.onSiteSince && !(d.punches || []).length) return '—';
      return fmtHM(m);
    },
    priceText() { return this.d ? fmtPrice(this.d.price, { free: true }) : ''; },
    warranty() { return this.d ? isWarranty(this.d.price) : false; },
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
        const running = p.kind === 'Arrive' && d.onSiteSince && p.time === d.onSiteSince;
        if (running) {
          note = (note ? note + ' · ' : '') + '现场计时中';
          dur = fmtHM(this.runningMinutes);
        }
        rows.push({
          key: 'p' + p.id,
          time: fmtMDHM(p.time),
          kind: label(PUNCH_KIND, p.kind),
          who: p.engineer && d.assignee && p.engineer.id !== d.assignee.id ? p.engineer.name : '',
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
      const actions = d.actions || [];
      const out = [];
      (LAYOUT[d.status] || []).forEach(b => {
        const meta = ORDER_ACTIONS[b.action] || {};
        const text = b.label || meta.label || b.action;
        if (b.always || actions.includes(b.action)) {
          out.push({ key: b.action, label: text, kind: b.kind, disabled: false, tip: '' });
        } else if (b.fallbackDisabled) {
          out.push({ key: b.action, label: text, kind: b.kind, disabled: true, tip: b.fallbackDisabled });
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
      if (b.disabled || this.busy) return;
      const d = this.d;
      const no = d.orderNo;
      try {
        if (b.key === 'assign' || b.key === 'reassign') { this.assignOpen = true; return; }
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
        } else if (b.key === 'hold') {
          const reason = await confirm('标为暂停搁置', '搁置期间工程师不能打卡，可随时恢复处理。',
            { input: true, multiline: true, required: true, inputLabel: '搁置原因', placeholder: '如：等待客户确认 / 等待备件到货', okText: '确认搁置' });
          if (reason === null) return;
          this.busy = b.key;
          res = await api.orders.hold(d.id, reason);
          this.applied(res, '已标为暂停搁置');
        } else if (b.key === 'resume') {
          if (!(await confirm('恢复处理', `确定将 ${no} 恢复为处理中？`, { okText: '恢复处理' }))) return;
          this.busy = b.key;
          res = await api.orders.resume(d.id);
          this.applied(res, '已恢复处理');
        } else if (b.key === 'reject') {
          const reason = await confirm('退回处理中', '退回后工单回到处理中，工程师需重新提交完工。',
            { input: true, multiline: true, inputLabel: '退回原因', placeholder: '选填，会写入状态记录并通知工程师', okText: '退回' });
          if (reason === null) return;
          this.busy = b.key;
          res = await api.orders.reject(d.id, reason);
          this.applied(res, '已退回处理中');
        } else if (b.key === 'close') {
          if (!(await confirm('确认闭环', `确认 ${no}（${d.customerName}）已处理完成并闭环？`, { okText: '确认闭环' }))) return;
          this.busy = b.key;
          res = await api.orders.close(d.id);
          this.applied(res, '已确认闭环');
        }
      } catch (e) {
        toastError(e);
        // 状态冲突等：刷新抽屉拿到最新状态与 actions
        if (e.code === 40900 || e.code === 40400) { this.load(); this.$emit('changed'); }
      } finally {
        this.busy = '';
      }
    },
    onAssigned(res, name) {
      const re = this.d && this.d.status !== 'Pending';
      this.assignOpen = false;
      this.applied(res, (re ? '已改派给 ' : '已指派给 ') + name);
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
      <div class="drawer-sub">{{ deviceLine }}</div>

      <dl class="kv">
        <div><dt>服务价格</dt><dd :class="{ 'text-success': warranty }">{{ priceText }}</dd></div>
        <div><dt>累计现场工时</dt><dd>{{ hoursText }}</dd></div>
        <div><dt>处理人</dt><dd>{{ d.assignee ? d.assignee.name : '未分配' }}</dd></div>
        <div><dt>{{ creatorLabel }}</dt><dd>{{ d.creator ? d.creator.name : '—' }}</dd></div>
        <div v-if="d.symptom" class="span-2"><dt>故障现象</dt><dd class="orders-dr-text">{{ d.symptom }}</dd></div>
      </dl>

      <div v-if="d.status === 'OnHold' && d.holdReason" class="alert alert-info" role="note">
        <div class="alert-row"><Icon name="info" :size="15" :stroke="2" /><span>搁置原因：{{ d.holdReason }}</span></div>
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

  <OrdersAssignModal v-if="assignOpen && d" :order="d" @close="assignOpen = false" @done="onAssigned" />
  `,
};
