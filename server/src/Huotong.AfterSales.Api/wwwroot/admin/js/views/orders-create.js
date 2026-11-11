// 新建工单弹窗：字段按 API.md POST /api/orders（v1.1 起建单不填价格，建单后在详情里定价）。PC 端没有地图选点，手工填写地址与经纬度（现场单必填）。
// v1.2：附件（照片 / 视频 / 文件）用 attachments-upload.js 先 POST /api/files（category=Attachment）上传，提交时带 attachments。
// v1.3（CHANGES-v1.3 §10.4）：
//   「设备资料」区固定为 *项目编号 → 设备（可多台，选填）→ *是否已完成 SAT → 时间（选填，标签随 SAT 选择变化）；
//   提交 projectNo / devices: [{ name, sn, model }]（过滤空行）/ satSigned / satDate，不再传 deviceModel / deviceSn；
//   附件按三类分三个上传区，三类合计最多 50 个，提交 attachments: [{ fileId, restricted, type }]。
import * as api from '../api.js';
import { confirm } from '../ui.js';
import { ORDER_SOURCE, PROBLEM_TYPE, URGENCY, SERVICE_TYPE, ATTACHMENT_TYPE, options } from '../enums.js';
import { toApiDate, parseTime } from '../format.js';
import AttachmentsUpload, { MAX_COUNT, attUploading, attHasError, attDone, attUsed } from './attachments-upload.js';

const PERIODS = ['凌晨', '上午', '中午', '下午', '晚上'];
const PHONE_RE = /^[0-9-]{7,20}$/;
const PICKER_URL = 'https://lbs.qq.com/getPoint/';
/** 设备最多几台（与服务端 DeviceRules.Max 一致） */
const MAX_DEVICES = 20;
const DATE_RE = /^\d{4}-(0[1-9]|1[0-2])-(0[1-9]|[12]\d|3[01])$/;
/** 附件类别，顺序即展示顺序 */
const ATT_TYPES = Object.keys(ATTACHMENT_TYPE);

let devSeq = 0;
/** 一台空设备；k 只用于 v-for 的 key，不提交 */
function emptyDevice() { return { k: ++devSeq, name: '', sn: '', model: '' }; }

function emptyForm() {
  return {
    source: '', problemType: '', urgency: 'Normal', serviceType: 'Onsite',
    customerName: '', contactName: '', contactPhone: '',
    address: '', latitude: '', longitude: '',
    projectNo: '', devices: [emptyDevice()], satSigned: null, satDate: '',
    faultDate: toApiDate(new Date()), faultPeriod: '',
    symptom: '', customerTried: '', siteEnvironment: '', customerRequest: '',
  };
}

/** 三类附件各自的条目列表 */
function emptyAtts() {
  const o = {};
  ATT_TYPES.forEach(t => { o[t] = []; });
  return o;
}

const trim = (v) => (v === null || v === undefined ? '' : String(v).trim());

