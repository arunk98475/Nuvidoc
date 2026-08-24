using System.Security.Claims;
using Docovee.BLL.Auth;
using Docovee.BLL.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Docovee.Controllers.Api;

[ApiController]
[Route("api/notifications")]
[Authorize(Roles = AuthRoles.Patient)]
public class NotificationsController : ControllerBase
{
    private readonly IPatientNotificationService _notifications;

    public NotificationsController(IPatientNotificationService notifications)
    {
        _notifications = notifications;
    }

    [HttpGet]
    public async Task<ActionResult<PatientNotificationsPreviewResponse>> Get(
        CancellationToken cancellationToken)
    {
        if (!TryGetPatientId(out var patientId))
            return Unauthorized();

        var items = await _notifications.GetForPatientAsync(patientId, cancellationToken);
        var unreadCount = await _notifications.CountUnreadAsync(patientId, cancellationToken);

        return Ok(new PatientNotificationsPreviewResponse
        {
            Items = items,
            UnreadCount = unreadCount
        });
    }

    private bool TryGetPatientId(out int patientId)
    {
        patientId = 0;
        var idClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return int.TryParse(idClaim, out patientId);
    }
}

public sealed class PatientNotificationsPreviewResponse
{
    public IReadOnlyList<PatientNotificationDto> Items { get; init; } = Array.Empty<PatientNotificationDto>();
    public int UnreadCount { get; init; }
}
