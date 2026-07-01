using Docovee.DS.Models;
using Docovee.BLL.Services;
using Microsoft.AspNetCore.Mvc;

namespace Docovee.Controllers.Api;

[ApiController]
[Route("api/[controller]")]
public class DoctorsController : ControllerBase
{
    private readonly IDoctorSearchService _doctorSearchService;

    public DoctorsController(IDoctorSearchService doctorSearchService) => _doctorSearchService = doctorSearchService;

    [HttpPost("search")]
    public async Task<ActionResult<IReadOnlyList<DoctorDto>>> Search([FromBody] DoctorSearchRequest request, CancellationToken cancellationToken)
    {
        if (request.SessionKey == Guid.Empty)
            return BadRequest("SessionKey is required.");

        var results = await _doctorSearchService.SearchAsync(request, cancellationToken);
        return Ok(results);
    }
}
