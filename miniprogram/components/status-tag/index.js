/**
 * 工单状态标签。
 * 属性：status {OrderStatus}，size {'md'|'sm'} 默认 md（高 22px），text 覆盖文案（可选）
 */
const enums = require('../../utils/enums.js');

Component({
  options: { addGlobalClass: true },
  properties: {
    status: { type: String, value: '' },
    size: { type: String, value: 'md' },
    text: { type: String, value: '' }
  },
  data: { label: '', bg: '#F0F0EE', fg: '#3A3A3C' },
  observers: {
    'status, text': function (status, text) {
      const s = enums.STATUS[status] || { label: status, bg: '#F0F0EE', fg: '#3A3A3C' };
      this.setData({ label: text || s.label, bg: s.bg, fg: s.fg });
    }
  }
});
