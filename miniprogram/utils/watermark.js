/**
 * 打卡照片水印（离屏 canvas 合成）。
 *
 * composeWatermark(tempFilePath, [
 *   '到场 10:05',                         // 第一行大字
 *   '2026.11.06 周五',
 *   '江苏省苏州市工业园区星湖街…',
 *   'N 31.30° · E 120.72°',
 *   '李工 · SH-20261105-003'
 * ]).then((path) => api.files.upload(path, 'PunchPhoto'))
 *
 * 打卡照片必须带水印（DESIGN §4.2）：合成失败时 reject {code:'WATERMARK_FAILED', message:'照片处理失败，请重拍'}，
 * 调用方不得上传原图。canvasToTempFilePath 不支持离屏 canvas 时会退回 toDataURL + 写临时文件。
 */

function getImageInfo(src) {
  return new Promise((resolve, reject) => {
    wx.getImageInfo({ src: src, success: resolve, fail: reject });
  });
}

function loadImage(canvas, src) {
  return new Promise((resolve, reject) => {
    const img = canvas.createImage();
    img.onload = () => resolve(img);
    img.onerror = reject;
    img.src = src;
  });
}

/** 按宽度折行（中文逐字） */
function wrapText(ctx, text, maxWidth, maxLines) {
  const out = [];
  let line = '';
  for (let i = 0; i < text.length; i++) {
    const next = line + text[i];
    if (ctx.measureText(next).width > maxWidth && line) {
      out.push(line);
      line = text[i];
      if (out.length === maxLines) break;
    } else {
      line = next;
    }
  }
  if (out.length < maxLines && line) out.push(line);
  if (out.length === maxLines && out.join('').length < text.length) {
    let last = out[maxLines - 1];
    while (last && ctx.measureText(last + '…').width > maxWidth) last = last.slice(0, -1);
    out[maxLines - 1] = last + '…';
  }
  return out;
}

function roundRect(ctx, x, y, w, h, r) {
  ctx.beginPath();
  ctx.moveTo(x + r, y);
  ctx.lineTo(x + w - r, y);
  ctx.arcTo(x + w, y, x + w, y + r, r);
  ctx.lineTo(x + w, y + h - r);
  ctx.arcTo(x + w, y + h, x + w - r, y + h, r);
  ctx.lineTo(x + r, y + h);
  ctx.arcTo(x, y + h, x, y + h - r, r);
  ctx.lineTo(x, y + r);
  ctx.arcTo(x, y, x + r, y, r);
  ctx.closePath();
}

/**
 * 在图片左下角绘制半透明黑底白字多行水印。
 * @param {string} tempFilePath 原图（wx.chooseMedia / camera 拍照结果）
 * @param {string[]} lines 第一行为大字标题（如 '到场 10:05'），其余为小字
 * @param {{maxSide?:number, quality?:number, accent?:string}} [opts]
 *   maxSide 输出最长边（默认 1600）；quality jpg 质量（默认 0.8）；accent 第一行左侧色块颜色（默认品牌红，传 '' 不画）
 * @returns {Promise<string>} 合成后的 tempFilePath；失败时 reject（WATERMARK_FAILED）
 */
