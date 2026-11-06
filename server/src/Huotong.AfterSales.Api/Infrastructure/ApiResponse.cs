namespace Huotong.AfterSales.Api.Infrastructure;

/// <summary>统一响应包装 { code, message, data }</summary>
public class ApiResponse<T>
{
    public int Code { get; set; }
    public string Message { get; set; } = "ok";
    public T? Data { get; set; }
}

public static class ApiResponse
{
    public static ApiResponse<T> Ok<T>(T data) => new() { Code = 0, Message = "ok", Data = data };
    public static ApiResponse<object> Fail(int code, string message) => new() { Code = code, Message = message, Data = null };
}
