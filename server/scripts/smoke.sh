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
expect "$(echo "$NEW" | py 'str(d["priceSet"])+"/"+str(d["price"])')" "False/None" "建单忽略旧客户端传入的 price（未定价）"
expect "$(echo "$NEW" | py '",".join(d["actions"])')" "suggestPrice" "销售 actions（可提价格建议）"
expect "$(echo "$NEW" | py 'd["progressStep"]')" "1" "progressStep=1"
expect "$(echo "$NEW" | py 'd["status"]')" "Pending" "新建工单 $(echo "$NEW" | py 'd["orderNo"]') → Pending"
expect "$(echo "$NEW" | py 'd["city"]+"/"+d["region"]')" "苏州/苏州市工业园区" "地址解析 city/region"
expect "$(echo "$NEW" | py 'len(d["files"]["orderPhotos"])')" "1" "建单照片已关联"
expect "$(echo "$NEW" | py 'd["logs"][0]["text"]')" "王敏 建单 · 紧急" "建单状态记录"

echo "== 工程师认领"
D=$(call POST "/api/orders/$OID/claim" "$ENG")
expect "$(echo "$D" | py 'd["status"]')" "Accepted" "认领 → Accepted"
expect "$(echo "$D" | py '",".join(d["actions"])')" "appoint,checkin,requestHold,requestAssistant" "工程师 actions"
expect "$(echo "$D" | py 'str(d["price"])+"/"+str(d["priceSet"])+"/"+str(d["priceSuggestion"])')" "None/None/None" "工程师看不到价格"
expect "$(echo "$D" | py 'd["myRole"]')" "Lead" "myRole=Lead"
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

echo "== 价格：销售建议 → 主管定价 → 闭环"
D=$(call GET "/api/orders/$OID" "$SUP")
expect "$(echo "$D" | py '",".join(d["actions"])')" "reject,setPrice" "主管 actions（待验收、未定价）"
expect "$(echo "$D" | py 'd["closeBlockedReason"]')" "请先设置服务价格（保内填 0）" "closeBlockedReason（未定价）"
req POST "/api/orders/$OID/close" "$SUP"; expect "$(py 'd["message"]' < "$TMP/body")" "请先设置服务价格（保内填 0）" "未定价闭环 → 409"
req POST "/api/orders/$OID/price" "$SALES" '{"price":1}'; expect "$(cat "$TMP/status")" "403" "销售定价 → 403"
req POST "/api/orders/$OID/price" "$ENG" '{"price":1}'; expect "$(cat "$TMP/status")" "403" "工程师定价 → 403"
req POST "/api/orders/$OID/price-suggestions" "$ENG" '{"amount":1}'; expect "$(cat "$TMP/status")" "403" "工程师提价格建议 → 403"
D=$(call POST "/api/orders/$OID/price-suggestions" "$SALES" '{"amount":1500,"note":"老客户，建议优惠"}')
expect "$(echo "$D" | py 'd["logs"][-1]["text"]+"/"+d["logs"][-1]["kind"]')" "王敏 建议价格 ¥1,500（老客户，建议优惠）/Price" "销售提价格建议"
D=$(call GET "/api/orders/$OID" "$SUP")
expect "$(echo "$D" | py 'str(d["priceSuggestion"]["pending"])+"/"+str(float(d["priceSuggestion"]["amount"]))')" "True/1500.0" "主管看到待处理的价格建议"
D=$(call GET "/api/orders/$OID" "$ENG")
expect "$(echo "$D" | py 'str(d["price"])+"/"+str(d["priceSuggestion"])+"/"+str(len([l for l in d["logs"] if l["kind"]=="Price"]))')" "None/None/0" "工程师看不到价格、建议与价格记录"
D=$(call POST "/api/orders/$OID/price" "$SUP" '{"price":1800}')
expect "$(echo "$D" | py 'd["logs"][-1]["text"]')" "赵主管 定价 ¥1,800" "主管定价"
expect "$(echo "$D" | py 'str(float(d["price"]))+"/"+str(d["priceSet"])+"/"+str(d["priceSuggestion"]["pending"])')" "1800.0/True/False" "定价后 price/priceSet/建议已处理"
expect "$(echo "$D" | py '",".join(d["actions"])')" "reject,close,setPrice" "主管 actions（待验收、已定价）"
expect "$(call GET "/api/orders/$OID" "$SALES" | py 'float(d["price"])')" "1800.0" "销售能看到价格"
D=$(call POST "/api/orders/$OID/close" "$SUP")
expect "$(echo "$D" | py 'd["status"]')" "Closed" "确认闭环 → Closed"
expect "$(echo "$D" | py '",".join(d["actions"])')" "setPrice" "闭环后主管仅可改价"
LOGS=$(echo "$D" | py '" | ".join(l["text"] for l in d["logs"])')
expect "$(echo "$D" | py 'len(d["logs"])')" "7" "状态记录 7 条：$LOGS"
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
expect "$(call GET /api/engineers "$ENG" | py 'all("assistCount" in x for x in d)')" "True" "工程师可取工程师列表（含 assistCount）"
expect "$(call GET "/api/orders/hall?filter=all" "$ENG" | py 'all(i["price"] is None and i["priceSet"] is None for i in d["items"])')" "True" "大厅不向工程师返回价格"
expect "$(call GET "/api/orders/mine?group=all" "$ENG" | py 'all(i["price"] is None and i["priceSet"] is None for i in d["items"])')" "True" "我的工单不向工程师返回价格"
curl -s "$BASE/api/home/engineer" -H "Authorization: Bearer $ENG" > "$TMP/home.json"
expect "$(python3 -c 'import re,sys; print(len(re.findall(r"\"price\":\s*[0-9]|\"priceSet\":\s*(?:true|false)|¥", open(sys.argv[1], encoding="utf-8").read())))' "$TMP/home.json")" "0" "工程师首页 JSON 不含价格"
expect "$(call GET "/api/orders?priceSet=false" "$SUP" | py 'all(i["priceSet"] is False and i["price"] is None for i in d["items"])')" "True" "orders?priceSet=false"
expect "$(call GET "/api/orders?pendingRequest=true" "$SALES" | py 'all(i["pendingRequestCount"] > 0 for i in d["items"])')" "True" "orders?pendingRequest=true（销售）"
req GET "/api/orders?priceSet=maybe" "$SUP"; expect "$(py 'd["message"]' < "$TMP/body")" "定价筛选不正确" "priceSet 非法 → 40000"
call GET /api/admin/overview "$SUP" > "$TMP/h"; expect "$(py 'all(k in d for k in ("onHold","pendingRequests","unpriced","priceSuggestions"))' < "$TMP/h")" "True" "overview v1.1 字段"
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
 \"faultTime\":\"今天上午\",\"symptom\":\"压力传感器多久校准一次？\",\"customerTried\":\"查阅说明书\",\"siteEnvironment\":\"教学实验室\"}")
