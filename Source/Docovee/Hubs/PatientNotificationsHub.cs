using Docovee.DS.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Docovee.Hubs;

/// <summary>Real-time booking / call updates for the mobile (and web) clients.</summary>
[AllowAnonymous]
public sealed class PatientNotificationsHub : Hub
{
    public Task JoinSession(string sessionKey)
    {
        if (!Guid.TryParse(sessionKey, out var key) || key == Guid.Empty)
            return Task.CompletedTask;
        return Groups.AddToGroupAsync(Context.ConnectionId, PatientPushGroupNames.Session(key));
    }

    public Task LeaveSession(string sessionKey)
    {
        if (!Guid.TryParse(sessionKey, out var key) || key == Guid.Empty)
            return Task.CompletedTask;
        return Groups.RemoveFromGroupAsync(Context.ConnectionId, PatientPushGroupNames.Session(key));
    }

    public Task JoinPatient()
    {
        var idClaim = Context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (!int.TryParse(idClaim, out var patientId) || patientId <= 0)
            return Task.CompletedTask;
        return Groups.AddToGroupAsync(Context.ConnectionId, PatientPushGroupNames.Patient(patientId));
    }
}