function composeWatermark(tempFilePath, lines, opts) {
  opts = opts || {};
  const maxSide = opts.maxSide || 1600;
  const quality = opts.quality || 0.8;
  const accent = opts.accent === undefined ? '#C81E2B' : opts.accent;
  lines = (lines || []).filter((l) => l !== null && l !== undefined && l !== '');

  return getImageInfo(tempFilePath).then((info) => {
    const scale = Math.min(1, maxSide / Math.max(info.width, info.height));
    const W = Math.round(info.width * scale);
    const H = Math.round(info.height * scale);
    const canvas = wx.createOffscreenCanvas({ type: '2d', width: W, height: H });
    const ctx = canvas.getContext('2d');

    return loadImage(canvas, tempFilePath).then((img) => {
      ctx.drawImage(img, 0, 0, W, H);
      if (!lines.length) return canvas;

      const base = Math.round(Math.min(W, H) * 0.034); // 小字字号
      const big = Math.round(base * 1.9);
      const pad = Math.round(base * 0.9);
      const gap = Math.round(base * 0.45);
      const margin = Math.round(Math.min(W, H) * 0.035);
      const boxMaxW = Math.round(W * 0.78);
      const textMaxW = boxMaxW - pad * 2;
      const font = (size, weight) => weight + ' ' + size + 'px "PingFang SC", "Helvetica Neue", sans-serif';

      // 预排版
      const rows = [];
      ctx.font = font(big, 700);
      rows.push({ text: lines[0], size: big, weight: 700 });
      ctx.font = font(base, 400);
      lines.slice(1).forEach((l) => {
        wrapText(ctx, String(l), textMaxW, 2).forEach((t) => rows.push({ text: t, size: base, weight: 400 }));
      });

      let contentW = 0;
      let contentH = 0;
      rows.forEach((r, i) => {
        ctx.font = font(r.size, r.weight);
        contentW = Math.max(contentW, ctx.measureText(r.text).width + (i === 0 && accent ? big * 0.35 : 0));
        contentH += r.size * 1.25 + (i ? gap * 0.4 : 0);
      });
      contentH += gap; // 标题下额外间距

      const boxW = Math.min(boxMaxW, Math.ceil(contentW) + pad * 2);
      const boxH = Math.ceil(contentH) + pad * 2;
      const x0 = margin;
      const y0 = H - margin - boxH;

      ctx.fillStyle = 'rgba(0,0,0,0.45)';
      roundRect(ctx, x0, y0, boxW, boxH, Math.round(base * 0.6));
      ctx.fill();

      ctx.textBaseline = 'top';
      ctx.fillStyle = '#FFFFFF';
      let y = y0 + pad;
      rows.forEach((r, i) => {
        ctx.font = font(r.size, r.weight);
        let x = x0 + pad;
        if (i === 0 && accent) {
          ctx.fillStyle = accent;
          ctx.fillRect(x, y + r.size * 0.12, Math.max(4, Math.round(big * 0.12)), r.size * 1.0);
          ctx.fillStyle = '#FFFFFF';
          x += big * 0.35;
        }
        ctx.fillText(r.text, x, y);
        y += r.size * 1.25 + (i === 0 ? gap : gap * 0.4);
      });
      return canvas;
    });
  }).then((canvas) => new Promise((resolve, reject) => {
    wx.canvasToTempFilePath({
      canvas: canvas,
      fileType: 'jpg',
      quality: quality,
      success: (res) => resolve(res.tempFilePath),
      fail: (err) => {
        // 部分基础库/机型不支持导出离屏 canvas：退回 toDataURL 写临时文件
        dataUrlToFile(canvas, quality).then(resolve, () => reject(err));
      }
    });
  })).catch((err) => {
    console.warn('[watermark] 合成失败', err);
    return Promise.reject({ code: 'WATERMARK_FAILED', message: '照片处理失败，请重拍', data: err });
  });
}

/** 离屏 canvas → dataURL → USER_DATA_PATH 下的临时 jpg */
function dataUrlToFile(canvas, quality) {
  return new Promise((resolve, reject) => {
    try {
      const url = canvas.toDataURL('image/jpeg', quality);
      const m = /^data:image\/\w+;base64,(.+)$/.exec(url || '');
      if (!m) { reject(new Error('toDataURL 失败')); return; }
      const path = wx.env.USER_DATA_PATH + '/wm_' + Date.now() + '_' + Math.floor(Math.random() * 1e6) + '.jpg';
      wx.getFileSystemManager().writeFile({
        filePath: path,
        data: m[1],
        encoding: 'base64',
        success: () => resolve(path),
        fail: reject
      });
    } catch (e) {
      reject(e);
    }
  });
}

module.exports = {
  composeWatermark
};
