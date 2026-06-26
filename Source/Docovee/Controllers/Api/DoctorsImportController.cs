using Docovee.BLL.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Docovee.Controllers.Api;

[ApiController]
[Authorize(Policy = AdminAuthService.AdminRole)]
[Route("api/doctors/import")]
public class DoctorsImportController : ControllerBase
{
    private readonly IDoctorImportJobService _importJobService;

    public DoctorsImportController(IDoctorImportJobService importJobService) => _importJobService = importJobService;

    [HttpPost]
    [RequestSizeLimit(50 * 1024 * 1024)]
    public async Task<IActionResult> StartImport(IFormFile file, CancellationToken cancellationToken)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { message = "Please select a CSV or Excel file." });

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext is not ".csv" and not ".xlsx" and not ".xls")
            return BadRequest(new { message = "Unsupported file type. Use .csv or .xlsx." });

        await using var ms = new MemoryStream();
        await file.CopyToAsync(ms, cancellationToken);
        var jobId = await _importJobService.StartImportAsync(ms.ToArray(), file.FileName, cancellationToken);
        return Ok(new { jobId });
    }

    [HttpGet("{jobId}")]
    public IActionResult GetStatus(string jobId)
    {
        var status = _importJobService.GetStatus(jobId);
        if (status == null)
            return NotFound(new { message = "Import job not found." });
        return Ok(status);
    }
}