RID=$(echo "$RO" | py 'd["id"]')
expect "$(echo "$RO" | py 'd["productStage"]+"/"+d["city"]')" "新产品/上海" "工程师建远程单（新产品、直辖市解析）"
WANG=$(call GET /api/me "$ENG" | py 'd["user"]["id"]')
LIU=$(call GET /api/admin/employees "$SUP" | py '[e["id"] for e in d if e["name"]=="刘工"][0]')
D=$(call POST "/api/orders/$RID/assign" "$SUP" "{\"engineerId\":$WANG}")
expect "$(echo "$D" | py 'd["logs"][-1]["text"]')" "赵主管 指派给 王工 → 待启动" "指派 → 待启动"
D=$(call POST "/api/orders/$RID/appointment" "$ENG" '{"appointmentTime":"2026-11-09T10:00:00"}')
expect "$(echo "$D" | py 'd["logs"][-1]["text"]')" "王工 预约 11月9日 10:00" "预约"
expect "$(echo "$D" | py '",".join(d["actions"])')" "appoint,start,requestHold,requestAssistant" "远程单 actions"

echo "== 暂停搁置：直接暂停 / 申请与审批"
D=$(call POST "/api/orders/$RID/hold" "$SUP" '{"reasonType":"WaitCustomer","note":"客户下周才有空"}')
expect "$(echo "$D" | py 'd["status"]+"/"+d["hold"]["text"]+"/"+d["hold"]["fromStatus"]+"/"+str(d["progressStep"])')" "OnHold/等客户：客户下周才有空/Accepted/2" "待启动直接暂停"
req POST "/api/orders/$RID/start" "$ENG"; expect "$(py 'd["message"]' < "$TMP/body")" "工单已暂停搁置，恢复后才能开始处理" "暂停时 start → 409"
expect "$(call POST "/api/orders/$RID/resume" "$SUP" | py 'd["status"]')" "Accepted" "恢复 → 回到待启动"
expect "$(call POST "/api/orders/$RID/start" "$ENG" | py 'd["status"]')" "InProgress" "开始处理 → InProgress"
req POST "/api/orders/$RID/hold" "$SUP" '{}'; expect "$(py 'd["message"]' < "$TMP/body")" "请选择暂停原因" "缺暂停原因 → 40000"
req POST "/api/orders/$RID/hold" "$SUP" '{"reasonType":"Nope"}'; expect "$(py 'd["message"]' < "$TMP/body")" "请选择暂停原因" "非法暂停原因 → 40000"
req POST "/api/orders/$RID/hold" "$SUP" '{"reasonType":"Other"}'; expect "$(py 'd["message"]' < "$TMP/body")" "请填写暂停说明" "其他无说明 → 40000"
expect "$(call POST "/api/orders/$RID/hold" "$SUP" '{"reasonType":"WaitParts"}' | py 'd["status"]+"/"+d["holdReason"]')" "OnHold/等备件" "暂停搁置"
expect "$(call POST "/api/orders/$RID/resume" "$SUP" | py 'd["status"]')" "InProgress" "恢复处理"
expect "$(call POST "/api/orders/$RID/hold" "$SUP" '{"reason":"等待备件"}' | py 'd["status"]+"/"+d["holdReason"]+"/"+d["hold"]["reasonType"]')" "OnHold/等待备件/Other" "旧请求体 {reason} 仍可用"
expect "$(call POST "/api/orders/$RID/resume" "$SUP" | py 'd["status"]')" "InProgress" "恢复处理"
D=$(call POST "/api/orders/$RID/hold-requests" "$ENG" '{"reasonType":"WaitParts","note":"轴承预计周五到"}')
HRQ=$(echo "$D" | py 'd["requests"][-1]["id"]')
expect "$(echo "$D" | py 'd["requests"][-1]["status"]+"/"+d["requests"][-1]["text"]+"/"+str(d["requests"][-1]["canCancel"])')" "Pending/等备件：轴承预计周五到/True" "工程师申请暂停"
req POST "/api/orders/$RID/hold-requests" "$ENG" '{"reasonType":"WaitParts"}'; expect "$(py 'd["message"]' < "$TMP/body")" "已有待审批的暂停申请" "重复申请 → 409"
req POST "/api/orders/$RID/hold-requests" "$LI" '{"reasonType":"WaitParts"}'; expect "$(py 'd["message"]' < "$TMP/body")" "只有负责工程师可以申请暂停" "非负责人申请 → 403"
expect "$(call GET /api/home/supervisor "$SUP" | py "len([t for t in d['todos'] if t['type']=='HoldRequest' and t['requestId']==$HRQ])")" "1" "主管待办出现暂停申请"
D=$(call POST "/api/orders/$RID/requests/$HRQ/reject" "$SUP" '{"reply":"备件明天到，先别停"}')
expect "$(echo "$D" | py 'd["status"]+"/"+d["requests"][-1]["status"]')" "InProgress/Rejected" "驳回暂停申请（状态不变）"
HRQ=$(call POST "/api/orders/$RID/hold-requests" "$ENG" '{"reasonType":"WaitThirdParty"}' | py 'd["requests"][-1]["id"]')
D=$(call POST "/api/orders/$RID/requests/$HRQ/approve" "$SUP")
expect "$(echo "$D" | py 'd["status"]+"/"+d["hold"]["text"]+"/"+d["logs"][-1]["text"]')" "OnHold/等第三方/赵主管 同意暂停申请 → 暂停搁置（等第三方）" "同意暂停申请"
req POST "/api/orders/$RID/requests/$HRQ/approve" "$SUP"; expect "$(py 'd["message"]' < "$TMP/body")" "该申请已处理" "重复审批 → 409"
expect "$(call POST "/api/orders/$RID/resume" "$SUP" | py 'd["status"]')" "InProgress" "恢复处理"
D=$(call POST "/api/orders/$RID/assign" "$SUP" "{\"engineerId\":$LIU}")
expect "$(echo "$D" | py 'd["status"]+"/"+d["assignee"]["name"]')" "InProgress/刘工" "改派（状态不变）"
LIUT=$(login 13900000003)

