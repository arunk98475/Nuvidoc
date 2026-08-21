using System.Security.Claims;
using Docovee.BLL.Auth;
using Docovee.BLL.Services;
using Docovee.DS.Entities;
using Docovee.DS.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Docovee.Controllers.Api;

/// <summary>
/// Public-profile booking endpoint. Guests may book (AllowAnonymous) with rate limits and
/// same-site checks; authenticated patients are linked via cookie identity.
/// Chat/Nuvi bookings do not use this API.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class BookingsController : ControllerBase
{
    private static readonly HashSet<string> AllowedAnonymousSources = new(StringComparer.OrdinalIgnoreCase)
    {
        AppointmentSources.PublicProfile
    };

    private readonly IAppointmentService _appointments;

    public BookingsController(IAppointmentService appointments) => _appointments = appointments;

    [HttpPost]
    [AllowAnonymous]
    [EnableRateLimiting("bookings")]
    public async Task<ActionResult<CreateAppointmentResponse>> Create(
        [FromBody] CreateAppointmentRequest? request,
        CancellationToken cancellationToken)
    {
        if (request == null)
            return BadRequest(new CreateAppointmentResponse { Success = false, Message = "Invalid request." });

        int? patientId = null;
        if (User.Identity?.IsAuthenticated == true
            && User.IsInRole(AuthRoles.Patient)
            && int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            && id > 0)
        {
            patientId = id;
        }

        // Guests: only public-profile booking, same-site browser, contact + DOB required.
        if (patientId is null)
        {
            if (!IsSameSiteRequest())
            {
                return BadRequest(new CreateAppointmentResponse
                {
                    Success = false,
                    Message = "Booking must be submitted from the NuviDoc site."
                });
            }

            var source = (request.Source ?? "").Trim();
            if (string.IsNullOrEmpty(source))
                request.Source = AppointmentSources.PublicProfile;
            else if (!AllowedAnonymousSources.Contains(source))
            {
                return BadRequest(new CreateAppointmentResponse
                {
                    Success = false,
                    Message = "Guest booking is only available from the doctor profile page."
                });
            }

            var phone = request.PatientPhone?.Trim() ?? "";
            var email = request.PatientEmail?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(phone) && (string.IsNullOrWhiteSpace(email) || !email.Contains('@')))
            {
                return BadRequest(new CreateAppointmentResponse
                {
                    Success = false,
                    Message = "Please enter a phone number or email so the office can reach you."
                });
            }

            if (string.IsNullOrWhiteSpace(request.DateOfBirth))
            {
                return BadRequest(new CreateAppointmentResponse
                {
                    Success = false,
                    Message = "Date of birth is required."
                });
            }
        }

        // Bound free-text fields before persistence.
        request.PatientName = Truncate(request.PatientName, 200) ?? "";
        request.PatientPhone = Truncate(request.PatientPhone, 30);
        request.PatientEmail = Truncate(request.PatientEmail, 200);
        request.VisitReason = Truncate(request.VisitReason, 200) ?? "";
        request.DateOfBirth = Truncate(request.DateOfBirth, 32);

        var result = await _appointments.CreateAsync(request, patientId, cancellationToken);
        if (!result.Success)
            return BadRequest(result);

        // Do not return internal appointment id to anonymous callers.
        if (patientId is null)
            result.AppointmentId = null;

        return Ok(result);
    }

    private bool IsSameSiteRequest()
    {
        var host = Request.Host.Host;
        if (string.IsNullOrWhiteSpace(host))
            return false;

        if (Request.Headers.TryGetValue("Origin", out var originValues))
        {
            var origin = originValues.ToString();
            if (Uri.TryCreate(origin, UriKind.Absolute, out var originUri)
                && string.Equals(originUri.Host, host, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (Request.Headers.TryGetValue("Referer", out var refererValues))
        {
            var referer = refererValues.ToString();
            if (Uri.TryCreate(referer, UriKind.Absolute, out var refererUri)
                && string.Equals(refererUri.Host, host, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // Same-origin navigations sometimes omit Origin; allow only when Sec-Fetch-Site says same-origin.
        if (Request.Headers.TryGetValue("Sec-Fetch-Site", out var fetchSite)
            && string.Equals(fetchSite.ToString(), "same-origin", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}
