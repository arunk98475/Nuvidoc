using Docovee.DS.Models;
using Docovee.BLL.Services;
using Microsoft.AspNetCore.Mvc;

namespace Docovee.Controllers.Api;

[ApiController]
[Route("api/[controller]")]
public class DoctorsController : ControllerBase
{
    private readonly IDoctorSearchService _doctorSearchService;
    private readonly IPublicDoctorService _publicDoctorService;

    public DoctorsController(
        IDoctorSearchService doctorSearchService,
        IPublicDoctorService publicDoctorService)
    {
        _doctorSearchService = doctorSearchService;
        _publicDoctorService = publicDoctorService;
    }

    [HttpGet("featured")]
    public async Task<ActionResult<IReadOnlyList<FeaturedDoctorCardDto>>> GetFeatured(
        [FromQuery] int count = 3,
        CancellationToken cancellationToken = default)
    {
        var results = await _publicDoctorService.GetFeaturedAsync(count, cancellationToken);
        return Ok(results);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<PublicDoctorProfileDto>> GetProfile(
        int id,
        [FromQuery] bool liveGoogleReviews = false,
        CancellationToken cancellationToken = default)
    {
        var profile = await _publicDoctorService.GetPublicProfileAsync(id, liveGoogleReviews, cancellationToken);
        if (profile == null)
            return NotFound();

        return Ok(profile);
    }

    [HttpPost("search")]
    public async Task<ActionResult<IReadOnlyList<DoctorDto>>> Search(
        [FromBody] DoctorSearchRequest request,
        CancellationToken cancellationToken)
    {
        if (request.SessionKey == Guid.Empty)
            return BadRequest("SessionKey is required.");

        var results = await _doctorSearchService.SearchAsync(request, cancellationToken);
        return Ok(results);
    }
}
