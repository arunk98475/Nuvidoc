using Amazon;
using Amazon.Runtime;
using Amazon.SimpleEmail;
using Amazon.SimpleEmail.Model;
using Docovee.BLL.Configuration;
using Docovee.logging;
using Microsoft.Extensions.Options;

namespace Docovee.BLL.Services;

public sealed class EmailSendResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
}

public interface IEmailSender
{
    bool IsConfigured { get; }
    Task<EmailSendResult> SendAsync(
        string toAddress,
        string subject,
        string textBody,
        string? htmlBody = null,
        CancellationToken cancellationToken = default);
}

public sealed class SesEmailSender : IEmailSender
{
    private readonly EmailOptions _options;
    private readonly IDocoveeLogger _logger;

    public SesEmailSender(IOptions<EmailOptions> options, IDocoveeLogger logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public bool IsConfigured => _options.IsConfigured;

    public async Task<EmailSendResult> SendAsync(
        string toAddress,
        string subject,
        string textBody,
        string? htmlBody = null,
        CancellationToken cancellationToken = default)
    {
        if (!_options.IsConfigured)
        {
            return new EmailSendResult
            {
                Success = false,
                Message = "Email is not configured. Add Email AccessKeyId, SecretAccessKey, Region, and FromAddress."
            };
        }

        if (string.IsNullOrWhiteSpace(toAddress) || !toAddress.Contains('@'))
        {
            return new EmailSendResult { Success = false, Message = "A valid recipient email is required." };
        }

        try
        {
            var region = RegionEndpoint.GetBySystemName(_options.Region.Trim());
            var credentials = new BasicAWSCredentials(_options.AccessKeyId.Trim(), _options.SecretAccessKey.Trim());
            using var client = new AmazonSimpleEmailServiceClient(credentials, region);

            var from = string.IsNullOrWhiteSpace(_options.FromDisplayName)
                ? _options.FromAddress.Trim()
                : $"{_options.FromDisplayName.Trim()} <{_options.FromAddress.Trim()}>";

            var body = new Body
            {
                Text = new Content(textBody)
            };
            if (!string.IsNullOrWhiteSpace(htmlBody))
                body.Html = new Content(htmlBody);

            var request = new SendEmailRequest
            {
                Source = from,
                Destination = new Destination { ToAddresses = [toAddress.Trim()] },
                Message = new Message
                {
                    Subject = new Content(subject),
                    Body = body
                }
            };

            await client.SendEmailAsync(request, cancellationToken);
            return new EmailSendResult { Success = true, Message = "Email sent." };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SES send failed to {To}", toAddress);
            return new EmailSendResult
            {
                Success = false,
                Message = "Could not send email. Check SES configuration and domain verification."
            };
        }
    }
}