echo "== 多人协作：协助人"
req POST "/api/orders/$RID/assistants" "$SUP" "{\"engineerIds\":[$LIU]}"; expect "$(py 'd["message"]' < "$TMP/body")" "协助人不能是负责人" "负责人作协助人 → 40000"
D=$(call POST "/api/orders/$RID/assistant-requests" "$LIUT" "{\"engineerId\":$WANG,\"note\":\"需要两人\"}")
ARQ=$(echo "$D" | py 'd["requests"][-1]["id"]')
expect "$(echo "$D" | py 'd["requests"][-1]["text"]')" "添加协助人 王工：需要两人" "负责人申请协助人"
D=$(call POST "/api/orders/$RID/requests/$ARQ/approve" "$SUP")
expect "$(echo "$D" | py '",".join(a["name"] for a in d["assistants"])+"/"+d["logs"][-1]["text"]')" "王工/赵主管 同意添加协助人 王工" "同意协助人申请"
expect "$(echo "$D" | py '",".join(p["user"]["name"]+":"+p["role"] for p in d["participants"])')" "刘工:Lead,王工:Assistant" "participants"
D=$(call GET "/api/orders/$RID" "$ENG")
expect "$(echo "$D" | py 'd["myRole"]+"/"+str(d["actions"])')" "Assistant/[]" "协助人可看详情（远程单无动作）"
expect "$(call GET "/api/orders/mine?group=all" "$ENG" | py "[i['myRole'] for i in d['items'] if i['id']==$RID][0]")" "Assistant" "协助单出现在我的工单"
D=$(call POST "/api/orders/$RID/assistants" "$SUP" '{"engineerIds":[]}')
expect "$(echo "$D" | py 'str(len(d["assistants"]))+"/"+d["logs"][-1]["text"]')" "0/赵主管 移除协助人 王工" "主管移除协助人"
PP=$(upload "$LIUT" ProcessPhoto)
BODY="{\"faultCause\":\"咨询\",\"solution\":\"远程指导校准\",\"runStatus\":\"Observe\",\"processPhotoIds\":[$PP]}"
expect "$(call POST "/api/orders/$RID/complete" "$LIUT" "$BODY" | py 'd["status"]')" "Submitted" "远程单无签字件可提交"
expect "$(call POST "/api/orders/$RID/reject" "$SUP" '{"reason":"缺少校准记录"}' | py 'd["status"]')" "InProgress" "退回处理中"
FACTORY_BODY="{\"source\":\"SalesReferral\",\"problemType\":\"Fault\",\"urgency\":\"Normal\",\"serviceType\":\"Factory\",
 \"customerName\":\"冒烟返厂客户\",\"contactName\":\"王工\",\"contactPhone\":\"13800000000\",\"address\":\"南京市玄武区\",
 \"deviceModel\":\"HTM-100\",\"deviceSn\":\"SN-V\",\"satDate\":\"2024-01\",\"satSigned\":true,
 \"faultTime\":\"昨天\",\"symptom\":\"异响\",\"customerTried\":\"无\",\"siteEnvironment\":\"实验室\"}"

echo "== 返厂流程：5 节点"
FO=$(call POST /api/orders "$SALES" "$FACTORY_BODY")
FID=$(echo "$FO" | py 'd["id"]')
expect "$(echo "$FO" | py 'd["factoryText"]+"/"+",".join(n["state"] for n in d["factory"]["nodes"])+"/"+",".join(d["actions"])')" "待客户寄出/current,todo,todo,todo,todo/suggestPrice,factory" "返厂单建单"
D=$(call POST "/api/orders/$FID/claim" "$ENG")
expect "$(echo "$D" | py '",".join(d["actions"])')" "appoint,factory,requestHold,requestAssistant" "返厂单工程师 actions（无 start）"
req POST "/api/orders/$FID/start" "$ENG"; expect "$(py 'd["message"]' < "$TMP/body")" "返厂工单记录「工厂收货」后自动开始处理" "返厂单 start → 409"
RP=$(upload "$ENG" FactoryReceivePhoto); ok "上传收货照片 id=$RP"
req POST "/api/orders/$FID/factory-nodes" "$ENG" "{\"node\":\"FactoryReceived\",\"photoIds\":[$RP]}"
expect "$(py 'd["message"]' < "$TMP/body")" "请先记录「客户寄出」" "工程师跳过客户寄出 → 409"
req POST "/api/orders/$FID/factory-nodes" "$LI" '{"node":"CustomerShipped","courier":"顺丰","trackingNo":"SF1"}'
expect "$(cat "$TMP/status")" "403" "无关工程师记节点 → 403"
req POST "/api/orders/$FID/factory-nodes" "$SALES" '{"node":"Repairing"}'; expect "$(py 'd["message"]' < "$TMP/body")" "检测/维修无需记录" "Repairing → 40000"
req POST "/api/orders/$FID/factory-nodes" "$SALES" '{"node":"Nope"}'; expect "$(py 'd["message"]' < "$TMP/body")" "请选择返厂节点" "非法节点 → 40000"
D=$(call POST "/api/orders/$FID/factory-nodes" "$SALES" '{"node":"CustomerShipped","courier":"顺丰","trackingNo":"SF 1234 5678","note":"含电源线 1 根"}')
expect "$(echo "$D" | py 'd["factoryText"]+"/"+d["factory"]["nodes"][0]["trackingNo"]+"/"+d["logs"][-1]["text"]')" "客户已寄出/SF12345678/王敏 记录客户寄出（顺丰 SF12345678）" "1 客户寄出（销售）"
D=$(call POST "/api/orders/$FID/factory-nodes" "$ENG" "{\"node\":\"FactoryReceived\",\"photoIds\":[$RP]}")
expect "$(echo "$D" | py 'd["status"]+"/"+d["factoryText"]+"/"+d["logs"][-1]["text"]')" "InProgress/检测/维修中/王工 工厂收货 → 处理中" "2 工厂收货 → 自动进入处理中"
expect "$(echo "$D" | py '",".join(n["state"] for n in d["factory"]["nodes"])+"/"+str(len(d["files"]["factoryPhotos"]))')" "done,done,current,current,todo/1" "节点状态与返厂照片"
req GET "/api/punches/context?orderId=$FID" "$ENG"; expect "$(py 'd["message"]' < "$TMP/body")" "远程/返厂工单无需打卡" "返厂单不打卡"
PP=$(upload "$ENG" ProcessPhoto)
FBODY="{\"faultCause\":\"轴承磨损\",\"solution\":\"返厂更换轴承\",\"runStatus\":\"Normal\",\"processPhotoIds\":[$PP]}"
req POST "/api/orders/$FID/complete" "$ENG" "$FBODY"; expect "$(py 'd["message"]' < "$TMP/body")" "返厂工单请先记录「寄回客户」再提交完工" "未寄回完工 → 409"
SP=$(upload "$ENG" FactoryShipPhoto)
req POST "/api/orders/$FID/factory-nodes" "$ENG" '{"node":"ShippedBack","courier":"顺丰","trackingNo":"SF2"}'
expect "$(py 'd["message"]' < "$TMP/body")" "请至少上传 1 张寄回照片" "寄回无照片 → 40000"
D=$(call POST "/api/orders/$FID/factory-nodes" "$ENG" "{\"node\":\"ShippedBack\",\"courier\":\"顺丰\",\"trackingNo\":\"SF2\",\"photoIds\":[$SP]}")
expect "$(echo "$D" | py 'd["factoryText"]+"/"+",".join(d["actions"])')" "已寄回客户/appoint,factory,complete,requestHold,requestAssistant" "4 寄回客户"
D=$(call POST "/api/orders/$FID/factory-nodes" "$ENG" "{\"node\":\"ShippedBack\",\"courier\":\"顺丰\",\"trackingNo\":\"SF3\",\"photoIds\":[$SP]}")
expect "$(echo "$D" | py 'd["logs"][-1]["text"]+"/"+d["status"]')" "王工 更正寄回客户（顺丰 SF3）/InProgress" "更正寄回单号（状态不变）"
expect "$(call POST "/api/orders/$FID/complete" "$ENG" "$FBODY" | py 'd["status"]+"/"+",".join(d["actions"])')" "Submitted/factory" "返厂单提交完工"
D=$(call POST "/api/orders/$FID/price" "$SUP" '{"price":0}')
expect "$(echo "$D" | py 'd["logs"][-1]["text"]+"/"+str(d["closeHints"])+"/"+str(d["closeBlockedReason"])')" "赵主管 定价 保内免费/['客户签收尚未记录']/None" "未签收：closeHints 提示但不拦闭环"
D=$(call POST "/api/orders/$FID/factory-nodes" "$SALES" '{"node":"CustomerSigned"}')
expect "$(echo "$D" | py 'd["factoryText"]+"/"+",".join(n["state"] for n in d["factory"]["nodes"])')" "客户已签收/done,done,done,done,done" "5 客户签收（销售）"
expect "$(call POST "/api/orders/$FID/close" "$SUP" | py 'd["status"]+"/"+str(d["closeHints"])')" "Closed/[]" "返厂单闭环"
req POST "/api/orders/$FID/factory-nodes" "$SUP" '{"node":"CustomerSigned"}'
expect "$(py 'd["message"]' < "$TMP/body")" "工单当前为「已闭环」，不能更正返厂记录" "闭环后更正 → 409"

