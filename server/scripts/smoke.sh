#!/usr/bin/env bash
# 冒烟测试：走通主流程。需先启动服务（Development，MockMode=true，Seed 开启）。
# 用法：BASE=http://127.0.0.1:5080 ./scripts/smoke.sh
set -euo pipefail

BASE="${BASE:-http://127.0.0.1:5080}"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
PASS=0
MAIN_PID=$$

ok()   { PASS=$((PASS + 1)); echo "  ✓ $*"; }
fail() { echo "  ✗ $*" >&2; kill -TERM "$MAIN_PID"; exit 1; }

# py '<python 表达式>' < json：d 为解析后的 JSON，表达式结果打印出来
py() { python3 -c "import sys,json; d=json.load(sys.stdin); r=($1); print(r if not isinstance(r,(dict,list)) else json.dumps(r,ensure_ascii=False))"; }

# req METHOD PATH TOKEN [JSON]  → 写 $TMP/body，返回 HTTP 状态到 $TMP/status
req() {
  local method="$1" path="$2" token="${3:-}" data="${4:-}"
  local args=(-s -o "$TMP/body" -w "%{http_code}" -X "$method" "$BASE$path")
  [ -n "$token" ] && args+=(-H "Authorization: Bearer $token")
  [ -n "$data" ] && args+=(-H "Content-Type: application/json" -d "$data")
  curl "${args[@]}" > "$TMP/status"
}

# call METHOD PATH TOKEN [JSON] → 断言 HTTP 200 且 code=0，输出 data
call() {
  req "$@"
  local st; st="$(cat "$TMP/status")"
  local code; code="$(py 'd.get("code")' < "$TMP/body" 2>/dev/null || echo "?")"
  if [ "$st" != "200" ] || [ "$code" != "0" ]; then
    echo "请求失败: $1 $2 → HTTP $st" >&2; cat "$TMP/body" >&2; echo >&2
    kill -TERM "$MAIN_PID"; exit 1
  fi
  py 'd["data"]' < "$TMP/body"
}

expect() { # expect 实际 期望 说明
  [ "$1" == "$2" ] && ok "$3" || fail "$3：期望 [$2]，实际 [$1]"
}

upload() { # upload TOKEN CATEGORY → fileId
  curl -s -X POST "$BASE/api/files" -H "Authorization: Bearer $1" \
    -F "file=@$TMP/p.png;type=image/png" -F "category=$2" | py 'd["data"]["id"]'
}

login() { call POST /api/auth/dev-login "" "{\"phone\":\"$1\"}" | py 'd["token"]'; }

# 1x1 PNG
python3 -c "import base64;open('$TMP/p.png','wb').write(base64.b64decode('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mN8/5+hHgAHggJ/PchI7wAAAABJRU5ErkJggg=='))"

echo "== 认证"
SUP=$(login 13900000001); ok "主管 dev-login"
ENG=$(login 13900000004); ok "工程师(王工) dev-login"
SALES=$(login 13900000010); ok "销售 dev-login"
LI=$(login 13900000000); ok "工程师(李工) dev-login"

req GET /api/me ""; expect "$(cat "$TMP/status")" "401" "未带 token → 401"
expect "$(py 'd["code"]' < "$TMP/body")" "40100" "401 包装体 code=40100"
req GET /api/orders "$ENG"; expect "$(cat "$TMP/status")" "403" "工程师查全部工单 → 403"
expect "$(py 'd["code"]' < "$TMP/body")" "40300" "403 包装体 code=40300"
req POST /api/auth/login "" '{"code":"smoke-unknown"}'
expect "$(py 'd["data"]["needBind"]' < "$TMP/body")" "True" "未绑定 openid → needBind"
TICKET=$(py 'd["data"]["bindTicket"]' < "$TMP/body")
req POST /api/auth/bind "" "{\"bindTicket\":\"$TICKET\",\"phoneCode\":\"13800009999\"}"
expect "$(py 'd["code"]' < "$TMP/body")" "40401" "未登记手机号绑定 → 40401"

echo "== GET /api/me"
expect "$(call GET /api/me "$SUP" | py 'd["user"]["role"]')" "Supervisor" "主管 me"
expect "$(call GET /api/me "$ENG" | py 'd["user"]["roleName"]')" "售后工程师" "工程师 me"
expect "$(call GET /api/me "$SALES" | py 'd["user"]["phoneMasked"]')" "139****0010" "销售 me（手机号脱敏）"

