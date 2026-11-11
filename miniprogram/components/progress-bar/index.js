/**
 * 工单 5 段进度条（待认领 / 待启动 / 处理中 / 待验收 / 已闭环），v1.1。
 *
 * <progress-bar order="{{order}}" />
 * <progress-bar order="{{order}}" labels="{{true}}" />
 *
 * 属性：
 *   order     {OrderListItem} 用 progressStep、status、holdText（一律用服务端的 progressStep，不做状态→段的本地映射）
 *   labels    {boolean} 是否在每段下方显示阶段名称，默认 false（列表用细条）
 *   showText  {boolean} 暂停时是否在下方显示「暂停 · {holdText}」，默认 true
 * 暂停搁置：第 progressStep 段显示为灰色斜纹 + 暂停标记，其之前的段正常点亮。
 */
const enums = require('../../utils/enums.js');

Component({
  options: { addGlobalClass: true },
  properties: {
    order: { type: Object, value: null },
    labels: { type: Boolean, value: false },
    showText: { type: Boolean, value: true }
  },
  data: { segs: [], paused: false, holdLine: '', aria: '' },
  observers: {
    order(order) {
      const o = order || {};
      const step = enums.progressStepOf(o);
      const paused = o.status === 'OnHold';
      const segs = enums.progressSegments(o).map((s, i) => ({
        i: i,
        on: s.on,
        paused: s.paused,
        current: !s.paused && i + 1 === step,
        label: s.paused ? '暂停' : enums.PROGRESS_LABELS[i]
      }));
      const holdText = enums.holdTextOf(o);
      const stepName = step ? enums.PROGRESS_LABELS[step - 1] : enums.label(enums.STATUS, o.status, '');
      this.setData({
        segs: segs,
        paused: paused,
        holdLine: paused ? '暂停' + (holdText ? ' · ' + holdText : '') : '',
        aria: '进度：' + (paused ? '暂停搁置（暂停于' + stepName + '）' + (holdText ? '，' + holdText : '') : stepName) +
          '，5 个阶段中的第 ' + step + ' 个'
      });
    }
  }
});