VO=$(call POST /api/orders "$SALES" "$FACTORY_BODY" | py 'd["id"]')
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

echo "== PC 主管后台：账号密码"
req POST /api/auth/password-login "" '{"phone":"13900000001","password":"Wrong1234"}'
expect "$(cat "$TMP/status")/$(py 'd["code"]' < "$TMP/body")" "400/40000" "密码错误 → 40000"
expect "$(py 'd["message"]' < "$TMP/body")" "手机号或密码错误" "密码错误文案"
req POST /api/auth/password-login "" '{"phone":"13900000000","password":"Huotong@2026"}'
expect "$(py 'd["message"]' < "$TMP/body")" "手机号或密码错误" "未设置密码的账号 → 同一文案"
PL=$(call POST /api/auth/password-login "" '{"phone":"13900000001","password":"Huotong@2026"}')
expect "$(echo "$PL" | py 'd["user"]["role"]')" "Supervisor" "赵主管密码登录"
PCTOKEN=$(echo "$PL" | py 'd["token"]')
expect "$(call GET /api/me "$PCTOKEN" | py 'd["user"]["name"]')" "赵主管" "密码登录 token 可用"
expect "$(call GET /api/admin/employees "$PCTOKEN" | py '[e["hasPassword"] for e in d if e["phone"]=="13900000001"][0]')" "True" "employees.hasPassword"
req PUT /api/me/password "$PCTOKEN" '{"oldPassword":"Huotong@2026","newPassword":"abcdefgh"}'
expect "$(py 'd["code"]' < "$TMP/body")" "40000" "新密码强度不足 → 40000"
req PUT /api/me/password "$PCTOKEN" '{"oldPassword":"Wrong1234","newPassword":"Smoke2026x"}'
expect "$(py 'd["message"]' < "$TMP/body")" "原密码错误" "原密码错误 → 40000"
EP=$(call POST "/api/admin/employees/$EID/password" "$SUP" '{"password":"Smoke2026x"}')
expect "$(echo "$EP" | py 'd["hasPassword"]')" "True" "主管为员工设置密码"
req POST "/api/admin/employees/$EID/password" "$SUP" '{"password":"short1"}'
expect "$(py 'd["code"]' < "$TMP/body")" "40000" "设置弱密码 → 40000"
req POST /api/auth/password-login "" "{\"phone\":\"$PH\",\"password\":\"Smoke2026x\"}"
expect "$(py 'd["code"]' < "$TMP/body")" "40300" "非主管/已停用账号密码登录 → 40300"

echo "== PC 主管后台：打卡记录 / 工时统计"
PU=$(call GET "/api/admin/punches" "$PCTOKEN")
expect "$(echo "$PU" | py 'sorted(d["counts"].keys())')" '["all", "distance", "missing", "supplement"]' "punches counts 键"
expect "$(echo "$PU" | py 'd["total"] == d["counts"]["all"] and d["counts"]["all"] > 0')" "True" "punches total = counts.all > 0"
expect "$(echo "$PU" | py '[i["time"] for i in d["items"]] == sorted([i["time"] for i in d["items"]], reverse=True)')" "True" "punches 时间倒序"
expect "$(echo "$PU" | py 'all(k in d["items"][0] for k in ("orderNo","customerName","missing","engineer","pairMinutes","photo"))')" "True" "punches 项字段"
DI=$(call GET "/api/admin/punches?abnormal=distance" "$PCTOKEN")
expect "$(echo "$DI" | py 'd["total"] == d["counts"]["distance"] and all(i["outOfRange"] for i in d["items"])')" "True" "abnormal=distance 过滤"
expect "$(echo "$DI" | py 'd["counts"]')" "$(echo "$PU" | py 'd["counts"]')" "counts 不受 abnormal 影响"
SU=$(call GET "/api/admin/punches?abnormal=supplement" "$PCTOKEN")
expect "$(echo "$SU" | py 'd["total"] >= 1 and all(i["isSupplement"] for i in d["items"])')" "True" "abnormal=supplement 过滤"
MI=$(call GET "/api/admin/punches?abnormal=missing" "$PCTOKEN")
expect "$(echo "$MI" | py 'all(i["missing"] and i["kind"]=="Arrive" for i in d["items"])')" "True" "abnormal=missing 过滤"
req GET "/api/admin/punches?abnormal=foo" "$PCTOKEN"; expect "$(py 'd["code"]' < "$TMP/body")" "40000" "abnormal 非法 → 40000"
req GET /api/admin/punches "$ENG"; expect "$(cat "$TMP/status")" "403" "工程师查打卡记录 → 403"
HO=$(call GET "/api/admin/hours" "$PCTOKEN")
expect "$(echo "$HO" | py 'd["totalMinutes"] == sum(e["totalMinutes"] for e in d["engineers"])')" "True" "hours totalMinutes = 各工程师合计"
expect "$(echo "$HO" | py 'len(d["engineers"]) >= 4')" "True" "hours 含无打卡的工程师"
expect "$(echo "$HO" | py '[e["totalMinutes"] for e in d["engineers"]] == sorted([e["totalMinutes"] for e in d["engineers"]], reverse=True)')" "True" "hours 按 totalMinutes 倒序"
expect "$(echo "$HO" | py 'all(set(("engineer","totalMinutes","tripDays","orderCount","supplementCount","missingCount","runningNow","days")) <= set(e) for e in d["engineers"])')" "True" "hours 行字段"
req GET "/api/admin/hours?year=2026&month=13" "$PCTOKEN"; expect "$(py 'd["code"]' < "$TMP/body")" "40000" "非法月份 → 40000"

