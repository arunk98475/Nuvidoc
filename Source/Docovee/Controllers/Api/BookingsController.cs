using System.Security.Claims;
using Docovee.BLL.Auth;
using Docovee.BLL.Services;
using Docovee.DS.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Docovee.Controllers.Api;

[ApiController]
[Route("api/[controller]")]
public class BookingsController : ControllerBase
{
    private readonly IAppointmentService _appointments;

    public BookingsController(IAppointmentService appointments) => _appointments = appointments;

    [HttpPost]
    [AllowAnonymous]
    public async Task<ActionResult<CreateAppointmentResponse>> Create(
        [FromBody] CreateAppointmentRequest request,
        CancellationToken cancellationToken)
    {
        int? patientId = null;
        if (User.IsInRole(AuthRoles.Patient)
            && int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id))
        {
            patientId = id;
        }

        var result = await _appointments.CreateAsync(request, patientId, cancellationToken);
        if (!result.Success)
            return BadRequest(result);

        return Ok(result);
    }
}
