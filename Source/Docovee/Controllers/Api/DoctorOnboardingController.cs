using Docovee.DS.Models;
using Docovee.BLL.Services;
using Microsoft.AspNetCore.Mvc;

namespace Docovee.Controllers.Api;

[ApiController]
[Route("api/doctor-onboarding")]
public class DoctorOnboardingController : ControllerBase
{
    private readonly IDoctorOnboardingService _onboarding;

    public DoctorOnboardingController(IDoctorOnboardingService onboarding) => _onboarding = onboarding;

    [HttpPost("message")]
    public async Task<ActionResult<DoctorOnboardingMessageResponse>> SendMessage(
        [FromBody] DoctorOnboardingMessageRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _onboarding.SendMessageAsync(request, HttpContext, cancellationToken);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = "Unable to process onboarding message.", detail = ex.Message });
        }
    }
}
