// 工时统计（#/hours）。数据：GET /api/admin/hours（API.md §9）；
// 单个工程师每日明细：GET /api/hours/month?year=&month=&engineerId=；导出：GET /api/admin/export/hours。
// 查询参数：?month=yyyy-MM&engineer=<id>（engineer 为打开明细抽屉的工程师）
import * as api from '../api.js';
import { route, navigate, updateQuery } from '../router.js';
import { toast, toastError } from '../ui.js';
import { pad2, fmtHM, fmtHours, fmtMD, fmtTime, fmtWeekday, toApiDate } from '../format.js';

const MONTH_RE = /^(\d{4})-(\d{1,2})$/;

/** 迷你日历格底色：按分钟线性加深（与小程序工时页热力图同一思路，品牌红透明度） */
function heatColor(minutes) {
  const a = Math.min(0.92, Math.max(0.2, 0.14 + minutes * 0.0016));
  return `rgba(200, 30, 43, ${a.toFixed(2)})`;
}

function currentYM(now = new Date()) {
  return { year: now.getFullYear(), month: now.getMonth() + 1 };
}

export default {
  name: 'HoursView',
  data() {
    const cur = currentYM();
    return {
      route,
      year: cur.year,
      month: cur.month,
      data: null,
      loading: false,
      error: '',
      exporting: false,
      // 明细抽屉
      detailId: null,
      detail: null,
      detailLoading: false,
      detailError: '',
      todayStr: toApiDate(new Date()),
    };
  },
  computed: {
    monthLabel() { return `${this.year}年${this.month}月`; },
    canNext() {
      const cur = currentYM();
      return this.year * 12 + this.month < cur.year * 12 + cur.month;
    },
    isCurrentMonth() {
      const cur = currentYM();
      return this.year === cur.year && this.month === cur.month;
    },
    engineers() { return (this.data && this.data.engineers) || []; },
    daysInMonth() { return new Date(this.year, this.month, 0).getDate(); },
    summary() {
      const list = this.engineers;
      const total = this.data ? this.data.totalMinutes || 0 : 0;
      return {
        totalMinutes: total,
        engineerCount: list.length,
        activeCount: list.filter(e => e.totalMinutes > 0).length,
        tripDays: list.reduce((s, e) => s + (e.tripDays || 0), 0),
        supplementPeople: list.filter(e => e.supplementCount > 0).length,
        supplementTimes: list.reduce((s, e) => s + (e.supplementCount || 0), 0),
        missingPeople: list.filter(e => e.missingCount > 0).length,
        missingTimes: list.reduce((s, e) => s + (e.missingCount || 0), 0),
        runningPeople: list.filter(e => e.runningNow).length,
      };
    },
    rows() {
      const prefix = `${this.year}-${pad2(this.month)}-`;
      return this.engineers.map(e => {
        const byDate = {};
        (e.days || []).forEach(d => { byDate[String(d.date).slice(0, 10)] = d.minutes || 0; });
        const cells = [];
        for (let n = 1; n <= this.daysInMonth; n++) {
          const ds = prefix + pad2(n);
          const has = Object.prototype.hasOwnProperty.call(byDate, ds);
          const minutes = has ? byDate[ds] : 0;
          const future = ds > this.todayStr;
          let bg = 'transparent';
          if (minutes > 0) bg = heatColor(minutes);
          else if (has) bg = '#E4E4E7';          // 有打卡但无已配对工时（如缺卡、计时中）
          else if (!future) bg = '#F1F1F3';
          const wd = new Date(this.year, this.month - 1, n).getDay();
          cells.push({
            ds, n, minutes, bg, future,
            today: ds === this.todayStr,
            weekend: wd === 0 || wd === 6,
            title: `${this.month}月${n}日 ${fmtWeekday(ds)} · ` + (minutes > 0 ? fmtHM(minutes) : (ds === this.todayStr && e.runningNow) ? '现场计时中' : has ? '有打卡，无已配对工时' : future ? '—' : '无工时'),
          });
        }
        return { ...e, id: e.engineer && e.engineer.id, cells, workDays: (e.days || []).filter(d => d.minutes > 0).length };
      });
    },
    detailRow() { return this.rows.find(r => String(r.id) === String(this.detailId)) || null; },
    detailDays() {
      const days = (this.detail && this.detail.days) || [];
      return days.slice().sort((a, b) => (a.date < b.date ? 1 : a.date > b.date ? -1 : 0)).map(d => {
        const entries = (d.entries || []).map(x => ({
          ...x,
          arrive: fmtTime(x.arriveTime),
          leave: x.leaveTime ? fmtTime(x.leaveTime) : '',
          state: x.missing ? 'missing' : (!x.leaveTime ? 'running' : ''),
        }));
        return {
          ...d,
          dateText: fmtMD(d.date),
          weekday: fmtWeekday(d.date),
          entries,
          hasMissing: entries.some(x => x.missing),
        };
      });
    },
  },
  watch: {
    'route.query': {
      handler(q) {
        if (route.path !== '/hours') return;
        const cur = currentYM();
        let y = cur.year;
        let m = cur.month;
        const mm = MONTH_RE.exec(q.month || '');
        if (mm && +mm[2] >= 1 && +mm[2] <= 12 && (+mm[1]) * 12 + (+mm[2]) <= cur.year * 12 + cur.month) {
          y = +mm[1];
          m = +mm[2];
        }
        const monthChanged = y !== this.year || m !== this.month || !this.data;
        this.year = y;
        this.month = m;
        const eid = /^\d+$/.test(q.engineer || '') ? q.engineer : null;
        if (monthChanged) this.load();
        if (eid !== this.detailId || (eid && monthChanged)) {
          this.detailId = eid;
          this.loadDetail();
        }
      },
      deep: true,
      immediate: true,
    },
  },
  methods: {
    async load() {
      const seq = (this._seq = (this._seq || 0) + 1);
      this.loading = true;
      this.error = '';
      this.todayStr = toApiDate(new Date());
      try {
        const data = await api.admin.hours(this.year, this.month);
        if (seq !== this._seq) return;
        this.data = data || { totalMinutes: 0, engineers: [] };
      } catch (e) {
        if (seq !== this._seq) return;
        this.data = null;
        this.error = e.message || '加载失败';
        toastError(e);
      } finally {
        if (seq === this._seq) this.loading = false;
      }
    },
    async loadDetail() {
      const seq = (this._dseq = (this._dseq || 0) + 1);
      this.detail = null;
      this.detailError = '';
      if (!this.detailId) { this.detailLoading = false; return; }
      this.detailLoading = true;
      try {
        const d = await api.hours.month(this.year, this.month, Number(this.detailId));
        if (seq !== this._dseq) return;
        this.detail = d;
      } catch (e) {
        if (seq !== this._dseq) return;
        this.detailError = e.message || '加载失败';
        toastError(e);
      } finally {
        if (seq === this._dseq) this.detailLoading = false;
      }
    },
    reload() {
      this.load();
      if (this.detailId) this.loadDetail();
    },
    shiftMonth(delta) {
      if (delta > 0 && !this.canNext) return;
      const d = new Date(this.year, this.month - 1 + delta, 1);
      const cur = currentYM();
      const isCur = d.getFullYear() === cur.year && d.getMonth() + 1 === cur.month;
      updateQuery({ month: isCur ? null : `${d.getFullYear()}-${pad2(d.getMonth() + 1)}` });
    },
    openDetail(row) {
      if (!row.id) return;
      updateQuery({ engineer: String(row.id) === String(this.detailId) ? null : row.id });
    },
    closeDetail() { updateQuery({ engineer: null }); },
    openOrder(no) { if (no) navigate('/orders', { keyword: no }); },
    async doExport() {
      if (this.exporting) return;
      this.exporting = true;
      try {
        await api.admin.exportHours(this.year, this.month);
        toast(`已导出 ${this.monthLabel} 工时`, 'success');
      } catch (e) {
        toastError(e);
      } finally {
        this.exporting = false;
      }
    },
    fmtHM,
    fmtHours,
  },
  template: `
  <PageHeader :crumbs="['工时统计', monthLabel]">
    <div class="hours-month" role="group" aria-label="切换月份">
      <button type="button" class="icon-btn" aria-label="上个月" @click="shiftMonth(-1)"><Icon name="chevronLeft" :size="16" /></button>
      <span class="hours-month-label num">{{ monthLabel }}</span>
      <button type="button" class="icon-btn" aria-label="下个月" :disabled="!canNext" @click="shiftMonth(1)"><Icon name="chevronRight" :size="16" /></button>
    </div>
    <button type="button" class="btn btn-secondary" :disabled="exporting" @click="doExport">
      <span v-if="exporting" class="spinner"></span><Icon v-else name="download" :size="15" />{{ exporting ? '导出中…' : '导出工时' }}
    </button>
  </PageHeader>
  <div class="page">
    <div class="page-head">
      <div>
        <h1>工时统计</h1>
        <div class="page-sub">{{ monthLabel }}<template v-if="isCurrentMonth">（截至今天）</template> · 按「到场—离场」配对计算，现场计时中的时长不计入</div>
      </div>
      <div class="page-actions">
        <button type="button" class="btn btn-ghost btn-sm" :disabled="loading" @click="reload"><Icon name="refresh" :size="14" />刷新</button>
      </div>
    </div>

    <section class="stats" aria-label="本月合计">
      <div class="stat">
        <div class="stat-label">本月总工时</div>
        <div class="stat-main"><span class="stat-value">{{ data ? fmtHours(summary.totalMinutes) : '—' }}</span><span class="stat-note">小时</span></div>
      </div>
      <div class="stat">
        <div class="stat-label">工程师人数</div>
        <div class="stat-main"><span class="stat-value">{{ data ? summary.engineerCount : '—' }}</span><span class="stat-note">{{ data ? summary.activeCount + ' 人有工时' : '' }}</span></div>
      </div>
      <div class="stat">
        <div class="stat-label">出差人天</div>
        <div class="stat-main"><span class="stat-value">{{ data ? summary.tripDays : '—' }}</span><span class="stat-note">{{ data && summary.runningPeople ? summary.runningPeople + ' 人现场中' : '' }}</span></div>
      </div>
      <div class="stat">
        <div class="stat-label">有补录</div>
        <div class="stat-main"><span class="stat-value">{{ data ? summary.supplementPeople : '—' }}</span><span class="stat-note">{{ data ? '人 · 共 ' + summary.supplementTimes + ' 次' : '' }}</span></div>
      </div>
      <div class="stat">
        <div class="stat-label">有缺卡</div>
        <div class="stat-main"><span class="stat-value" :class="{ 'text-danger': summary.missingPeople > 0 }">{{ data ? summary.missingPeople : '—' }}</span>
          <span class="stat-note" :class="{ danger: summary.missingTimes > 0 }">{{ data ? (summary.missingTimes ? '人 · 共 ' + summary.missingTimes + ' 次待补录' : '人') : '' }}</span></div>
      </div>
    </section>

    <section class="card" aria-label="工程师工时">
      <table class="data-table hours-table">
        <colgroup>
          <col style="width: 168px"><col style="width: 124px"><col style="width: 76px"><col style="width: 64px">
          <col style="width: 64px"><col style="width: 64px"><col>
        </colgroup>
        <thead>
          <tr>
            <th>工程师</th><th class="text-right">本月工时</th><th class="text-right">出差天数</th><th class="text-right">工单数</th>
            <th class="text-right">补录</th><th class="text-right">缺卡</th>
            <th>
              <div class="hours-strip-head">
                <span>每日工时</span>
                <span class="hours-legend" aria-hidden="true">少<i style="background: rgba(200,30,43,.22)"></i><i style="background: rgba(200,30,43,.5)"></i><i style="background: rgba(200,30,43,.9)"></i>多</span>
              </div>
            </th>
          </tr>
        </thead>
        <tbody v-if="data && rows.length">
          <tr v-for="r in rows" :key="r.id" class="clickable" :class="{ selected: String(r.id) === String(detailId) }"
              tabindex="0" :aria-label="'查看 ' + (r.engineer && r.engineer.name) + ' 的每日明细'"
              @click="openDetail(r)" @keydown.enter="openDetail(r)">
            <td>
              <span class="cell-user">
                <Avatar :user="r.engineer" :size="28" />
                <span class="hours-name ellipsis">{{ r.engineer ? r.engineer.name : '—' }}</span>
                <span v-if="r.runningNow" class="hours-live" title="今天有未离场的到场打卡"><i></i>现场中</span>
              </span>
            </td>
            <td class="text-right">
              <div class="hours-total num" :class="{ 'text-faint': !r.totalMinutes }">{{ fmtHM(r.totalMinutes) }}</div>
              <div class="cell-sub num">{{ fmtHours(r.totalMinutes) }} 小时</div>
            </td>
            <td class="num text-right" :class="{ 'text-faint': !r.tripDays }">{{ r.tripDays }}</td>
            <td class="num text-right" :class="{ 'text-faint': !r.orderCount }">{{ r.orderCount }}</td>
            <td class="num text-right" :class="r.supplementCount ? 'text-warn' : 'text-faint'">{{ r.supplementCount }}</td>
            <td class="num text-right" :class="r.missingCount ? 'hours-missing' : 'text-faint'">{{ r.missingCount }}</td>
            <td>
              <div class="hours-strip" :style="{ gridTemplateColumns: 'repeat(' + daysInMonth + ', minmax(0, 1fr))' }">
                <span v-for="c in r.cells" :key="c.ds" class="hours-cell"
                      :class="{ 'hours-cell-today': c.today, 'hours-cell-future': c.future, 'hours-cell-weekend': c.weekend }"
                      :style="{ background: c.bg }" :title="c.title"></span>
              </div>
            </td>
          </tr>
        </tbody>
      </table>
      <Loading v-if="loading && !data" />
      <Empty v-else-if="error && !data" :text="'加载失败：' + error">
        <button type="button" class="btn btn-secondary btn-sm" @click="load"><Icon name="refresh" :size="14" />重试</button>
      </Empty>
      <Empty v-else-if="data && !rows.length" text="暂无工程师账号，可在「账号管理」中添加" />
      <div class="table-foot">
        <span>页面显示到分钟；导出时按小时保留 1 位小数。</span>
        <span v-if="data && rows.length">点击任一行查看该工程师当月每日明细</span>
      </div>
    </section>
  </div>

  <Drawer v-if="detailId" :width="420" label="工程师工时明细" @close="closeDetail">
    <template #head>
      <template v-if="detailRow">
        <Avatar :user="detailRow.engineer" :size="22" />
        <span class="hours-drawer-name">{{ detailRow.engineer.name }}</span>
        <span v-if="detailRow.runningNow" class="hours-live"><i></i>现场中</span>
      </template>
      <span v-else>工时明细</span>
    </template>

    <div class="drawer-title">{{ detailRow ? detailRow.engineer.name + ' · ' : '' }}{{ monthLabel }}</div>
    <div class="drawer-sub">每日工时明细，按到场日期归属</div>

    <dl class="kv">
      <div><dt>本月工时</dt><dd class="num">{{ detail ? fmtHM(detail.totalMinutes) : (detailRow ? fmtHM(detailRow.totalMinutes) : '—') }}
        <span class="hours-kv-sub">{{ fmtHours(detail ? detail.totalMinutes : (detailRow ? detailRow.totalMinutes : null)) }} 小时</span></dd></div>
      <div><dt>出差天数</dt><dd class="num">{{ detail ? detail.tripDays : (detailRow ? detailRow.tripDays : '—') }} 天</dd></div>
      <div v-if="detailRow"><dt>补录</dt><dd class="num" :class="{ 'text-warn': detailRow.supplementCount }">{{ detailRow.supplementCount }} 次</dd></div>
      <div v-if="detailRow"><dt>缺卡</dt><dd class="num" :class="{ 'text-danger': detailRow.missingCount }">{{ detailRow.missingCount }} 次</dd></div>
    </dl>

    <div v-if="detailRow && detailRow.missingCount" class="alert alert-danger">
      <div class="alert-row"><Icon name="alert" :size="15" :stroke="2" />
        <span>有 {{ detailRow.missingCount }} 次缺卡未补录，对应当天不计工时。可在工单详情中补录离场时间。</span></div>
    </div>

    <div class="section-title">每日明细</div>
    <Loading v-if="detailLoading" />
    <Empty v-else-if="detailError" :text="'加载失败：' + detailError">
      <button type="button" class="btn btn-secondary btn-sm" @click="loadDetail"><Icon name="refresh" :size="14" />重试</button>
    </Empty>
    <Empty v-else-if="detail && !detailDays.length" text="本月没有打卡记录" />
    <div v-else class="hours-days">
      <div v-for="d in detailDays" :key="d.date" class="hours-day">
        <div class="hours-day-head">
          <span class="hours-day-date">{{ d.dateText }} <span class="text-muted">{{ d.weekday }}</span></span>
          <span v-if="d.running" class="hours-live"><i></i>计时中</span>
          <span class="hours-day-total num" :class="{ 'text-faint': !d.minutes }">{{ fmtHM(d.minutes) }}</span>
        </div>
        <div v-for="(x, i) in d.entries" :key="i" class="hours-entry">
          <div class="hours-entry-main">
            <div class="hours-entry-title hours-entry-title-row">
              <span class="ellipsis" :title="x.customerName">{{ x.customerName || '—' }}</span>
              <span v-if="x.role === 'Assistant'" class="tag tag-neutral" title="该工程师在本单是协助人，工时计入本人">协助</span>
            </div>
            <div class="hours-entry-sub">
              <button v-if="x.orderNo" type="button" class="punches-order-link hours-order mono" @click="openOrder(x.orderNo)">{{ x.orderNo }}</button>
              <span class="num">{{ x.arrive }} – <template v-if="x.leave">{{ x.leave }}</template><span v-else-if="x.missing" class="text-danger">缺离场卡</span><span v-else class="text-muted">计时中</span></span>
            </div>
            <div v-if="x.isSupplement" class="hours-entry-note text-warn" :title="x.supplementReason || ''">主管补录{{ x.supplementReason ? ' · ' + x.supplementReason : '' }}</div>
            <div v-if="x.missing" class="hours-entry-note text-danger">缺卡未补录，当天不计工时</div>
          </div>
          <div class="hours-entry-side">
            <span class="hours-entry-dur num" :class="{ 'text-faint': !x.minutes }">{{ x.leave ? fmtHM(x.minutes) : '—' }}</span>
            <span v-if="x.isSupplement" class="tag tag-warn">补录</span>
            <span v-if="x.missing" class="tag tag-missing">缺卡</span>
          </div>
        </div>
      </div>
    </div>
  </Drawer>`,
};
