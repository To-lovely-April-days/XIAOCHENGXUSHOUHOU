// 定价 / 改价弹窗（v1.1）：金额（0 = 保内免费）→ POST /api/orders/{id}/price。
// 展示销售最新的价格建议，可一键采用（填入金额，仍需点「保存」）。
import * as api from '../api.js';
import { fmtPrice, fmtMDHM } from '../format.js';

const MAX_PRICE = 100000000;

export default {
  name: 'OrdersPriceModal',
  props: {
    /** OrderDetail */
    order: { type: Object, required: true },
    /** 打开时预填的金额（从抽屉点「采用」进来时传建议金额）；null = 用当前价格 */
    initial: { type: Number, default: null },
  },
  emits: ['close', 'done'],
  data() {
    const o = this.order;
    let value = '';
    if (this.initial !== null && this.initial !== undefined) value = String(this.initial);
    else if (o.priceSet && o.price !== null && o.price !== undefined) value = String(o.price);
    return { value, error: '', saving: false };
  },
  computed: {
    isChange() { return this.order.priceSet === true; },
    title() { return (this.isChange ? '改价 ' : '定价 ') + this.order.orderNo; },
    currentText() { return fmtPrice(this.order.price, { free: true, priceSet: this.order.priceSet }); },
    suggestion() {
      const s = this.order.priceSuggestion;
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
    hint() {
      const v = String(this.value === null || this.value === undefined ? '' : this.value).trim();
      return v !== '' && Number(v) === 0 ? '将显示为「保内免费」' : '质保期内填 0（显示「保内免费」）';
    },
  },
  mounted() {
    this.$nextTick(() => { const el = this.$refs.input; if (el) { el.focus(); el.select(); } });
  },
  methods: {
    adopt() {
      if (!this.suggestion) return;
      this.value = String(this.suggestion.amount);
      this.error = '';
    },
    async submit() {
      if (this.saving) return;
      const raw = String(this.value === null || this.value === undefined ? '' : this.value).trim();
      if (raw === '' || !isFinite(Number(raw))) { this.error = '请填写服务价格（保内填 0）'; return; }
      const n = Math.round(Number(raw) * 100) / 100;
      if (n < 0) { this.error = '服务价格不能小于 0'; return; }
      if (n > MAX_PRICE) { this.error = '服务价格过大'; return; }
      this.saving = true;
      this.error = '';
      try {
        const res = await api.orders.setPrice(this.order.id, n);
        this.$emit('done', res, n);
      } catch (e) {
        if (e.code === 40100) { this.$emit('close'); return; }
        this.error = e.message || '保存失败';
      } finally {
        this.saving = false;
      }
    },
  },
  template: `
  <Modal :title="title" :width="420" @close="$emit('close')">
    <form class="form-stack" novalidate @submit.prevent="submit">
      <div class="orders-price-head">
        <div class="orders-assign-customer">{{ order.customerName }}</div>
        <div class="text-muted">当前价格：<span :class="{ 'text-danger': order.priceSet === false }">{{ currentText }}</span></div>
      </div>
      <div v-if="suggestion" class="orders-suggest" :class="{ pending: suggestion.pending }">
        <div class="orders-suggest-main">
          <div class="orders-suggest-title">{{ suggestion.who }} 建议 <span class="num">{{ suggestion.money }}</span>
            <span v-if="suggestion.pending" class="tag tag-warn">待确认</span></div>
          <div class="orders-suggest-sub"><template v-if="suggestion.note">{{ suggestion.note }} · </template><span class="num">{{ suggestion.time }}</span></div>
        </div>
        <button type="button" class="btn btn-secondary btn-sm" @click="adopt">采用建议</button>
      </div>
      <label class="field">
        <span class="field-label">服务价格（元）<span class="req">*</span></span>
        <input ref="input" class="input num" :class="{ invalid: error }" v-model="value" type="number" min="0" step="0.01"
               placeholder="0" aria-label="服务价格（元）" @input="error = ''">
        <span v-if="error" class="field-error">{{ error }}</span>
        <span v-else class="field-hint">{{ hint }}。工程师看不到价格。</span>
      </label>
      <button type="submit" hidden></button>
    </form>
    <template #footer>
      <button type="button" class="btn btn-secondary" :disabled="saving" @click="$emit('close')">取消</button>
      <button type="button" class="btn btn-primary" :disabled="saving" @click="submit">{{ saving ? '保存中…' : '保存' }}</button>
    </template>
  </Modal>
  `,
};
