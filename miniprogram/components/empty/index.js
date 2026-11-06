/**
 * 空状态（白色卡片 + 居中灰字，参照原型「该状态下暂无工单」）。
 * 属性：text {string} 默认「暂无数据」；desc {string} 次要说明；card {boolean} 是否白卡底，默认 true
 * 插槽：默认插槽放按钮等操作
 */
Component({
  options: { addGlobalClass: true },
  properties: {
    text: { type: String, value: '暂无数据' },
    desc: { type: String, value: '' },
    card: { type: Boolean, value: true }
  }
});
