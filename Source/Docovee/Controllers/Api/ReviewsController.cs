using System.Security.Claims;
using Docovee.BLL.Auth;
using Docovee.DS.Models;
using Docovee.BLL.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Docovee.Controllers.Api;

[ApiController]
[Route("api/[controller]")]
public class ReviewsController : ControllerBase
{
    private readonly IDoctorReviewService _reviewService;

    public ReviewsController(IDoctorReviewService reviewService) => _reviewService = reviewService;

    [HttpGet("doctor/{doctorId:int}")]
    public async Task<ActionResult<IReadOnlyList<DoctorReviewDto>>> GetByDoctor(int doctorId, CancellationToken cancellationToken)
    {
        var reviews = await _reviewService.GetByDoctorAsync(doctorId, cancellationToken);
        return Ok(reviews);
    }

    [Authorize(Roles = AuthRoles.Patient)]
    [HttpPost]
    public async Task<IActionResult> AddReview([FromBody] DoctorReviewRequest request, CancellationToken cancellationToken)
    {
        if (int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var patientId))
        {
            var (success, error) = await _reviewService.AddReviewForPatientAsync(
                patientId, request.DoctorId, request.Rating, request.ReviewText, cancellationToken);
            if (!success)
                return BadRequest(new { message = error });
            return Ok(new { message = "Review submitted successfully." });
        }

        var (anonSuccess, anonError) = await _reviewService.AddReviewAsync(request, cancellationToken);
        if (!anonSuccess)
            return BadRequest(new { message = anonError });
        return Ok(new { message = "Review submitted successfully." });
    }
}
