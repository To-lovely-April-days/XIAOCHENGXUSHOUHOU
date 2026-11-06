/**
 * 定位工具（GCJ-02）。没有地图 key，不做逆地址解析：打卡地址用工单地址，建单地址用 wx.chooseLocation 结果。
 */

function isAuthDeny(err) {
  const msg = (err && err.errMsg) || '';
  return /auth deny|auth denied|authorize no response|permission/i.test(msg) && !/system/i.test(msg);
}

function isSystemDeny(err) {
  const msg = (err && err.errMsg) || '';
  return /system permission denied|location service|ERROR_NOCELL|ERROR_NETWORK|定位服务/i.test(msg) || (err && (err.errCode === 2 || err.errno === 1505001));
}

/** 引导用户去设置页打开位置权限 @returns {Promise<boolean>} 是否已授权 */
function guideToSetting(content) {
  return new Promise((resolve) => {
    wx.showModal({
      title: '需要位置权限',
      content: content || '现场打卡需要获取你的位置，用于计算与客户地址的距离。请在设置中允许使用位置信息。',
      confirmText: '去设置',
      confirmColor: '#1D1D1F',
      success(r) {
        if (!r.confirm) return resolve(false);
        wx.openSetting({
          success: (s) => resolve(!!(s.authSetting && s.authSetting['scope.userLocation'])),
          fail: () => resolve(false)
        });
      },
      fail: () => resolve(false)
    });
  });
}

function rawGetLocation() {
  return new Promise((resolve, reject) => {
    wx.getLocation({
      type: 'gcj02',
      isHighAccuracy: true,
      highAccuracyExpireTime: 4000,
      success: (r) => resolve({ latitude: r.latitude, longitude: r.longitude, accuracy: r.accuracy }),
      fail: reject
    });
  });
}

/**
 * 获取当前位置（gcj02）。授权被拒时弹窗引导 wx.openSetting，授权后自动重试一次。
 * @param {{silent?:boolean}} [opts] silent：失败时不弹 toast
 * @returns {Promise<{latitude:number, longitude:number, accuracy:number}>}
 *   失败 reject {code:'AUTH_DENY'|'SYSTEM_DENY'|'FAIL', message}
 */
function getLocation(opts) {
  opts = opts || {};
  return rawGetLocation().catch((err) => {
    if (isSystemDeny(err)) {
      const e = { code: 'SYSTEM_DENY', message: '请在手机系统设置中打开定位服务，并允许微信使用位置', data: err };
      if (!opts.silent) wx.showToast({ title: e.message, icon: 'none', duration: 3000 });
      return Promise.reject(e);
    }
    if (isAuthDeny(err)) {
      return guideToSetting().then((ok) => {
        if (ok) return rawGetLocation();
        const e = { code: 'AUTH_DENY', message: '未授权位置信息，无法打卡', data: err };
        if (!opts.silent) wx.showToast({ title: e.message, icon: 'none' });
        return Promise.reject(e);
      });
    }
    const e = { code: 'FAIL', message: '定位失败，请稍后重试', data: err };
    if (!opts.silent) wx.showToast({ title: e.message, icon: 'none' });
    return Promise.reject(e);
  });
}

/**
 * 地图选点（建单客户地址）。
 * @param {{latitude?:number, longitude?:number}} [init] 初始中心点
 * @returns {Promise<{name:string, address:string, latitude:number, longitude:number}>} 用户取消 reject {code:'CANCEL'}
 */
function chooseLocation(init) {
  const params = {};
  if (init && init.latitude && init.longitude) {
    params.latitude = init.latitude;
    params.longitude = init.longitude;
  }
  return new Promise((resolve, reject) => {
    wx.chooseLocation(Object.assign(params, {
      success: (r) => resolve({ name: r.name || '', address: r.address || '', latitude: r.latitude, longitude: r.longitude }),
      fail: (err) => {
        const msg = (err && err.errMsg) || '';
        if (/cancel/.test(msg)) return reject({ code: 'CANCEL', message: '已取消' });
        if (isAuthDeny(err)) {
          return guideToSetting('选择客户地址需要使用位置信息，请在设置中允许。').then(() => reject({ code: 'AUTH_DENY', message: '未授权位置信息' }));
        }
        wx.showToast({ title: '打开地图失败', icon: 'none' });
        reject({ code: 'FAIL', message: '打开地图失败', data: err });
      }
    }));
  });
}

/**
 * Haversine 距离（米），用于打卡页本地预估距离；最终以服务器计算为准。
 * @returns {number}
 */
function distanceMeters(lat1, lng1, lat2, lng2) {
  if ([lat1, lng1, lat2, lng2].some((v) => v === null || v === undefined || isNaN(v))) return NaN;
  const R = 6371000;
  const rad = Math.PI / 180;
  const dLat = (lat2 - lat1) * rad;
  const dLng = (lng2 - lng1) * rad;
  const a = Math.sin(dLat / 2) * Math.sin(dLat / 2) +
    Math.cos(lat1 * rad) * Math.cos(lat2 * rad) * Math.sin(dLng / 2) * Math.sin(dLng / 2);
  return 2 * R * Math.atan2(Math.sqrt(a), Math.sqrt(1 - a));
}

/** 坐标显示：'N 31.30° · E 120.72°' */
function formatCoords(latitude, longitude, digits) {
  const d = digits === undefined ? 2 : digits;
  if (latitude === null || latitude === undefined || longitude === null || longitude === undefined) return '';
  const ns = latitude >= 0 ? 'N' : 'S';
  const ew = longitude >= 0 ? 'E' : 'W';
  return ns + ' ' + Math.abs(latitude).toFixed(d) + '° · ' + ew + ' ' + Math.abs(longitude).toFixed(d) + '°';
}

module.exports = {
  getLocation,
  chooseLocation,
  distanceMeters,
  formatCoords,
  guideToSetting
};
