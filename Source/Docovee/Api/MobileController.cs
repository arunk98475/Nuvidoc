using Docovee.BLL.Services;
using Docovee.DS.Models;
using Microsoft.AspNetCore.Mvc;

namespace Docovee.Api;

[ApiController]
[Route("api/mobile")]
public class MobileController : ControllerBase
{
    private readonly IBrandingService _branding;
    private readonly IAccountRegistrationService _registration;
    private readonly IAccountAuthService _auth;

    public MobileController(
        IBrandingService branding,
        IAccountRegistrationService registration,
        IAccountAuthService auth)
    {
        _branding = branding;
        _registration = registration;
        _auth = auth;
    }

    /// <summary>Minimal home-screen content for the native Android / iOS app.</summary>
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

    /// <summary>Check whether an email can be used as a new patient login.</summary>
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

    /// <summary>Patient login for the native app.</summary>
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

        var (success, error) = await _auth.LoginAsync(new AccountLoginRequest
        {
            AccountType = AccountType.Patient,
            Username = request.Email.Trim(),
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

        return Ok(new MobilePatientLoginResponse
        {
            Success = true,
            Message = "Signed in.",
            Email = request.Email.Trim(),
            FullName = HttpContext.User?.Identity?.Name
        });
    }

    /// <summary>Create a free patient account (Nuvi registration steps).</summary>
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
}
