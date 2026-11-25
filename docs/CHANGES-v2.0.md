# v2.0 三种服务方式各有各的完工资料；升级服务方式；寄回只要快递单照片

对应《三种服务执行流程》：上门、远程、返厂要交的资料不同，工程师 **提交完工时就卡住**。

## 完工表按服务方式区分（`formVersion: 2`）

`POST /api/orders/{id}/complete` 新增：`formVersion`、`beforePhotoIds`、`afterPhotoIds`、`repairFormIds`、`customerConfirm`、`testResult`。

| | 上门 Onsite | 远程 Remote | 返厂 Factory |
|---|---|---|---|
| 照片 | 维修前 ≥1、维修后 ≥1 | 沟通截图，选填 | 维修照片 ≥1 |
| 维修登记表 | 现场维修登记表（签字版）≥1 | — | 返厂维修登记表 ≥1 |
| 其他必填 | — | 客户确认方式：电话确认 / 微信确认 / 邮件确认 / 其他 | 寄回前测试结果 |

- 维修登记表：`repairFormIds` 里的文件在提交时挂成对应类别的附件；**之前已在附件里传过同类别的也算**。
- 维修前 / 后照片仍是完工照片（`ProcessPhoto`），`StoredFiles.PhotoStage` = `Before` / `After`，`FileDto.stage` 返回。
- 上门不再要求「客户签字件」，签字体现在现场维修登记表上；旧单已有的签字件照常显示。
- `formVersion` 不传 / 为 1 = 旧规则，旧版小程序升级前照常能提交。
- `WorkOrders.CustomerConfirm`、`WorkOrders.TestResult`；`CompletionDto.customerConfirm / testResult`。

## 升级服务方式（需主管同意）

远程 → 上门 / 返厂；上门 → 返厂。只能往更重的方向，返厂不能再升。

- 负责工程师：`POST /api/orders/{id}/escalate-requests` `{targetServiceType, reason, findings}`（原因、已排查情况都必填）。工单在待启动 / 处理中才能申请；同一时间只能有一条待审批。
- 新动作 `requestEscalate`；新申请类型 `OrderRequestType.Escalate`（`RequestDto.targetServiceType`，`text` = 「升级为上门服务：原因；已排查：…」）。
- 主管同意（沿用 `requests/{id}/approve`）：改 `ServiceType`，状态回到 **待启动**，同一张工单、同一个工单号，原有记录、打卡、工时保留；价格不变，提醒建单人确认是否调整。
  上门转返厂时现场还有人在计时 → `40900 某某还在现场计时，请其先离场打卡`。
- 驳回 / 撤回沿用原接口；工单暂停、提交完工、闭环、作废时待审批的升级申请自动撤销。

## 返厂「寄回客户」

只要 **快递单照片**（≥1 张，`40000 请上传寄回的快递单照片`），快递公司、单号改为选填；没填时进度里写「见快递单照片」。「客户寄出」不变。

## 这次没做（下一步）

主管验收面板里的资料清单；返厂收货的外观 / 配件 / 检测结果；建单时的运费承担；主管直接改服务方式（现在要工程师先申请）。

## 客户地址只有上门必填

- 建单：`serviceType = Onsite` 时客户地址（和地图选点）必填；远程、返厂可以不填，库里存空串。
- 远程单申请转上门、而工单没有地址 / 坐标时，`escalate-requests` 必须带 `address`、`latitude`、`longitude`（`40000 转为上门服务请在地图上选择客户地址`），提交申请时直接补到工单上并记一条进度。
