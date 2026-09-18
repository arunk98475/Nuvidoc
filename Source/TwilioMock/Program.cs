using System.Collections.Concurrent;
using System.Net.Http.Headers;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<TwilioMockOptions>(builder.Configuration.GetSection("TwilioMock"));
builder.Services.AddSingleton<SmsInboxStore>();
builder.Services.AddHttpClient("NuvidocWebhook", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/messages", (SmsInboxStore store) => Results.Ok(store.Snapshot()));

app.MapPost("/api/outbound", (OutboundMessageRequest request, SmsInboxStore store) =>
{
    if (string.IsNullOrWhiteSpace(request.To) || string.IsNullOrWhiteSpace(request.From))
        return Results.BadRequest(new { error = "to and from are required." });

    var msg = store.AddOutbound(request);
    return Results.Ok(new { sid = msg.Sid, id = msg.Id });
});

app.MapPost("/api/reply", async (
    ReplyRequest request,
    SmsInboxStore store,
    IHttpClientFactory httpClientFactory,
    IOptions<TwilioMockOptions> options,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    var opts = options.Value;
    var webhookUrl = string.IsNullOrWhiteSpace(request.WebhookUrl)
        ? opts.NuvidocSmsWebhookUrl
        : request.WebhookUrl.Trim();

    if (string.IsNullOrWhiteSpace(webhookUrl))
        return Results.BadRequest(new { error = "Webhook URL is not configured." });

    var from = string.IsNullOrWhiteSpace(request.From)
        ? request.ConversationPhone
        : request.From.Trim();
    if (string.IsNullOrWhiteSpace(from))
        return Results.BadRequest(new { error = "From / conversation phone is required." });

    var body = (request.Body ?? "").Trim();
    if (string.IsNullOrWhiteSpace(body))
        return Results.BadRequest(new { error = "Reply body is required." });

    var to = string.IsNullOrWhiteSpace(request.To)
        ? opts.NuvidocFromNumber
        : request.To.Trim();

    var inbound = store.AddInbound(from, to, body);

    var form = new Dictionary<string, string>
    {
        ["MessageSid"] = inbound.Sid,
        ["SmsSid"] = inbound.Sid,
        ["AccountSid"] = "ACmock000000000000000000000000000",
        ["From"] = from,
        ["To"] = to ?? "",
        ["Body"] = body,
        ["NumMedia"] = "0"
    };

    try
    {
        var client = httpClientFactory.CreateClient("NuvidocWebhook");
        using var content = new FormUrlEncodedContent(form);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
        using var response = await client.PostAsync(webhookUrl, content, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        store.MarkWebhookResult(inbound.Id, (int)response.StatusCode, responseBody);

        logger.LogInformation(
            "Reply webhook posted. From={From} Status={Status}",
            from, (int)response.StatusCode);

        return Results.Ok(new
        {
            success = response.IsSuccessStatusCode,
            statusCode = (int)response.StatusCode,
            webhookUrl,
            responseBody,
            message = inbound
        });
    }
    catch (Exception ex)
    {
        store.MarkWebhookResult(inbound.Id, 0, ex.Message);
        logger.LogWarning(ex, "Reply webhook failed");
        return Results.Ok(new
        {
            success = false,
            error = ex.Message,
            webhookUrl,
            message = inbound
        });
    }
});

app.MapPost("/api/clear", (SmsInboxStore store) =>
{
    store.Clear();
    return Results.Ok(new { cleared = true });
});

app.MapGet("/api/config", (IOptions<TwilioMockOptions> options) =>
{
    var o = options.Value;
    return Results.Ok(new
    {
        nuvidocSmsWebhookUrl = o.NuvidocSmsWebhookUrl,
        nuvidocFromNumber = o.NuvidocFromNumber
    });
});

app.Run();

public sealed class TwilioMockOptions
{
    /// <summary>Nuvidoc inbound SMS webhook, e.g. http://localhost:5xxx/api/integrations/twilio/sms</summary>
    public string NuvidocSmsWebhookUrl { get; set; } =
        "http://localhost:5188/api/integrations/twilio/sms";

    /// <summary>Nuvidoc Twilio From number (appears as To on inbound replies).</summary>
    public string NuvidocFromNumber { get; set; } = "+12533083687";
}

public sealed class OutboundMessageRequest
{
    public string? IntendedTo { get; set; }
    public string To { get; set; } = "";
    public string From { get; set; } = "";
    public string Body { get; set; } = "";
    public bool IsWhatsApp { get; set; }
    public string? ContentSid { get; set; }
    public string? ContentVariables { get; set; }
}

public sealed class ReplyRequest
{
    public string? From { get; set; }
    public string? To { get; set; }
    public string? ConversationPhone { get; set; }
    public string Body { get; set; } = "";
    public string? WebhookUrl { get; set; }
}

public sealed class SmsMessageDto
{
    public string Id { get; set; } = "";
    public string Sid { get; set; } = "";
    public DateTimeOffset At { get; set; }
    public string Direction { get; set; } = ""; // outbound | inbound
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string? IntendedTo { get; set; }
    public string Body { get; set; } = "";
    public bool IsWhatsApp { get; set; }
    public int? WebhookStatusCode { get; set; }
    public string? WebhookResponse { get; set; }
}

public sealed class SmsInboxStore
{
    private readonly ConcurrentQueue<SmsMessageDto> _messages = new();
    private readonly object _gate = new();

    public IReadOnlyList<SmsMessageDto> Snapshot()
    {
        lock (_gate)
            return _messages.Reverse().ToList();
    }

    public SmsMessageDto AddOutbound(OutboundMessageRequest request)
    {
        var msg = new SmsMessageDto
        {
            Id = Guid.NewGuid().ToString("N"),
            Sid = "SM" + Guid.NewGuid().ToString("N")[..32],
            At = DateTimeOffset.UtcNow,
            Direction = "outbound",
            From = request.From.Trim(),
            To = request.To.Trim(),
            IntendedTo = string.IsNullOrWhiteSpace(request.IntendedTo) ? null : request.IntendedTo.Trim(),
            Body = request.Body ?? "",
            IsWhatsApp = request.IsWhatsApp
        };
        Enqueue(msg);
        return msg;
    }

    public SmsMessageDto AddInbound(string from, string? to, string body)
    {
        var msg = new SmsMessageDto
        {
            Id = Guid.NewGuid().ToString("N"),
            Sid = "SM" + Guid.NewGuid().ToString("N")[..32],
            At = DateTimeOffset.UtcNow,
            Direction = "inbound",
            From = from.Trim(),
            To = (to ?? "").Trim(),
            Body = body,
            IsWhatsApp = false
        };
        Enqueue(msg);
        return msg;
    }

    public void MarkWebhookResult(string id, int statusCode, string? body)
    {
        lock (_gate)
        {
            var msg = _messages.FirstOrDefault(m => m.Id == id);
            if (msg == null)
                return;
            msg.WebhookStatusCode = statusCode;
            msg.WebhookResponse = Truncate(body, 500);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            while (_messages.TryDequeue(out _)) { }
        }
    }

    private void Enqueue(SmsMessageDto msg)
    {
        lock (_gate)
        {
            _messages.Enqueue(msg);
            while (_messages.Count > 500 && _messages.TryDequeue(out _)) { }
        }
    }

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value))
            return value;
        return value.Length <= max ? value : value[..max] + "…";
    }
}
