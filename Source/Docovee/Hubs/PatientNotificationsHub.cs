using Docovee.BLL.Auth;
using Docovee.DS;
using Docovee.DS.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Docovee.Hubs;

/// <summary>
/// Real-time booking / call updates for web clients.
/// Connection may be anonymous (guest chat needs session groups), but group joins are gated:
/// session groups require a real SearchSession; patient groups require Patient role.
/// </summary>
[AllowAnonymous]
public sealed class PatientNotificationsHub : Hub
{
    private readonly DocoveeDbContext _db;

    public PatientNotificationsHub(DocoveeDbContext db) => _db = db;

    /// <summary>Join a Nuvi chat session group. Session key must exist in the database.</summary>
    [AllowAnonymous]
    public async Task JoinSession(string sessionKey)
    {
        if (!Guid.TryParse(sessionKey, out var key) || key == Guid.Empty)
            return;

        var exists = await _db.SearchSessions.AsNoTracking()
            .AnyAsync(s => s.SessionKey == key);
        if (!exists)
            return;

        await Groups.AddToGroupAsync(Context.ConnectionId, PatientPushGroupNames.Session(key));
    }

    [AllowAnonymous]
    public async Task LeaveSession(string sessionKey)
    {
        if (!Guid.TryParse(sessionKey, out var key) || key == Guid.Empty)
            return;

        await Groups.RemoveFromGroupAsync(Context.ConnectionId, PatientPushGroupNames.Session(key));
    }

    /// <summary>Join the authenticated patient's private notification group.</summary>
    [Authorize(Roles = AuthRoles.Patient)]
    public async Task JoinPatient()
    {
        var idClaim = Context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (!int.TryParse(idClaim, out var patientId) || patientId <= 0)
            return;

        await Groups.AddToGroupAsync(Context.ConnectionId, PatientPushGroupNames.Patient(patientId));
    }
}
