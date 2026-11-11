// 暂停搁置弹窗（v1.1）：原因下拉（4 项）+ 说明（选「其他」时必填）→ POST /api/orders/{id}/hold。
// 主管可在「待启动」「处理中」直接暂停；恢复时回到暂停前的状态。
import * as api from '../api.js';
import { ORDER_STATUS, HOLD_REASON, options, label } from '../enums.js';

const NOTE_MAX = 200;

export default {
  name: 'OrdersHoldModal',
  props: {
    /** OrderDetail */
    order: { type: Object, required: true },
  },
  emits: ['close', 'done'],
  data() {
    return {
      reasonType: '',
      note: '',
      reasonOptions: options(HOLD_REASON),
      noteMax: NOTE_MAX,
      errors: {},
      saving: false,
      submitError: '',
    };
  },
  computed: {
    isOther() { return this.reasonType === 'Other'; },
    fromText() { return label(ORDER_STATUS, this.order.status); },
  },
  methods: {
    clearErr(key) {
      if (this.errors[key]) { const e = Object.assign({}, this.errors); delete e[key]; this.errors = e; }
      this.submitError = '';
    },
    async submit() {
      if (this.saving) return;
      const note = (this.note || '').trim();
      const e = {};
      if (!HOLD_REASON[this.reasonType]) e.reasonType = '请选择暂停原因';
      if (this.isOther && !note) e.note = '请填写暂停说明';
      else if (note.length > NOTE_MAX) e.note = `暂停说明不能超过 ${NOTE_MAX} 个字`;
      this.errors = e;
      if (Object.keys(e).length) return;
      this.saving = true;
      this.submitError = '';
      try {
        const res = await api.orders.hold(this.order.id, this.reasonType, note);
        this.$emit('done', res);
      } catch (err) {
        if (err.code === 40100) { this.$emit('close'); return; }
        this.submitError = err.message || '操作失败';
      } finally {
        this.saving = false;
      }
    },
  },
  template: `
  <Modal :title="'标为暂停搁置 ' + order.orderNo" :width="440" @close="$emit('close')">
    <form class="form-stack" novalidate @submit.prevent="submit">
      <p class="dialog-msg">{{ order.customerName }} 当前为「{{ fromText }}」。搁置期间工程师不能打卡、开始处理或提交完工；恢复后回到「{{ fromText }}」。</p>
      <label class="field">
        <span class="field-label">暂停原因<span class="req">*</span></span>
        <select class="select" :class="{ invalid: errors.reasonType }" v-model="reasonType" @change="clearErr('reasonType'); clearErr('note')">
          <option value="" disabled>请选择</option>
          <option v-for="o in reasonOptions" :key="o.value" :value="o.value">{{ o.label }}</option>
        </select>
        <span v-if="errors.reasonType" class="field-error">{{ errors.reasonType }}</span>
      </label>
      <label class="field">
        <span class="field-label">说明<span v-if="isOther" class="req">*</span><em v-else>（选填）</em></span>
        <textarea class="textarea" :class="{ invalid: errors.note }" rows="3" v-model="note" :maxlength="noteMax"
                  placeholder="如：客户下周一才能安排停机" @input="clearErr('note')"></textarea>
        <span v-if="errors.note" class="field-error">{{ errors.note }}</span>
        <span v-else class="field-hint">原因和说明会写入状态记录，并通知负责人与协助人</span>
      </label>
      <div v-if="submitError" class="field-error">{{ submitError }}</div>
      <button type="submit" hidden></button>
    </form>
    <template #footer>
      <button type="button" class="btn btn-secondary" :disabled="saving" @click="$emit('close')">取消</button>
      <button type="button" class="btn btn-primary" :disabled="saving" @click="submit">{{ saving ? '提交中…' : '确认搁置' }}</button>
    </template>
  </Modal>
  `,
};