echo "== PC 主管后台：静态页面"
ST=$(curl -s -o "$TMP/admin.html" -w "%{http_code} %{content_type}" "$BASE/admin/")
expect "$(echo "$ST" | cut -d' ' -f1)" "200" "GET /admin/ → 200"
expect "$(echo "$ST" | grep -c 'text/html')" "1" "GET /admin/ 返回 html"
expect "$(grep -ci '<html' "$TMP/admin.html" | tr -d ' ' | sed 's/^[1-9][0-9]*$/1/')" "1" "/admin/ 内容为 HTML 页面"
RD=$(curl -s -o /dev/null -w "%{http_code} %{redirect_url}" "$BASE/admin")
expect "$(echo "$RD" | cut -d' ' -f1 | cut -c1)" "3" "GET /admin → 重定向"
expect "$(echo "$RD" | cut -d' ' -f2 | sed 's#^.*://[^/]*##')" "/admin/" "重定向到 /admin/"

# ============================== v1.2：工单附件 + 文件中心 ==============================
echo "== v1.2 附件：上传校验"
# 前面的账号用例可能让旧 token 失效，这里重新登录
SUP=$(login 13900000001); ENG=$(login 13900000004); SALES=$(login 13900000010); LI=$(login 13900000000)

# upf TOKEN 本地文件 CATEGORY 上传用文件名 [fileName 字段] → 写 $TMP/body 与 $TMP/status
upf() {
  local args=(-s -o "$TMP/body" -w "%{http_code}" -X POST "$BASE/api/files" -H "Authorization: Bearer $1" -H "Expect:"
    -F "file=@$2;filename=$4;type=application/octet-stream" -F "category=$3")
  [ -n "${5:-}" ] && args+=(-F "fileName=$5")
  curl "${args[@]}" > "$TMP/status" || echo "000" > "$TMP/status"
}
# upok ... → 断言成功并输出 data
upok() {
  upf "$@"
  if [ "$(cat "$TMP/status")" != "200" ]; then echo "上传失败: $4 → HTTP $(cat "$TMP/status")" >&2; cat "$TMP/body" >&2; kill -TERM "$MAIN_PID"; exit 1; fi
  py 'd["data"]' < "$TMP/body"
}
upmsg() { echo "$(cat "$TMP/status")/$(py 'd["message"]' < "$TMP/body" 2>/dev/null || echo '?')"; }

python3 - "$TMP" <<'PY'
import sys, zipfile, os
t = sys.argv[1]
open(t + '/a.pdf', 'wb').write(b'%PDF-1.4\n1 0 obj\n<<>>\nendobj\n%%EOF\n')
open(t + '/b.pdf', 'wb').write(b'%PDF-1.4\n%%EOF\n')
open(t + '/c.pdf', 'wb').write(b'%PDF-1.4\n% engineer\n%%EOF\n')
with zipfile.ZipFile(t + '/a.docx', 'w') as z:
    z.writestr('[Content_Types].xml', '<Types/>')
    z.writestr('word/document.xml', '<w/>')
open(t + '/a.txt', 'wb').write('现场情况说明\n'.encode('utf-8'))
open(t + '/a.exe', 'wb').write(b'MZ\x90\x00' + b'\x00' * 60)
open(t + '/fake.pdf', 'wb').write(b'MZ\x90\x00' + b'\x00' * 60)
head = b'\x00\x00\x00\x18ftypisom\x00\x00\x02\x00isomiso2'
for name, mb in (('v21.mp4', 21), ('v32.mp4', 32), ('v101.mp4', 101)):
    with open(t + '/' + name, 'wb') as f:
        f.write(head)
        f.truncate(mb * 1024 * 1024)
PY

F=$(upok "$SALES" "$TMP/a.pdf" Attachment a.pdf "报价单.pdf")
expect "$(echo "$F" | py 'd["kind"]+"/"+d["name"]+"/"+d["ext"]+"/"+d["mimeType"]+"/"+d["category"]')" "Document/报价单.pdf/pdf/application/pdf/Attachment" "上传 PDF 附件（带 fileName）"
expect "$(echo "$F" | py 'd["size"]')" "$(wc -c < "$TMP/a.pdf" | tr -d ' ')" "FileDto.size = 字节数"
SECRET=$(echo "$F" | py 'd["id"]')
SECRETURL=$(echo "$F" | py 'd["url"]')
F=$(upok "$SALES" "$TMP/b.pdf" Attachment b.pdf "现场说明.pdf"); OPEN=$(echo "$F" | py 'd["id"]'); OPENURL=$(echo "$F" | py 'd["url"]')
expect "$(upok "$SALES" "$TMP/a.docx" Attachment a.docx "验收报告.docx" | py 'd["kind"]+"/"+d["ext"]')" "Document/docx" "上传 docx"
expect "$(upok "$SALES" "$TMP/a.txt" Attachment a.txt "说明.txt" | py 'd["mimeType"]')" "text/plain" "上传 txt"
F=$(upok "$SALES" "$TMP/p.png" Attachment p.png)
expect "$(echo "$F" | py 'd["kind"]+"/"+d["ext"]+"/"+str(d["name"].startswith("图片_"))')" "Image/png/True" "上传 PNG 附件（无 fileName → 生成名）"
upf "$SALES" "$TMP/a.exe" Attachment setup.exe setup.exe
expect "$(upmsg)" "400/不支持的文件类型，仅支持图片、视频、Word、Excel、PPT、PDF、TXT、CSV" ".exe → 400"
upf "$SALES" "$TMP/fake.pdf" Attachment fake.pdf fake.pdf
expect "$(upmsg)" "400/文件内容与扩展名不符，请确认文件未损坏" "伪装成 PDF 的可执行文件 → 400"
upf "$SALES" "$TMP/a.pdf" OrderPhoto a.pdf
expect "$(upmsg)" "400/只能上传图片或视频" "PDF 以 category=OrderPhoto 上传 → 400"
expect "$(upok "$SALES" "$TMP/v21.mp4" Attachment v21.mp4 | py 'd["kind"]+"/"+str(d["size"])')" "Video/22020096" "21MB 视频以 Attachment 上传 → 200"
upf "$SALES" "$TMP/v21.mp4" OrderPhoto v21.mp4
expect "$(upmsg)" "400/文件不能超过 20MB" "21MB 视频以 OrderPhoto 上传 → 400"
expect "$(upok "$SALES" "$TMP/v32.mp4" Attachment v32.mp4 "现场视频.mp4" | py 'd["name"]+"/"+str(d["size"])')" "现场视频.mp4/33554432" "32MB 视频（超过 Kestrel 全局 30MB 上限）以 Attachment 上传 → 200"
upf "$SALES" "$TMP/v101.mp4" Attachment v101.mp4
expect "$(upmsg)" "400/文件不能超过 100MB" "101MB 以 Attachment 上传 → 400"
rm -f "$TMP/v21.mp4" "$TMP/v32.mp4" "$TMP/v101.mp4"