echo "== 工单大厅"
HALL=$(call GET "/api/orders/hall?filter=all" "$ENG")
echo "$HALL" | py 'd["counts"]' >/dev/null; ok "hall counts: $(echo "$HALL" | py 'd["counts"]')"

echo "== 新建现场工单"
PHOTO=$(upload "$SALES" OrderPhoto); ok "上传建单照片 id=$PHOTO"
NEW=$(call POST /api/orders "$SALES" "{\"source\":\"SalesReferral\",\"problemType\":\"Fault\",\"urgency\":\"Urgent\",\"serviceType\":\"Onsite\",
 \"customerName\":\"冒烟测试客户\",\"contactName\":\"张工\",\"contactPhone\":\"138 0000 0000\",\"address\":\"江苏省苏州市工业园区星湖街 1 号\",
 \"latitude\":31.30,\"longitude\":120.72,\"deviceModel\":\"高压反应釜 HTR-500\",\"deviceSn\":\"SN-SMOKE\",\"satDate\":\"2024-08\",\"satSigned\":true,
 \"projectNo\":null,\"faultTime\":\"11月4日 下午\",\"symptom\":\"升温到 180℃ 不再加热\",\"customerTried\":\"断电重启\",\"siteEnvironment\":\"通风橱内\",
 \"customerRequest\":null,\"price\":1800,\"photoFileIds\":[$PHOTO]}")
OID=$(echo "$NEW" | py 'd["id"]')
expect "$(echo "$NEW" | py 'd["status"]')" "Pending" "新建工单 $(echo "$NEW" | py 'd["orderNo"]') → Pending"
expect "$(echo "$NEW" | py 'd["city"]+"/"+d["region"]')" "苏州/苏州市工业园区" "地址解析 city/region"
expect "$(echo "$NEW" | py 'len(d["files"]["orderPhotos"])')" "1" "建单照片已关联"
expect "$(echo "$NEW" | py 'd["logs"][0]["text"]')" "王敏 建单 · 紧急" "建单状态记录"

echo "== 工程师认领"
D=$(call POST "/api/orders/$OID/claim" "$ENG")
expect "$(echo "$D" | py 'd["status"]')" "Accepted" "认领 → Accepted"
expect "$(echo "$D" | py '",".join(d["actions"])')" "appoint,checkin" "工程师 actions"
req POST "/api/orders/$OID/claim" "$LI"; expect "$(py 'd["message"]' < "$TMP/body")" "工单已被他人认领" "重复认领 → 409"

echo "== 打卡"
CTX=$(call GET "/api/punches/context?orderId=$OID" "$ENG")
expect "$(echo "$CTX" | py 'd["nextKind"]')" "Arrive" "context nextKind=Arrive"
expect "$(echo "$CTX" | py 'd["blockedReason"]')" "None" "无阻止"
req GET "/api/punches/context?orderId=$OID" "$LI"; expect "$(cat "$TMP/status")" "403" "非负责工程师 context → 403"
P1=$(upload "$ENG" PunchPhoto); ok "上传到场照片 id=$P1"
R=$(call POST /api/punches "$ENG" "{\"orderId\":$OID,\"kind\":\"Arrive\",\"latitude\":31.3005,\"longitude\":120.7202,\"address\":\"江苏省苏州市工业园区星湖街\",\"photoFileId\":$P1}")
expect "$(echo "$R" | py 'd["order"]["status"]')" "InProgress" "首次到场 → InProgress"
expect "$(echo "$R" | py 'd["punch"]["outOfRange"]')" "False" "距离在范围内（$(echo "$R" | py 'd["punch"]["distanceMeters"]') m）"
expect "$(call GET "/api/punches/context?orderId=$OID" "$ENG" | py 'd["nextKind"]')" "Leave" "下一次为离场"
P2=$(upload "$ENG" PunchPhoto)
req POST /api/punches "$ENG" "{\"orderId\":$OID,\"kind\":\"Arrive\",\"latitude\":31.30,\"longitude\":120.72,\"address\":\"x\",\"photoFileId\":$P2}"
expect "$(py 'd["message"]' < "$TMP/body")" "打卡状态已变化，请刷新" "kind 不一致 → 409"
req POST "/api/orders/$OID/complete" "$ENG" '{"faultCause":"a","solution":"b","runStatus":"Normal","processPhotoIds":[1],"signaturePhotoIds":[1]}'
expect "$(py 'd["message"]' < "$TMP/body")" "请先离场打卡" "计时中提交完工 → 请先离场打卡"
R=$(call POST /api/punches "$ENG" "{\"orderId\":$OID,\"kind\":\"Leave\",\"latitude\":31.32,\"longitude\":120.72,\"address\":\"江苏省苏州市工业园区\",\"photoFileId\":$P2}")
expect "$(echo "$R" | py 'd["punch"]["kind"]')" "Leave" "离场打卡"
expect "$(echo "$R" | py 'd["punch"]["outOfRange"]')" "True" "离场距离异常已标记（$(echo "$R" | py 'd["punch"]["distanceMeters"]') m）"
expect "$(echo "$R" | py 'd["order"]["onSiteSince"]')" "None" "离场后不在现场计时"

