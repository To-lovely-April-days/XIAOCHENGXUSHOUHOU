// 新建工单弹窗：字段按 API.md POST /api/orders（v1.1 起建单不填价格，建单后在详情里定价）。PC 端没有地图选点，手工填写地址与经纬度（现场单必填）。
// 照片/视频先 POST /api/files（category=OrderPhoto）上传，最多 9 个，提交时带 photoFileIds。
import * as api from '../api.js';
import { confirm } from '../ui.js';
import { ORDER_SOURCE, PROBLEM_TYPE, URGENCY, SERVICE_TYPE, options } from '../enums.js';
import { toApiDate, parseTime } from '../format.js';

const MAX_PHOTOS = 9;
const MAX_SIZE = 20 * 1024 * 1024;
const PERIODS = ['凌晨', '上午', '中午', '下午', '晚上'];
const PHONE_RE = /^[0-9-]{7,20}$/;
const PICKER_URL = 'https://lbs.qq.com/getPoint/';

function emptyForm() {
  return {
    source: '', problemType: '', urgency: 'Normal', serviceType: 'Onsite',
    customerName: '', contactName: '', contactPhone: '',
    address: '', latitude: '', longitude: '',
    deviceModel: '', deviceSn: '', satDate: '', satSigned: null, projectNo: '',
    faultDate: toApiDate(new Date()), faultPeriod: '',
    symptom: '', customerTried: '', siteEnvironment: '', customerRequest: '',
  };
}

const trim = (v) => (v === null || v === undefined ? '' : String(v).trim());

