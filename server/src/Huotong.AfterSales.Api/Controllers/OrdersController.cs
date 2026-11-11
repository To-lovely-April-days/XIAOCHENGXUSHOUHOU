using Huotong.AfterSales.Api.Dtos;
using Huotong.AfterSales.Api.Infrastructure;
using Huotong.AfterSales.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Huotong.AfterSales.Api.Controllers;

[Route("api/orders")]
[Authorize]
public class OrdersController : ApiControllerBase
{
    private readonly OrderService _orders;

    public OrdersController(OrderService orders) => _orders = orders;

    [HttpGet("hall")]
    public async Task<ApiResponse<ListResult<OrderListItem>>> Hall([FromQuery] string? filter) => Ok(await _orders.Hall(filter, Me));

    [HttpGet("mine")]
    [Authorize(Roles = "Engineer")]
    public async Task<ApiResponse<ListResult<OrderListItem>>> Mine([FromQuery] string? group) => Ok(await _orders.Mine(Me, group));

    [HttpGet("created")]
    public async Task<ApiResponse<ListResult<OrderListItem>>> Created([FromQuery] string? group) => Ok(await _orders.Created(Me, group));

    [HttpGet]
    [Authorize(Roles = "Sales,Supervisor")]
    public async Task<ApiResponse<PagedOrders>> Query([FromQuery] string? status, [FromQuery] string? keyword, [FromQuery] string? issue,
        [FromQuery] int? assigneeId, [FromQuery] string? serviceType, [FromQuery] string? from, [FromQuery] string? to,
        [FromQuery] string? priceSet = null, [FromQuery] bool? pendingRequest = null,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20) =>
        Ok(await _orders.Query(new OrderQuery
        {
            Status = status, Keyword = keyword, Issue = issue, AssigneeId = assigneeId, ServiceType = serviceType,
            From = from, To = to, PriceSet = priceSet, PendingRequest = pendingRequest, Page = page, PageSize = pageSize
        }, Me));

    [HttpGet("recent-customers")]
    public async Task<ApiResponse<List<RecentCustomerDto>>> RecentCustomers() => Ok(await _orders.RecentCustomers(Me));

    [HttpGet("{id:int}")]
    public async Task<ApiResponse<OrderDetail>> Detail(int id) => Ok(await _orders.Detail(id, Me));

    [HttpPost]
    public async Task<ApiResponse<OrderDetail>> Create([FromBody] CreateOrderRequest req) => Ok(await _orders.Create(req, Me));

    [HttpPost("{id:int}/claim")]
    [Authorize(Roles = "Engineer")]
    public async Task<ApiResponse<OrderDetail>> Claim(int id) => Ok(await _orders.Claim(id, Me));

    [HttpPost("{id:int}/appointment")]
    [Authorize(Roles = "Engineer")]
    public async Task<ApiResponse<OrderDetail>> Appointment(int id, [FromBody] AppointmentRequest req) => Ok(await _orders.Appointment(id, req, Me));

    [HttpPost("{id:int}/start")]
    [Authorize(Roles = "Engineer")]
    public async Task<ApiResponse<OrderDetail>> Start(int id) => Ok(await _orders.Start(id, Me));

    [HttpPost("{id:int}/complete")]
    [Authorize(Roles = "Engineer")]
    public async Task<ApiResponse<OrderDetail>> Complete(int id, [FromBody] CompleteRequest req) => Ok(await _orders.Complete(id, req, Me));

    [HttpPost("{id:int}/assign")]
    [Authorize(Roles = "Supervisor")]
    public async Task<ApiResponse<OrderDetail>> Assign(int id, [FromBody] AssignRequest req) => Ok(await _orders.Assign(id, req, Me));

    [HttpPost("{id:int}/void")]
    [Authorize(Roles = "Supervisor")]
    public async Task<ApiResponse<OrderDetail>> Void(int id, [FromBody] ReasonRequest? req) => Ok(await _orders.Void(id, req ?? new(), Me));

    [HttpPost("{id:int}/hold")]
    [Authorize(Roles = "Supervisor")]
    public async Task<ApiResponse<OrderDetail>> Hold(int id, [FromBody] HoldRequest? req) => Ok(await _orders.Hold(id, req ?? new(), Me));

    [HttpPost("{id:int}/resume")]
    [Authorize(Roles = "Supervisor")]
    public async Task<ApiResponse<OrderDetail>> Resume(int id) => Ok(await _orders.Resume(id, Me));

    [HttpPost("{id:int}/reject")]
    [Authorize(Roles = "Supervisor")]
    public async Task<ApiResponse<OrderDetail>> Reject(int id, [FromBody] ReasonRequest? req) => Ok(await _orders.Reject(id, req ?? new(), Me));

    [HttpPost("{id:int}/close")]
    [Authorize(Roles = "Supervisor")]
    public async Task<ApiResponse<OrderDetail>> Close(int id) => Ok(await _orders.Close(id, Me));

    // ---------- v1.1：价格 ----------

    [HttpPost("{id:int}/price")]
    [Authorize(Roles = "Supervisor")]
    public async Task<ApiResponse<OrderDetail>> SetPrice(int id, [FromBody] PriceRequest? req) => Ok(await _orders.SetPrice(id, req ?? new(), Me));

    [HttpPost("{id:int}/price-suggestions")]
    [Authorize(Roles = "Sales")]
    public async Task<ApiResponse<OrderDetail>> SuggestPrice(int id, [FromBody] PriceSuggestRequest? req) =>
        Ok(await _orders.SuggestPrice(id, req ?? new(), Me));