echo "== 完工提交"
PP=$(upload "$ENG" ProcessPhoto); SG=$(upload "$ENG" Signature)
D=$(call POST "/api/orders/$OID/complete" "$ENG" "{\"faultCause\":\"加热棒接线端子氧化\",\"solution\":\"更换接线端子并紧固\",\"partsReplaced\":\"接线端子 ×2\",\"runStatus\":\"Normal\",\"processPhotoIds\":[$PP],\"signaturePhotoIds\":[$SG]}")
expect "$(echo "$D" | py 'd["status"]')" "Submitted" "提交完工 → Submitted"
expect "$(echo "$D" | py 'd["completion"]["runStatus"]')" "Normal" "完工信息"

echo "== 主管闭环"
D=$(call GET "/api/orders/$OID" "$SUP")
expect "$(echo "$D" | py '",".join(d["actions"])')" "reject,close" "主管 actions（待验收）"
D=$(call POST "/api/orders/$OID/close" "$SUP")
expect "$(echo "$D" | py 'd["status"]')" "Closed" "确认闭环 → Closed"
expect "$(echo "$D" | py 'd["actions"]')" "[]" "闭环后主管无动作"
LOGS=$(echo "$D" | py '" | ".join(l["text"] for l in d["logs"])')
expect "$(echo "$D" | py 'len(d["logs"])')" "5" "状态记录 5 条：$LOGS"
expect "$(echo "$D" | py 'd["punches"][1]["pairMinutes"] is not None')" "True" "离场有 pairMinutes"
expect "$(echo "$D" | py 'd["issues"]')" '["DistanceAbnormal"]' "issues 含距离异常"
expect "$(call GET "/api/orders/$OID" "$SALES" | py 'd["actions"]')" "[]" "销售 actions 为空"

echo "== 聚合与列表"
call GET /api/home/engineer "$LI" > "$TMP/h"; expect "$(py 'd["state"]' < "$TMP/h")" "working" "home/engineer（李工 working）"
call GET /api/home/supervisor "$SUP" > "$TMP/h"; ok "home/supervisor onSiteCount=$(py 'd["onSiteCount"]' < "$TMP/h") todos=$(py 'len(d["todos"])' < "$TMP/h")"
call GET /api/hours/today "$LI" > "$TMP/h"; ok "hours/today running=$(py 'd["running"]' < "$TMP/h") tripDay=$(py 'd["tripDay"]' < "$TMP/h")"
call GET "/api/hours/month" "$LI" > "$TMP/h"; ok "hours/month totalMinutes=$(py 'd["totalMinutes"]' < "$TMP/h") tripDays=$(py 'd["tripDays"]' < "$TMP/h")"
call GET "/api/hours/today?engineerId=$(call GET /api/me "$ENG" | py 'd["user"]["id"]')" "$SUP" > /dev/null; ok "主管查看他人 hours/today"
call GET "/api/orders/mine?group=all" "$ENG" > "$TMP/h"; ok "orders/mine counts=$(py 'd["counts"]' < "$TMP/h")"
call GET "/api/orders/created?group=all" "$SALES" > "$TMP/h"; ok "orders/created counts=$(py 'd["counts"]' < "$TMP/h")"
call GET "/api/orders?page=1&pageSize=20" "$SUP" > "$TMP/h"; ok "orders total=$(py 'd["total"]' < "$TMP/h") counts=$(py 'd["counts"]' < "$TMP/h")"
call GET "/api/orders?issue=MissingPunch" "$SALES" > /dev/null; ok "orders?issue=MissingPunch（销售）"
call GET /api/orders/recent-customers "$SALES" > "$TMP/h"; ok "recent-customers $(py 'len(d)' < "$TMP/h") 个"
call GET /api/engineers "$SUP" > "$TMP/h"; ok "engineers: $(py '"; ".join(x["user"]["name"]+" "+x["loadText"] for x in d)' < "$TMP/h")"
call GET /api/admin/overview "$SUP" > "$TMP/h"; ok "admin/overview $(cat "$TMP/h")"
call GET /api/admin/employees "$SUP" > "$TMP/h"; ok "admin/employees $(py 'len(d)' < "$TMP/h") 人"

