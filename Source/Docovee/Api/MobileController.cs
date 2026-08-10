using System.Security.Claims;
using Docovee.BLL.Auth;
using Docovee.BLL.Services;
using Docovee.DS;
using Docovee.DS.Entities;
using Docovee.DS.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Docovee.Api;

[ApiController]
[Route("api/mobile")]
public class MobileController : ControllerBase
{
    private readonly IBrandingService _branding;
    private readonly IAccountRegistrationService _registration;
    private readonly IAccountAuthService _auth;
    private readonly IMobileJwtTokenService _jwt;
    private readonly DocoveeDbContext _db;
    private readonly IAnthropicChatService _chat;
    private readonly IPatientDoctorContactService _contactViews;
    private readonly IPatientNotificationService _notifications;
    private readonly IAppointmentService _appointments;
    private readonly IVoiceCallBookingService _voiceCalls;
    private readonly IProfileService _profile;

    public MobileController(
        IBrandingService branding,
        IAccountRegistrationService registration,
        IAccountAuthService auth,
        IMobileJwtTokenService jwt,
        DocoveeDbContext db,
        IAnthropicChatService chat,
        IPatientDoctorContactService contactViews,
        IPatientNotificationService notifications,
        IAppointmentService appointments,
        IVoiceCallBookingService voiceCalls,
        IProfileService profile)
    {
        _branding = branding;
        _registration = registration;
        _auth = auth;
        _jwt = jwt;
        _db = db;
        _chat = chat;
        _contactViews = contactViews;
        _notifications = notifications;
        _appointments = appointments;
        _voiceCalls = voiceCalls;
        _profile = profile;
    }

    [HttpGet("bootstrap")]
    [ProducesResponseType(typeof(MobileBootstrapDto), StatusCodes.Status200OK)]
    public ActionResult<MobileBootstrapDto> GetBootstrap()
    {
        var bot = _branding.ChatBotName;
        var site = _branding.SiteName;

        return Ok(new MobileBootstrapDto
        {
            SiteName = site,
            ChatBotName = bot,
            Tagline = "Find the right dentist — not just any dentist.",
            WelcomeMessage =
                $"Hi! I'm {bot}. I'm here to match you with the right dentist for YOU. " +
                "Tooth pain, cleaning, implants, or a new dental home — tell me what's going on.",
            QuickConcerns =
            [
                "I need a dentist",
                "Tooth pain",
                "Dental implants",
                "Teeth cleaning",
                "Invisalign",
                "Emergency dental"
            ],
            ApiStatus = "ok"
        });
    }

