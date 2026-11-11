using Huotong.AfterSales.Api.Domain;
using Huotong.AfterSales.Api.Dtos;
using Huotong.AfterSales.Api.Infrastructure;
using Huotong.AfterSales.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Huotong.AfterSales.Api.Controllers;

/// <summary>
/// 在模型绑定之前读取表单：请求体超过接口上限时返回「文件不能超过 100MB」（CHANGES-v1.2 §3.1 第 6 步），
/// 否则 MVC 会把读取失败吞成模型校验错误「请求参数格式错误」。读成功的表单会被缓存，模型绑定直接复用。
/// </summary>
public sealed class UploadTooLargeFilterAttribute : Attribute, IAsyncResourceFilter
{
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var req = context.HttpContext.Request;
        if (req.HasFormContentType)
        {
            try
            {
                await req.ReadFormAsync(context.HttpContext.RequestAborted);
            }
            catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
            {
                throw BizException.BadRequest("文件不能超过 100MB");
            }
            catch (InvalidDataException ex) when (ex.Message.Contains("length limit", StringComparison.OrdinalIgnoreCase))
            {
                throw BizException.BadRequest("文件不能超过 100MB");
            }
        }
        await next();
    }
}

[Route("api/files")]
[Authorize]
public class FilesController : ApiControllerBase
{
    private readonly FileService _files;

    public FilesController(FileService files) => _files = files;

    [HttpPost]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(FileService.MaxAttachmentBytes + 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = FileService.MaxAttachmentBytes + 1024 * 1024)]
    [UploadTooLargeFilter]
    public async Task<ApiResponse<FileDto>> Upload(IFormFile? file, [FromForm] string? category, [FromForm] string? fileName = null) =>
        Ok(await _files.Upload(file, category, Me, fileName));

    /// <summary>
    /// 鉴权下载（v1.2）：返回文件原始内容，Content-Disposition 带原始文件名（filename* 为 UTF-8）；支持 Range。
    /// 失败时由 ExceptionMiddleware 返回 JSON 包装体。
    /// </summary>
    [HttpGet("{id:int}/download")]
    public async Task<IActionResult> Download(int id)
    {
        var info = await _files.ResolveDownload(id, Me);
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return PhysicalFile(info.FullPath, info.MimeType, info.Name, enableRangeProcessing: true);
    }
}