    // ---------- v1.1：暂停申请 / 协助人 / 审批 ----------

    [HttpPost("{id:int}/hold-requests")]
    [Authorize(Roles = "Engineer")]
    public async Task<ApiResponse<OrderDetail>> RequestHold(int id, [FromBody] HoldRequest? req) => Ok(await _orders.RequestHold(id, req ?? new(), Me));

    [HttpPost("{id:int}/assistants")]
    [Authorize(Roles = "Supervisor")]
    public async Task<ApiResponse<OrderDetail>> SetAssistants(int id, [FromBody] AssistantsRequest? req) =>
        Ok(await _orders.SetAssistants(id, req ?? new(), Me));

    [HttpPost("{id:int}/assistant-requests")]
    [Authorize(Roles = "Engineer")]
    public async Task<ApiResponse<OrderDetail>> RequestAssistant(int id, [FromBody] AssistantRequestRequest? req) =>
        Ok(await _orders.RequestAssistant(id, req ?? new(), Me));

    [HttpPost("{id:int}/requests/{requestId:int}/approve")]
    [Authorize(Roles = "Supervisor")]
    public async Task<ApiResponse<OrderDetail>> ApproveRequest(int id, int requestId) => Ok(await _orders.ApproveRequest(id, requestId, Me));

    [HttpPost("{id:int}/requests/{requestId:int}/reject")]
    [Authorize(Roles = "Supervisor")]
    public async Task<ApiResponse<OrderDetail>> RejectRequest(int id, int requestId, [FromBody] ReplyRequest? req) =>
        Ok(await _orders.RejectRequest(id, requestId, req ?? new(), Me));

    [HttpPost("{id:int}/requests/{requestId:int}/cancel")]
    [Authorize(Roles = "Engineer")]
    public async Task<ApiResponse<OrderDetail>> CancelRequest(int id, int requestId) => Ok(await _orders.CancelRequest(id, requestId, Me));

    // ---------- v1.1：返厂流程（任何角色，服务端按节点判权）----------

    [HttpPost("{id:int}/factory-nodes")]
    public async Task<ApiResponse<OrderDetail>> FactoryNode(int id, [FromBody] FactoryNodeRequest? req) =>
        Ok(await _orders.RecordFactoryNode(id, req ?? new(), Me));

    // ---------- v1.2：附件（任何角色，服务端按 AttachmentRules 判权）----------

    [HttpPost("{id:int}/attachments")]
    public async Task<ApiResponse<OrderDetail>> AddAttachments(int id, [FromBody] AttachmentsRequest? req) =>
        Ok(await _orders.AddAttachments(id, req ?? new(), Me));

    [HttpPost("{id:int}/attachments/{fileId:int}/delete")]
    public async Task<ApiResponse<OrderDetail>> DeleteAttachment(int id, int fileId) => Ok(await _orders.DeleteAttachment(id, fileId, Me));

    [HttpPost("{id:int}/attachments/{fileId:int}/restricted")]
    [Authorize(Roles = "Supervisor")]
    public async Task<ApiResponse<OrderDetail>> SetAttachmentRestricted(int id, int fileId, [FromBody] RestrictedRequest? req) =>
        Ok(await _orders.SetAttachmentRestricted(id, fileId, req ?? new(), Me));

    /// <summary>v1.3：修改附件类别（上传人本人或主管；任何角色，服务端判权）</summary>
    [HttpPost("{id:int}/attachments/{fileId:int}/type")]
    public async Task<ApiResponse<OrderDetail>> SetAttachmentType(int id, int fileId, [FromBody] AttachmentTypeRequest? req) =>
        Ok(await _orders.SetAttachmentType(id, fileId, req ?? new(), Me));

    // ---------- v1.3：SAT 信息（任何角色，服务端判权）----------

    [HttpPost("{id:int}/sat")]
    public async Task<ApiResponse<OrderDetail>> SetSat(int id, [FromBody] SatRequest? req) => Ok(await _orders.SetSat(id, req ?? new(), Me));

    // ---------- v1.3：转交（负责工程师发起，接手人确认 / 拒绝；不需要主管批准）----------

    [HttpPost("{id:int}/transfers")]
    [Authorize(Roles = "Engineer")]
    public async Task<ApiResponse<OrderDetail>> Transfer(int id, [FromBody] TransferRequest? req) => Ok(await _orders.Transfer(id, req ?? new(), Me));

    [HttpPost("{id:int}/transfers/{transferId:int}/accept")]
    [Authorize(Roles = "Engineer")]
    public async Task<ApiResponse<OrderDetail>> AcceptTransfer(int id, int transferId) => Ok(await _orders.AcceptTransfer(id, transferId, Me));

    /// <summary>拒绝接手：拒绝后当前用户已无查看权时 data 为 null（全系统唯一可能返回 null 的工单动作）</summary>
    [HttpPost("{id:int}/transfers/{transferId:int}/reject")]
    [Authorize(Roles = "Engineer")]
    public async Task<ApiResponse<OrderDetail?>> RejectTransfer(int id, int transferId, [FromBody] ReplyRequest? req) =>
        Ok(await _orders.RejectTransfer(id, transferId, req ?? new(), Me));

    /// <summary>发起人撤回 / 主管撤销</summary>
    [HttpPost("{id:int}/transfers/{transferId:int}/cancel")]
    [Authorize(Roles = "Engineer,Supervisor")]
    public async Task<ApiResponse<OrderDetail>> CancelTransfer(int id, int transferId) => Ok(await _orders.CancelTransfer(id, transferId, Me));
}
