using System.Text;
using Docovee.BLL.Services;
using Docovee.Integrations.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Docovee.Controllers.Api;

[ApiController]
[Route("api/integrations")]
public class IntegrationsWebhookController : ControllerBase
{
    private readonly IPmsCalendarService _pms;
    private readonly IVoiceCallBookingService _voiceBookings;
    private readonly ILogger<IntegrationsWebhookController> _logger;

    public IntegrationsWebhookController(
        IPmsCalendarService pms,
        IVoiceCallBookingService voiceBookings,
        ILogger<IntegrationsWebhookController> logger)
    {
        _pms = pms;
        _voiceBookings = voiceBookings;
        _logger = logger;
    }

    [HttpPost("opendental/webhook")]
    [AllowAnonymous]
    public async Task<IActionResult> OpenDentalWebhook(
        [FromQuery] int? doctorId,
        CancellationToken cancellationToken)
    {
        return await HandleInboundAsync(PmsProviders.OpenDental, doctorId, cancellationToken);
    }

    [HttpPost("nexhealth/webhook")]
    [AllowAnonymous]
    public async Task<IActionResult> NexHealthWebhook(
        [FromQuery] int? doctorId,
        CancellationToken cancellationToken)
    {
        return await HandleInboundAsync(PmsProviders.NexHealth, doctorId, cancellationToken);
    }

    /// <summary>
    /// ElevenLabs post-call webhook. Configure in Agents → Settings → Post-call webhooks:
    /// URL: {PublicBaseUrl}/api/integrations/elevenlabs/webhook
    /// </summary>
    [HttpPost("elevenlabs/webhook")]
    [AllowAnonymous]
    public async Task<IActionResult> ElevenLabsWebhook(CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var rawBody = await reader.ReadToEndAsync(cancellationToken) ?? string.Empty;
        var signature = Request.Headers["ElevenLabs-Signature"].FirstOrDefault()
            ?? Request.Headers["elevenlabs-signature"].FirstOrDefault();

        _logger.LogInformation(
            "ElevenLabs webhook received. HasSignature={HasSignature}, BodyLength={BodyLength}, Body={Body}",
            !string.IsNullOrWhiteSpace(signature),
            rawBody.Length,
            rawBody);

        try
        {
            var ok = await _voiceBookings.ProcessPostCallWebhookAsync(rawBody, signature, cancellationToken);
            if (!ok)
                return Unauthorized(new { success = false, error = "Invalid webhook signature or payload." });

            return Ok(new { success = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ElevenLabs webhook processing failed");
            return StatusCode(500, new { success = false, error = "Webhook processing failed." });
        }
    }

    [HttpPost("sync")]
    [AllowAnonymous]
    public async Task<IActionResult> TriggerSync(
        [FromQuery] int? doctorId,
        CancellationToken cancellationToken)
    {
        var changed = doctorId is > 0
            ? await _pms.SyncInboundForDoctorAsync(doctorId.Value, cancellationToken)
            : await _pms.SyncInboundAsync(cancellationToken);

        return Ok(new { success = true, changed });
    }

    private async Task<IActionResult> HandleInboundAsync(
        string provider,
        int? doctorId,
        CancellationToken cancellationToken)
    {
        try
        {
            var changed = doctorId is > 0
                ? await _pms.SyncInboundForDoctorAsync(doctorId.Value, cancellationToken)
                : await _pms.SyncInboundAsync(cancellationToken);

            _logger.LogInformation(
                "PMS {Provider} webhook processed; changed={Changed}, doctorId={DoctorId}",
                provider, changed, doctorId);

            return Ok(new { success = true, provider, changed });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PMS {Provider} webhook failed", provider);
            return StatusCode(500, new { success = false, error = "Sync failed." });
        }
    }
}