echo "== v1.2 附件：建单 / 可见性 / 添加"
ORDER_BODY() { # ORDER_BODY 客户名 额外字段
  echo "{\"source\":\"SalesReferral\",\"problemType\":\"Fault\",\"urgency\":\"Normal\",\"serviceType\":\"Onsite\",
 \"customerName\":\"$1\",\"contactName\":\"张工\",\"contactPhone\":\"13800000000\",\"address\":\"江苏省苏州市工业园区星湖街 1 号\",
 \"latitude\":31.30,\"longitude\":120.72,\"deviceModel\":\"HTR-500\",\"deviceSn\":\"SN-V12\",\"satDate\":\"2024-08\",\"satSigned\":true,
 \"faultTime\":\"昨天\",\"symptom\":\"不加热\",\"customerTried\":\"重启\",\"siteEnvironment\":\"通风橱\",$2}"
}
# 先拼到变量里：macOS 自带的 bash 3.2 在嵌套的 $(...) 中会对花括号做展开
EXTRA="\"attachments\":[{\"fileId\":$OPEN},{\"fileId\":$SECRET,\"restricted\":true}]"
BODY=$(ORDER_BODY "附件冒烟客户" "$EXTRA")
D=$(call POST /api/orders "$SALES" "$BODY")
AID=$(echo "$D" | py 'd["id"]')
expect "$(echo "$D" | py 'str(len(d["attachments"]))+"/"+str(d["attachmentRemaining"])+"/"+str(d["canRestrictAttachment"])+"/"+str(d["canAddAttachment"])')" "2/18/True/True" "销售建单带 2 个附件（1 受限）"
expect "$(echo "$D" | py '[a["name"] for a in d["attachments"] if a["restricted"]]')" '["报价单.pdf"]' "restricted 标记正确"
expect "$(echo "$D" | py 'len(d["logs"])')" "1" "建单时的附件不另写状态记录"

call POST "/api/orders/$AID/claim" "$ENG" > /dev/null
call GET "/api/orders/$AID" "$ENG" > "$TMP/eng.json"
expect "$(py 'str(len(d["attachments"]))+"/"+str(d["canAddAttachment"])+"/"+str(d["canRestrictAttachment"])+"/"+str(d["attachmentRemaining"])' < "$TMP/eng.json")" "1/True/False/18" "工程师详情：只见 1 个附件，名额仍为 18"
expect "$(grep -c '报价单' "$TMP/eng.json" || true)" "0" "工程师详情全文不含受限文件名"
expect "$(py 'any(l["kind"]=="AttachmentPrivate" for l in d["logs"])' < "$TMP/eng.json")" "False" "工程师 logs 不含 AttachmentPrivate"

EF=$(upok "$ENG" "$TMP/c.pdf" Attachment c.pdf "现场记录.pdf" | py 'd["id"]')
D=$(call POST "/api/orders/$AID/attachments" "$ENG" "{\"attachments\":[{\"fileId\":$EF}]}")
expect "$(echo "$D" | py 'str(len(d["attachments"]))+"/"+d["logs"][-1]["text"]+"/"+d["logs"][-1]["kind"]')" "2/王工 上传附件 现场记录.pdf/Attachment" "工程师添加附件 + 状态记录"
EF2=$(upok "$ENG" "$TMP/c.pdf" Attachment c.pdf "x.pdf" | py 'd["id"]')
req POST "/api/orders/$AID/attachments" "$ENG" "{\"attachments\":[{\"fileId\":$EF2,\"restricted\":true}]}"
expect "$(cat "$TMP/status")/$(py 'd["message"]' < "$TMP/body")" "403/只有销售和主管可以设置「仅销售和主管可见」" "工程师设受限 → 403"
LF=$(upok "$LI" "$TMP/c.pdf" Attachment c.pdf "y.pdf" | py 'd["id"]')
req POST "/api/orders/$AID/attachments" "$LI" "{\"attachments\":[{\"fileId\":$LF}]}"
expect "$(cat "$TMP/status")/$(py 'd["message"]' < "$TMP/body")" "403/无权查看该工单" "无关工程师添加 → 403"
req POST "/api/orders/$AID/attachments" "$ENG" '{"attachments":[]}'
expect "$(cat "$TMP/status")/$(py 'd["message"]' < "$TMP/body")" "400/请选择要添加的附件" "空附件列表 → 400"

