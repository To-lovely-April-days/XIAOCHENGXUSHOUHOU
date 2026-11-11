// 返厂节点记录 / 更正弹窗（v1.1）：POST /api/orders/{id}/factory-nodes。
// 按节点显示字段：客户寄出 = 快递公司 + 单号；工厂收货 = 照片（≥1）；寄回客户 = 快递公司 + 单号 + 照片（≥1）；客户签收 = 照片（选填）。
// 照片先 POST /api/files（category 按节点）上传，提交时 photoIds 为该节点照片的最终全集，最多 9 张。
import * as api from '../api.js';
import { confirm, preview } from '../ui.js';
import { FACTORY_NODE } from '../enums.js';

const MAX_PHOTOS = 9;
const MAX_SIZE = 20 * 1024 * 1024;
const TEXT_MAX = 50;
const NOTE_MAX = 500;

const NOTE_PLACEHOLDER = {
  CustomerShipped: '选填，如：客户预计周三寄出，含主机 1 台',
  FactoryReceived: '选填，如：外箱完好，含电源线 1 根',
  ShippedBack: '选填，如：含电源线 1 根、检测报告 1 份',
  CustomerSigned: '选填，如：客户王工电话确认已签收',
};

const trim = (v) => (v === null || v === undefined ? '' : String(v).trim());

