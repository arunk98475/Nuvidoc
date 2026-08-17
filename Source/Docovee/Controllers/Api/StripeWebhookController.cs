using System.Text;
using Docovee.BLL.Configuration;
using Docovee.BLL.Services.Billing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Stripe;

namespace Docovee.Controllers.Api;

[ApiController]
[Route("api/webhooks/stripe")]
public class StripeWebhookController : ControllerBase
{
    private readonly IVisitBillingService _visitBilling;
    private readonly IStripePaymentMethodService _paymentMethods;
    private readonly StripeOptions _options;
    private readonly ILogger<StripeWebhookController> _logger;

    public StripeWebhookController(
        IVisitBillingService visitBilling,
        IStripePaymentMethodService paymentMethods,
        IOptions<StripeOptions> options,
        ILogger<StripeWebhookController> logger)
    {
        _visitBilling = visitBilling;
        _paymentMethods = paymentMethods;
        _options = options.Value;
        _logger = logger;
    }

    [HttpPost]
    [AllowAnonymous]
    public async Task<IActionResult> Handle(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.WebhookSecret))
        {
            _logger.LogWarning("Stripe webhook received but WebhookSecret is not configured.");
            return BadRequest("Webhook not configured.");
        }

        var json = await ReadBodyAsync();
        var signature = Request.Headers["Stripe-Signature"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(signature))
            return BadRequest("Missing Stripe-Signature header.");

        Event stripeEvent;
        try
        {
            stripeEvent = EventUtility.ConstructEvent(json, signature, _options.WebhookSecret);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Stripe webhook signature verification failed.");
            return BadRequest("Invalid signature.");
        }

        switch (stripeEvent.Type)
        {
            case "payment_intent.succeeded":
                if (stripeEvent.Data.Object is PaymentIntent succeeded)
                {
                    await _visitBilling.UpdateChargeFromPaymentIntentAsync(
                        succeeded.Id, succeeded.Status, null, cancellationToken);
                }
                break;

            case "payment_intent.payment_failed":
                if (stripeEvent.Data.Object is PaymentIntent failed)
                {
                    await _visitBilling.UpdateChargeFromPaymentIntentAsync(
                        failed.Id,
                        failed.Status,
                        failed.LastPaymentError?.Message,
                        cancellationToken);
                }
                break;

            case "setup_intent.succeeded":
                if (stripeEvent.Data.Object is SetupIntent setup
                    && !string.IsNullOrWhiteSpace(setup.CustomerId)
                    && !string.IsNullOrWhiteSpace(setup.PaymentMethodId)
                    && setup.Metadata != null
                    && setup.Metadata.TryGetValue("doctor_id", out var doctorIdStr)
                    && int.TryParse(doctorIdStr, out var doctorId))
                {
                    var methods = await _paymentMethods.ListPaymentMethodsAsync(doctorId, cancellationToken);
                    if (!methods.Any(m => m.IsDefault))
                    {
                        await _paymentMethods.SetDefaultPaymentMethodAsync(doctorId, setup.PaymentMethodId, cancellationToken);
                    }
                }
                break;
        }

        return Ok();
    }

    private async Task<string> ReadBodyAsync()
    {
        Request.EnableBuffering();
        using var reader = new StreamReader(Request.Body, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync();
        Request.Body.Position = 0;
        return body;
    }
}
