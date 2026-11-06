using Huotong.AfterSales.Api.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Huotong.AfterSales.Api.Controllers;

[ApiController]
[Produces("application/json")]
public abstract class ApiControllerBase : ControllerBase
{
    protected CurrentUser Me => User.ToCurrentUser();

    protected static ApiResponse<T> Ok<T>(T data) => ApiResponse.Ok(data);
}
