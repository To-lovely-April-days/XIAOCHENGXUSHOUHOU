// 修改 SAT 信息弹窗（v1.3，CHANGES-v1.3 §3.5 / §10.5）：是否已完成 SAT + 对应时间 → POST /api/orders/{id}/sat。
// 两项一起提交、整体覆盖；时间选填（接单时拿不到的可以之后补填），留空 = 清空。
// 例外：旧数据的 yyyy-MM 带不进日期框，没切换 SAT、也没重新选日期时原样带回（不因为点了「保存」就丢掉旧记录）。
// 入口只看服务端的 detail.canEditSat，这里不推导谁能改。
import * as api from '../api.js';

const DATE_RE = /^\d{4}-(0[1-9]|1[0-2])-(0[1-9]|[12]\d|3[01])$/;

export default {
  name: 'OrdersSatModal',
  props: {
    /** OrderDetail */
    order: { type: Object, required: true },
  },
  emits: ['close', 'done'],
  data() {
    const o = this.order || {};
    const old = String(o.satDate || '').trim();
    return {
      satSigned: o.satSigned === true || o.satSigned === false ? o.satSigned : null,
      // 只有 yyyy-MM-dd 才带入日期框；旧数据的 yyyy-MM 用提示文字显示
      satDate: DATE_RE.test(old) ? old : '',
      legacyDate: old && !DATE_RE.test(old) ? old : '',
      // 旧记录（yyyy-MM）没法带入日期框：没切换「已完成 / 未完成」、也没重新选日期就保存时原样带回，
      // 否则只是打开弹窗点一下「保存」就会把旧时间清空
      keepLegacy: !!old && !DATE_RE.test(old),
      errors: {},
      saving: false,
      submitError: '',
    };
  },
  computed: {
    dateLabel() {
      if (this.satSigned === true) return 'SAT 完成时间';
      if (this.satSigned === false) return '发货时间';
      return 'SAT 完成时间 / 发货时间';
    },
  },
  methods: {
    clearErr(key) {
      if (this.errors[key]) { const e = Object.assign({}, this.errors); delete e[key]; this.errors = e; }
      this.submitError = '';
    },
    /** 切换「已完成 / 未完成」时清空已填的时间（两个时间含义不同） */
    pick(v) {
      if (this.saving) return;
      if (this.satSigned !== v) { this.satDate = ''; this.keepLegacy = false; this.clearErr('satDate'); }
      this.satSigned = v;
      this.clearErr('satSigned');
    },
    async submit() {
      if (this.saving) return;
      const picked = String(this.satDate || '').trim();
      // 没重新选日期且旧记录仍然有效 → 带回旧记录（服务端接受 yyyy-MM，值没变不写记录）
      const date = picked || (this.keepLegacy ? this.legacyDate : '');
      const e = {};
      if (this.satSigned !== true && this.satSigned !== false) e.satSigned = '请选择是否已完成 SAT';
      if (picked && !DATE_RE.test(picked)) e.satDate = this.dateLabel + '格式应为 yyyy-MM-dd';
      this.errors = e;
      if (Object.keys(e).length) return;
      this.saving = true;
      this.submitError = '';
      try {
        const res = await api.orders.setSat(this.order.id, { satSigned: this.satSigned, satDate: date });
        this.$emit('done', res);
      } catch (err) {
        if (err.code === 40100) { this.$emit('close'); return; }
        this.submitError = err.message || '保存失败';
      } finally {
        this.saving = false;
      }
    },
  },
  template: `
  <Modal title="修改 SAT 信息" :width="440" @close="$emit('close')">
    <form class="form-stack" novalidate @submit.prevent="submit">
      <p class="dialog-msg">{{ order.orderNo }} · {{ order.customerName }}。接单时拿不到的时间可以之后补填。</p>
      <div class="field">
        <span class="field-label">是否已完成 SAT<span class="req">*</span></span>
        <div class="radio-group" role="radiogroup" aria-label="是否已完成 SAT">
          <button type="button" role="radio" class="radio-chip" :class="{ active: satSigned === true }"
                  :aria-checked="satSigned === true ? 'true' : 'false'" @click="pick(true)">已完成</button>
          <button type="button" role="radio" class="radio-chip" :class="{ active: satSigned === false }"
                  :aria-checked="satSigned === false ? 'true' : 'false'" @click="pick(false)">未完成</button>
        </div>
        <span v-if="errors.satSigned" class="field-error">{{ errors.satSigned }}</span>
      </div>
      <label class="field">
        <span class="field-label">{{ dateLabel }}<em>（选填）</em></span>
        <input class="input dev-sat-date" type="date" max="9999-12-31" :class="{ invalid: errors.satDate }" v-model="satDate"
               :disabled="saving" @input="clearErr('satDate')">
        <span v-if="errors.satDate" class="field-error">{{ errors.satDate }}</span>
        <span v-if="legacyDate" class="field-hint">原记录：{{ legacyDate }}<template v-if="keepLegacy">（不重新选择日期则保留）</template></span>
      </label>
      <div v-if="submitError" class="field-error">{{ submitError }}</div>
      <button type="submit" hidden></button>
    </form>
    <template #footer>
      <button type="button" class="btn btn-secondary" :disabled="saving" @click="$emit('close')">取消</button>
      <button type="button" class="btn btn-primary" :disabled="saving" @click="submit">{{ saving ? '保存中…' : '保存' }}</button>
    </template>
  </Modal>
  `,
};