export default {
  name: 'OrdersCreateModal',
  emits: ['close', 'created'],
  data() {
    return {
      f: emptyForm(),
      errors: {},
      photos: [], // { key, file, name, previewUrl, isVideo, status: 'uploading'|'done'|'error', id, url, error }
      saving: false,
      submitError: '',
      opts: {
        source: options(ORDER_SOURCE),
        problemType: options(PROBLEM_TYPE),
        urgency: options(URGENCY),
        serviceType: options(SERVICE_TYPE),
      },
      periods: PERIODS,
      pickerUrl: PICKER_URL,
      maxPhotos: MAX_PHOTOS,
    };
  },
  computed: {
    onsite() { return this.f.serviceType === 'Onsite'; },
    faultTimeText() {
      const d = parseTime(this.f.faultDate);
      if (!d || !this.f.faultPeriod) return '';
      return `${d.getMonth() + 1}月${d.getDate()}日 ${this.f.faultPeriod}`;
    },
    uploading() { return this.photos.some(p => p.status === 'uploading'); },
    dirty() {
      const e = emptyForm();
      return this.photos.length > 0 || Object.keys(e).some(k => k !== 'faultDate' && this.f[k] !== e[k]);
    },
  },
  beforeUnmount() {
    this.photos.forEach(p => { if (p.previewUrl) URL.revokeObjectURL(p.previewUrl); });
  },
  methods: {
    set(key, v) { this.f[key] = v; this.clearErr(key); },
    clearErr(key) {
      if (this.errors[key]) { const e = Object.assign({}, this.errors); delete e[key]; this.errors = e; }
      this.submitError = '';
    },
    /** 纬度框里粘贴「31.30,120.72」（腾讯坐标拾取器格式）时自动拆分 */
    onLatInput(e) {
      const v = e.target.value;
      const m = /^\s*(-?\d+(?:\.\d+)?)\s*[,，\s]\s*(-?\d+(?:\.\d+)?)\s*$/.exec(v);
      if (m) { this.f.latitude = m[1]; this.f.longitude = m[2]; this.clearErr('longitude'); }
      else this.f.latitude = v;
      this.clearErr('latitude');
    },

    // ------------------------------------------------ 照片
    pickFiles() { const el = this.$refs.file; if (el) { el.value = ''; el.click(); } },
    onFiles(e) {
      const files = Array.from(e.target.files || []);
      const room = MAX_PHOTOS - this.photos.length;
      if (files.length > room) this.errors = Object.assign({}, this.errors, { photos: `最多上传 ${MAX_PHOTOS} 个，已忽略多余的 ${files.length - Math.max(0, room)} 个` });
      else this.clearErr('photos');
      files.slice(0, Math.max(0, room)).forEach(file => {
        const isVideo = /^video\//.test(file.type);
        const isImage = /^image\//.test(file.type);
        const item = {
          key: Math.random().toString(36).slice(2), file, name: file.name, isVideo,
          previewUrl: isImage ? URL.createObjectURL(file) : '', status: 'uploading', id: null, url: '', error: '', fatal: false,
        };
        if (!isVideo && !isImage) { item.status = 'error'; item.error = '只支持图片或视频'; item.fatal = true; }
        else if (file.size > MAX_SIZE) { item.status = 'error'; item.error = '文件超过 20MB'; item.fatal = true; }
        this.photos.push(item);
        if (item.status === 'uploading') this.upload(this.photos[this.photos.length - 1]);
      });
    },
    async upload(item) {
      item.status = 'uploading';
      item.error = '';
      try {
        const res = await api.files.upload(item.file, 'OrderPhoto');
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

    // ------------------------------------------------ 校验 / 提交
    validate() {
      const f = this.f;
      const e = {};
      const req = (k, msg) => { if (!trim(f[k])) e[k] = msg; };
      if (!f.source) e.source = '请选择工单来源';
      if (!f.problemType) e.problemType = '请选择问题类型';
      if (!f.urgency) e.urgency = '请选择紧急程度';
      if (!f.serviceType) e.serviceType = '请选择维修类型';
      req('customerName', '请填写客户单位');
      req('contactName', '请填写联系人');
      const phone = trim(f.contactPhone).replace(/\s+/g, '');
      if (!phone) e.contactPhone = '请填写联系电话';
      else if (!PHONE_RE.test(phone)) e.contactPhone = '联系电话格式不正确（7–20 位数字或横杠）';
      req('address', '请填写客户地址');
      const lat = trim(f.latitude);
      const lng = trim(f.longitude);
      if (lat !== '' || lng !== '' || this.onsite) {
        if (lat === '') e.latitude = this.onsite ? '现场工单必须填写纬度' : '请同时填写纬度';
        else if (!isFinite(Number(lat)) || Number(lat) < -90 || Number(lat) > 90) e.latitude = '纬度应在 -90 ~ 90 之间';
        if (lng === '') e.longitude = this.onsite ? '现场工单必须填写经度' : '请同时填写经度';
        else if (!isFinite(Number(lng)) || Number(lng) < -180 || Number(lng) > 180) e.longitude = '经度应在 -180 ~ 180 之间';
      }
      req('deviceModel', '请填写设备型号');
      req('deviceSn', '请填写设备编号');
      if (!/^\d{4}-(0[1-9]|1[0-2])$/.test(trim(f.satDate))) e.satDate = '请选择购买/SAT 时间（年月）';
      if (f.satSigned !== true && f.satSigned !== false) e.satSigned = '请选择 SAT 是否已签字';
      if (!f.faultDate) e.faultTime = '请选择发生日期';
      else if (!f.faultPeriod) e.faultTime = '请选择发生时段';
      req('symptom', '请按客户原话填写故障现象/报错');
      req('customerTried', '请填写客户已尝试的操作');
      req('siteEnvironment', '请填写现场环境条件');
      if (this.photos.some(p => p.status === 'error')) e.photos = '有文件未上传成功，请重试或移除';
      this.errors = e;
      return Object.keys(e).length === 0;
    },
    buildBody() {
      const f = this.f;
      const lat = trim(f.latitude);
      const lng = trim(f.longitude);
      const opt = (v) => (trim(v) ? trim(v) : null);
      return {
        source: f.source, problemType: f.problemType, urgency: f.urgency, serviceType: f.serviceType,
        customerName: trim(f.customerName), contactName: trim(f.contactName),
        contactPhone: trim(f.contactPhone).replace(/\s+/g, ''),
        address: trim(f.address),
        latitude: lat === '' ? null : Number(lat), longitude: lng === '' ? null : Number(lng),
        deviceModel: trim(f.deviceModel), deviceSn: trim(f.deviceSn),
        satDate: trim(f.satDate), satSigned: f.satSigned, projectNo: opt(f.projectNo),
        faultTime: this.faultTimeText,
        symptom: trim(f.symptom), customerTried: trim(f.customerTried), siteEnvironment: trim(f.siteEnvironment),
        customerRequest: opt(f.customerRequest),
        photoFileIds: this.photos.filter(p => p.status === 'done').map(p => p.id),
      };
    },
    async submit() {
      if (this.saving) return;
      this.submitError = '';
      if (this.uploading) { this.submitError = '文件仍在上传，请稍候'; return; }
      if (!this.validate()) {
        this.submitError = '请检查标红的字段';
        this.$nextTick(() => {
          const el = document.querySelector('.orders-create .invalid, .orders-create .field-error');
          if (el && el.scrollIntoView) el.scrollIntoView({ block: 'center', behavior: 'smooth' });
        });
        return;
      }
      this.saving = true;
      try {
        const detail = await api.orders.create(this.buildBody());
        this.$emit('created', detail);
      } catch (e) {
        if (e.code === 40100) { this.$emit('close'); return; }
        this.submitError = e.message || '提交失败';
      } finally {
        this.saving = false;
      }
    },
    async tryClose() {
      if (this.saving) return;
      if (this.dirty && !(await confirm('放弃新建工单？', '已填写的内容不会保存。', { okText: '放弃', danger: true }))) return;
      this.$emit('close');
    },
  },
  template: `
  <Modal title="新建工单" :width="760" persistent @close="tryClose">
    <form class="form-grid orders-create" novalidate @submit.prevent="submit">
      <div class="form-section-title">类型</div>
      <div v-for="g in [['source','工单来源'],['problemType','问题类型'],['urgency','紧急程度'],['serviceType','维修类型']]" :key="g[0]" class="field">
        <span class="field-label">{{ g[1] }}<span class="req">*</span></span>
        <div class="radio-group" role="radiogroup" :aria-label="g[1]">
          <button v-for="o in opts[g[0]]" :key="o.value" type="button" role="radio" class="radio-chip"
                  :class="{ active: f[g[0]] === o.value }" :aria-checked="f[g[0]] === o.value ? 'true' : 'false'"
                  @click="set(g[0], o.value)">{{ o.label }}</button>
        </div>
        <span v-if="g[0] === 'serviceType'" class="field-hint">只有现场单需要工程师打卡</span>
        <span v-if="errors[g[0]]" class="field-error">{{ errors[g[0]] }}</span>
      </div>

      <div class="form-section-title">客户</div>
      <label class="field span-2"><span class="field-label">客户单位<span class="req">*</span></span>
        <input class="input" :class="{ invalid: errors.customerName }" v-model="f.customerName" maxlength="100"
               placeholder="客户单位全称" @input="clearErr('customerName')">
        <span v-if="errors.customerName" class="field-error">{{ errors.customerName }}</span></label>
      <label class="field"><span class="field-label">联系人<span class="req">*</span></span>
        <input class="input" :class="{ invalid: errors.contactName }" v-model="f.contactName" maxlength="50" @input="clearErr('contactName')">
        <span v-if="errors.contactName" class="field-error">{{ errors.contactName }}</span></label>
      <label class="field"><span class="field-label">联系电话<span class="req">*</span></span>
        <input class="input" :class="{ invalid: errors.contactPhone }" v-model="f.contactPhone" maxlength="40" inputmode="tel"
               placeholder="手机或座机，如 0512-12345678" @input="clearErr('contactPhone')">
        <span v-if="errors.contactPhone" class="field-error">{{ errors.contactPhone }}</span></label>
      <label class="field span-2"><span class="field-label">客户地址<span class="req">*</span></span>
        <input class="input" :class="{ invalid: errors.address }" v-model="f.address" maxlength="200"
               placeholder="省市区 + 详细地址，如 江苏省苏州市工业园区星湖街 xx 号" @input="clearErr('address')">
        <span v-if="errors.address" class="field-error">{{ errors.address }}</span></label>
      <label class="field"><span class="field-label">纬度<span v-if="onsite" class="req">*</span></span>
        <input class="input num" :class="{ invalid: errors.latitude }" :value="f.latitude" inputmode="decimal"
               placeholder="如 31.30（可粘贴「纬度,经度」）" @input="onLatInput">
        <span v-if="errors.latitude" class="field-error">{{ errors.latitude }}</span></label>
      <label class="field"><span class="field-label">经度<span v-if="onsite" class="req">*</span></span>
        <input class="input num" :class="{ invalid: errors.longitude }" v-model="f.longitude" inputmode="decimal"
               placeholder="如 120.72" @input="clearErr('longitude')">
        <span v-if="errors.longitude" class="field-error">{{ errors.longitude }}</span></label>
      <div class="field-hint span-2 orders-create-geo">
        <Icon name="info" :size="13" />
        <span>{{ onsite ? '现场单必须填写经纬度，用于计算打卡距离。' : '远程/返厂单经纬度选填。' }}可用腾讯地图坐标拾取器获取：
          <a :href="pickerUrl" target="_blank" rel="noopener noreferrer">lbs.qq.com/getPoint</a>（GCJ-02 坐标）</span>
      </div>

      <div class="form-section-title">设备</div>
      <label class="field"><span class="field-label">设备型号<span class="req">*</span></span>
        <input class="input" :class="{ invalid: errors.deviceModel }" v-model="f.deviceModel" maxlength="100"
               placeholder="如 高压反应釜 HTR-500" @input="clearErr('deviceModel')">
        <span v-if="errors.deviceModel" class="field-error">{{ errors.deviceModel }}</span></label>
      <label class="field"><span class="field-label">设备编号<span class="req">*</span></span>
        <input class="input mono" :class="{ invalid: errors.deviceSn }" v-model="f.deviceSn" maxlength="100" @input="clearErr('deviceSn')">
        <span v-if="errors.deviceSn" class="field-error">{{ errors.deviceSn }}</span></label>
      <label class="field"><span class="field-label">购买/SAT 时间<span class="req">*</span></span>
        <input class="input" type="month" :class="{ invalid: errors.satDate }" v-model="f.satDate" @input="clearErr('satDate')">
        <span v-if="errors.satDate" class="field-error">{{ errors.satDate }}</span></label>
      <div class="field"><span class="field-label">SAT 是否已签字<span class="req">*</span></span>
        <div class="radio-group" role="radiogroup" aria-label="SAT 是否已签字">
          <button type="button" role="radio" class="radio-chip" :class="{ active: f.satSigned === true }"
                  :aria-checked="f.satSigned === true ? 'true' : 'false'" @click="set('satSigned', true)">已签字（老产品）</button>
          <button type="button" role="radio" class="radio-chip" :class="{ active: f.satSigned === false }"
                  :aria-checked="f.satSigned === false ? 'true' : 'false'" @click="set('satSigned', false)">未签字（新产品）</button>
        </div>
        <span v-if="errors.satSigned" class="field-error">{{ errors.satSigned }}</span></div>
      <label class="field"><span class="field-label">项目编号</span>
        <input class="input" v-model="f.projectNo" maxlength="50" placeholder="选填"></label>

      <div class="form-section-title">故障</div>
      <div class="field span-2"><span class="field-label">发生时间<span class="req">*</span></span>
        <div class="orders-create-fault">
          <input class="input" type="date" :class="{ invalid: errors.faultTime && !f.faultDate }" v-model="f.faultDate"
                 aria-label="发生日期" @input="clearErr('faultTime')">
          <div class="radio-group" role="radiogroup" aria-label="发生时段">
            <button v-for="p in periods" :key="p" type="button" role="radio" class="radio-chip" :class="{ active: f.faultPeriod === p }"
                    :aria-checked="f.faultPeriod === p ? 'true' : 'false'" @click="set('faultPeriod', p); clearErr('faultTime')">{{ p }}</button>
          </div>
          <span v-if="faultTimeText" class="text-muted">「{{ faultTimeText }}」</span>
        </div>
        <span v-if="errors.faultTime" class="field-error">{{ errors.faultTime }}</span></div>
      <label class="field span-2"><span class="field-label">故障现象/报错<span class="req">*</span></span>
        <textarea class="textarea" :class="{ invalid: errors.symptom }" v-model="f.symptom" rows="2" maxlength="1000"
                  placeholder="按客户原话填写，如：升温到 180℃ 左右就不再加热，面板报 E03" @input="clearErr('symptom')"></textarea>
        <span v-if="errors.symptom" class="field-error">{{ errors.symptom }}</span></label>
      <label class="field"><span class="field-label">客户已尝试的操作<span class="req">*</span></span>
        <textarea class="textarea" :class="{ invalid: errors.customerTried }" v-model="f.customerTried" rows="2" maxlength="500"
                  placeholder="如：断电重启、检查电源线" @input="clearErr('customerTried')"></textarea>
        <span v-if="errors.customerTried" class="field-error">{{ errors.customerTried }}</span></label>
      <label class="field"><span class="field-label">现场环境条件<span class="req">*</span></span>
        <textarea class="textarea" :class="{ invalid: errors.siteEnvironment }" v-model="f.siteEnvironment" rows="2" maxlength="500"
                  placeholder="如：通风橱内，220V 单相电" @input="clearErr('siteEnvironment')"></textarea>
        <span v-if="errors.siteEnvironment" class="field-error">{{ errors.siteEnvironment }}</span></label>
      <label class="field span-2"><span class="field-label">客户诉求与限制</span>
        <textarea class="textarea" v-model="f.customerRequest" rows="2" maxlength="500" placeholder="选填，如：希望本周内上门，周末不方便"></textarea></label>

      <div class="form-section-title">现场照片</div>
      <div class="field span-2"><span class="field-label">现场照片/视频（选填，最多 {{ maxPhotos }} 个）</span>
        <div class="orders-create-photos">
          <div v-for="(p, i) in photos" :key="p.key" class="orders-create-photo" :class="'is-' + p.status" :title="p.error || p.name">
            <img v-if="p.previewUrl" :src="p.previewUrl" alt="">
            <span v-else class="orders-create-photo-ph">{{ p.isVideo ? '视频' : '文件' }}</span>
            <span v-if="p.status === 'uploading'" class="orders-create-photo-mask"><span class="spinner"></span></span>
            <span v-else-if="p.status === 'error' && p.fatal" class="orders-create-photo-mask orders-create-photo-bad">{{ p.error }}</span>
            <button v-else-if="p.status === 'error'" type="button" class="orders-create-photo-mask orders-create-photo-retry"
                    @click="upload(p)">上传失败<br>点击重试</button>
            <button type="button" class="orders-create-photo-del" :aria-label="'移除 ' + p.name" @click="removePhoto(i)">
              <Icon name="close" :size="12" :stroke="2.4" /></button>
          </div>
          <button v-if="photos.length < maxPhotos" type="button" class="orders-create-add" @click="pickFiles">
            <Icon name="plus" :size="18" /><span>添加</span></button>
          <input ref="file" type="file" accept="image/*,video/*" multiple hidden @change="onFiles">
        </div>
        <span v-if="errors.photos" class="field-error">{{ errors.photos }}</span>
        <span v-else class="field-hint">支持图片或视频，单个不超过 20MB。服务价格不在建单时填写，建单后由主管在工单详情中定价。</span>
      </div>
      <button type="submit" hidden></button>
    </form>
    <template #footer>
      <span v-if="submitError" class="field-error orders-create-submit-err">{{ submitError }}</span>
      <button type="button" class="btn btn-secondary" :disabled="saving" @click="tryClose">取消</button>
      <button type="button" class="btn btn-primary" :disabled="saving || uploading" @click="submit">
        {{ saving ? '提交中…' : (uploading ? '上传中…' : '提交工单') }}</button>
    </template>
  </Modal>
  `,
};
