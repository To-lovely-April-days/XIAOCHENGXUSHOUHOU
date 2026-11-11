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

echo
echo "全部通过：$PASS 项"
