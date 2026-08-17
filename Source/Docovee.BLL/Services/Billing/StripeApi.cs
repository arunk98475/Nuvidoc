using Docovee.BLL.Configuration;
using Microsoft.Extensions.Options;
using Stripe;

namespace Docovee.BLL.Services.Billing;

internal static class StripeApi
{
    public static void Apply(IOptions<StripeOptions> options)
    {
        var secret = options.Value.SecretKey?.Trim();
        if (!string.IsNullOrWhiteSpace(secret))
            StripeConfiguration.ApiKey = secret;
    }
}