    [HttpGet("email-available")]
    [ProducesResponseType(typeof(MobileEmailAvailableResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<MobileEmailAvailableResponse>> EmailAvailable(
        [FromQuery] string email,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
        {
            return Ok(new MobileEmailAvailableResponse
            {
                Available = false,
                Message = "That doesn't look like an email — could you try again?"
            });
        }

        var exists = await _registration.PatientUsernameExistsAsync(email.Trim(), cancellationToken);
        return Ok(new MobileEmailAvailableResponse
        {
            Available = !exists,
            Message = exists
                ? "You already have an account with that email. Please sign in instead."
                : null
        });
    }

    [HttpPost("login")]
    [ProducesResponseType(typeof(MobilePatientLoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(MobilePatientLoginResponse), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<MobilePatientLoginResponse>> LoginPatient(
        [FromBody] MobilePatientLoginRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest(new MobilePatientLoginResponse
            {
                Success = false,
                Message = "Email and password are required."
            });
        }

        var email = request.Email.Trim();
        var (success, error) = await _auth.LoginAsync(new AccountLoginRequest
        {
            AccountType = AccountType.Patient,
            Username = email,
            Password = request.Password
        }, HttpContext, cancellationToken);

        if (!success)
        {
            return BadRequest(new MobilePatientLoginResponse
            {
                Success = false,
                Message = error ?? "Invalid email or password."
            });
        }

        var patient = await _db.Patients.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Username == email, cancellationToken);
        if (patient == null)
        {
            return BadRequest(new MobilePatientLoginResponse
            {
                Success = false,
                Message = "Patient account not found."
            });
        }

        var (token, expires) = _jwt.CreatePatientToken(patient.Id, patient.Username, patient.FullName);
        return Ok(new MobilePatientLoginResponse
        {
            Success = true,
            Message = "Signed in.",
            Email = patient.Username,
            FullName = patient.FullName,
            PatientId = patient.Id,
            AccessToken = token,
            TokenType = "Bearer",
            ExpiresAt = expires
        });
    }

    [HttpPost("register")]
    [ProducesResponseType(typeof(AccountRegisterResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(AccountRegisterResponse), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<AccountRegisterResponse>> RegisterPatient(
        [FromBody] MobilePatientRegisterRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
            return BadRequest(new AccountRegisterResponse { Success = false, Message = "Request body is required.", AccountType = AccountType.Patient });

        var email = request.Email.Trim();
        var result = await _registration.RegisterAsync(new AccountRegisterRequest
        {
            AccountType = AccountType.Patient,
            FullName = request.FullName.Trim(),
            Username = email,
            Phone = request.Phone.Trim(),
            DateOfBirth = request.DateOfBirth,
            Password = request.Password,
            ConfirmPassword = request.ConfirmPassword
        }, doctorPhoto: null, cancellationToken);

        if (!result.Success)
            return BadRequest(result);

        result.Message = $"You're all set, {request.FullName.Trim().Split(' ')[0]}! Your free {_branding.SiteName} account is ready.";
        return Ok(result);
    }

    [HttpGet("me")]
    [Authorize(Roles = AuthRoles.Patient)]
    [ProducesResponseType(typeof(MobileMeResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<MobileMeResponse>> Me(CancellationToken cancellationToken)
    {
        if (!TryGetPatientId(out var patientId))
            return Unauthorized();

        var profile = await _profile.GetPatientProfileAsync(patientId, cancellationToken);
        if (profile == null)
            return NotFound();

        return Ok(new MobileMeResponse
        {
            PatientId = patientId,
            Email = profile.Username,
            FullName = profile.FullName,
            Phone = profile.Phone
        });
    }

    [HttpPost("chat/message")]
    [ProducesResponseType(typeof(ChatMessageResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<ChatMessageResponse>> ChatMessage(
        [FromBody] ChatMessageRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null
            || (string.IsNullOrWhiteSpace(request.Message)
                && request.SelectedDoctorId == null
                && string.IsNullOrWhiteSpace(request.Action)))
        {
            return BadRequest(new { message = "Message is required." });
        }

        try
        {
            var result = await _chat.SendMessageAsync(request, HttpContext, cancellationToken);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = "Unable to process chat message.", detail = ex.Message });
        }
    }

    [HttpPost("chat/record-contact-view")]
    public async Task<IActionResult> RecordContactView(
        [FromBody] ChatRecordContactViewRequest request,
        CancellationToken cancellationToken)
    {
        if (request.SessionKey == Guid.Empty || request.DoctorId <= 0)
            return BadRequest(new { message = "Session key and doctor id are required." });

        await _contactViews.TryRecordContactViewBySessionAsync(request.SessionKey, request.DoctorId, cancellationToken);
        return Ok(new { recorded = true });
    }

    [HttpGet("sessions/{sessionKey:guid}/calls")]
    [ProducesResponseType(typeof(IReadOnlyList<MobileVoiceCallDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<MobileVoiceCallDto>>> SessionCalls(
        Guid sessionKey,
        CancellationToken cancellationToken)
    {
        var calls = await _voiceCalls.GetCallsForSessionAsync(sessionKey, cancellationToken);
        return Ok(calls);
    }

    [HttpGet("notifications")]
    [Authorize(Roles = AuthRoles.Patient)]
    [ProducesResponseType(typeof(MobileNotificationsResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<MobileNotificationsResponse>> Notifications(CancellationToken cancellationToken)
    {
        if (!TryGetPatientId(out var patientId))
            return Unauthorized();

        var items = await _notifications.GetForPatientAsync(patientId, cancellationToken);
        var unread = await _notifications.CountUnreadAsync(patientId, cancellationToken);
        var today = DateTime.Today;

        return Ok(new MobileNotificationsResponse
        {
            UnreadCount = unread,
            Items = items.Select(n =>
            {
                var ends = n.AppointmentEndsAt ?? n.AppointmentStartsAt?.AddHours(1);
                string? slot = null;
                if (n.AppointmentStartsAt is DateTime start && ends is DateTime end)
                    slot = VoiceCallBookingService.FormatPstSlot(start, end);

                return new MobileNotificationDto
                {
                    Id = n.Id,
                    Type = n.Type,
                    Title = n.Title,
                    Body = n.Body,
                    AppointmentId = n.AppointmentId,
                    DoctorId = n.DoctorId,
                    AppointmentStartsAt = n.AppointmentStartsAt,
                    AppointmentEndsAt = ends,
                    SlotLabel = slot,
                    IsRead = n.IsRead,
                    CreatedAt = n.CreatedAt,
                    IsFutureAppointment = n.AppointmentStartsAt is DateTime s && s.Date >= today
                };
            }).ToList()
        });
    }

    [HttpPost("notifications/mark-read")]
    [Authorize(Roles = AuthRoles.Patient)]
    public async Task<IActionResult> MarkNotificationsRead(CancellationToken cancellationToken)
    {
        if (!TryGetPatientId(out var patientId))
            return Unauthorized();

        await _notifications.MarkAllReadAsync(patientId, cancellationToken);
        return Ok(new { marked = true });
    }

    [HttpGet("appointments")]
    [Authorize(Roles = AuthRoles.Patient)]
    [ProducesResponseType(typeof(MobileAppointmentsResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<MobileAppointmentsResponse>> Appointments(CancellationToken cancellationToken)
    {
        if (!TryGetPatientId(out var patientId))
            return Unauthorized();

        var all = await _appointments.GetForPatientAsync(patientId, cancellationToken);
        var startOfToday = DateTime.Today;

        var upcoming = all
            .Where(a => a.StartsAt >= startOfToday
                        && !AppointmentStatuses.IsCanceled(a.Status)
                        && a.Status != AppointmentStatuses.Completed
                        && AppointmentStatuses.Normalize(a.Status) != AppointmentStatuses.PatientNoShow)
            .OrderBy(a => a.StartsAt)
            .ToList();

        var past = all
            .Where(a => a.StartsAt < startOfToday
                        || a.Status == AppointmentStatuses.Completed
                        || AppointmentStatuses.IsCanceled(a.Status)
                        || AppointmentStatuses.Normalize(a.Status) == AppointmentStatuses.PatientNoShow)
            .OrderByDescending(a => a.StartsAt)
            .ToList();

        return Ok(new MobileAppointmentsResponse
        {
            Upcoming = upcoming,
            Past = past
        });
    }

    private bool TryGetPatientId(out int patientId)
    {
        patientId = 0;
        var raw = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return int.TryParse(raw, out patientId) && patientId > 0;
    }
}
