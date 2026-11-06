namespace Huotong.AfterSales.Api.Domain;

/// <summary>业务异常：code 对应 API.md 的错误码，HTTP 状态 = code / 100</summary>
public class BizException : Exception
{
    public int Code { get; }

    public BizException(int code, string message) : base(message) => Code = code;

    public int HttpStatus => Code / 100;

    public static BizException BadRequest(string msg) => new(40000, msg);
    public static BizException Forbidden(string msg = "无权限") => new(40300, msg);
    public static BizException NotFound(string msg = "不存在") => new(40400, msg);
    public static BizException Conflict(string msg) => new(40900, msg);
}
