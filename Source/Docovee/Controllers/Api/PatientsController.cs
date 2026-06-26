using Docovee.BLL.Models;
using Docovee.BLL.Services;
using Microsoft.AspNetCore.Mvc;

namespace Docovee.Controllers.Api;

[ApiController]
[Route("api/[controller]")]
public class PatientsController : ControllerBase
{
    private readonly IPatientService _patientService;

    public PatientsController(IPatientService patientService) => _patientService = patientService;

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
}
