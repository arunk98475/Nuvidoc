using Docovee.BLL.Configuration;
using Docovee.BLL.Services.PatientPush;
using Docovee.DS;
using Docovee.DS.Entities;
using Docovee.DS.Models;
using Docovee.logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

// Retry states stored per session in-memory during a cascade run.
// For cross-process durability this would need a DB column, but in-process Task.Run is enough.

namespace Docovee.BLL.Services;

public interface IVoiceCallCascadeService
{
    /// <summary>
    /// When CallScope is All, dial the next ranked doctor after a book call ended without booking.
    /// </summary>
    Task<VoiceCallCascadeResult> TryCallNextDoctorAsync(
        VoiceOutboundCall completedCall,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Dial the next ranked doctor (e.g. when the prior dial never connected).
    /// </summary>
    Task<VoiceCallCascadeResult> TryCallNextDoctorAsync(
        SearchSession session,
        SearchContextData context,
        VoiceOutboundCallRecordRequest patientInfo,
        IReadOnlyCollection<int> additionallyAttemptedDoctorIds,
        string? previousDoctorName,
        CancellationToken cancellationToken = default);
}

public sealed class VoiceCallCascadeResult
{
    public bool NextCallStarted { get; init; }
    public bool AllDoctorsExhausted { get; init; }
    public string? ChatMessage { get; init; }
    public string? NotificationTitle { get; init; }
    public string? NotificationBody { get; init; }
    public int? NextDoctorId { get; init; }
    public string? ConversationId { get; init; }
    public string? CallSid { get; init; }
}

public sealed class VoiceCallCascadeService : IVoiceCallCascadeService
{
    private readonly DocoveeDbContext _db;
    private readonly INuviVoiceCallingService _voiceCalling;
    private readonly TwilioOptions _twilio;
    private readonly ElevenLabsOptions _elevenLabs;
    private readonly IDocoveeLogger _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IPatientPushDispatcher _push;

    public VoiceCallCascadeService(
        DocoveeDbContext db,
        INuviVoiceCallingService voiceCalling,
        IOptions<TwilioOptions> twilio,
        IOptions<ElevenLabsOptions> elevenLabs,
        IDocoveeLogger logger,
        IServiceScopeFactory scopeFactory,
        IPatientPushDispatcher push)
    {
        _db = db;
        _voiceCalling = voiceCalling;
        _twilio = twilio.Value;
        _elevenLabs = elevenLabs.Value;
        _logger = logger;
        _scopeFactory = scopeFactory;
        _push = push;
    }

