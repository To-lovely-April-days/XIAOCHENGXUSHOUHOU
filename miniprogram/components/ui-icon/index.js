/**
 * 线性图标。<ui-icon name="camera" color="#FFFFFF" size="24" stroke="2" />
 * 属性：name（见 utils/icons.js names）、color 默认 #1D1D1F、size 设计稿 px 默认 24、stroke 线宽默认 1.8
 */
const icons = require('../../utils/icons.js');

Component({
  options: { addGlobalClass: true },
  properties: {
    name: { type: String, value: '' },
    color: { type: String, value: '#1D1D1F' },
    size: { type: Number, value: 24 },
    stroke: { type: Number, value: 1.8 }
  },
  data: { src: '' },
  observers: {
    'name, color, stroke': function (name, color, stroke) {
      this.setData({ src: name ? icons.get(name, color, stroke) : '' });
    }
  }
});
