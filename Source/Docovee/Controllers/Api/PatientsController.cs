using System.Security.Claims;
using Docovee.BLL.Auth;
using Docovee.DS.Models;
using Docovee.BLL.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Docovee.Controllers.Api;

[ApiController]
[Route("api/[controller]")]
public class PatientsController : ControllerBase
{
    private readonly IPatientService _patientService;
    private readonly IPatientDoctorContactService _contactViews;

    public PatientsController(IPatientService patientService, IPatientDoctorContactService contactViews)
    {
        _patientService = patientService;
        _contactViews = contactViews;
    }

    [HttpPost("register")]
    public async Task<ActionResult<PatientRegisterResponse>> Register([FromBody] PatientRegisterRequest request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.Email) || !string.IsNullOrWhiteSpace(request.Username))
        {
            if (string.IsNullOrWhiteSpace(request.Password))
                return BadRequest("Password is required.");
        }
        else
            return BadRequest("Email or username is required.");

        var result = await _patientService.RegisterAsync(request, cancellationToken);
        if (!result.Success)
            return BadRequest(result);

        return Ok(result);
    }

    [Authorize(Roles = AuthRoles.Patient)]
    [HttpPost("me/viewed-doctors/{doctorId:int}")]
    public async Task<IActionResult> RecordContactView(
        int doctorId,
        [FromBody] RecordContactViewRequest? request,
        CancellationToken cancellationToken)
    {
        if (!TryGetPatientId(out var patientId))
            return Unauthorized();

        int? searchSessionId = null;
        if (request?.SessionKey is Guid sessionKey)
        {
            searchSessionId = await _contactViews.TryResolveSearchSessionIdAsync(sessionKey, cancellationToken);
        }

        await _contactViews.RecordContactViewAsync(patientId, doctorId, searchSessionId, cancellationToken);
        return Ok(new { recorded = true });
    }

    private bool TryGetPatientId(out int patientId)
    {
        patientId = 0;
        var idClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return int.TryParse(idClaim, out patientId);
    }
}

public class RecordContactViewRequest
{
    public Guid? SessionKey { get; set; }
}