    public Task<VoiceCallCascadeResult> TryCallNextDoctorAsync(
        VoiceOutboundCall completedCall,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(completedCall.CallIntent, VoiceOutboundCallIntents.Book, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(new VoiceCallCascadeResult());

        if (completedCall.SearchSessionId is not > 0)
            return Task.FromResult(new VoiceCallCascadeResult());

        return TryCallNextDoctorCoreAsync(
            completedCall.SearchSessionId.Value,
            completedCall,
            additionallyAttemptedDoctorIds: Array.Empty<int>(),
            previousDoctorName: null,
            cancellationToken);
    }

    public Task<VoiceCallCascadeResult> TryCallNextDoctorAsync(
        SearchSession session,
        SearchContextData context,
        VoiceOutboundCallRecordRequest patientInfo,
        IReadOnlyCollection<int> additionallyAttemptedDoctorIds,
        string? previousDoctorName,
        CancellationToken cancellationToken = default) =>
        TryCallNextDoctorCoreAsync(
            session.Id,
            patientInfo,
            additionallyAttemptedDoctorIds,
            previousDoctorName,
            cancellationToken);

    private async Task<VoiceCallCascadeResult> TryCallNextDoctorCoreAsync(
        int searchSessionId,
        VoiceOutboundCallRecordRequest patientInfo,
        IReadOnlyCollection<int> additionallyAttemptedDoctorIds,
        string? previousDoctorName,
        CancellationToken cancellationToken)
    {
        var session = await _db.SearchSessions
            .FirstOrDefaultAsync(s => s.Id == searchSessionId, cancellationToken);
        if (session == null)
            return new VoiceCallCascadeResult();

        var context = SearchContextHelper.Load(session);
        return await TryCallNextDoctorCoreAsync(
            searchSessionId,
            patientInfo,
            context,
            session,
            additionallyAttemptedDoctorIds,
            previousDoctorName,
            cancellationToken);
    }

    private async Task<VoiceCallCascadeResult> TryCallNextDoctorCoreAsync(
        int searchSessionId,
        VoiceOutboundCall completedCall,
        IReadOnlyCollection<int> additionallyAttemptedDoctorIds,
        string? previousDoctorName,
        CancellationToken cancellationToken)
    {
        var session = await _db.SearchSessions
            .FirstOrDefaultAsync(s => s.Id == searchSessionId, cancellationToken);
        if (session == null)
            return new VoiceCallCascadeResult();

        var context = SearchContextHelper.Load(session);
        var patientInfo = new VoiceOutboundCallRecordRequest
        {
            ConversationId = completedCall.ConversationId,
            SessionKey = completedCall.SessionKey,
            SearchSessionId = completedCall.SearchSessionId,
            PatientId = completedCall.PatientId,
            DoctorId = completedCall.DoctorId,
            PatientName = completedCall.PatientName,
            PatientPhone = completedCall.PatientPhone,
            PatientEmail = completedCall.PatientEmail,
            VisitReason = completedCall.VisitReason,
            CallIntent = completedCall.CallIntent
        };

        return await TryCallNextDoctorCoreAsync(
            searchSessionId,
            patientInfo,
            context,
            session,
            additionallyAttemptedDoctorIds,
            previousDoctorName,
            cancellationToken);
    }

    private async Task<VoiceCallCascadeResult> TryCallNextDoctorCoreAsync(
        int searchSessionId,
        VoiceOutboundCallRecordRequest patientInfo,
        SearchContextData context,
        SearchSession session,
        IReadOnlyCollection<int> additionallyAttemptedDoctorIds,
        string? previousDoctorName,
        CancellationToken cancellationToken)
    {
        // Allow retries for both TopOne and All scopes.
        // For TopOne the matched list only has one doctor so the loop naturally
        // retries that same doctor up to MaxCallRetriesPerDoctor times, then exhausts.
        var isAllScope = context.CallScope == CallOfficeScope.All;
        var maxRetries = Math.Max(0, _elevenLabs.MaxCallRetriesPerDoctor);
        if (!isAllScope && maxRetries == 0)
            return new VoiceCallCascadeResult();

        if (context.MatchedDoctorIds == null || context.MatchedDoctorIds.Count == 0)
            return new VoiceCallCascadeResult();

        var pendingCall = await _db.VoiceOutboundCalls.AsNoTracking()
            .AnyAsync(c =>
                c.SearchSessionId == searchSessionId
                && c.CallIntent == VoiceOutboundCallIntents.Book
                && c.Status == VoiceOutboundCallStatuses.Initiated,
                cancellationToken);
        if (pendingCall)
            return new VoiceCallCascadeResult();

        var attemptedDoctorIds = await _db.VoiceOutboundCalls.AsNoTracking()
            .Where(c =>
                c.SearchSessionId == searchSessionId
                && c.CallIntent == VoiceOutboundCallIntents.Book)
            .Select(c => c.DoctorId)
            .Distinct()
            .ToListAsync(cancellationToken);

        foreach (var id in additionallyAttemptedDoctorIds)
        {
            if (!attemptedDoctorIds.Contains(id))
                attemptedDoctorIds.Add(id);
        }

        var overrideTo = ElevenLabsTwilioCallingService.ToE164(_twilio.OutboundOverrideToNumber);
        var allowMissingPhone = !string.IsNullOrWhiteSpace(overrideTo);

        string completedName = previousDoctorName ?? "the office";
        if (string.IsNullOrWhiteSpace(previousDoctorName) && patientInfo.DoctorId > 0)
        {
            var completedDoctor = await _db.Doctors.AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == patientInfo.DoctorId, cancellationToken);
            if (!string.IsNullOrWhiteSpace(completedDoctor?.Name))
                completedName = completedDoctor!.Name;
        }

        // Doctors whose call was answered but had no available slot — never call again.
        var answeredNoBookingDoctorIds = await _db.VoiceOutboundCalls.AsNoTracking()
            .Where(c =>
                c.SearchSessionId == searchSessionId
                && c.CallIntent == VoiceOutboundCallIntents.Book
                && (c.Status == VoiceOutboundCallStatuses.NoSlot
                    || c.Status == VoiceOutboundCallStatuses.Declined))
            .Select(c => c.DoctorId)
            .Distinct()
            .ToListAsync(cancellationToken);

        // Count how many times each doctor has already been dialed (for retry budget).
        var callCountPerDoctor = await _db.VoiceOutboundCalls.AsNoTracking()
            .Where(c =>
                c.SearchSessionId == searchSessionId
                && c.CallIntent == VoiceOutboundCallIntents.Book)
            .GroupBy(c => c.DoctorId)
            .Select(g => new { DoctorId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        var dialCountById = callCountPerDoctor.ToDictionary(x => x.DoctorId, x => x.Count);

        var retryDelay = TimeSpan.FromSeconds(Math.Max(0, _elevenLabs.CallRetryDelaySeconds));

        var bookingWindow = AppointmentRescheduleService.BuildPacificBookingWindow(context.UrgencyPreference);
        var urgencyWindow = bookingWindow.Phrase;
        var preferredTimeWindow = context.CallPreference == CallOfficePreference.DateAndTime
            ? "prefer a specific date and time that works within the booking window (Pacific Time)"
            : "any available time during office hours (Pacific Time)";
        var callPreferenceLabel = context.CallPreference == CallOfficePreference.DateAndTime
            ? "date_and_time"
            : context.CallPreference == CallOfficePreference.Dentist
                ? "dentist"
                : "any_time";
        var callContext = context.CallPreference == CallOfficePreference.DateAndTime
            ? $"Call offices in rank order until a booking is available {urgencyWindow}."
            : "Call offices in rank order until any booking is available.";
        var chiefComplaint = string.IsNullOrWhiteSpace(patientInfo.VisitReason)
            ? "dental appointment"
            : patientInfo.VisitReason!;

        // For TopOne scope, only retry the first doctor — never cascade to others.
        var candidateDoctorIds = isAllScope
            ? context.MatchedDoctorIds
            : context.MatchedDoctorIds.Take(1).ToList();

        foreach (var doctorId in candidateDoctorIds)
        {
            // Never retry a practice that answered but had no slots/declined.
            if (answeredNoBookingDoctorIds.Contains(doctorId))
            {
                _logger.LogInformation(
                    "Skipping doctor {DoctorId} — call was answered but no booking was available (session {SessionId})",
                    doctorId, searchSessionId);
                if (!attemptedDoctorIds.Contains(doctorId))
                    attemptedDoctorIds.Add(doctorId);
                continue;
            }

            // How many times has this doctor already been dialed?
            dialCountById.TryGetValue(doctorId, out var priorDialCount);
            // total allowed dials = 1 initial + maxRetries
            if (priorDialCount > maxRetries)
            {
                _logger.LogInformation(
                    "Skipping doctor {DoctorId} — already dialed {Count} time(s), retry limit is {Max} (session {SessionId})",
                    doctorId, priorDialCount, maxRetries, searchSessionId);
                if (!attemptedDoctorIds.Contains(doctorId))
                    attemptedDoctorIds.Add(doctorId);
                continue;
            }

            var doctor = await _db.Doctors.AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == doctorId, cancellationToken);
            if (doctor == null)
                continue;

            var phoneE164 = await ResolveDoctorPhoneE164Async(doctor.Id, doctor.OfficePhoneNumber, cancellationToken);
            if (string.IsNullOrWhiteSpace(phoneE164) && !allowMissingPhone)
                continue;

            var dialNumber = !string.IsNullOrWhiteSpace(overrideTo) ? overrideTo! : phoneE164;
            if (string.IsNullOrWhiteSpace(dialNumber))
                continue;

            if (!_voiceCalling.IsConfigured)
                return new VoiceCallCascadeResult { AllDoctorsExhausted = true };

            // If this is a retry (not the first call to this doctor), wait the configured delay.
            var isRetry = priorDialCount > 0;
            if (isRetry && retryDelay > TimeSpan.Zero)
            {
                _logger.LogInformation(
                    "Retry {Attempt}/{Max} for doctor {DoctorId} — waiting {Delay}s before redialing (session {SessionId})",
                    priorDialCount + 1, maxRetries + 1, doctorId, _elevenLabs.CallRetryDelaySeconds, searchSessionId);
                await Task.Delay(retryDelay, cancellationToken);
            }

            var callResult = await _voiceCalling.PlaceOfficeCallAsync(new NuviOutboundCallRequest
            {
                ToNumber = dialNumber,
                DoctorName = doctor.Name,
                PracticeName = doctor.PracticeName,
                PracticePhone = phoneE164,
                PatientName = patientInfo.PatientName,
                PatientPhone = patientInfo.PatientPhone,
                PatientEmail = patientInfo.PatientEmail,
                CallPreference = callPreferenceLabel,
                AvailabilityWindow = urgencyWindow,
                PreferredDate = urgencyWindow,
                PreferredTimeWindow = preferredTimeWindow,
                BookingWindowStart = bookingWindow.StartDate,
                BookingWindowEnd = bookingWindow.EndDate,
                AppointmentType = chiefComplaint,
                InsuranceName = context.InsurancePreference ?? context.InsuranceCategory,
                ChiefComplaint = chiefComplaint,
                CallContext = callContext,
                SessionKey = session.SessionKey.ToString()
            }, cancellationToken);

            if (!callResult.Success || string.IsNullOrWhiteSpace(callResult.ConversationId))
            {
                _logger.LogInformation(
                    "Cascade dial failed for doctor {DoctorId} session {SessionId}: {Message}",
                    doctor.Id, session.Id, callResult.Message);
                attemptedDoctorIds.Add(doctor.Id);
                continue;
            }

            using (var scope = _scopeFactory.CreateScope())
            {
                var voiceBookings = scope.ServiceProvider.GetRequiredService<IVoiceCallBookingService>();
                await voiceBookings.RecordInitiatedCallAsync(new VoiceOutboundCallRecordRequest
                {
                    ConversationId = callResult.ConversationId!,
                    CallSid = callResult.CallSid,
                    SessionKey = session.SessionKey,
                    SearchSessionId = session.Id,
                    PatientId = patientInfo.PatientId ?? session.PatientId,
                    DoctorId = doctor.Id,
                    PatientName = patientInfo.PatientName,
                    PatientPhone = patientInfo.PatientPhone,
                    PatientEmail = patientInfo.PatientEmail,
                    VisitReason = chiefComplaint,
                    ToNumber = dialNumber
                }, cancellationToken);
                voiceBookings.ScheduleConversationPolling(callResult.ConversationId!);
            }

            context.SelectedDoctorId = doctor.Id;
            context.Stage = NuviConversationStage.CallingOffices;
            SearchContextHelper.Save(session, context);

            var nextName = string.IsNullOrWhiteSpace(doctor.Name) ? "the next matched doctor" : doctor.Name;
            var nextPractice = VoiceCallBookingService.FormatPracticeLabel(doctor.PracticeName, doctor.Name);
            string chatText;
            if (isRetry)
            {
                chatText =
                    $"I wasn't able to reach {nextPractice} — retrying now (attempt {priorDialCount + 1} of {maxRetries + 1}).";
            }
            else
            {
                var completedPractice = VoiceCallBookingService.FormatPracticeLabel(null, completedName);
                chatText =
                    $"I couldn't complete a booking with {completedPractice}. {VoiceCallBookingService.FormatAttemptingCallChat(nextPractice)}";
            }
            if (!string.IsNullOrWhiteSpace(overrideTo))
                chatText += $"\n\n(Dev override: dialing {overrideTo} instead of the office number.)";

            await AppendAssistantChatMessageAsync(
                session,
                chatText,
                patientInfo.PatientId ?? session.PatientId,
                doctor.Id,
                cancellationToken);

            _logger.LogInformation(
                "Cascade call started for doctor {DoctorId} (attempt {Attempt}/{Max}) after {PreviousDoctor} session {SessionId} conversation {ConversationId}",
                doctor.Id, priorDialCount + 1, maxRetries + 1, completedName, session.Id, callResult.ConversationId);

            return new VoiceCallCascadeResult
            {
                NextCallStarted = true,
                ChatMessage = chatText,
                NotificationTitle = "Nuvi call update",
                NotificationBody = isRetry
                    ? $"Retrying {nextName} (attempt {priorDialCount + 1} of {maxRetries + 1})."
                    : $"Couldn't reach {completedName}. Calling {nextName} next.",
                NextDoctorId = doctor.Id,
                ConversationId = callResult.ConversationId,
                CallSid = callResult.CallSid
            };
        }

        var exhaustedText =
            $"I tried calling your matched doctors starting with {completedName}, but couldn't complete a booking. You can call an office directly from the list above, or start a new search anytime.";
        await AppendAssistantChatMessageAsync(
            session,
            exhaustedText,
            patientInfo.PatientId ?? session.PatientId,
            patientInfo.DoctorId > 0 ? patientInfo.DoctorId : null,
            cancellationToken);

        return new VoiceCallCascadeResult
        {
            AllDoctorsExhausted = true,
            ChatMessage = exhaustedText,
            NotificationTitle = "Nuvi call update",
            NotificationBody =
                "I couldn't book with any of your matched offices. Please call them directly or try again."
        };
    }

    private async Task AppendAssistantChatMessageAsync(
        SearchSession session,
        string content,
        int? patientId,
        int? doctorId,
        CancellationToken cancellationToken)
    {
        _db.ChatMessages.Add(new ChatMessage
        {
            SearchSessionId = session.Id,
            Role = "assistant",
            Content = content,
            CreatedAt = DateTime.UtcNow
        });
        session.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        await _push.DispatchAsync(new PatientPushMessage
        {
            Type = PatientNotificationTypes.VoiceCallUpdate,
            PatientId = patientId ?? session.PatientId,
            SessionKey = session.SessionKey,
            Status = VoiceOutboundCallStatuses.Initiated,
            Title = "Nuvi call update",
            Body = content,
            ChatMessage = content,
            DoctorId = doctorId
        }, cancellationToken);
    }

    private async Task<string?> ResolveDoctorPhoneE164Async(
        int doctorId,
        string? officePhone,
        CancellationToken cancellationToken)
    {
        var phone = ElevenLabsTwilioCallingService.ToE164(officePhone);
        if (!string.IsNullOrWhiteSpace(phone))
            return phone;

        var locationPhone = await _db.DoctorLocations.AsNoTracking()
            .Where(l => l.DoctorId == doctorId && l.PhoneNumber != null && l.PhoneNumber != "")
            .Select(l => l.PhoneNumber)
            .FirstOrDefaultAsync(cancellationToken);
        return ElevenLabsTwilioCallingService.ToE164(locationPhone);
    }
}
