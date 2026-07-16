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
    private readonly ILogger<IntegrationsWebhookController> _logger;

    public IntegrationsWebhookController(
        IPmsCalendarService pms,
        ILogger<IntegrationsWebhookController> logger)
    {
        _pms = pms;
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
