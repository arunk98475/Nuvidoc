using Docovee.BLL.Models;
using Docovee.BLL.Services;
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

    [HttpPost]
    public async Task<IActionResult> AddReview([FromBody] DoctorReviewRequest request, CancellationToken cancellationToken)
    {
        var (success, error) = await _reviewService.AddReviewAsync(request, cancellationToken);
        if (!success)
            return BadRequest(new { message = error });
        return Ok(new { message = "Review submitted successfully." });
    }
}
