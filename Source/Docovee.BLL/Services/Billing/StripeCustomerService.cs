using Docovee.BLL.Configuration;
using Docovee.DS;
using Docovee.DS.Entities;
using Docovee.logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stripe;

namespace Docovee.BLL.Services.Billing;

public interface IStripeCustomerService
{
    Task<(bool Success, string Message, string? CustomerId)> GetOrCreateCustomerIdAsync(
        int doctorId,
        CancellationToken cancellationToken = default);
}

public sealed class StripeCustomerService : IStripeCustomerService
{
    private readonly DocoveeDbContext _db;
    private readonly StripeOptions _options;
    private readonly IDocoveeLogger _logger;

    public StripeCustomerService(
        DocoveeDbContext db,
        IOptions<StripeOptions> options,
        IDocoveeLogger logger)
    {
        _db = db;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<(bool Success, string Message, string? CustomerId)> GetOrCreateCustomerIdAsync(
        int doctorId,
        CancellationToken cancellationToken = default)
    {
        if (!_options.IsConfigured)
            return (false, "Stripe is not configured.", null);

        StripeApi.Apply(Microsoft.Extensions.Options.Options.Create(_options));

        var doctor = await _db.Doctors.FirstOrDefaultAsync(d => d.Id == doctorId, cancellationToken);
        if (doctor == null)
            return (false, "Doctor not found.", null);

        if (!string.IsNullOrWhiteSpace(doctor.StripeCustomerId))
            return (true, "OK", doctor.StripeCustomerId);

        var service = new CustomerService();
        var email = !string.IsNullOrWhiteSpace(doctor.BillingEmail)
            ? doctor.BillingEmail.Trim()
            : doctor.Username?.Trim();

        var customer = await service.CreateAsync(new CustomerCreateOptions
        {
            Email = string.IsNullOrWhiteSpace(email) ? null : email,
            Name = string.IsNullOrWhiteSpace(doctor.PracticeName) ? doctor.Name : doctor.PracticeName,
            Metadata = new Dictionary<string, string>
            {
                ["doctor_id"] = doctorId.ToString()
            }
        }, cancellationToken: cancellationToken);

        doctor.StripeCustomerId = customer.Id;
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Created Stripe customer {CustomerId} for doctor {DoctorId}", customer.Id, doctorId);
        return (true, "OK", customer.Id);
    }
}