export default {
  name: 'OrdersCreateModal',
  components: { AttachmentsUpload },
  emits: ['close', 'created'],
  data() {
    return {
      f: emptyForm(),
      errors: {},
      atts: emptyAtts(), // 附件条目，按类别分三组：{ Sat: [], RepairForm: [], Other: [] }，条目结构见 attachments-upload.js
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
      maxAtts: MAX_COUNT,
      maxDevices: MAX_DEVICES,
    };
  },
  computed: {
    onsite() { return this.f.serviceType === 'Onsite'; },
    faultTimeText() {
      const d = parseTime(this.f.faultDate);
      if (!d || !this.f.faultPeriod) return '';
      return `${d.getMonth() + 1}月${d.getDate()}日 ${this.f.faultPeriod}`;
    },
    /** 时间栏的标签：随「是否已完成 SAT」变化 */
    satDateLabel() {
      if (this.f.satSigned === true) return 'SAT 完成时间';
      if (this.f.satSigned === false) return '发货时间';
      return 'SAT 完成时间 / 发货时间';
    },
    canAddDevice() { return this.f.devices.length < MAX_DEVICES; },
    /** 三类附件合在一起（提交、上传中 / 失败的判断都看它） */
    allAtts() { return ATT_TYPES.reduce((all, t) => all.concat(this.atts[t] || []), []); },
    /** 三个上传区：每个区的名额 = 50 − 另外两个区已选的数量 */
    attZones() {
      const used = {};
      ATT_TYPES.forEach(t => { used[t] = attUsed(this.atts[t]); });
      const total = ATT_TYPES.reduce((n, t) => n + used[t], 0);
      return ATT_TYPES.map(t => ({
        type: t,
        label: ATTACHMENT_TYPE[t].label,
        items: this.atts[t],
        limit: Math.max(0, MAX_COUNT - (total - used[t])),
      }));
    },
    uploading() { return attUploading(this.allAtts); },
    dirty() {
      const e = emptyForm();
      const f = this.f;
      if (this.allAtts.length > 0) return true;
      // 设备：任一行任一项非空即为已填写
      if ((f.devices || []).some(d => trim(d.name) || trim(d.sn) || trim(d.model))) return true;
      return Object.keys(e).some(k => k !== 'faultDate' && k !== 'devices' && f[k] !== e[k]);
    },
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

    // ------------------------------------------------ 设备资料
    addDevice() {
      if (!this.canAddDevice) return;
      this.f.devices.push(emptyDevice());
      // 光标移到新加的那一行（弹窗经 Teleport 渲染，直接按类名找）
      this.$nextTick(() => {
        const rows = document.querySelectorAll('.orders-create .dev-table tbody tr');
        const input = rows.length ? rows[rows.length - 1].querySelector('input') : null;
        if (input) input.focus();
      });
    },
    /** 删除一台设备；只剩一行时 = 清空该行 */
    removeDevice(i) {
      const list = this.f.devices;
      if (list.length <= 1) { this.f.devices = [emptyDevice()]; return; }
      list.splice(i, 1);
    },
    /** 选择是否已完成 SAT；在「已完成 / 未完成」之间切换时清空已填的时间（两个时间含义不同）。第一次选择不清空：先填了时间再选的不用重填 */
    setSat(v) {
      const prev = this.f.satSigned;
      if ((prev === true || prev === false) && prev !== v) { this.f.satDate = ''; this.clearErr('satDate'); }
      this.set('satSigned', v);
    },

    // ------------------------------------------------ 附件
    onAtts(type, list) { this.atts[type] = list; this.clearErr('attachments'); },

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
      req('projectNo', '请填写项目编号');
      // 设备、时间都是选填；satSigned 是布尔值，「未完成」（false）也算已选
      if (f.satSigned !== true && f.satSigned !== false) e.satSigned = '请选择是否已完成 SAT';
      if (trim(f.satDate) && !DATE_RE.test(trim(f.satDate))) e.satDate = (f.satSigned === false ? '发货时间' : 'SAT 完成时间') + '格式应为 yyyy-MM-dd';
      if (!f.faultDate) e.faultTime = '请选择发生日期';
      else if (!f.faultPeriod) e.faultTime = '请选择发生时段';
      req('symptom', '请按客户原话填写故障现象/报错');
      req('customerTried', '请填写客户已尝试的操作');
      req('siteEnvironment', '请填写现场环境条件');
      if (attHasError(this.allAtts)) e.attachments = '有附件未上传成功，请重试或移除';
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
        projectNo: trim(f.projectNo),
        // 整行不填的设备直接忽略；一台都没有时传 []（与旧客户端「不传 devices」含义不同）
        devices: (f.devices || []).map(d => ({ name: trim(d.name), sn: trim(d.sn), model: trim(d.model) }))
          .filter(d => d.name || d.sn || d.model),
        satSigned: f.satSigned, satDate: trim(f.satDate),
        faultTime: this.faultTimeText,
        symptom: trim(f.symptom), customerTried: trim(f.customerTried), siteEnvironment: trim(f.siteEnvironment),
        customerRequest: opt(f.customerRequest),
        attachments: attDone(this.allAtts),
      };
    },
    async submit() {
      if (this.saving) return;
      this.submitError = '';
      if (this.uploading) { this.submitError = '附件仍在上传，请稍候'; return; }
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

      <div class="form-section-title">设备资料</div>
      <label class="field span-2"><span class="field-label">项目编号<span class="req">*</span></span>
        <input class="input" :class="{ invalid: errors.projectNo }" v-model="f.projectNo" maxlength="50" placeholder="必填"
               @input="clearErr('projectNo')">
        <span v-if="errors.projectNo" class="field-error">{{ errors.projectNo }}</span></label>
      <div class="field span-2"><span class="field-label">设备（选填，可添加多台）</span>
        <table class="dev-table">
          <thead>
            <tr><th>设备名称</th><th>设备编号</th><th>设备型号</th><th class="dev-col-act"><span class="dev-sr">操作</span></th></tr>
          </thead>
          <tbody>
            <tr v-for="(dv, i) in f.devices" :key="dv.k">
              <td><input class="input" v-model="dv.name" maxlength="100" placeholder="如 高压反应釜" :aria-label="'设备 ' + (i + 1) + ' 名称'"></td>
              <td><input class="input mono" v-model="dv.sn" maxlength="100" placeholder="铭牌上的序列号" :aria-label="'设备 ' + (i + 1) + ' 编号'"></td>
              <td><input class="input" v-model="dv.model" maxlength="100" placeholder="如 HTR-500" :aria-label="'设备 ' + (i + 1) + ' 型号'"></td>
              <td class="dev-col-act"><button type="button" class="btn-link att-link" :aria-label="'删除设备 ' + (i + 1)" @click="removeDevice(i)">删除</button></td>
            </tr>
          </tbody>
        </table>
        <div class="dev-add">
          <button type="button" class="btn btn-secondary btn-sm" :disabled="!canAddDevice" :title="canAddDevice ? null : '设备最多 ' + maxDevices + ' 台'"
                  @click="addDevice">+ 添加设备</button>
        </div>
        <span class="field-hint">一个项目多台设备同时维修时逐台添加；整行不填的会被忽略。</span>
      </div>
      <div class="field"><span class="field-label">是否已完成 SAT<span class="req">*</span></span>
        <div class="radio-group" role="radiogroup" aria-label="是否已完成 SAT">
          <button type="button" role="radio" class="radio-chip" :class="{ active: f.satSigned === true }"
                  :aria-checked="f.satSigned === true ? 'true' : 'false'" @click="setSat(true)">已完成（老产品）</button>
          <button type="button" role="radio" class="radio-chip" :class="{ active: f.satSigned === false }"
                  :aria-checked="f.satSigned === false ? 'true' : 'false'" @click="setSat(false)">未完成（新产品）</button>
        </div>
        <span v-if="errors.satSigned" class="field-error">{{ errors.satSigned }}</span></div>
      <label class="field"><span class="field-label">{{ satDateLabel }}<em>（选填）</em></span>
        <input class="input" type="date" max="9999-12-31" :class="{ invalid: errors.satDate }" v-model="f.satDate" @input="clearErr('satDate')">
        <span v-if="errors.satDate" class="field-error">{{ errors.satDate }}</span></label>

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

      <div class="form-section-title">附件</div>
      <div class="field span-2"><span class="field-label">附件（照片 / 视频 / 文件，选填，三类合计最多 {{ maxAtts }} 个）</span>
        <div v-for="z in attZones" :key="z.type" class="att-zone">
          <div class="att-zone-title">{{ z.label }}</div>
          <AttachmentsUpload :model-value="z.items" :type="z.type" :limit="z.limit" :can-restrict="true" :disabled="saving"
                             @update:model-value="onAtts(z.type, $event)" />
        </div>
        <span v-if="errors.attachments" class="field-error">{{ errors.attachments }}</span>
        <span class="field-hint">支持图片、视频、Word、Excel、PPT、PDF、TXT、CSV；图片单个不超过 20MB，其他不超过 100MB。服务价格不在建单时填写，建单后由主管在工单详情中定价。</span>
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
