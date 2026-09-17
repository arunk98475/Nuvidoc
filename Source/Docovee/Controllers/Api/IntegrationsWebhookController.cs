using System.Text;
using Docovee.BLL.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Docovee.Controllers.Api;

[ApiController]
[Route("api/integrations")]
public class IntegrationsWebhookController : ControllerBase
{
    private readonly IVoiceCallBookingService _voiceBookings;
    private readonly IPatientSmsNurtureService _smsNurture;
    private readonly IAppointmentFeedbackService _feedback;
    private readonly ILogger<IntegrationsWebhookController> _logger;

    public IntegrationsWebhookController(
        IVoiceCallBookingService voiceBookings,
        IPatientSmsNurtureService smsNurture,
        IAppointmentFeedbackService feedback,
        ILogger<IntegrationsWebhookController> logger)
    {
        _voiceBookings = voiceBookings;
        _smsNurture = smsNurture;
        _feedback = feedback;
        _logger = logger;
    }

    // Frozen: PMS not used in current Nuvi call-and-book flow.
    // To re-enable: inject IPmsCalendarService and uncomment below.
    // [HttpPost("opendental/webhook")]
    // [AllowAnonymous]
    // public async Task<IActionResult> OpenDentalWebhook(
    //     [FromQuery] int? doctorId,
    //     CancellationToken cancellationToken)
    // {
    //     return await HandleInboundAsync(PmsProviders.OpenDental, doctorId, cancellationToken);
    // }

    // [HttpPost("nexhealth/webhook")]
    // [AllowAnonymous]
    // public async Task<IActionResult> NexHealthWebhook(
    //     [FromQuery] int? doctorId,
    //     CancellationToken cancellationToken)
    // {
    //     return await HandleInboundAsync(PmsProviders.NexHealth, doctorId, cancellationToken);
    // }

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
            "ElevenLabs webhook received. HasSignature={HasSignature}, BodyLength={BodyLength}",
            !string.IsNullOrWhiteSpace(signature),
            rawBody.Length);

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

    /// <summary>
    /// Twilio SMS inbound webhook for nurture and post-booking feedback replies.
    /// Configure on the SMS sender: When a message comes in →
    /// {PublicBaseUrl}/api/integrations/twilio/sms
    /// </summary>
    [HttpPost("twilio/sms")]
    [AllowAnonymous]
    public async Task<IActionResult> TwilioSms(CancellationToken cancellationToken)
    {
        var form = await Request.ReadFormAsync(cancellationToken);
        var from = form["From"].ToString();
        var body = form["Body"].ToString();

        _logger.LogInformation(
            "Twilio SMS inbound. From={From}, HasBody={HasBody}",
            from,
            !string.IsNullOrWhiteSpace(body));

        await HandleInboundTextAsync(from, body, cancellationToken);
        return Content("<Response></Response>", "text/xml");
    }

    /// <summary>
    /// Compat path for in-flight WhatsApp conversations. New sends use SMS only.
    /// Configure on the WhatsApp sender: When a message comes in →
    /// {PublicBaseUrl}/api/integrations/twilio/whatsapp
    /// </summary>
    [HttpPost("twilio/whatsapp")]
    [AllowAnonymous]
    public async Task<IActionResult> TwilioWhatsApp(CancellationToken cancellationToken)
    {
        var form = await Request.ReadFormAsync(cancellationToken);
        var from = form["From"].ToString();
        var body = form["Body"].ToString();
        var buttonPayload = form["ButtonPayload"].ToString();
        var listId = form["ListId"].ToString();
        if (string.IsNullOrWhiteSpace(listId))
            listId = form["ButtonPayload"].ToString();

        var selection = FirstNonEmpty(listId, buttonPayload, body);

        _logger.LogInformation(
            "Twilio WhatsApp inbound (compat). From={From}, ListId={ListId}, HasBody={HasBody}",
            from,
            string.IsNullOrWhiteSpace(listId) ? "(none)" : listId,
            !string.IsNullOrWhiteSpace(body));

        await HandleInboundTextAsync(from, selection, cancellationToken);
        return Content("<Response></Response>", "text/xml");
    }

    private async Task HandleInboundTextAsync(
        string from,
        string? body,
        CancellationToken cancellationToken)
    {
        try
        {
            if (SmsReplyExtractionService.IsStopKeyword(body))
            {
                await _smsNurture.HandleStopRequestAsync(from, cancellationToken);
                return;
            }

            var handledByNurture = await _smsNurture.HandleInboundSmsAsync(from, body, cancellationToken);
            if (!handledByNurture)
            {
                await _feedback.HandleInboundSmsAsync(from, body, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Twilio inbound text handling failed");
        }
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    // [HttpPost("sync")]
    // [AllowAnonymous]
    // public async Task<IActionResult> TriggerSync(
    //     [FromQuery] int? doctorId,
    //     CancellationToken cancellationToken)
    // {
    //     var changed = doctorId is > 0
    //         ? await _pms.SyncInboundForDoctorAsync(doctorId.Value, cancellationToken)
    //         : await _pms.SyncInboundAsync(cancellationToken);
    //
    //     return Ok(new { success = true, changed });
    // }

    // private async Task<IActionResult> HandleInboundAsync(
    //     string provider,
    //     int? doctorId,
    //     CancellationToken cancellationToken)
    // {
    //     try
    //     {
    //         var changed = doctorId is > 0
    //             ? await _pms.SyncInboundForDoctorAsync(doctorId.Value, cancellationToken)
    //             : await _pms.SyncInboundAsync(cancellationToken);
    //
    //         _logger.LogInformation(
    //             "PMS {Provider} webhook processed; changed={Changed}, doctorId={DoctorId}",
    //             provider, changed, doctorId);
    //
    //         return Ok(new { success = true, provider, changed });
    //     }
    //     catch (Exception ex)
    //     {
    //         _logger.LogWarning(ex, "PMS {Provider} webhook failed", provider);
    //         return StatusCode(500, new { success = false, error = "Sync failed." });
    //     }
    // }
}
