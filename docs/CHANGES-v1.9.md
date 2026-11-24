# v1.9 文件中心按工单归档；维修登记表拆成现场 / 返厂

## 附件类别

`AttachmentType`：`Sat`、`OnsiteRepairForm`（现场维修登记表）、`FactoryRepairForm`（返厂维修登记表）、`Other`。展示顺序即此顺序。

- 旧值 `RepairForm` 只为兼容旧客户端保留：请求里收到它时，按工单的服务方式落库 —— 返厂工单 → `FactoryRepairForm`，其余 → `OnsiteRepairForm`。
  文件中心用 `attachmentType=RepairForm` 筛选时等于两类维修登记表。
- 迁移 `V19_SplitRepairForm`（只改数据）：库里已有的 `RepairForm` 按同样的规则拆开。
- 两个前端的 `enums.js` 同步为四类；建单、工单详情、文件中心的类别下拉和分组随之变成四组。

## 文件中心：一个工单一个文件夹（电脑后台）

- 「文件」页默认是 **按工单**：每行一个工单文件夹，显示工单号、状态、客户 / 项目、签字单据是否已传（SAT 验收单、对应的维修登记表）、文件数、大小、最近上传时间；可按工单号 / 客户 / 项目编号 / 文件名搜索。
- 点开文件夹：这张工单的全部文件，按 SAT 验收单 → 现场维修登记表 → 返厂维修登记表 → 其他附件 → 打卡照片 / 完工照片 / 签字件 / 返厂照片 分组；
  SAT 验收单和该工单应有的那种维修登记表即使没传也显示「还没有上传」。可预览、下载。
- 「管理这些文件」跳到 **全部文件** 视图并按该工单筛选，在那里删除、改类别、改可见范围（原有功能不变）。
- 新接口 `GET /api/admin/files/folders?keyword=&page=&pageSize=` → `{items:[{orderId, orderNo, customerName, projectNo, deviceText, status, serviceType, fileCount, totalSize, lastUploadAt, satCount, onsiteRepairCount, factoryRepairCount}], total, totalFiles, totalSize, page, pageSize}`。
  只列出名下有文件的工单，按最近上传倒序。