for p in "/api/admin/export/orders" "/api/admin/export/hours"; do
  ST=$(curl -s -o "$TMP/x.xlsx" -w "%{http_code} %{content_type}" "$BASE$p" -H "Authorization: Bearer $SUP")
  case "$ST" in
    "200 application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"*) ok "$p → xlsx $(wc -c < "$TMP/x.xlsx" | tr -d ' ') 字节";;
    *) fail "$p → $ST";;
  esac
done

echo "== 远程单与其他主管动作"
RO=$(call POST /api/orders "$ENG" "{\"source\":\"CustomerDirect\",\"problemType\":\"Consult\",\"urgency\":\"Consult\",\"serviceType\":\"Remote\",
 \"customerName\":\"冒烟远程客户\",\"contactName\":\"李老师\",\"contactPhone\":\"021-12345678\",\"address\":\"上海市杨浦区 [示例地址]\",
 \"deviceModel\":\"微型反应釜 HTM-100\",\"deviceSn\":\"SN-REMOTE\",\"satDate\":\"2025-02\",\"satSigned\":false,
 \"faultTime\":\"今天上午\",\"symptom\":\"压力传感器多久校准一次？\",\"customerTried\":\"查阅说明书\",\"siteEnvironment\":\"教学实验室\",\"price\":0}")
RID=$(echo "$RO" | py 'd["id"]')
expect "$(echo "$RO" | py 'd["productStage"]+"/"+d["city"]')" "新产品/上海" "工程师建远程单（新产品、直辖市解析）"
WANG=$(call GET /api/me "$ENG" | py 'd["user"]["id"]')
LIU=$(call GET /api/admin/employees "$SUP" | py '[e["id"] for e in d if e["name"]=="刘工"][0]')
D=$(call POST "/api/orders/$RID/assign" "$SUP" "{\"engineerId\":$WANG}")
expect "$(echo "$D" | py 'd["logs"][-1]["text"]')" "赵主管 指派给 王工 → 待启动" "指派 → 待启动"
D=$(call POST "/api/orders/$RID/appointment" "$ENG" '{"appointmentTime":"2026-11-09T10:00:00"}')
expect "$(echo "$D" | py 'd["logs"][-1]["text"]')" "王工 预约 11月9日 10:00" "预约"
expect "$(echo "$D" | py '",".join(d["actions"])')" "appoint,start" "远程单 actions"
expect "$(call POST "/api/orders/$RID/start" "$ENG" | py 'd["status"]')" "InProgress" "开始处理 → InProgress"
expect "$(call POST "/api/orders/$RID/hold" "$SUP" '{"reason":"等待备件"}' | py 'd["status"]+"/"+d["holdReason"]')" "OnHold/等待备件" "暂停搁置"
expect "$(call POST "/api/orders/$RID/resume" "$SUP" | py 'd["status"]')" "InProgress" "恢复处理"
D=$(call POST "/api/orders/$RID/assign" "$SUP" "{\"engineerId\":$LIU}")
expect "$(echo "$D" | py 'd["status"]+"/"+d["assignee"]["name"]')" "InProgress/刘工" "改派（状态不变）"
LIUT=$(login 13900000003)
PP=$(upload "$LIUT" ProcessPhoto)
BODY="{\"faultCause\":\"咨询\",\"solution\":\"远程指导校准\",\"runStatus\":\"Observe\",\"processPhotoIds\":[$PP]}"
expect "$(call POST "/api/orders/$RID/complete" "$LIUT" "$BODY" | py 'd["status"]')" "Submitted" "远程单无签字件可提交"
expect "$(call POST "/api/orders/$RID/reject" "$SUP" '{"reason":"缺少校准记录"}' | py 'd["status"]')" "InProgress" "退回处理中"
VO=$(call POST /api/orders "$SALES" "{\"source\":\"SalesReferral\",\"problemType\":\"Fault\",\"urgency\":\"Normal\",\"serviceType\":\"Factory\",
 \"customerName\":\"冒烟作废客户\",\"contactName\":\"王工\",\"contactPhone\":\"13800000000\",\"address\":\"南京市玄武区\",
 \"deviceModel\":\"HTM-100\",\"deviceSn\":\"SN-V\",\"satDate\":\"2024-01\",\"satSigned\":true,
 \"faultTime\":\"昨天\",\"symptom\":\"异响\",\"customerTried\":\"无\",\"siteEnvironment\":\"实验室\",\"price\":680}" | py 'd["id"]')
