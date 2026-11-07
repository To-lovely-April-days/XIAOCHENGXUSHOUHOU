// 指派 / 改派弹窗：GET /api/engineers 列出工程师负载（loadText），POST /api/orders/{id}/assign。
import * as api from '../api.js';

export default {
  name: 'OrdersAssignModal',
  props: {
    /** OrderDetail */
    order: { type: Object, required: true },
  },
  emits: ['close', 'done'],
  data() {
    return { list: [], loading: true, error: '', picked: null, saving: false, submitError: '' };
  },
  computed: {
    isReassign() { return this.order.status !== 'Pending'; },
    title() { return (this.isReassign ? '改派 ' : '指派 ') + this.order.orderNo; },
    currentId() { return this.order.assignee ? this.order.assignee.id : null; },
    pickedUser() {
      const e = this.list.find(x => x.user.id === this.picked);
      return e ? e.user : null;
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
    async submit() {
      if (!this.picked || this.saving) return;
      this.saving = true;
      this.submitError = '';
      try {
        const res = await api.orders.assign(this.order.id, this.picked);
        this.$emit('done', res, this.pickedUser ? this.pickedUser.name : '');
      } catch (e) {
        if (e.code === 40100) { this.$emit('close'); return; }
        this.submitError = e.message || '操作失败';
      } finally {
        this.saving = false;
      }
    },
  },
  template: `
  <Modal :title="title" :width="460" @close="$emit('close')">
    <div class="orders-assign-head">
      <div class="orders-assign-customer">{{ order.customerName }}</div>
      <div class="text-muted">
        {{ isReassign ? ('当前处理人：' + (order.assignee ? order.assignee.name : '未分配')) : '选择负责该工单的工程师，指派后状态变为「待启动」' }}
      </div>
    </div>
    <Loading v-if="loading" />
    <Empty v-else-if="error" :text="'工程师列表加载失败：' + error">
      <button type="button" class="btn btn-secondary btn-sm" @click="load">重试</button>
    </Empty>
    <Empty v-else-if="!list.length" text="暂无可指派的工程师，请先在账号管理中添加" />
    <div v-else class="orders-assign-list" role="radiogroup" aria-label="选择工程师">
      <button v-for="e in list" :key="e.user.id" type="button" role="radio" class="orders-assign-item"
              :class="{ active: picked === e.user.id, current: e.user.id === currentId }"
              :aria-checked="picked === e.user.id ? 'true' : 'false'" :disabled="e.user.id === currentId"
              @click="pick(e)" @dblclick="pick(e); submit()">
        <Avatar :user="e.user" :size="32" />
        <span class="orders-assign-main">
          <span class="orders-assign-name">{{ e.user.name }}
            <Tag v-if="e.user.id === currentId" tone="neutral">当前处理人</Tag>
            <Tag v-else-if="e.onSiteNow" tone="danger">在现场</Tag>
          </span>
          <span class="orders-assign-load">{{ e.loadText }}</span>
        </span>
        <span class="orders-assign-radio" aria-hidden="true"></span>
      </button>
    </div>
    <div v-if="submitError" class="field-error orders-assign-err">{{ submitError }}</div>
    <template #footer>
      <button type="button" class="btn btn-secondary" @click="$emit('close')">取消</button>
      <button type="button" class="btn btn-primary" :disabled="!picked || saving" @click="submit">
        {{ saving ? '提交中…' : (isReassign ? '确认改派' : '确认指派') }}</button>
    </template>
  </Modal>
  `,
};
