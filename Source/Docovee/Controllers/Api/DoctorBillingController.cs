using System.Security.Claims;
using Docovee.BLL.Auth;
using Docovee.BLL.Services;
using Docovee.BLL.Services.Billing;
using Docovee.DS.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Docovee.Controllers.Api;

[ApiController]
[Route("api/doctor/billing")]
[Authorize(Roles = AuthRoles.Doctor)]
public class DoctorBillingController : ControllerBase
{
    private readonly IStripePaymentMethodService _paymentMethods;
    private readonly IDoctorBillingService _billing;
    private readonly IDoctorSponsorshipService _sponsorship;

    public DoctorBillingController(
        IStripePaymentMethodService paymentMethods,
        IDoctorBillingService billing,
        IDoctorSponsorshipService sponsorship)
    {
        _paymentMethods = paymentMethods;
        _billing = billing;
        _sponsorship = sponsorship;
    }

    [HttpPost("setup-intent")]
    public async Task<ActionResult<SetupIntentResultDto>> CreateSetupIntent(CancellationToken cancellationToken)
    {
        if (!TryGetDoctorId(out var doctorId))
            return Unauthorized();

        var result = await _paymentMethods.CreateSetupIntentAsync(doctorId, cancellationToken);
        if (!result.Success)
            return BadRequest(result);

        return Ok(result);
    }

    [HttpGet("payment-methods")]
    public async Task<ActionResult<IReadOnlyList<DoctorPaymentMethodDto>>> ListPaymentMethods(CancellationToken cancellationToken)
    {
        if (!TryGetDoctorId(out var doctorId))
            return Unauthorized();

        var methods = await _paymentMethods.ListPaymentMethodsAsync(doctorId, cancellationToken);
        return Ok(methods);
    }

    [HttpPost("payment-methods/default")]
    public async Task<ActionResult<BillingOperationResultDto>> SetDefault(
        [FromBody] SetDefaultPaymentMethodRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetDoctorId(out var doctorId))
            return Unauthorized();

        var result = await _paymentMethods.SetDefaultPaymentMethodAsync(doctorId, request.PaymentMethodId, cancellationToken);
        if (!result.Success)
            return BadRequest(result);

        return Ok(result);
    }

    [HttpDelete("payment-methods/{paymentMethodId}")]
    public async Task<ActionResult<BillingOperationResultDto>> Detach(
        string paymentMethodId,
        CancellationToken cancellationToken)
    {
        if (!TryGetDoctorId(out var doctorId))
            return Unauthorized();

        var result = await _paymentMethods.DetachPaymentMethodAsync(doctorId, paymentMethodId, cancellationToken);
        if (!result.Success)
            return BadRequest(result);

        return Ok(result);
    }

    [HttpPut("contact")]
    public async Task<ActionResult<BillingOperationResultDto>> UpdateContact(
        [FromBody] DoctorBillingContactDto contact,
        CancellationToken cancellationToken)
    {
        if (!TryGetDoctorId(out var doctorId))
            return Unauthorized();

        var result = await _billing.UpdateBillingContactAsync(doctorId, contact, cancellationToken);
        if (!result.Success)
            return BadRequest(result);

        return Ok(result);
    }

    [HttpGet("contact")]
    public async Task<ActionResult<DoctorBillingContactDto>> GetContact(CancellationToken cancellationToken)
    {
        if (!TryGetDoctorId(out var doctorId))
            return Unauthorized();

        var contact = await _billing.GetBillingContactAsync(doctorId, cancellationToken);
        return Ok(contact);
    }

    [HttpGet("charges")]
    public async Task<ActionResult<IReadOnlyList<DoctorBillingChargeDto>>> ListCharges(
        [FromQuery] int? year,
        CancellationToken cancellationToken)
    {
        if (!TryGetDoctorId(out var doctorId))
            return Unauthorized();

        var charges = await _billing.GetChargesAsync(doctorId, year, cancellationToken);
        return Ok(charges);
    }

    [HttpGet("sponsorship")]
    public async Task<ActionResult<DoctorSponsorshipStatusDto>> GetSponsorship(CancellationToken cancellationToken)
    {
        if (!TryGetDoctorId(out var doctorId))
            return Unauthorized();

        var status = await _sponsorship.GetStatusAsync(doctorId, cancellationToken);
        if (status == null)
            return NotFound();

        return Ok(status);
    }

    [HttpPut("sponsorship")]
    public async Task<ActionResult<BillingOperationResultDto>> SetSponsorship(
        [FromBody] SetSponsorshipRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetDoctorId(out var doctorId))
            return Unauthorized();

        var result = await _sponsorship.SetEnabledAsync(doctorId, request.Enabled, cancellationToken);
        if (!result.Success)
            return BadRequest(result);

        return Ok(result);
    }

    private bool TryGetDoctorId(out int doctorId)
    {
        doctorId = 0;
        return int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out doctorId);
    }

    public sealed class SetDefaultPaymentMethodRequest
    {
        public string PaymentMethodId { get; set; } = string.Empty;
    }
}
