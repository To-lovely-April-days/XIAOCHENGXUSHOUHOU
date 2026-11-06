using Huotong.AfterSales.Api.Dtos;
using Huotong.AfterSales.Api.Infrastructure;
using Huotong.AfterSales.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Huotong.AfterSales.Api.Controllers;

[Route("api/files")]
[Authorize]
public class FilesController : ApiControllerBase
{
    private readonly FileService _files;

    public FilesController(FileService files) => _files = files;

    [HttpPost]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(FileService.MaxBytes + 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = FileService.MaxBytes + 1024 * 1024)]
    public async Task<ApiResponse<FileDto>> Upload(IFormFile? file, [FromForm] string? category) =>
        Ok(await _files.Upload(file, category, Me));
}