echo "== v1.2 附件：下载"
ST=$(curl -s -D "$TMP/hdr" -o "$TMP/dl" -w "%{http_code}" -H "Authorization: Bearer $ENG" "$BASE/api/files/$OPEN/download")
expect "$ST" "200" "工程师下载公开附件 → 200"
expect "$(grep -i '^content-disposition:' "$TMP/hdr" | grep -c "filename\*=UTF-8''%E7%8E%B0%E5%9C%BA%E8%AF%B4%E6%98%8E.pdf")" "1" "Content-Disposition 含 filename*=UTF-8'' 原始文件名"
expect "$(grep -i '^content-disposition:' "$TMP/hdr" | grep -c '^[Cc]ontent-[Dd]isposition: attachment')" "1" "Content-Disposition: attachment"
expect "$(grep -i '^content-type:' "$TMP/hdr" | tr -d '\r' | cut -d' ' -f2)" "application/pdf" "下载 Content-Type = 存储的 MIME"
expect "$(cmp -s "$TMP/dl" "$TMP/b.pdf" && echo same)" "same" "下载内容与上传一致"
ST=$(curl -s -o "$TMP/part" -w "%{http_code}" -H "Authorization: Bearer $ENG" -H "Range: bytes=0-4" "$BASE/api/files/$OPEN/download")
expect "$ST/$(cat "$TMP/part")" "206/%PDF-" "下载支持 Range"
req GET "/api/files/$SECRET/download" "$ENG"
expect "$(cat "$TMP/status")/$(py 'd["message"]' < "$TMP/body")" "404/文件不存在" "工程师下载受限附件 → 404"
req GET "/api/files/$OPEN/download" ""
expect "$(cat "$TMP/status")/$(py 'd["code"]' < "$TMP/body")" "401/40100" "未带 token 下载 → 401"
req GET "/api/files/$OPEN/download?token=$ENG" ""
expect "$(cat "$TMP/status")" "401" "query token 不被接受 → 401"
req GET "/api/files/$OPEN/download" "$LI"
expect "$(cat "$TMP/status")/$(py 'd["message"]' < "$TMP/body")" "403/无权查看该工单" "无关工程师下载 → 403"
ST=$(curl -s -o "$TMP/dl" -w "%{http_code}" -H "Authorization: Bearer $SALES" "$BASE/api/files/$SECRET/download")
expect "$ST/$(cmp -s "$TMP/dl" "$TMP/a.pdf" && echo same)" "200/same" "销售下载受限附件 → 200"

echo "== v1.2 附件：切换受限 / 删除"
D=$(call POST "/api/orders/$AID/attachments/$EF/restricted" "$SUP" '{"restricted":true}')
expect "$(echo "$D" | py '[a["restricted"] for a in d["attachments"] if a["id"]=='"$EF"'][0]')" "True" "主管把工程师的附件设为受限"
expect "$(echo "$D" | py 'd["logs"][-1]["text"]+"/"+d["logs"][-1]["kind"]')" "赵主管 将附件 现场记录.pdf 设为仅销售和主管可见/AttachmentPrivate" "设为受限的状态记录"
expect "$(call GET "/api/orders/$AID" "$ENG" | py 'any(a["id"]=='"$EF"' for a in d["attachments"])')" "False" "工程师详情里该附件消失"
req POST "/api/orders/$AID/attachments/$EF/delete" "$ENG"
expect "$(cat "$TMP/status")/$(py 'd["message"]' < "$TMP/body")" "404/附件不存在" "工程师删除已受限的自己的附件 → 404"
req POST "/api/orders/$AID/attachments/$EF/restricted" "$SALES" '{"restricted":false}'
expect "$(cat "$TMP/status")" "403" "销售切换受限 → 403"
D=$(call POST "/api/orders/$AID/attachments/$EF/restricted" "$SUP" '{"restricted":false}')
expect "$(echo "$D" | py 'd["logs"][-1]["text"]')" "赵主管 将附件 现场记录.pdf 设为所有人可见" "再设回公开"
expect "$(call GET "/api/orders/$AID" "$ENG" | py '[a["canDelete"] for a in d["attachments"] if a["id"]=='"$EF"']')" "[true]" "工程师又能看到并可删除"
req POST "/api/orders/$AID/attachments/$OPEN/delete" "$ENG"
expect "$(cat "$TMP/status")/$(py 'd["message"]' < "$TMP/body")" "403/只能删除自己上传的附件" "工程师删除销售的附件 → 403"
D=$(call POST "/api/orders/$AID/attachments/$EF/delete" "$ENG")
expect "$(echo "$D" | py 'str(len(d["attachments"]))+"/"+d["logs"][-1]["text"]')" "1/王工 删除附件 现场记录.pdf" "工程师删除自己的附件"
D=$(call POST "/api/orders/$AID/attachments/$OPEN/delete" "$SUP")
expect "$(echo "$D" | py 'str(len(d["attachments"]))+"/"+str(d["attachmentRemaining"])')" "1/19" "主管删除销售的附件，名额释放"
req POST "/api/orders/$AID/attachments/$OPEN/delete" "$SUP"
expect "$(cat "$TMP/status")/$(py 'd["message"]' < "$TMP/body")" "404/附件不存在" "再次删除 → 404"
req GET "/api/files/$OPEN/download" "$SUP"
expect "$(cat "$TMP/status")" "404" "已删除的附件不能下载"

echo "== v1.2 附件：旧请求体兼容"
OLDP=$(upload "$SALES" OrderPhoto)
EXTRA="\"photoFileIds\":[$OLDP]"
BODY=$(ORDER_BODY "旧版建单客户" "$EXTRA")
D=$(call POST /api/orders "$SALES" "$BODY")
OLDID=$(echo "$D" | py 'd["id"]')
expect "$(echo "$D" | py 'str(len(d["files"]["orderPhotos"]))+"/"+str(len(d["attachments"]))+"/"+d["attachments"][0]["source"]+"/"+d["attachments"][0]["category"]')" "1/1/OrderPhoto/OrderPhoto" "photoFileIds 建单：orderPhotos 1 个且 attachments 1 个"

