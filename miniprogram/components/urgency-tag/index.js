/**
 * 紧急程度标签。Normal（一般）默认不显示，传 showNormal 才显示。
 * 属性：urgency {Urgency}，showNormal {boolean}，size {'md'|'sm'} 默认 sm（高 20px）
 */
const enums = require('../../utils/enums.js');

Component({
  options: { addGlobalClass: true },
  properties: {
    urgency: { type: String, value: '' },
    showNormal: { type: Boolean, value: false },
    size: { type: String, value: 'sm' }
  },
  data: { visible: false, label: '', bg: '', fg: '' },
  observers: {
    'urgency, showNormal': function (urgency, showNormal) {
      const u = enums.URGENCY[urgency];
      const visible = !!u && (urgency !== 'Normal' || showNormal);
      this.setData({ visible: visible, label: u ? u.label : '', bg: u ? u.bg : '', fg: u ? u.fg : '' });
    }
  }
});
