// 指派 / 改派 / 调整协助人弹窗：GET /api/engineers 列出工程师负载（loadText）。
//   mode="assign"（默认）：单选负责人 + 多选协助人 → POST /api/orders/{id}/assign { engineerId, assistantIds }
//   mode="assistants"：只调整协助人（负责人不可选）→ POST /api/orders/{id}/assistants { engineerIds }
// 协助人最多 5 人，不能是负责人；改派时预选当前协助人；改派给当前协助人 = 该协助人转为负责人。
import * as api from '../api.js';

const MAX_ASSISTANTS = 5;

export default {
  name: 'OrdersAssignModal',
  props: {
    /** OrderDetail */
    order: { type: Object, required: true },
    /** 'assign' | 'assistants' */
    mode: { type: String, default: 'assign' },
  },
  emits: ['close', 'done'],
  data() {
    const current = (this.order.assistants || []).map(u => u.id);
    return {
      list: [], loading: true, error: '', picked: null, saving: false, submitError: '',
      assistantIds: current.slice(),
      initialAssistantIds: current,
      touched: false, // 是否动过协助人勾选；指派/改派时没动过就不传 assistantIds（= 不调整协助人）
      maxAssistants: MAX_ASSISTANTS,
    };
  },
  computed: {
    onlyAssistants() { return this.mode === 'assistants'; },
    isReassign() { return this.order.status !== 'Pending'; },
    title() {
      if (this.onlyAssistants) return '调整协助人 ' + this.order.orderNo;
      return (this.isReassign ? '改派 ' : '指派 ') + this.order.orderNo;
    },
    currentId() { return this.order.assignee ? this.order.assignee.id : null; },
    /** 提交后的负责人：调整协助人模式 = 当前负责人；指派模式 = 已选的人（未选时改派场景先按当前负责人算） */
    leadId() { return this.onlyAssistants ? this.currentId : (this.picked || this.currentId); },
    pickedUser() {
      const e = this.list.find(x => x.user.id === this.picked);
      return e ? e.user : null;
    },
    headText() {
      const lead = this.order.assignee ? this.order.assignee.name : '未分配';
      if (this.onlyAssistants) return `负责人：${lead}。协助人可查看工单、对现场单打卡，工时按人计算；最多 ${MAX_ASSISTANTS} 人。`;
      return this.isReassign ? ('当前负责人：' + lead) : '选择负责该工单的工程师，指派后状态变为「待启动」';
    },
    /** 列表里已不存在（如已停用）的当前协助人也要能看到并取消 */
    assistantChoices() {
      const out = this.list.map(e => ({ user: e.user, onSiteNow: e.onSiteNow, loadText: e.loadText, gone: false }));
      (this.order.assistants || []).forEach(u => {
        if (!out.some(x => x.user.id === u.id)) out.push({ user: u, onSiteNow: false, loadText: '已停用或不在工程师列表中', gone: true });
      });
      return out.map(x => ({
        ...x,
        isLead: x.user.id === this.leadId,
        checked: this.assistantIds.includes(x.user.id) && x.user.id !== this.leadId,
      }));
    },
    /** 最终提交的协助人（去掉负责人） */
    finalAssistantIds() { return this.assistantIds.filter(id => id !== this.leadId); },
    assistantsChanged() {
      const a = this.finalAssistantIds.slice().sort((x, y) => x - y).join(',');
      const b = this.initialAssistantIds.slice().sort((x, y) => x - y).join(',');
      return a !== b;
    },
    canSubmit() {
      if (this.saving || this.loading) return false;
      return this.onlyAssistants ? this.assistantsChanged : !!this.picked;
    },
    /** 仍被勾选、但已停用（或不再是工程师）的当前协助人：服务端不接受，需先取消勾选 */
    goneChecked() { return this.assistantChoices.filter(x => x.gone && x.checked); },
    okText() {
      if (this.saving) return '提交中…';
      if (this.onlyAssistants) return '保存协助人';
      return this.isReassign ? '确认改派' : '确认指派';
    },
  },
  created() { this.load(); },
  methods: {
    async load() {
      this.loading = true;
      this.error = '';
      try {
        this.list = (await api.engineers.list()) || [];
      } catch (e) {
        this.error = e.message || '加载失败';
      } finally {
        this.loading = false;
      }
    },
    pick(e) {
      if (e.user.id === this.currentId) return;
      this.picked = e.user.id;
      this.submitError = '';
    },
    toggleAssistant(x) {
      if (x.isLead) return;
      const id = x.user.id;
      this.submitError = '';
      this.touched = true;
      if (this.assistantIds.includes(id)) {
        this.assistantIds = this.assistantIds.filter(v => v !== id);
        return;
      }
      if (this.finalAssistantIds.length >= MAX_ASSISTANTS) { this.submitError = `协助人最多 ${MAX_ASSISTANTS} 人`; return; }
      this.assistantIds = this.assistantIds.concat([id]);
    },
    async submit() {
      if (!this.canSubmit) return;
      const ids = this.finalAssistantIds;
      if (ids.length > MAX_ASSISTANTS) { this.submitError = `协助人最多 ${MAX_ASSISTANTS} 人`; return; }
      // 指派 / 改派时没动过协助人 → 不传 assistantIds，服务端保持现有协助人（新负责人若是协助人会自动转正）
      const sendIds = this.onlyAssistants || this.touched;
      if (sendIds && this.goneChecked.length) {
        this.submitError = `${this.goneChecked.map(x => x.user.name).join('、')} 已停用，请先取消勾选后再保存`;
        return;
      }
      this.saving = true;
      this.submitError = '';
      try {
        if (this.onlyAssistants) {
          const res = await api.orders.setAssistants(this.order.id, ids);
          this.$emit('done', res, '', 'assistants');
        } else {
          const res = await api.orders.assign(this.order.id, this.picked, sendIds ? ids : undefined);
          this.$emit('done', res, this.pickedUser ? this.pickedUser.name : '', 'assign');
        }
      } catch (e) {
        if (e.code === 40100) { this.$emit('close'); return; }
        this.submitError = e.message || '操作失败';
      } finally {
        this.saving = false;
      }
    },
  },
  template: `
  <Modal :title="title" :width="480" @close="$emit('close')">
    <div class="orders-assign-head">
      <div class="orders-assign-customer">{{ order.customerName }}</div>
      <div class="text-muted">{{ headText }}</div>
    </div>
    <Loading v-if="loading" />
    <Empty v-else-if="error" :text="'工程师列表加载失败：' + error">
      <button type="button" class="btn btn-secondary btn-sm" @click="load">重试</button>
    </Empty>
    <Empty v-else-if="!list.length && !assistantChoices.length" text="暂无可指派的工程师，请先在账号管理中添加" />
    <template v-else>
      <template v-if="!onlyAssistants">
        <div class="orders-assign-label">负责人<span class="req">*</span></div>
        <div class="orders-assign-list" role="radiogroup" aria-label="选择负责人">
          <button v-for="e in list" :key="e.user.id" type="button" role="radio" class="orders-assign-item"
                  :class="{ active: picked === e.user.id, current: e.user.id === currentId }"
                  :aria-checked="picked === e.user.id ? 'true' : 'false'" :disabled="e.user.id === currentId"
                  @click="pick(e)">
            <Avatar :user="e.user" :size="32" />
            <span class="orders-assign-main">
              <span class="orders-assign-name">{{ e.user.name }}
                <Tag v-if="e.user.id === currentId" tone="neutral">当前负责人</Tag>
                <Tag v-else-if="initialAssistantIds.includes(e.user.id)" tone="neutral">当前协助人</Tag>
                <Tag v-if="e.onSiteNow && e.user.id !== currentId" tone="danger">在现场</Tag>
              </span>
              <span class="orders-assign-load">{{ e.loadText }}</span>
            </span>
            <span class="orders-assign-radio" aria-hidden="true"></span>
          </button>
        </div>
        <div class="orders-assign-label">协助人<em>（可多选，最多 {{ maxAssistants }} 人）</em>
          <span class="orders-assign-count num">已选 {{ finalAssistantIds.length }}</span></div>
        <div class="orders-assign-chips" role="group" aria-label="选择协助人">
          <button v-for="x in assistantChoices" :key="x.user.id" type="button" role="checkbox" class="orders-assign-chip"
                  :class="{ active: x.checked }" :aria-checked="x.checked ? 'true' : 'false'" :disabled="x.isLead"
                  :title="x.isLead ? '负责人不能同时是协助人' : x.loadText" @click="toggleAssistant(x)">
            <Avatar :user="x.user" :size="18" /><span>{{ x.user.name }}</span>
            <span v-if="x.isLead" class="orders-assign-chip-note">负责人</span>
            <span v-else-if="x.gone" class="orders-assign-chip-note">已停用</span>
            <Icon v-if="!x.isLead && x.checked" name="check" :size="12" :stroke="2.6" />
          </button>
        </div>
      </template>
      <div v-else class="orders-assign-list" role="group" aria-label="选择协助人">
        <button v-for="x in assistantChoices" :key="x.user.id" type="button" role="checkbox" class="orders-assign-item"
                :class="{ active: x.checked, current: x.isLead }" :aria-checked="x.checked ? 'true' : 'false'"
                :disabled="x.isLead" @click="toggleAssistant(x)">
          <Avatar :user="x.user" :size="32" />
          <span class="orders-assign-main">
            <span class="orders-assign-name">{{ x.user.name }}
              <Tag v-if="x.isLead" tone="neutral">负责人</Tag>
              <Tag v-else-if="x.gone" tone="neutral">已停用</Tag>
              <Tag v-else-if="x.onSiteNow" tone="danger">在现场</Tag>
            </span>
            <span class="orders-assign-load">{{ x.loadText }}</span>
          </span>
          <span class="orders-assign-box" aria-hidden="true"><Icon v-if="x.checked" name="check" :size="11" :stroke="3" /></span>
        </button>
      </div>
    </template>
    <div v-if="submitError" class="field-error orders-assign-err">{{ submitError }}</div>
    <template #footer>
      <span v-if="onlyAssistants" class="orders-assign-foot-note num">已选 {{ finalAssistantIds.length }} / {{ maxAssistants }}</span>
      <button type="button" class="btn btn-secondary" @click="$emit('close')">取消</button>
      <button type="button" class="btn btn-primary" :disabled="!canSubmit" @click="submit">{{ okText }}</button>
    </template>
  </Modal>
  `,
};
