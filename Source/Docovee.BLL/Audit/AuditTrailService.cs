using System.Security.Claims;
using System.Text.Json;
using Docovee.DS;
using Docovee.DS.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Docovee.BLL.Audit;

public sealed class AuditRequestContext
{
    public string? ActorUserId { get; init; }
    public string? ActorUsername { get; init; }
    public string? ActorRole { get; init; }
    public string? IpAddress { get; init; }
    public string? UserAgent { get; init; }
}

public sealed class AuditLogRequest
{
    public required string Action { get; init; }
    public required string EntityType { get; init; }
    public string? EntityId { get; init; }
    public bool Success { get; init; } = true;
    public string? ErrorMessage { get; init; }
    public string? Summary { get; init; }
    public string? OldValuesJson { get; init; }
    public string? NewValuesJson { get; init; }
    public AuditRequestContext? Context { get; init; }
}

public interface IAuditTrailService
{
    AuditRequestContext GetCurrentContext();
    Task LogAsync(DocoveeDbContext db, AuditLogRequest request, CancellationToken cancellationToken = default);
    void AppendEntityChanges(DbContext db, IList<AuditTrail> buffer);

    Task LogReadAsync(DocoveeDbContext db, string entityType, string? entityId, string? summary = null, CancellationToken cancellationToken = default);
    Task LogSearchAsync(DocoveeDbContext db, string entityType, string? summary = null, CancellationToken cancellationToken = default);
    Task LogExportAsync(DocoveeDbContext db, string entityType, string? entityId, string? summary = null, CancellationToken cancellationToken = default);
    Task LogDiscloseAsync(DocoveeDbContext db, string entityType, string? entityId, string? summary = null, bool success = true, string? errorMessage = null, CancellationToken cancellationToken = default);
}

public sealed class AuditTrailService : IAuditTrailService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly IHttpContextAccessor _httpContextAccessor;

    public AuditTrailService(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public AuditRequestContext GetCurrentContext()
    {
        var http = _httpContextAccessor.HttpContext;
        if (http?.User?.Identity?.IsAuthenticated != true)
        {
            return new AuditRequestContext
            {
                ActorUsername = "system",
                ActorRole = "System",
                IpAddress = http?.Connection.RemoteIpAddress?.ToString(),
                UserAgent = Truncate(http?.Request.Headers["User-Agent"].ToString(), 500)
            };
        }

        return new AuditRequestContext
        {
            ActorUserId = http.User.FindFirstValue(ClaimTypes.NameIdentifier),
            ActorUsername = http.User.Identity?.Name,
            ActorRole = http.User.FindFirstValue(ClaimTypes.Role),
            IpAddress = http.Connection.RemoteIpAddress?.ToString(),
            UserAgent = Truncate(http.Request.Headers["User-Agent"].ToString(), 500)
        };
    }

    public async Task LogAsync(DocoveeDbContext db, AuditLogRequest request, CancellationToken cancellationToken = default)
    {
        var ctx = request.Context ?? GetCurrentContext();
        db.AuditTrails.Add(new AuditTrail
        {
            OccurredAtUtc = DateTime.UtcNow,
            Action = request.Action,
            EntityType = request.EntityType,
            EntityId = request.EntityId,
            ActorUserId = ctx.ActorUserId,
            ActorUsername = ctx.ActorUsername,
            ActorRole = ctx.ActorRole,
            IpAddress = ctx.IpAddress,
            UserAgent = ctx.UserAgent,
            Success = request.Success,
            ErrorMessage = Truncate(request.ErrorMessage, 1000),
            Summary = Truncate(request.Summary, 500),
            OldValuesJson = request.OldValuesJson,
            NewValuesJson = request.NewValuesJson
        });

        await db.SaveChangesAsync(cancellationToken);
    }

    public Task LogReadAsync(
        DocoveeDbContext db,
        string entityType,
        string? entityId,
        string? summary = null,
        CancellationToken cancellationToken = default) =>
        LogAsync(db, new AuditLogRequest
        {
            Action = AuditActions.Read,
            EntityType = entityType,
            EntityId = entityId,
            Summary = summary ?? $"Read {entityType}"
        }, cancellationToken);

    public Task LogSearchAsync(
        DocoveeDbContext db,
        string entityType,
        string? summary = null,
        CancellationToken cancellationToken = default) =>
        LogAsync(db, new AuditLogRequest
        {
            Action = AuditActions.Search,
            EntityType = entityType,
            Summary = summary ?? $"Searched {entityType}"
        }, cancellationToken);

    public Task LogExportAsync(
        DocoveeDbContext db,
        string entityType,
        string? entityId,
        string? summary = null,
        CancellationToken cancellationToken = default) =>
        LogAsync(db, new AuditLogRequest
        {
            Action = AuditActions.Export,
            EntityType = entityType,
            EntityId = entityId,
            Summary = summary ?? $"Exported {entityType}"
        }, cancellationToken);

    public Task LogDiscloseAsync(
        DocoveeDbContext db,
        string entityType,
        string? entityId,
        string? summary = null,
        bool success = true,
        string? errorMessage = null,
        CancellationToken cancellationToken = default) =>
        LogAsync(db, new AuditLogRequest
        {
            Action = AuditActions.Disclose,
            EntityType = entityType,
            EntityId = entityId,
            Success = success,
            ErrorMessage = errorMessage,
            Summary = summary ?? $"Disclosed {entityType}"
        }, cancellationToken);

    public void AppendEntityChanges(DbContext db, IList<AuditTrail> buffer)
    {
        var ctx = GetCurrentContext();

        foreach (var entry in db.ChangeTracker.Entries())
        {
            if (entry.Entity is AuditTrail)
                continue;

            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
                continue;

            var entityType = entry.Metadata.ClrType.Name;
            var entityId = GetEntityId(entry);
            var action = entry.State switch
            {
                EntityState.Added => AuditActions.Create,
                EntityState.Modified => AuditActions.Update,
                EntityState.Deleted => AuditActions.Delete,
                _ => "Unknown"
            };

            string? oldJson = null;
            string? newJson = null;
            string summary;

            if (entry.State == EntityState.Added)
            {
                newJson = AuditValueSerializer.SerializeEntity(entry.CurrentValues);
                summary = $"Created {entityType} {entityId}".Trim();
            }
            else if (entry.State == EntityState.Deleted)
            {
                oldJson = AuditValueSerializer.SerializeEntity(entry.OriginalValues);
                summary = $"Deleted {entityType} {entityId}".Trim();
            }
            else
            {
                var changes = AuditValueSerializer.SerializeChanges(entry);
                if (string.IsNullOrWhiteSpace(changes.oldJson) && string.IsNullOrWhiteSpace(changes.newJson))
                    continue;

                oldJson = changes.oldJson;
                newJson = changes.newJson;
                summary = $"Updated {entityType} {entityId}".Trim();
            }

            buffer.Add(new AuditTrail
            {
                OccurredAtUtc = DateTime.UtcNow,
                Action = action,
                EntityType = entityType,
                EntityId = entityId,
                ActorUserId = ctx.ActorUserId,
                ActorUsername = ctx.ActorUsername,
                ActorRole = ctx.ActorRole,
                IpAddress = ctx.IpAddress,
                UserAgent = ctx.UserAgent,
                Success = true,
                Summary = summary,
                OldValuesJson = oldJson,
                NewValuesJson = newJson
            });
        }
    }

    private static string? GetEntityId(EntityEntry entry)
    {
        var key = entry.Metadata.FindPrimaryKey();
        if (key == null || key.Properties.Count == 0)
            return null;

        if (key.Properties.Count == 1)
        {
            var value = entry.Property(key.Properties[0].Name).CurrentValue
                        ?? entry.Property(key.Properties[0].Name).OriginalValue;
            return value?.ToString();
        }

        var parts = key.Properties
            .Select(p => (entry.Property(p.Name).CurrentValue ?? entry.Property(p.Name).OriginalValue)?.ToString())
            .Where(v => !string.IsNullOrWhiteSpace(v));
        return string.Join(":", parts);
    }

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? value : value.Length <= max ? value : value[..max];
}