expect "$(call POST "/api/orders/$VO/void" "$SUP" '{"reason":"重复建单"}' | py 'd["status"]')" "Voided" "作废"
req POST "/api/orders/$VO/close" "$SUP"; expect "$(cat "$TMP/status")" "409" "已作废再闭环 → 409"
expect "$(call GET "/api/orders?status=Voided" "$SUP" | py 'd["total"] >= 1')" "True" "status=Voided 可单独查询"

echo "== 账号与头像"
PH="139$(date +%s | tail -c 9)"
EMP=$(call POST /api/admin/employees "$SUP" "{\"name\":\"测试工\",\"phone\":\"$PH\",\"role\":\"Engineer\",\"department\":\"售后部\"}")
EID=$(echo "$EMP" | py 'd["id"]')
expect "$(echo "$EMP" | py 'd["roleName"]')" "售后工程师" "新增员工"
req POST /api/admin/employees "$SUP" "{\"name\":\"重复\",\"phone\":\"$PH\",\"role\":\"Sales\",\"department\":\"\"}"
expect "$(cat "$TMP/status")" "409" "手机号重复 → 409"
req POST /api/auth/login "" "{\"code\":\"smoke-$PH\"}"
TICKET=$(py 'd["data"]["bindTicket"]' < "$TMP/body")
BODY="{\"bindTicket\":\"$TICKET\",\"phoneCode\":\"$PH\"}"
expect "$(call POST /api/auth/bind "" "$BODY" | py 'd["user"]["name"]')" "测试工" "微信绑定（mock）"
expect "$(call POST /api/auth/login "" "{\"code\":\"smoke-$PH\"}" | py 'd["needBind"]')" "False" "绑定后自动登录"
expect "$(call POST "/api/admin/employees/$EID/unbind" "$SUP" | py 'd["bound"]')" "False" "解绑"
BODY="{\"name\":\"测试工\",\"phone\":\"$PH\",\"role\":\"Engineer\",\"department\":\"售后部\",\"enabled\":false}"
expect "$(call PUT "/api/admin/employees/$EID" "$SUP" "$BODY" | py 'd["enabled"]')" "False" "停用员工"
AV=$(upload "$SALES" Avatar)
expect "$(call PUT /api/me/avatar "$SALES" "{\"fileId\":$AV}" | py 'd["avatarUrl"] is not None')" "True" "设置头像"

echo "== 补录合肥缺卡后闭环"
HF=$(call GET "/api/orders?keyword=%E5%90%88%E8%82%A5" "$SUP" | py 'd["items"][0]["id"]')
D=$(call GET "/api/orders/$HF" "$SUP")
if [ "$(echo "$D" | py 'd["status"]')" != "Submitted" ]; then
  echo "  - 合肥单已不是待验收（重复运行？），跳过补录检查"
else
  expect "$(echo "$D" | py '"close" in d["actions"]')" "False" "缺卡时不能闭环"
  req POST "/api/orders/$HF/close" "$SUP"; expect "$(py 'd["message"]' < "$TMP/body")" "请先补录缺卡" "close → 请先补录缺卡"
  AP=$(echo "$D" | py '[i for i in d["issueDetails"] if i["type"]=="MissingPunch"][0]["punchId"]')
  ADATE=$(echo "$D" | py '[i for i in d["issueDetails"] if i["type"]=="MissingPunch"][0]["date"]')
  D=$(call POST /api/admin/punches/supplement "$SUP" "{\"orderId\":$HF,\"arrivePunchId\":$AP,\"kind\":\"Leave\",\"time\":\"${ADATE}T18:20:00\",\"reason\":\"手机电量耗尽，工程师电话确认约 18:20 离场\"}")
  expect "$(echo "$D" | py '"MissingPunch" in d["issues"]')" "False" "补录后缺卡消失"
  expect "$(echo "$D" | py 'd["totalMinutes"]')" "685" "工时重新计算 = 11:25"
  expect "$(echo "$D" | py 'd["logs"][-1]["text"]')" "赵主管 补录离场 18:20" "补录状态记录"
  D=$(call POST "/api/orders/$HF/close" "$SUP")
  expect "$(echo "$D" | py 'd["status"]')" "Closed" "补录后闭环成功"
fi

echo
echo "全部通过：$PASS 项"
