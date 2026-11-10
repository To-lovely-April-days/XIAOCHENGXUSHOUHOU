#!/usr/bin/env bash
# 在开发机上打包：生成 dist/huotong-aftersales-linux-x64.tar.gz（自带 .NET 运行时，服务器无需安装 .NET）
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/dist/huotong-aftersales"
export PATH="$HOME/.dotnet:$PATH"

rm -rf "$OUT" && mkdir -p "$OUT"
dotnet publish "$ROOT/server/src/Huotong.AfterSales.Api" -c Release -r linux-x64 --self-contained true -o "$OUT" -v q
rm -f "$OUT"/appsettings.Development.json "$OUT"/*.pdb
cp "$ROOT/deploy/huotong-aftersales.service" "$ROOT/deploy/huotong-aftersales.env.example" "$ROOT/deploy/install.sh" "$OUT/"

COPYFILE_DISABLE=1 tar --no-xattrs -C "$ROOT/dist" -czf "$ROOT/dist/huotong-aftersales-linux-x64.tar.gz" huotong-aftersales
echo "已生成 $ROOT/dist/huotong-aftersales-linux-x64.tar.gz"