internal static class AuditValueSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private static readonly HashSet<string> SensitiveProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "PasswordHash",
        "Password",
        "NewPassword",
        "ConfirmPassword",
        "ApiKey",
        "DeveloperApiKey",
        "CustomerApiKey",
        "PrivateKey",
        "ClientSecret",
        "FullName",
        "Username",
        "Email",
        "Phone",
        "PatientPhone",
        "PatientEmail",
        "PatientName",
        "DateOfBirth",
        "PatientDateOfBirth",
        "Content",
        "Notes",
        "Transcript",
        "Summary",
        "OutcomeNotes",
        "PreferenceProfileJson",
        "SearchContextJson",
        "MedicalIssuesSummary",
        "ChiefComplaint",
        "MemberId",
        "CardPhotoUrl",
        "IdCardPhotoUrl",
        "PhoneVerificationCodeHash",
        "VerificationCodeHash"
    };

    private const int MaxValueLength = 2000;

    public static string? SerializeEntity(PropertyValues values)
    {
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in values.Properties)
        {
            if (ShouldSkip(prop.Name))
                continue;

            dict[prop.Name] = RedactOrTruncate(prop.Name, values[prop.Name]);
        }

        return dict.Count == 0 ? null : JsonSerializer.Serialize(dict, JsonOptions);
    }

    public static (string? oldJson, string? newJson) SerializeChanges(EntityEntry entry)
    {
        var oldDict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var newDict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var prop in entry.Properties)
        {
            if (ShouldSkip(prop.Metadata.Name))
                continue;

            if (!prop.IsModified && entry.State == EntityState.Modified)
                continue;

            if (entry.State == EntityState.Modified && Equals(prop.OriginalValue, prop.CurrentValue))
                continue;

            oldDict[prop.Metadata.Name] = RedactOrTruncate(prop.Metadata.Name, prop.OriginalValue);
            newDict[prop.Metadata.Name] = RedactOrTruncate(prop.Metadata.Name, prop.CurrentValue);
        }

        var oldJson = oldDict.Count == 0 ? null : JsonSerializer.Serialize(oldDict, JsonOptions);
        var newJson = newDict.Count == 0 ? null : JsonSerializer.Serialize(newDict, JsonOptions);
        return (oldJson, newJson);
    }

    private static bool ShouldSkip(string name) =>
        name.EndsWith("Navigation", StringComparison.Ordinal);

    private static object? RedactOrTruncate(string name, object? value)
    {
        if (SensitiveProperties.Contains(name))
            return "[REDACTED]";

        if (value is null)
            return null;

        if (value is string s)
            return s.Length <= MaxValueLength ? s : s[..MaxValueLength] + "…";

        if (value.GetType().IsClass && value.GetType() != typeof(string) && !(value is DateTime or DateOnly or DateTimeOffset))
        {
            var typeName = value.GetType().Name;
            return $"[{typeName}]";
        }

        return value;
    }
}
