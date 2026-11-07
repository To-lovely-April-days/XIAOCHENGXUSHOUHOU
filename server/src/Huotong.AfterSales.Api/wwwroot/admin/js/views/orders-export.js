// 导出台账弹窗：选择建单日期范围 + 状态 → GET /api/admin/export/orders（xlsx）。
import * as api from '../api.js';
import { toast } from '../ui.js';
import { toApiDate, thisWeekRange, lastDaysRange } from '../format.js';
import { ORDER_STATUS } from '../enums.js';

const STATUS_OPTIONS = [{ value: '', label: '全部（不含已作废）' }].concat(
  Object.keys(ORDER_STATUS).map(k => ({ value: k, label: ORDER_STATUS[k].label })),
);

export default {
  name: 'OrdersExportModal',
  props: {
    defaultStatus: { type: String, default: '' },
  },
  emits: ['close'],
  data() {
    const now = new Date();
    return {
      from: toApiDate(new Date(now.getFullYear(), now.getMonth(), 1)),
      to: toApiDate(now),
      status: ORDER_STATUS[this.defaultStatus] ? this.defaultStatus : '',
      statusOptions: STATUS_OPTIONS,
      error: '',
      saving: false,
    };
  },
  computed: {
    presets() {
      const now = new Date();
      return [
        { key: 'week', label: '本周', ...thisWeekRange(now) },
        { key: 'month', label: '本月', from: toApiDate(new Date(now.getFullYear(), now.getMonth(), 1)), to: toApiDate(now) },
        { key: '30', label: '最近 30 天', ...lastDaysRange(30, now) },
        { key: 'all', label: '全部', from: '', to: '' },
      ];
    },
  },
  methods: {
    usePreset(p) { this.from = p.from; this.to = p.to; this.error = ''; },
    isPreset(p) { return this.from === p.from && this.to === p.to; },
    async submit() {
      if (this.saving) return;
      this.error = '';
      if (this.from && this.to && this.from > this.to) { this.error = '开始日期不能晚于结束日期'; return; }
      this.saving = true;
      try {
        const name = `售后进度台账_${this.from || '全部'}${this.to ? '_' + this.to : ''}.xlsx`;
        await api.admin.exportOrders({ from: this.from, to: this.to, status: this.status }, name);
        toast('台账已开始下载', 'success');
        this.$emit('close');
      } catch (e) {
        if (e.code === 40100) { this.$emit('close'); return; }
        this.error = e.message || '导出失败';
      } finally {
        this.saving = false;
      }
    },
  },
  template: `
  <Modal title="导出台账" :width="440" @close="$emit('close')">
    <form class="form-stack" @submit.prevent="submit">
      <div class="field">
        <span class="field-label">建单日期</span>
        <div class="radio-group">
          <button v-for="p in presets" :key="p.key" type="button" class="radio-chip" :class="{ active: isPreset(p) }"
                  :aria-checked="isPreset(p) ? 'true' : 'false'" role="radio" @click="usePreset(p)">{{ p.label }}</button>
        </div>
        <div class="orders-export-range">
          <input class="input" type="date" v-model="from" aria-label="开始日期" @input="error = ''">
          <span class="text-muted">至</span>
          <input class="input" type="date" v-model="to" aria-label="结束日期" @input="error = ''">
        </div>
        <span class="field-hint">日期留空表示不限</span>
      </div>
      <label class="field">
        <span class="field-label">状态</span>
        <select class="select" v-model="status">
          <option v-for="o in statusOptions" :key="o.value" :value="o.value">{{ o.label }}</option>
        </select>
      </label>
      <div class="field-hint">列与 SOP 进度台账一致：工单号、客户、设备、故障描述、处理人、进度状态、服务价格、现场工时（小时，1 位小数）等。</div>
      <div v-if="error" class="field-error">{{ error }}</div>
      <button type="submit" hidden></button>
    </form>
    <template #footer>
      <button type="button" class="btn btn-secondary" @click="$emit('close')">取消</button>
      <button type="button" class="btn btn-primary" :disabled="saving" @click="submit">
        <Icon name="download" :size="15" />{{ saving ? '导出中…' : '导出' }}</button>
    </template>
  </Modal>
  `,
};