echo "== v1.2 文件中心"
FC=$(call GET "/api/admin/files" "$SUP")
expect "$(echo "$FC" | py 'all(k in d for k in ("items","total","totalSize","page","pageSize","counts")) and d["pageSize"]==30 and d["total"]>0 and d["totalSize"]>0')" "True" "文件中心：total / totalSize / counts"
expect "$(echo "$FC" | py 'sorted(d["counts"].keys())')" '["Document", "Image", "Video", "all"]' "counts 键"
expect "$(echo "$FC" | py 'd["counts"]["all"]==d["total"]==d["counts"]["Image"]+d["counts"]["Video"]+d["counts"]["Document"]')" "True" "counts.all = total = 三类之和"
expect "$(echo "$FC" | py '[i["createdAt"] for i in d["items"]]==sorted([i["createdAt"] for i in d["items"]], reverse=True)')" "True" "文件中心按时间倒序"
expect "$(echo "$FC" | py 'all(k in d["items"][0] for k in ("name","size","kind","ext","source","sourceLabel","restricted","uploader","canDelete","canToggleRestricted","orderId","orderNo","customerName","orderStatus"))')" "True" "FileCenterItem 字段"
R=$(call GET "/api/admin/files?type=Document&pageSize=200" "$SUP")
expect "$(echo "$R" | py 'd["total"]>=1 and all(i["kind"]=="Document" for i in d["items"]) and d["total"]==d["counts"]["Document"]')" "True" "type=Document"
R=$(call GET "/api/admin/files?source=PunchPhoto&pageSize=200" "$SUP")
expect "$(echo "$R" | py 'd["total"]>=1 and all(i["source"]=="PunchPhoto" and i["sourceLabel"]=="打卡照片" and not i["canDelete"] for i in d["items"])')" "True" "source=PunchPhoto"
R=$(call GET "/api/admin/files?orderId=$AID" "$SUP")
expect "$(echo "$R" | py 'str(d["total"])+"/"+d["items"][0]["name"]+"/"+str(d["items"][0]["restricted"])+"/"+str(d["items"][0]["canDelete"])+"/"+str(d["items"][0]["canToggleRestricted"])')" "1/报价单.pdf/True/True/True" "orderId 筛选（已删除的不出现）"
R=$(call GET "/api/admin/files?keyword=%E6%8A%A5%E4%BB%B7" "$SUP")
expect "$(echo "$R" | py 'd["total"]>=1 and all("报价" in i["name"] for i in d["items"])')" "True" "keyword=报价"
R=$(call GET "/api/admin/files?restricted=true&pageSize=200" "$SUP")
expect "$(echo "$R" | py 'd["total"]>=1 and all(i["restricted"] for i in d["items"])')" "True" "restricted=true"
R=$(call GET "/api/admin/files?source=OrderPhoto&orderId=$OLDID" "$SUP")
expect "$(echo "$R" | py 'str(d["total"])+"/"+d["items"][0]["sourceLabel"]')" "1/建单照片" "旧建单照片出现在文件中心"
req GET "/api/admin/files?type=Nope" "$SUP"
expect "$(cat "$TMP/status")/$(py 'd["message"]' < "$TMP/body")" "400/文件类型不正确" "type 非法 → 400"
req GET "/api/admin/files?source=Nope" "$SUP"
expect "$(cat "$TMP/status")/$(py 'd["message"]' < "$TMP/body")" "400/文件来源不正确" "source 非法 → 400"
req GET /api/admin/files "$ENG"; expect "$(cat "$TMP/status")" "403" "工程师访问文件中心 → 403"
req GET /api/admin/files "$SALES"; expect "$(cat "$TMP/status")" "403" "销售访问文件中心 → 403"

echo "== v1.2 静态资源"
upath() { echo "$1" | sed 's#^.*://[^/]*##'; }
shead() { curl -s -D - -o /dev/null "$BASE$1" | tr -d '\r'; }   # 匿名访问静态地址，只看响应头
scode() { curl -s -o /dev/null -w "%{http_code}" "$BASE$1"; }
# 图片：匿名可访问、nosniff、不强制下载
F=$(upok "$SALES" "$TMP/p.png" Attachment p.png "静态图.png"); IMGID=$(echo "$F" | py 'd["id"]'); IMGPATH=$(upath "$(echo "$F" | py 'd["url"]')")
expect "$(shead "$IMGPATH" | grep -ci '^x-content-type-options: nosniff')" "1" "/uploads 响应头含 X-Content-Type-Options: nosniff"
expect "$(shead "$IMGPATH" | grep -ci '^content-disposition')" "0" "/uploads 图片不带 Content-Disposition（可直接预览）"
# 文档：公开的可取但强制下载；受限 / 已删除的静态地址 404
F=$(upok "$SALES" "$TMP/a.pdf" Attachment a.pdf "静态公开.pdf"); PUBID=$(echo "$F" | py 'd["id"]'); PUBPATH=$(upath "$(echo "$F" | py 'd["url"]')")
call POST "/api/orders/$AID/attachments" "$SALES" "{\"attachments\":[{\"fileId\":$PUBID},{\"fileId\":$IMGID,\"restricted\":true}]}" > /dev/null
expect "$(scode "$PUBPATH")" "200" "/uploads 公开文档 → 200"
expect "$(shead "$PUBPATH" | grep -ci '^content-disposition: attachment')" "1" "/uploads 文档以 attachment 输出（不在本站源下渲染）"
expect "$(shead "$PUBPATH" | grep -ci '^x-content-type-options: nosniff')" "1" "/uploads 文档响应头含 nosniff"
expect "$(scode "$(upath "$SECRETURL")")" "404" "/uploads 受限文档的静态地址 → 404"
expect "$(curl -s -o /dev/null -w "%{http_code}" -H "Authorization: Bearer $SALES" "$BASE/api/files/$SECRET/download")" "200" "受限文档仍可经鉴权下载接口取得"
expect "$(scode "$(upath "$OPENURL")")" "404" "/uploads 已删除文档的静态地址 → 404"
expect "$(scode "$IMGPATH")" "200" "/uploads 受限图片的静态地址仍可访问（预览依赖，契约 §12.1）"
call POST "/api/orders/$AID/attachments/$PUBID/restricted" "$SUP" '{"restricted":true}' > /dev/null
expect "$(scode "$PUBPATH")" "404" "文档设为受限后静态地址立即 404"
D=$(call GET "/api/orders/$AID" "$ENG")
expect "$(echo "$D" | grep -c '静态公开' || true)" "0" "文档设为受限后，工程师的详情（含状态记录）不再出现其文件名"
call POST "/api/orders/$AID/attachments/$PUBID/restricted" "$SUP" '{"restricted":false}' > /dev/null
expect "$(scode "$PUBPATH")" "200" "取消受限后静态地址恢复"
# 头像与工单文件不能互相挪用
req PUT /api/me/avatar "$SALES" "{\"fileId\":$IMGID}"
expect "$(cat "$TMP/status")/$(py 'd["message"]' < "$TMP/body")" "400/头像文件已用于工单，请重新上传" "工单附件不能设为头像"
# HEIC（ftyp heic）上传后静态地址可访问
python3 -c "import sys; open(sys.argv[1],'wb').write(b'\x00\x00\x00\x18ftypheic'+b'\x00'*64)" "$TMP/h.heic"
F=$(upok "$SALES" "$TMP/h.heic" Attachment h.heic)
expect "$(echo "$F" | py 'd["mimeType"]')" "image/heic" "HEIC 上传 → image/heic"
expect "$(scode "$(upath "$(echo "$F" | py 'd["url"]')")")" "200" "/uploads HEIC 静态地址 → 200"
# 以可见字符开头、偏移 4 恰为 free 的文本不被误判为视频
printf 'The free trial ends tomorrow\n' > "$TMP/free.txt"
expect "$(upok "$SALES" "$TMP/free.txt" Attachment free.txt "free.txt" | py 'd["mimeType"]')" "text/plain" "「The free…」文本作为 txt 上传成功"
expect "$(curl -s -o /dev/null -w "%{http_code}" "$BASE/admin/js/views/files.js")" "200" "GET /admin/js/views/files.js → 200"

echo
echo "全部通过：$PASS 项"
