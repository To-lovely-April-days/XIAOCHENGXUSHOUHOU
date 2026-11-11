using System.Text.Json;
using Huotong.AfterSales.Api.Domain;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace Huotong.AfterSales.Api.Infrastructure;

/// <summary>把异常映射为统一包装体</summary>
public class ExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionMiddleware> _logger;

    public ExceptionMiddleware(RequestDelegate next, ILogger<ExceptionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task Invoke(HttpContext ctx)
    {
        try
        {
            await _next(ctx);
        }
        catch (BizException ex)
        {
            await Write(ctx, ex.HttpStatus, ex.Code, ex.Message);
        }
        catch (BadHttpRequestException ex)
        {
            await Write(ctx, ex.StatusCode == 413 ? 400 : 400, 40000, ex.StatusCode == 413 ? "文件不能超过 100MB" : "请求格式错误");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "未处理异常 {Path}", ctx.Request.Path);
            await Write(ctx, 500, 50000, "服务器错误，请稍后重试");
        }
    }

    public static async Task Write(HttpContext ctx, int http, int code, string message)
    {
        if (ctx.Response.HasStarted) return;
        ctx.Response.Clear();
        ctx.Response.StatusCode = http;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        var opts = ctx.RequestServices.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(ApiResponse.Fail(code, message), opts));
    }
}
