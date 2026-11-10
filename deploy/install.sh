#!/usr/bin/env bash
# 在服务器上以 root 运行：首次安装或更新。
#   tar -xzf huotong-aftersales-linux-x64.tar.gz -C /tmp && bash /tmp/huotong-aftersales/install.sh
# 数据库 data/ 和上传文件 uploads/ 在更新时保留。
set -euo pipefail
SRC="$(cd "$(dirname "$0")" && pwd)"
APP=/www/wwwroot/huotong-aftersales
ENV=/etc/huotong-aftersales.env

# .NET 依赖的 ICU 库
if ! ldconfig -p | grep -q libicuuc; then
  (command -v yum >/dev/null && yum install -y libicu) || (command -v apt-get >/dev/null && apt-get install -y libicu-dev) || true
fi

mkdir -p "$APP/data" "$APP/uploads"
systemctl stop huotong-aftersales 2>/dev/null || true
# 只覆盖程序文件，不动 data/ uploads/
( cd "$SRC" && tar --exclude=./data --exclude=./uploads -cf - . ) | ( cd "$APP" && tar -xf - )
chmod +x "$APP/Huotong.AfterSales.Api"

if [ ! -f "$ENV" ]; then
  cp "$SRC/huotong-aftersales.env.example" "$ENV"
  chmod 600 "$ENV"
  echo "已创建 $ENV ，请填写 AppSecret / Jwt__Key / 初始主管 后重新运行本脚本。"
  exit 1
fi

cp "$SRC/huotong-aftersales.service" /etc/systemd/system/huotong-aftersales.service
systemctl daemon-reload
systemctl enable --now huotong-aftersales
sleep 3
systemctl --no-pager --lines=20 status huotong-aftersales || true
curl -fsS -o /dev/null -w "本机自检 /admin/ → HTTP %{http_code}\n" http://127.0.0.1:5080/admin/ || echo "本机自检失败，查看日志：journalctl -u huotong-aftersales -n 100"
