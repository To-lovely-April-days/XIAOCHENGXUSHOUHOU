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
    public async Task<ApiResponse<ListResult<OrderListItem>>> Hall([FromQuery] string? filter) => Ok(await _orders.Hall(filter));

    [HttpGet("mine")]
    [Authorize(Roles = "Engineer")]
    public async Task<ApiResponse<ListResult<OrderListItem>>> Mine([FromQuery] string? group) => Ok(await _orders.Mine(Me, group));

    [HttpGet("created")]
    public async Task<ApiResponse<ListResult<OrderListItem>>> Created([FromQuery] string? group) => Ok(await _orders.Created(Me, group));

    [HttpGet]
    [Authorize(Roles = "Sales,Supervisor")]
    public async Task<ApiResponse<PagedOrders>> Query([FromQuery] string? status, [FromQuery] string? keyword, [FromQuery] string? issue,
        [FromQuery] int? assigneeId, [FromQuery] string? serviceType, [FromQuery] string? from, [FromQuery] string? to,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20) =>
        Ok(await _orders.Query(new OrderQuery
        {
            Status = status, Keyword = keyword, Issue = issue, AssigneeId = assigneeId, ServiceType = serviceType,
            From = from, To = to, Page = page, PageSize = pageSize
        }));

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
    public async Task<ApiResponse<OrderDetail>> Hold(int id, [FromBody] ReasonRequest? req) => Ok(await _orders.Hold(id, req ?? new(), Me));

    [HttpPost("{id:int}/resume")]
    [Authorize(Roles = "Supervisor")]
    public async Task<ApiResponse<OrderDetail>> Resume(int id) => Ok(await _orders.Resume(id, Me));

    [HttpPost("{id:int}/reject")]
    [Authorize(Roles = "Supervisor")]
    public async Task<ApiResponse<OrderDetail>> Reject(int id, [FromBody] ReasonRequest? req) => Ok(await _orders.Reject(id, req ?? new(), Me));

    [HttpPost("{id:int}/close")]
    [Authorize(Roles = "Supervisor")]
    public async Task<ApiResponse<OrderDetail>> Close(int id) => Ok(await _orders.Close(id, Me));
}