export default {
  name: 'OrdersFactoryModal',
  props: {
    /** OrderDetail */
    order: { type: Object, required: true },
    /** FactoryDto.nodes 里的一项（recorded=true 时为更正） */
    node: { type: Object, required: true },
  },
  emits: ['close', 'done'],
  data() {
    const n = this.node;
    const meta = FACTORY_NODE[n.node] || {};
    return {
      meta,
      editing: !!n.recorded,
      courier: n.courier || '',
      trackingNo: n.trackingNo || '',
      note: n.note || '',
      // { key, name, previewUrl, status: 'uploading'|'done'|'error', id, url, error, fatal, file }
      photos: meta.photo ? (n.photos || []).map(p => ({
        key: 'f' + p.id, name: '已上传照片', previewUrl: '', status: 'done', id: p.id, url: p.url, error: '', fatal: false, file: null,
      })) : [],
      errors: {},
      saving: false,
      submitError: '',
      maxPhotos: MAX_PHOTOS,
      textMax: TEXT_MAX,
      noteMax: NOTE_MAX,
    };
  },
  computed: {
    title() { return (this.editing ? '更正' : '记录') + '「' + (this.meta.label || this.node.label || '') + '」 ' + this.order.orderNo; },
    okText() { return this.editing ? '保存更正' : (this.meta.action || '保存'); },
    photoRequired() { return this.meta.photo === 'required'; },
    photoLabel() {
      const k = this.node.node;
      if (k === 'FactoryReceived') return '收货照片（外观/配件）';
      if (k === 'ShippedBack') return '寄回照片';
      return '签收凭证';
    },
    notePlaceholder() { return NOTE_PLACEHOLDER[this.node.node] || '选填'; },
    uploading() { return this.photos.some(p => p.status === 'uploading'); },
    /** 首次记录时的提示（规则由服务端判定，这里只做说明） */
    tip() {
      const k = this.node.node;
      if (this.editing) return '更正只覆盖本节点的内容，不改变工单状态，会写入状态记录。';
      if (k === 'FactoryReceived') {
        const first = ((this.order.factory && this.order.factory.nodes) || []).find(x => x.node === 'CustomerShipped');
        const skip = first && !first.recorded ? '「客户寄出」尚未记录，主管可直接记录收货（该节点将标为已跳过）。' : '';
        return skip + (this.order.status === 'Accepted' ? '保存后工单自动进入「处理中」。' : '');
      }
      if (k === 'ShippedBack') return '记录寄回后，负责工程师才能提交完工。';
      if (k === 'CustomerSigned') return '客户签收不影响闭环，仅作留痕。';
      return this.order.assignee ? '保存后通知负责工程师。' : '';
    },
    dirty() {
      const n = this.node;
      if (this.courier !== (n.courier || '') || this.trackingNo !== (n.trackingNo || '') || this.note !== (n.note || '')) return true;
      const before = (this.meta.photo ? (n.photos || []) : []).map(p => p.id).join(',');
      return before !== this.photos.filter(p => p.status === 'done').map(p => p.id).join(',') || this.photos.some(p => p.status !== 'done');
    },
  },
  beforeUnmount() {
    this.photos.forEach(p => { if (p.previewUrl) URL.revokeObjectURL(p.previewUrl); });
  },
  methods: {
    clearErr(key) {
      if (this.errors[key]) { const e = Object.assign({}, this.errors); delete e[key]; this.errors = e; }
      this.submitError = '';
    },

    // ------------------------------------------------ 照片
    pickFiles() { const el = this.$refs.file; if (el) { el.value = ''; el.click(); } },
    onFiles(e) {
      const files = Array.from(e.target.files || []);
      const room = Math.max(0, MAX_PHOTOS - this.photos.length);
      if (files.length > room) this.errors = Object.assign({}, this.errors, { photos: `照片最多 ${MAX_PHOTOS} 张，已忽略多余的 ${files.length - room} 张` });
      else this.clearErr('photos');
      files.slice(0, room).forEach(file => {
        const isImage = /^image\//.test(file.type);
        const item = {
          key: Math.random().toString(36).slice(2), file, name: file.name,
          previewUrl: isImage ? URL.createObjectURL(file) : '', status: 'uploading', id: null, url: '', error: '', fatal: false,
        };
        if (!isImage) { item.status = 'error'; item.error = '只支持图片'; item.fatal = true; }
        else if (file.size > MAX_SIZE) { item.status = 'error'; item.error = '文件超过 20MB'; item.fatal = true; }
        this.photos.push(item);
        if (item.status === 'uploading') this.upload(this.photos[this.photos.length - 1]);
      });
    },
    async upload(item) {
      item.status = 'uploading';
      item.error = '';
      try {
        const res = await api.files.upload(item.file, this.meta.category);
        item.id = res.id;
        item.url = res.url;
        item.status = 'done';
      } catch (e) {
        item.status = 'error';
        item.error = e.message || '上传失败';
      }
    },
    removePhoto(i) {
      const p = this.photos[i];
      if (p && p.previewUrl) URL.revokeObjectURL(p.previewUrl);
      this.photos.splice(i, 1);
      this.clearErr('photos');
    },
    openPhoto(p) {
      const src = p.previewUrl || p.url;
      if (!src || p.status !== 'done') return;
      const list = this.photos.filter(x => x.status === 'done' && (x.previewUrl || x.url)).map(x => x.previewUrl || x.url);
      preview(list, list.indexOf(src));
    },

    // ------------------------------------------------ 校验 / 提交
    validate() {
      const e = {};
      const k = this.node.node;
      if (this.meta.courier) {
        const courier = trim(this.courier);
        const no = trim(this.trackingNo).replace(/\s+/g, '');
        if (!courier) e.courier = '请填写快递公司';
        else if (courier.length > TEXT_MAX) e.courier = `快递公司不能超过 ${TEXT_MAX} 个字`;
        if (!no) e.trackingNo = '请填写快递单号';
        else if (no.length > TEXT_MAX) e.trackingNo = `快递单号不能超过 ${TEXT_MAX} 个字`;
      }
      if (this.meta.photo) {
        const done = this.photos.filter(p => p.status === 'done').length;
        if (this.photos.some(p => p.status === 'error')) e.photos = '有照片未上传成功，请重试或移除';
        else if (this.photoRequired && done < 1) e.photos = k === 'FactoryReceived' ? '请至少上传 1 张收货照片（外观/配件）' : '请至少上传 1 张寄回照片';
        else if (done > MAX_PHOTOS) e.photos = `照片最多 ${MAX_PHOTOS} 张`;
      }
      if (trim(this.note).length > NOTE_MAX) e.note = `备注不能超过 ${NOTE_MAX} 个字`;
      this.errors = e;
      return Object.keys(e).length === 0;
    },
    buildBody() {
      const body = { node: this.node.node, note: trim(this.note) || null };
      if (this.meta.courier) {
        body.courier = trim(this.courier);
        body.trackingNo = trim(this.trackingNo).replace(/\s+/g, '');
      }
      if (this.meta.photo) body.photoIds = this.photos.filter(p => p.status === 'done').map(p => p.id);
      return body;
    },
    async submit() {
      if (this.saving) return;
      this.submitError = '';
      if (this.uploading) { this.submitError = '照片仍在上传，请稍候'; return; }
      if (!this.validate()) return;
      this.saving = true;
      try {
        const res = await api.orders.factoryNode(this.order.id, this.buildBody());
        this.$emit('done', res, this.meta.label || '', this.editing);
      } catch (e) {
        if (e.code === 40100) { this.$emit('close'); return; }
        this.submitError = e.message || '保存失败';
      } finally {
        this.saving = false;
      }
    },
    async tryClose() {
      if (this.saving) return;
      if (this.dirty && !(await confirm('放弃填写？', '已填写的内容不会保存。', { okText: '放弃', danger: true }))) return;
      this.$emit('close');
    },
  },
  template: `
  <Modal :title="title" :width="480" persistent @close="tryClose">
    <form class="form-stack orders-factory-form" novalidate @submit.prevent="submit">
      <div class="orders-assign-head">
        <div class="orders-assign-customer">{{ order.customerName }}</div>
        <div v-if="tip" class="text-muted">{{ tip }}</div>
      </div>
      <template v-if="meta.courier">
        <label class="field"><span class="field-label">快递公司<span class="req">*</span></span>
          <input class="input" :class="{ invalid: errors.courier }" v-model="courier" :maxlength="textMax"
                 placeholder="如 顺丰" @input="clearErr('courier')">
          <span v-if="errors.courier" class="field-error">{{ errors.courier }}</span></label>
        <label class="field"><span class="field-label">快递单号<span class="req">*</span></span>
          <input class="input mono" :class="{ invalid: errors.trackingNo }" v-model="trackingNo" :maxlength="textMax + 10"
                 placeholder="如 SF1234567890" @input="clearErr('trackingNo')">
          <span v-if="errors.trackingNo" class="field-error">{{ errors.trackingNo }}</span></label>
      </template>
      <div v-if="meta.photo" class="field">
        <span class="field-label">{{ photoLabel }}<span v-if="photoRequired" class="req">*</span><em v-else>（选填）</em> · 最多 {{ maxPhotos }} 张</span>
        <div class="orders-create-photos">
          <div v-for="(p, i) in photos" :key="p.key" class="orders-create-photo" :class="'is-' + p.status" :title="p.error || p.name">
            <img v-if="p.previewUrl || p.url" :src="p.previewUrl || p.url" alt="" class="orders-factory-photo-img" @click="openPhoto(p)">
            <span v-else class="orders-create-photo-ph">照片</span>
            <span v-if="p.status === 'uploading'" class="orders-create-photo-mask"><span class="spinner"></span></span>
            <span v-else-if="p.status === 'error' && p.fatal" class="orders-create-photo-mask orders-create-photo-bad">{{ p.error }}</span>
            <button v-else-if="p.status === 'error'" type="button" class="orders-create-photo-mask orders-create-photo-retry"
                    @click="upload(p)">上传失败<br>点击重试</button>
            <button type="button" class="orders-create-photo-del" :aria-label="'移除 ' + p.name" @click="removePhoto(i)">
              <Icon name="close" :size="12" :stroke="2.4" /></button>
          </div>
          <button v-if="photos.length < maxPhotos" type="button" class="orders-create-add" @click="pickFiles">
            <Icon name="plus" :size="18" /><span>添加</span></button>
          <input ref="file" type="file" accept="image/*" multiple hidden @change="onFiles">
        </div>
        <span v-if="errors.photos" class="field-error">{{ errors.photos }}</span>
        <span v-else class="field-hint">单张不超过 20MB{{ photoRequired ? '，至少 1 张' : '' }}</span>
      </div>
      <label class="field"><span class="field-label">备注<em>（选填）</em></span>
        <textarea class="textarea" :class="{ invalid: errors.note }" rows="2" v-model="note" :maxlength="noteMax"
                  :placeholder="notePlaceholder" @input="clearErr('note')"></textarea>
        <span v-if="errors.note" class="field-error">{{ errors.note }}</span></label>
      <button type="submit" hidden></button>
    </form>
    <template #footer>
      <span v-if="submitError" class="field-error orders-create-submit-err">{{ submitError }}</span>
      <button type="button" class="btn btn-secondary" :disabled="saving" @click="tryClose">取消</button>
      <button type="button" class="btn btn-primary" :disabled="saving || uploading" @click="submit">
        {{ saving ? '保存中…' : (uploading ? '上传中…' : okText) }}</button>
    </template>
  </Modal>
  `,
};
