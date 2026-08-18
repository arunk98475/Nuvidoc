using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Docovee.BLL.Auth;
using Docovee.BLL.Configuration;
using Docovee.BLL.Data;
using Docovee.DS;
using Docovee.DS.Entities;
using Docovee.DS.Enums;
using Docovee.DS.Models;
using Docovee.logging;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Docovee.BLL.Services;

public interface IAnthropicChatService
{
    Task<ChatMessageResponse> SendMessageAsync(ChatMessageRequest request, HttpContext? httpContext = null, CancellationToken cancellationToken = default);
}

public class AnthropicChatService : IAnthropicChatService
{
    private const int MaxTriageQuestions = 3;
    private const int MaxDeepDiveQuestions = 10;
    private const int LogisticsStepNewLocation = 11;
    private const string RedactedPasswordPlaceholder = "[password hidden]";
    private static readonly DateOnly PlaceholderDateOfBirth = new(1990, 1, 1);

    private static readonly Regex RoutingRegex = new(
        @"SPECIALTY:\s*([^|]+)\s*\|\s*URGENCY:\s*([^|]+)(?:\|\s*NOTES:\s*(.+))?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly HttpClient _httpClient;
    private readonly DocoveeDbContext _db;
    private readonly AnthropicOptions _options;
    private readonly IDocoveeLogger _logger;
    private readonly IPollingQuestionService _pollingQuestions;
    private readonly IAnthropicValidationService _validationService;
    private readonly IDoctorSearchService _doctorSearch;
    private readonly IPatientService _patientService;
    private readonly IAccountAuthService _accountAuthService;
    private readonly IBrandingService _branding;
    private readonly IDoctorLanguageService _doctorLanguages;
    private readonly IPatientDoctorContactService _patientDoctorContacts;
    private readonly IClaudeGoogleReviewService _googleReviews;
    private readonly INuviVoiceCallingService _voiceCalling;
    private readonly IVoiceCallBookingService _voiceBookings;
    private readonly IVoiceCallCascadeService _voiceCascade;
    private readonly IAppointmentService _appointments;
    private readonly IAppointmentCancelService _appointmentCancel;
    private readonly IAppointmentRescheduleService _appointmentReschedule;
    private readonly TwilioOptions _twilioOptions;

    public AnthropicChatService(
        HttpClient httpClient,
        DocoveeDbContext db,
        IOptions<AnthropicOptions> options,
        IDocoveeLogger logger,
        IPollingQuestionService pollingQuestions,
        IAnthropicValidationService validationService,
        IDoctorSearchService doctorSearch,
        IPatientService patientService,
        IAccountAuthService accountAuthService,
        IBrandingService branding,
        IDoctorLanguageService doctorLanguages,
        IPatientDoctorContactService patientDoctorContacts,
        IClaudeGoogleReviewService googleReviews,
        INuviVoiceCallingService voiceCalling,
        IVoiceCallBookingService voiceBookings,
        IVoiceCallCascadeService voiceCascade,
        IAppointmentService appointments,
        IAppointmentCancelService appointmentCancel,
        IAppointmentRescheduleService appointmentReschedule,
        IOptions<TwilioOptions> twilioOptions)
    {
        _httpClient = httpClient;
        _db = db;
        _options = options.Value;
        _logger = logger;
        _pollingQuestions = pollingQuestions;
        _validationService = validationService;
        _doctorSearch = doctorSearch;
        _patientService = patientService;
        _accountAuthService = accountAuthService;
        _branding = branding;
        _doctorLanguages = doctorLanguages;
        _patientDoctorContacts = patientDoctorContacts;
        _googleReviews = googleReviews;
        _voiceCalling = voiceCalling;
        _voiceBookings = voiceBookings;
        _voiceCascade = voiceCascade;
        _appointments = appointments;
        _appointmentCancel = appointmentCancel;
        _appointmentReschedule = appointmentReschedule;
        _twilioOptions = twilioOptions.Value;
    }

    private string TriageSystemPrompt => $"""
        You are {_branding.ChatBotName}, {_branding.SiteName}'s AI doctor-matching concierge. Your job is to understand enough about the patient's situation to match them with the RIGHT doctor — not just the right specialty.

        You are NOT a doctor. Never diagnose. Never recommend treatment. Never interrogate symptoms like a clinical intake.

        PHASE — TRIAGE
        1. Read the patient's message and internally identify 1–2 likely specialties from the valid list below.
        2. Respond with genuine empathy (1 sentence) acknowledging what they shared.
        3. Ask ONE clarifying question that helps you match them to the best doctor — focused on care goals, timing, or fit — NOT on symptoms or diagnosis.

        GOOD clarifying questions (matching-focused):
        - "Are you looking for someone to help manage it long-term, or would you like it properly evaluated first?"
        - "Do you already have a specialty in mind, or would you like my recommendation?"
        - For tooth/dental issues: "Are you looking to get the pain taken care of first, focus on replacing missing teeth, or both?"
        - For tooth/dental issues: acknowledge and move toward matching — e.g. "Sounds like you're looking for a dentist — let's get you taken care of."

        Do NOT ask how soon they want to be seen or about urgency — that is asked later in logistics.

        EXAMPLE tone:
        "That sounds really frustrating — ongoing back pain is exhausting. Are you looking for someone to help manage it long-term, or would you like it properly evaluated first?"

        FORBIDDEN — never ask about:
        - Whether they already have a dentist, doctor, or provider ("are you currently seeing...", "already working with...", "do you have a dentist")
        - Pain quality (sharp, dull, throbbing), severity scales, or symptom characterization
        - When symptoms started, how long they've had them, or whether something is recent vs building up over time
        - Swelling, fever, triggers, hot/cold sensitivity, or other clinical detail
        - Medications, test results, or anything that narrows a diagnosis

        IF the patient's message is vague (e.g. "I need a doctor", "not feeling well", "health issues") use this style of clarifying question:
        "Got it — that sounds like something worth addressing. Are you looking more for a doctor to help manage something ongoing, or do you have a specific concern you'd like evaluated?"

        Even when the patient names a clear specialty or symptom (e.g. tooth pain, back pain, skin rash), still ask ONE matching-focused clarifying question before routing — never skip straight to the routing signal on the first response.

        After the patient answers your ONE clarifying question (e.g. "both", "pain first", "long-term"), output the ROUTING SIGNAL immediately on that turn. Do NOT ask another question — especially not about symptom onset, duration, severity, or whether something is recent or ongoing.

        Ask ONE question per turn. Only ONE clarifying question total before routing.

        RULES:
        - SHORT responses — empathy + one question (2–3 sentences max)
        - Warm and calm — patients may be anxious
        - ONE question per turn, never multiple
        - Do NOT output the routing signal on your first response when the message is vague — ask one clarifying question first
        - Emergency symptoms (chest pain, difficulty breathing, stroke signs) — say call 911 immediately, set URGENCY: emergency, then route

        Valid specialties: General Dentist, Oral Surgeon, Periodontist, Orthodontist, Family Medicine, Internal Medicine, Dermatologist, Orthopedic Surgeon, Neurologist, Cardiologist, OB/GYN, Pediatrician, Psychiatrist, Physical Therapist, Urgent Care

        ROUTING SIGNAL — output on its own line only when ready to move on (after at least one clarifying exchange):
        SPECIALTY: [name] | URGENCY: [routine/urgent/emergency] | NOTES: [1 sentence about matching context — NOT clinical assessment]
        """;

    public async Task<ChatMessageResponse> SendMessageAsync(ChatMessageRequest request, HttpContext? httpContext = null, CancellationToken cancellationToken = default)
    {
        var session = await GetOrCreateSessionAsync(request.SessionKey, cancellationToken);
        var context = SearchContextHelper.Load(session);
        await ApplyAuthenticatedPatientAsync(session, context, httpContext, cancellationToken);

        if (string.Equals(request.Action, "signup", StringComparison.OrdinalIgnoreCase))
        {
            ChatMessageResponse signupResponse;
            if (context.SkipAccountCreation)
            {
                var alreadyIn = "You're already signed in. Tell me what you're looking for and I'll find the right dentist.";
                await SaveAssistantMessageAsync(session, alreadyIn, cancellationToken);
                signupResponse = BuildResponse(session, context, alreadyIn, stage: context.Stage);
            }
            else
            {
                signupResponse = await BeginAccountCreationAsync(session, context, cancellationToken);
            }

            SearchContextHelper.Save(session, context);
            await _db.SaveChangesAsync(cancellationToken);
            return signupResponse;
        }

        if (context.AwaitingMatchSearch
            && string.Equals(request.Action, "match_search", StringComparison.OrdinalIgnoreCase))
        {
            var matchResponse = await ExecuteMatchSearchAsync(session, context, cancellationToken);
            SearchContextHelper.Save(session, context);
            await _db.SaveChangesAsync(cancellationToken);
            return matchResponse;
        }

        var isDoctorCardOnly = IsDoctorCardOnlyRequest(request);
        var effectiveMessage = request.Message ?? string.Empty;
        var skipIncomingValidation =
            isDoctorCardOnly
            || IsPasswordSubmission(context)
            || IsCollectingMissingProfileFields(context)
            || context.Stage == NuviConversationStage.CancelBooking
            || context.Stage == NuviConversationStage.RescheduleBooking
            || context.Stage == NuviConversationStage.PostCancelNewBooking
            || IsCancelBookingChip(request.Message)
            || IsRescheduleBookingChip(request.Message);

        // Free-text cancel intent (Claude) must run before triage validation, which would reject it.
        if (!skipIncomingValidation
            && context.SkipAccountCreation
            && CanEnterCancelFromStage(context.Stage)
            && !string.IsNullOrWhiteSpace(request.Message)
            && await DetectCancelBookingIntentAsync(request.Message, cancellationToken))
        {
            skipIncomingValidation = true;
            context.PendingNormalizedAnswer = NuviFlowContent.CancelBookingChip;
        }

        if (!string.IsNullOrWhiteSpace(request.Message) && !skipIncomingValidation)
        {
            var validationBlock = await TryValidateIncomingMessageAsync(
                session, context, request.Message, cancellationToken);
            if (validationBlock != null)
            {
                SearchContextHelper.Save(session, context);
                return validationBlock;
            }

            effectiveMessage = context.PendingNormalizedAnswer ?? request.Message.Trim();
            context.PendingNormalizedAnswer = null;
        }
        else if (!string.IsNullOrWhiteSpace(context.PendingNormalizedAnswer))
        {
            effectiveMessage = context.PendingNormalizedAnswer;
            context.PendingNormalizedAnswer = null;
        }
        else if (!string.IsNullOrWhiteSpace(request.Message))
        {
            effectiveMessage = request.Message.Trim();
        }

        if (!string.IsNullOrWhiteSpace(request.Message) && !isDoctorCardOnly)
        {
            _db.ChatMessages.Add(new ChatMessage
            {
                SearchSessionId = session.Id,
                Role = "user",
                Content = IsPasswordSubmission(context)
                    ? RedactedPasswordPlaceholder
                    : request.Message
            });
            await _db.SaveChangesAsync(cancellationToken);
        }

        if (request.SelectedDoctorId.HasValue)
            context.SelectedDoctorId = request.SelectedDoctorId;

        if (request.Action == "book" && context.SelectedDoctorId.HasValue)
            context.BookingConfirmed = true;

        // Registered-user cancel / reschedule shortcuts (chip or free-text intent).
        if (context.SkipAccountCreation
            && !IsCollectingMissingProfileFields(context)
            && context.Stage != NuviConversationStage.CancelBooking
            && context.Stage != NuviConversationStage.RescheduleBooking
            && context.Stage != NuviConversationStage.PostCancelNewBooking
            && !string.IsNullOrWhiteSpace(effectiveMessage))
        {
            if (IsRescheduleBookingChip(effectiveMessage))
            {
                var rescheduleStart = await BeginRescheduleBookingAsync(session, context, cancellationToken);
                SearchContextHelper.Save(session, context);
                await _db.SaveChangesAsync(cancellationToken);
                return rescheduleStart;
            }

            if (IsCancelBookingChip(effectiveMessage)
                || string.Equals(effectiveMessage, NuviFlowContent.CancelBookingChip, StringComparison.OrdinalIgnoreCase))
            {
                var cancelStart = await BeginCancelBookingAsync(session, context, cancellationToken);
                SearchContextHelper.Save(session, context);
                await _db.SaveChangesAsync(cancellationToken);
                return cancelStart;
            }
        }

        var response = context.Stage switch
        {
            NuviConversationStage.Greeting => await HandleGreetingAsync(session, context, effectiveMessage, httpContext, cancellationToken),
            NuviConversationStage.Triage => await HandleTriageAsync(session, context, effectiveMessage, cancellationToken),
            NuviConversationStage.ImplantQualification => await HandleImplantQualificationAsync(session, context, effectiveMessage, cancellationToken),
            NuviConversationStage.Logistics => await HandleLogisticsAsync(session, context, effectiveMessage, cancellationToken),
            NuviConversationStage.MomentumBridge => await HandleMomentumBridgeAsync(session, context, effectiveMessage, httpContext, cancellationToken),
            NuviConversationStage.DeepDivePermission => await HandleDeepDivePermissionAsync(session, context, effectiveMessage, httpContext, cancellationToken),
            NuviConversationStage.AccountCreation => await HandleAccountCreationAsync(session, context, effectiveMessage, httpContext, cancellationToken),
            NuviConversationStage.DeepDive => await HandleDeepDiveAsync(session, context, effectiveMessage, cancellationToken),
            NuviConversationStage.RecommendationReveal => await HandleRecommendationRevealAsync(session, context, request, cancellationToken),
            NuviConversationStage.DoctorExplore => await HandleDoctorExploreAsync(session, context, request, cancellationToken),
            NuviConversationStage.CallingConsent => await HandleCallingConsentAsync(session, context, request, effectiveMessage, cancellationToken),
            NuviConversationStage.CallingOffices => await HandleCallingOfficesAsync(session, context, cancellationToken),
            NuviConversationStage.BookingInitiation => await HandleBookingInitiationAsync(session, context, request, cancellationToken),
            NuviConversationStage.CancelBooking => await HandleCancelBookingAsync(session, context, effectiveMessage, cancellationToken),
            NuviConversationStage.RescheduleBooking => await HandleRescheduleBookingAsync(session, context, effectiveMessage, cancellationToken),
            NuviConversationStage.PostCancelNewBooking => await HandlePostCancelNewBookingAsync(session, context, effectiveMessage, cancellationToken),
            NuviConversationStage.Confirmation or NuviConversationStage.Complete =>
                context.SkipAccountCreation
                    ? BuildResponse(session, context,
                        "You're all set! I'm here whenever you need to find another doctor, cancel a booking, or ask something else.",
                        options: RegisteredQuickConcernChips(),
                        optionsOnly: false,
                        flowComplete: true)
                    : BuildResponse(session, context,
                        "You're all set! I'm here whenever you need to find another doctor.", flowComplete: true),
            _ => await HandleGreetingAsync(session, context, effectiveMessage, httpContext, cancellationToken)
        };

        SearchContextHelper.Save(session, context);
        await _db.SaveChangesAsync(cancellationToken);
        return response;
    }

    private async Task<ChatMessageResponse> HandleGreetingAsync(
        SearchSession session, SearchContextData context, string message, HttpContext? httpContext, CancellationToken cancellationToken)
    {
        if (context.SkipAccountCreation)
            return await HandleSignedInPatientConcernAsync(session, context, message, cancellationToken);

        var answer = message.Trim();

        switch (context.GreetingStep)
        {
            case 0:
                return await HandleGuestImplantWelcomeAsync(session, context, answer, cancellationToken);

            case 1:
                if (TryParseFirstVisitAnswer(answer, out var isFirstVisit))
                {
                    if (isFirstVisit)
                    {
                        context.Stage = NuviConversationStage.Triage;
                        var healthMessage = await GetInitialHealthConcernAsync(session.Id, cancellationToken);
                        return await HandleTriageAsync(session, context, healthMessage, cancellationToken);
                    }

                    context.GreetingStep = 2;
                    await SaveAssistantMessageAsync(session, NuviFlowContent.ReturningUsernameQuestion, cancellationToken);
                    return BuildResponse(session, context, NuviFlowContent.ReturningUsernameQuestion,
                        stage: NuviConversationStage.Greeting);
                }

                return BuildResponse(session, context,
                    "Please choose Yes or No — is this your first time visiting us?",
                    stage: NuviConversationStage.Greeting,
                    options: NuviFlowContent.FirstVisitOptions);

            case 2:
                if (string.IsNullOrWhiteSpace(answer))
                {
                    return BuildResponse(session, context,
                        "Please enter your username or email address.",
                        stage: NuviConversationStage.Greeting);
                }

                context.PendingUsername = answer.Trim();
                context.GreetingStep = 3;
                await SaveAssistantMessageAsync(session, NuviFlowContent.ReturningPasswordQuestion, cancellationToken);
                return BuildResponse(session, context, NuviFlowContent.ReturningPasswordQuestion,
                    stage: NuviConversationStage.Greeting, usePasswordInput: true);

            case 3:
                if (httpContext == null)
                {
                    return BuildResponse(session, context,
                        "Unable to sign in right now. Please try again.",
                        stage: NuviConversationStage.Greeting, usePasswordInput: true);
                }

                if (string.IsNullOrWhiteSpace(answer))
                {
                    return BuildResponse(session, context,
                        "Please enter your password.",
                        stage: NuviConversationStage.Greeting, usePasswordInput: true);
                }

                var loginResult = await _accountAuthService.LoginAsync(new AccountLoginRequest
                {
                    AccountType = AccountType.Patient,
                    Username = context.PendingUsername!,
                    Password = answer
                }, httpContext, cancellationToken);

                if (!loginResult.Success)
                {
                    return BuildResponse(session, context,
                        loginResult.Error ?? "That password didn't work. Please try again.",
                        stage: NuviConversationStage.Greeting, usePasswordInput: true);
                }

                var signedInPatient = await _db.Patients
                    .AsNoTracking()
                    .FirstOrDefaultAsync(p => p.Username == context.PendingUsername, cancellationToken);

                if (signedInPatient != null)
                {
                    session.PatientId = signedInPatient.Id;
                    context.PendingFullName = signedInPatient.FullName;
                    context.PatientDateOfBirth = signedInPatient.DateOfBirth;
                    ApplyPatientPhoneToContext(context, signedInPatient.Phone);
                    context.SkipAccountCreation = true;
                    await LoadReturningPatientProfileAsync(session, context, signedInPatient, cancellationToken);
                }

                context.GreetingStep = 0;
                context.Stage = NuviConversationStage.Triage;
                var healthConcern = await GetInitialHealthConcernAsync(session.Id, cancellationToken);
                return await HandleSignedInPatientConcernAsync(session, context, healthConcern, cancellationToken);

            default:
                context.GreetingStep = 0;
                return await HandleGreetingAsync(session, context, message, httpContext, cancellationToken);
        }
    }

    private async Task<ChatMessageResponse> HandleGuestImplantWelcomeAsync(
        SearchSession session,
        SearchContextData context,
        string answer,
        CancellationToken cancellationToken)
    {
        if (IsGuestImplantWelcomeNo(answer))
            return await EndGuestImplantWelcomeAsync(session, context, cancellationToken);

        if (IsGuestImplantWelcomeYes(answer) || IsImplantConcern(answer))
        {
            await PrepareImplantSessionAsync(session, context, cancellationToken);
            context.ImplantIntentQualified = true;
            context.Stage = NuviConversationStage.ImplantQualification;
            return await AskImplantQualificationQuestionAsync(session, context, 1, cancellationToken);
        }

        var reprompt = "Please choose Yes or No.";
        await SaveAssistantMessageAsync(session, reprompt, cancellationToken);
        return BuildResponse(
            session,
            context,
            reprompt,
            stage: NuviConversationStage.Greeting,
            options: NuviFlowContent.GuestImplantWelcomeOptions,
            optionsOnly: true);
    }

    private async Task<ChatMessageResponse> EndGuestImplantWelcomeAsync(
        SearchSession session,
        SearchContextData context,
        CancellationToken cancellationToken)
    {
        context.Stage = NuviConversationStage.Complete;
        await SaveAssistantMessageAsync(session, NuviFlowContent.GuestImplantWelcomeDeclinedMessage, cancellationToken);
        return BuildResponse(
            session,
            context,
            NuviFlowContent.GuestImplantWelcomeDeclinedMessage,
            stage: NuviConversationStage.Complete,
            flowComplete: true);
    }

    private async Task<ChatMessageResponse> HandleSignedInPatientConcernAsync(
        SearchSession session,
        SearchContextData context,
        string message,
        CancellationToken cancellationToken)
    {
        if (IsImplantConcern(message))
        {
            await PrepareImplantSessionAsync(session, context, cancellationToken);

            if (IsImplantQualificationPassAnswer(message))
            {
                context.ImplantIntentQualified = true;
                context.Stage = NuviConversationStage.ImplantQualification;
                return await AskImplantQualificationQuestionAsync(session, context, 1, cancellationToken);
            }

            return await BeginImplantQualificationAsync(session, context, cancellationToken);
        }

        context.Stage = NuviConversationStage.Triage;
        return await HandleTriageAsync(session, context, message, cancellationToken);
    }

    private async Task<ChatMessageResponse> HandleTriageAsync(
        SearchSession session, SearchContextData context, string message, CancellationToken cancellationToken)
    {
        context.TriageQuestionCount++;

        if (context.TriageQuestionCount >= 2)
        {
            var empathy = GetTriageCompletionEmpathy(message);
            return await CompleteTriageWithInferenceAsync(session, context, empathy, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(_options.ApiKey) || string.IsNullOrWhiteSpace(_options.Model))
        {
            return await HandleTriageFallbackAsync(session, context, message, cancellationToken);
        }

        try
        {
            var history = await GetChatHistoryAsync(session.Id, cancellationToken);
            var payload = AnthropicApiHelper.BuildPayload(
                _options,
                maxTokens: 1000,
                system: TriageSystemPrompt,
                messages: history,
                includeWebSearch: true);

            using var httpRequest = AnthropicApiHelper.CreateMessageRequest(_options, payload);
            var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError(new InvalidOperationException(responseBody), "Anthropic API call failed");
                return await HandleTriageFallbackAsync(session, context, message, cancellationToken);
            }

            var aiText = AnthropicApiHelper.ExtractTextContent(responseBody);

            if (LooksLikeDiagnosticQuestion(aiText) && !RoutingRegex.IsMatch(aiText))
            {
                _logger.LogInformation("Triage response looked diagnostic; moving to logistics.");
                if (context.TriageQuestionCount >= 2)
                {
                    return await CompleteTriageWithInferenceAsync(session, context,
                        "Got it — I have a good sense of what you need. Let me ask a few quick logistics questions.",
                        cancellationToken);
                }

                aiText = GetFollowUpQuestion(message, context.TriageQuestionCount);
            }

            if (LooksLikeAlreadySeeingQuestion(aiText) && !RoutingRegex.IsMatch(aiText))
            {
                _logger.LogInformation("Triage response asked about existing provider; moving to logistics.");
                if (context.TriageQuestionCount >= 2)
                {
                    return await CompleteTriageWithInferenceAsync(session, context,
                        "Got it — I have a good sense of what you need. Let me ask a few quick logistics questions.",
                        cancellationToken);
                }

                aiText = GetFollowUpQuestion(message, context.TriageQuestionCount);
            }

            var routingMatch = RoutingRegex.Match(aiText);
            if (routingMatch.Success && context.TriageQuestionCount >= 2)
            {
                return await CompleteTriageAsync(session, context, aiText, routingMatch, cancellationToken);
            }

            if (routingMatch.Success)
                aiText = RoutingRegex.Replace(aiText, string.Empty).Trim();

            if (context.TriageQuestionCount > MaxTriageQuestions)
            {
                await SaveAssistantMessageAsync(session, aiText, cancellationToken);
                return await CompleteTriageWithInferenceAsync(session, context, aiText, cancellationToken);
            }

            await SaveAssistantMessageAsync(session, aiText, cancellationToken);
            return BuildResponse(session, context, aiText, stage: NuviConversationStage.Triage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling Anthropic API during triage");
            return await HandleTriageFallbackAsync(session, context, message, cancellationToken);
        }
    }

    private async Task<ChatMessageResponse> HandleTriageFallbackAsync(
        SearchSession session, SearchContextData context, string message, CancellationToken cancellationToken)
    {
        if (context.TriageQuestionCount >= 2)
        {
            var empathy = GetTriageCompletionEmpathy(message);
            return await CompleteTriageWithInferenceAsync(session, context, empathy, cancellationToken);
        }

        if (context.TriageQuestionCount <= MaxTriageQuestions)
        {
            var question = GetFollowUpQuestion(message, context.TriageQuestionCount);
            await SaveAssistantMessageAsync(session, question, cancellationToken);
            return BuildResponse(session, context, question, stage: NuviConversationStage.Triage);
        }

        var allUserText = await GetAllUserMessagesAsync(session.Id, cancellationToken);
        session.Specialty = InferSpecialtyFromText(string.Join(" ", allUserText));
        session.SearchNotes = BuildNotesFromConversation(allUserText);
        session.MedicalIssuesSummary = string.Join(" | ", allUserText);
        session.UpdatedAt = DateTime.UtcNow;

        var text = $"That sounds really frustrating — I hear you. I think I have a good sense of what you need. Let me ask a few quick logistics questions.";
        return await BeginLogisticsAsync(session, context, text, cancellationToken);
    }

    private async Task<ChatMessageResponse> CompleteTriageAsync(
        SearchSession session, SearchContextData context, string aiText, Match routingMatch, CancellationToken cancellationToken)
    {
        session.Specialty = routingMatch.Groups[1].Value.Trim();
        session.Urgency = ParseUrgency(routingMatch.Groups[2].Value.Trim());
        session.SearchNotes = routingMatch.Groups[3].Success ? routingMatch.Groups[3].Value.Trim() : null;
        session.MedicalIssuesSummary = string.Join(" | ", await GetAllUserMessagesAsync(session.Id, cancellationToken));
        session.UpdatedAt = DateTime.UtcNow;

        var cleanText = RoutingRegex.Replace(aiText, string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(cleanText))
            cleanText = "Got it — I have a good sense of what you need. Let me ask a few quick logistics questions.";

        if (ShouldStartImplantQualification(session, context) && context.SkipAccountCreation)
            return await BeginImplantQualificationAsync(session, context, cancellationToken);

        return await BeginLogisticsAsync(session, context, cleanText, cancellationToken);
    }

    private async Task<ChatMessageResponse> CompleteTriageWithInferenceAsync(
        SearchSession session, SearchContextData context, string aiText, CancellationToken cancellationToken)
    {
        var allUserText = await GetAllUserMessagesAsync(session.Id, cancellationToken);
        session.Specialty = InferSpecialtyFromText(string.Join(" ", allUserText));
        session.SearchNotes = "Based on your description";
        session.MedicalIssuesSummary = string.Join(" | ", allUserText);
        session.UpdatedAt = DateTime.UtcNow;

        var text = string.IsNullOrWhiteSpace(RoutingRegex.Replace(aiText, string.Empty).Trim())
            ? "Thanks for sharing all of that. Let me ask a few quick logistics questions."
            : RoutingRegex.Replace(aiText, string.Empty).Trim();

        if (ShouldStartImplantQualification(session, context) && context.SkipAccountCreation)
            return await BeginImplantQualificationAsync(session, context, cancellationToken);

        return await BeginLogisticsAsync(session, context, text, cancellationToken);
    }

    private async Task<ChatMessageResponse> BeginLogisticsAsync(
        SearchSession session, SearchContextData context, string priorText, CancellationToken cancellationToken)
    {
        context.Stage = NuviConversationStage.Logistics;
        context.VisitPreference = "In-person only";
        context.LogisticsStep = 0;

        string logisticsQuestion = NuviFlowContent.LogisticsLocationQuestion;
        IReadOnlyList<string>? options = IsReturningWithSavedLocation(context)
            ? NuviFlowContent.FormatLogisticsLocationOptionsWithSaved(context.LastKnownLocation!)
            : NuviFlowContent.LogisticsLocationOptions;

        var combined = string.IsNullOrWhiteSpace(priorText) ? logisticsQuestion : $"{priorText}\n\n{logisticsQuestion}";

        await SaveAssistantMessageAsync(session, combined, cancellationToken);
        return BuildResponse(session, context, combined, stage: NuviConversationStage.Logistics,
            options: options);
    }

    private async Task<ChatMessageResponse> HandleLogisticsAsync(
        SearchSession session, SearchContextData context, string message, CancellationToken cancellationToken)
    {
        var answer = message.Trim();

        switch (context.LogisticsStep)
        {
            case 0:
            case LogisticsStepNewLocation:
                ApplyLocationAnswer(session, context, answer);
                return await ContinueLogisticsAfterLocationAsync(session, context, cancellationToken);

            case 1:
                context.InsuranceCategory = ClassifyInsuranceCategory(answer);
                if (IsInsuredCategory(context.InsuranceCategory))
                {
                    context.LogisticsStep = 2;
                    await SaveAssistantMessageAsync(session, NuviFlowContent.LogisticsInsurancePlanQuestion, cancellationToken);
                    return BuildResponse(session, context, NuviFlowContent.LogisticsInsurancePlanQuestion,
                        stage: NuviConversationStage.Logistics,
                        options: NuviFlowContent.LogisticsInsurancePlanOptions);
                }

                context.InsurancePreference = context.InsuranceCategory == "self-pay" ? "Self-pay" : null;
                session.InsurancePlanText = context.InsuranceCategory == "self-pay" ? null : session.InsurancePlanText;
                context.LogisticsStep = 3;
                await SaveAssistantMessageAsync(session, NuviFlowContent.LogisticsUrgencyQuestion, cancellationToken);
                return BuildResponse(session, context, NuviFlowContent.LogisticsUrgencyQuestion,
                    stage: NuviConversationStage.Logistics,
                    options: NuviFlowContent.LogisticsUrgencyOptions);

            case 2:
                if (!answer.Contains("skip", StringComparison.OrdinalIgnoreCase))
                {
                    context.InsurancePreference = answer;
                    session.InsurancePlanText = answer;
                }
                context.LogisticsStep = 3;
                await SaveAssistantMessageAsync(session, NuviFlowContent.LogisticsUrgencyQuestion, cancellationToken);
                return BuildResponse(session, context, NuviFlowContent.LogisticsUrgencyQuestion,
                    stage: NuviConversationStage.Logistics,
                    options: NuviFlowContent.LogisticsUrgencyOptions);

            case 3:
                context.UrgencyPreference = answer;
                session.AvailabilityPreference = MapUrgencyToAvailability(answer);
                session.UpdatedAt = DateTime.UtcNow;
                return await BeginMomentumBridgeAsync(session, context, cancellationToken);

            default:
                return await BeginMomentumBridgeAsync(session, context, cancellationToken);
        }
    }

    private async Task<ChatMessageResponse> BeginImplantQualificationAsync(
        SearchSession session,
        SearchContextData context,
        CancellationToken cancellationToken)
    {
        if (context.ImplantQualificationComplete)
            return await ContinueAfterImplantQualificationAsync(session, context, cancellationToken);

        context.Stage = NuviConversationStage.ImplantQualification;
        context.ImplantQualStep = 0;

        return await AskImplantQualificationQuestionAsync(session, context, 0, cancellationToken);
    }

    private async Task<ChatMessageResponse> AskImplantQualificationQuestionAsync(
        SearchSession session,
        SearchContextData context,
        int step,
        CancellationToken cancellationToken)
    {
        context.ImplantQualStep = step;
        var (question, options) = step switch
        {
            0 => (NuviFlowContent.ImplantQualificationQuestion1, GetImplantQualificationQuestion1Options(context)),
            1 => (NuviFlowContent.ImplantQualificationQuestion2, NuviFlowContent.ImplantQualificationQuestion2Options),
            2 => (NuviFlowContent.ImplantQualificationQuestion3, NuviFlowContent.ImplantQualificationQuestion3Options),
            3 => (NuviFlowContent.ImplantQualificationQuestion4, NuviFlowContent.ImplantQualificationQuestion4Options),
            4 => (NuviFlowContent.ImplantQualificationQuestion5, NuviFlowContent.ImplantQualificationQuestion5Options),
            _ => (NuviFlowContent.ImplantQualificationQuestion1, GetImplantQualificationQuestion1Options(context))
        };

        await SaveAssistantMessageAsync(session, question, cancellationToken);
        return BuildResponse(
            session,
            context,
            question,
            stage: NuviConversationStage.ImplantQualification,
            options: options,
            optionsOnly: true);
    }

    private async Task<ChatMessageResponse> HandleImplantQualificationAsync(
        SearchSession session,
        SearchContextData context,
        string message,
        CancellationToken cancellationToken)
    {
        var answer = (message ?? string.Empty).Trim();

        switch (context.ImplantQualStep)
        {
            case 0:
                if (IsCancelBookingChip(answer))
                    return await BeginCancelBookingAsync(session, context, cancellationToken);

                if (IsRescheduleBookingChip(answer))
                    return await BeginRescheduleBookingAsync(session, context, cancellationToken);

                var q1Options = GetImplantQualificationQuestion1Options(context);
                if (!MatchesOption(q1Options, answer) && !IsImplantQualificationPassAnswer(answer))
                {
                    return await RepromptImplantQualificationAsync(
                        session,
                        context,
                        "Please choose one of the options below.",
                        q1Options,
                        cancellationToken);
                }

                context.ImplantIntentQualified = IsImplantQualificationPassAnswer(answer);
                if (context.ImplantIntentQualified == false)
                    return await DisqualifyImplantLeadAsync(session, context, cancellationToken);

                return await AskImplantQualificationQuestionAsync(session, context, 1, cancellationToken);

            case 1:
                if (!MatchesOption(NuviFlowContent.ImplantQualificationQuestion2Options, answer))
                {
                    return await RepromptImplantQualificationAsync(
                        session,
                        context,
                        "Please choose one of the timing options below.",
                        NuviFlowContent.ImplantQualificationQuestion2Options,
                        cancellationToken);
                }

                context.ImplantTimingQualified = IsImplantTimingPass(answer);
                if (context.ImplantTimingQualified == false)
                    return await DisqualifyImplantLeadAsync(session, context, cancellationToken);

                return await AskImplantQualificationQuestionAsync(session, context, 2, cancellationToken);

            case 2:
                if (!MatchesOption(NuviFlowContent.ImplantQualificationQuestion3Options, answer))
                {
                    return await RepromptImplantQualificationAsync(
                        session,
                        context,
                        "Please choose one of the payment options below.",
                        NuviFlowContent.ImplantQualificationQuestion3Options,
                        cancellationToken);
                }

                context.ImplantPayerType = answer;
                if (IsImplantPayerDisqualified(answer))
                    return await DisqualifyImplantLeadAsync(session, context, cancellationToken);

                if (string.Equals(answer, "Monthly financing", StringComparison.OrdinalIgnoreCase))
                    return await AskImplantQualificationQuestionAsync(session, context, 3, cancellationToken);

                context.ImplantFinancingQualified = true;
                return await CompleteImplantQualificationAndBeginLogisticsAsync(session, context, cancellationToken);

            case 3:
                if (!MatchesOption(NuviFlowContent.ImplantQualificationQuestion4Options, answer))
                {
                    return await RepromptImplantQualificationAsync(
                        session,
                        context,
                        "Please choose one of the options below.",
                        NuviFlowContent.ImplantQualificationQuestion4Options,
                        cancellationToken);
                }

                if (string.Equals(answer, "Cash/card", StringComparison.OrdinalIgnoreCase))
                {
                    context.ImplantFinancingQualified = true;
                    return await CompleteImplantQualificationAndBeginLogisticsAsync(session, context, cancellationToken);
                }

                return await AskImplantQualificationQuestionAsync(session, context, 4, cancellationToken);

            case 4:
                if (!MatchesOption(NuviFlowContent.ImplantQualificationQuestion5Options, answer))
                {
                    return await RepromptImplantQualificationAsync(
                        session,
                        context,
                        "Please choose one of the options below.",
                        NuviFlowContent.ImplantQualificationQuestion5Options,
                        cancellationToken);
                }

                context.ImplantFinancingQualified =
                    string.Equals(answer, "Yes Continue", StringComparison.OrdinalIgnoreCase);
                return context.ImplantFinancingQualified == true
                    ? await CompleteImplantQualificationAndBeginLogisticsAsync(session, context, cancellationToken)
                    : await DisqualifyImplantLeadAsync(session, context, cancellationToken);

            default:
                context.ImplantQualStep = 0;
                return await BeginImplantQualificationAsync(session, context, cancellationToken);
        }
    }

    private async Task<ChatMessageResponse> CompleteImplantQualificationAndBeginLogisticsAsync(
        SearchSession session,
        SearchContextData context,
        CancellationToken cancellationToken)
    {
        context.ImplantQualificationComplete = true;
        return await BeginLogisticsAsync(session, context, string.Empty, cancellationToken);
    }

    private async Task<ChatMessageResponse> ContinueAfterImplantQualificationAsync(
        SearchSession session,
        SearchContextData context,
        CancellationToken cancellationToken)
    {
        if (context.Stage == NuviConversationStage.Logistics && context.LogisticsStep < 3)
            return await BeginLogisticsAsync(session, context, string.Empty, cancellationToken);

        if (context.SkipAccountCreation)
            return await ContinueSignedInAfterAccountAsync(session, context, cancellationToken);

        return await BeginDeepDivePermissionAsync(session, context, cancellationToken);
    }

    private async Task<ChatMessageResponse> RepromptImplantQualificationAsync(
        SearchSession session,
        SearchContextData context,
        string text,
        IReadOnlyList<string> options,
        CancellationToken cancellationToken)
    {
        await SaveAssistantMessageAsync(session, text, cancellationToken);
        return BuildResponse(
            session,
            context,
            text,
            stage: NuviConversationStage.ImplantQualification,
            options: options,
            optionsOnly: true);
    }

    private async Task<ChatMessageResponse> DisqualifyImplantLeadAsync(
        SearchSession session,
        SearchContextData context,
        CancellationToken cancellationToken)
    {
        context.Stage = NuviConversationStage.Complete;
        await SaveAssistantMessageAsync(session, NuviFlowContent.ImplantQualificationDisqualifiedMessage, cancellationToken);
        return BuildResponse(
            session,
            context,
            NuviFlowContent.ImplantQualificationDisqualifiedMessage,
            stage: NuviConversationStage.Complete,
            flowComplete: true);
    }

    private static string ClassifyInsuranceCategory(string answer)
    {
        var lower = answer.ToLowerInvariant();
        if (lower.Contains("self-pay") || lower.Contains("self pay") || lower.Contains("cash"))
            return "self-pay";
        if (lower.Contains("not sure") || lower.Contains("unsure"))
            return "not-sure";
        return "insured";
    }

    private static bool IsInsuredCategory(string? category) =>
        string.Equals(category, "insured", StringComparison.OrdinalIgnoreCase);

    private async Task<ChatMessageResponse> BeginMomentumBridgeAsync(
        SearchSession session, SearchContextData context, CancellationToken cancellationToken)
    {
        if (context.SkipAccountCreation && context.HasPriorDeepDiveAnswers)
            return await BeginPostAccountFlowAsync(session, context, cancellationToken);

        if (context.SkipAccountCreation)
            return await BeginPostAccountFlowAsync(session, context, cancellationToken);

        context.Stage = NuviConversationStage.AccountCreation;
        context.AccountStep = AccountCreationStep.Name;
        var text = $"{NuviFlowContent.MomentumBridgeMessage}\n\n{NuviFlowContent.AccountNameQuestion}";
        await SaveAssistantMessageAsync(session, text, cancellationToken);
        return BuildResponse(session, context, text, stage: NuviConversationStage.AccountCreation);
    }

    private async Task<ChatMessageResponse> HandleMomentumBridgeAsync(
        SearchSession session, SearchContextData context, string message, HttpContext? httpContext, CancellationToken cancellationToken)
    {
        return await BeginAccountCreationAsync(session, context, cancellationToken);
    }

    private async Task<ChatMessageResponse> BeginAccountCreationAsync(
        SearchSession session, SearchContextData context, CancellationToken cancellationToken)
    {
        if (context.SkipAccountCreation)
            return await BeginPostAccountFlowAsync(session, context, cancellationToken);

        context.Stage = NuviConversationStage.AccountCreation;
        context.AccountStep = AccountCreationStep.Name;
        var text = NuviFlowContent.AccountNameQuestion;
        await SaveAssistantMessageAsync(session, text, cancellationToken);
        return BuildResponse(session, context, text, stage: NuviConversationStage.AccountCreation);
    }

    private async Task<ChatMessageResponse> BeginDeepDivePermissionAsync(
        SearchSession session, SearchContextData context, CancellationToken cancellationToken)
    {
        context.Stage = NuviConversationStage.DeepDivePermission;
        var text = NuviFlowContent.FormatDeepDivePermissionQuestion(GetDisplayName(context));
        await SaveAssistantMessageAsync(session, text, cancellationToken);
        return BuildResponse(session, context, text, stage: NuviConversationStage.DeepDivePermission,
            options: NuviFlowContent.DeepDivePermissionOptions);
    }

    private async Task<ChatMessageResponse> HandleDeepDivePermissionAsync(
        SearchSession session, SearchContextData context, string message, HttpContext? httpContext, CancellationToken cancellationToken)
    {
        var lower = message.Trim().ToLowerInvariant();
        var allowed = lower.Contains("yes") || lower.Contains("ask") || lower.Contains("fine") || lower.Contains("sure") || lower.Contains("ok");
        var declined = lower.Contains("no") || lower.Contains("thanks") || lower.Contains("skip") || lower.Contains("show") || lower.Contains("match");

        if (!allowed && !declined)
        {
            return BuildResponse(session, context,
                "No pressure — just let me know if you'd like a few quick preference questions, or if you'd rather see your matches now.",
                stage: NuviConversationStage.DeepDivePermission,
                options: NuviFlowContent.DeepDivePermissionOptions);
        }

        if (allowed)
        {
            context.SkipDeepDive = false;
            var welcome = $"{FormatDeepDiveWelcome(GetDisplayName(context))}";
            return await BeginDeepDiveAfterAccountAsync(session, context, welcome, cancellationToken,
                signedIn: context.SkipAccountCreation);
        }

        context.SkipDeepDive = true;
        return await BeginMatchSearchAsync(session, context, cancellationToken);
    }

    private async Task<ChatMessageResponse> BeginDeepDiveAfterAccountAsync(
        SearchSession session, SearchContextData context, string welcomeText, CancellationToken cancellationToken, bool signedIn = false)
    {
        context.Stage = NuviConversationStage.DeepDive;
        return await AskNextDeepDiveQuestionAsync(session, context, welcomeText, cancellationToken, signedIn: signedIn);
    }

    private async Task<ChatMessageResponse> HandleAccountCreationAsync(
        SearchSession session, SearchContextData context, string message, HttpContext? httpContext, CancellationToken cancellationToken)
    {
        if (context.SkipAccountCreation)
        {
            if (NeedsMissingPhone(context)
                && context.AccountStep == AccountCreationStep.Phone)
            {
                return await HandleMissingPhoneAsync(session, context, message, cancellationToken);
            }

            if (NeedsMissingDateOfBirth(context)
                && context.AccountStep == AccountCreationStep.DateOfBirth)
            {
                return await HandleMissingDateOfBirthAsync(session, context, message, cancellationToken);
            }

            return await ContinueSignedInAfterAccountAsync(session, context, cancellationToken);
        }

        var answer = message.Trim();

        switch (context.AccountStep)
        {
            case AccountCreationStep.Name:
            {
                var interp = await InterpretRegistrationReplyAsync(session, context, answer, "name", cancellationToken);
                ApplyRegistrationCorrections(context, interp);
                var fromAnswer = ExtractNameHeuristic(answer);
                var fromInterp = interp.Valid && !string.IsNullOrWhiteSpace(interp.Value)
                    ? ExtractNameHeuristic(interp.Value)
                    : null;
                var name = PreferFullerName(fromAnswer, fromInterp);
                if (string.IsNullOrWhiteSpace(name) || AnthropicValidationService.LooksLikeGibberish(name))
                {
                    var retry = WithNextRegistrationQuestion(
                        interp.Ack,
                        "I didn't quite catch your name — what should I call you?");
                    await SaveAssistantMessageAsync(session, retry, cancellationToken);
                    return BuildResponse(session, context, retry, stage: NuviConversationStage.AccountCreation);
                }

                context.PendingFullName = ToDisplayName(name);
                context.AccountStep = AccountCreationStep.Email;
                var emailPrompt = BuildRegistrationFollowUp(
                    interp.Ack,
                    $"Nice to meet you, {context.PendingFullName}! What's the best email address for you?",
                    "What's the best email address for you?");
                await SaveAssistantMessageAsync(session, emailPrompt, cancellationToken);
                return BuildResponse(session, context, emailPrompt, stage: NuviConversationStage.AccountCreation);
            }

            case AccountCreationStep.Email:
            {
                var interp = await InterpretRegistrationReplyAsync(session, context, answer, "email", cancellationToken);
                ApplyRegistrationCorrections(context, interp);

                var email = interp.Valid ? interp.Value : null;
                if (string.IsNullOrWhiteSpace(email) || !TryExtractEmail(email, out email))
                    TryExtractEmail(answer, out email);

                if (string.IsNullOrWhiteSpace(email))
                {
                    var retry = LooksLikeDeclinedEmail(answer)
                        || AckOffersEmailSkip(interp.Ack)
                        || TryNormalizePhone(answer, out _)
                        ? NuviFlowContent.AccountEmailRequiredMessage
                        : WithNextRegistrationQuestion(
                            interp.Ack,
                            NuviFlowContent.AccountEmailRequiredMessage);
                    await SaveAssistantMessageAsync(session, retry, cancellationToken);
                    return BuildResponse(session, context, retry, stage: NuviConversationStage.AccountCreation);
                }

                email = email.Trim().ToLowerInvariant();
                context.PendingEmail = email;
                context.PendingUsername = email;
                var existingPatient = await _db.Patients
                    .AsNoTracking()
                    .FirstOrDefaultAsync(p => p.Username == email, cancellationToken);

                if (existingPatient != null)
                {
                    context.IsExistingAccountLogin = true;
                    context.AccountStep = AccountCreationStep.LoginPassword;
                    var loginText = WithNextRegistrationQuestion(
                        interp.Ack,
                        "You already have an account with that email. Please enter your password and I'll sign you in.");
                    await SaveAssistantMessageAsync(session, loginText, cancellationToken);
                    return BuildResponse(session, context, loginText, stage: NuviConversationStage.AccountCreation, usePasswordInput: true);
                }

                context.AccountStep = AccountCreationStep.Phone;
                var phonePrompt = WithNextRegistrationQuestion(interp.Ack, NuviFlowContent.AccountPhoneQuestion);
                await SaveAssistantMessageAsync(session, phonePrompt, cancellationToken);
                return BuildResponse(session, context, phonePrompt, stage: NuviConversationStage.AccountCreation);
            }

            case AccountCreationStep.LoginPassword:
                if (httpContext == null)
                    return BuildResponse(session, context, "Unable to sign in right now. Please try again.", stage: NuviConversationStage.AccountCreation, usePasswordInput: true);

                if (string.IsNullOrWhiteSpace(answer))
                    return BuildResponse(session, context, "Please enter your password.", stage: NuviConversationStage.AccountCreation, usePasswordInput: true);

                var loginResult = await _accountAuthService.LoginAsync(new AccountLoginRequest
                {
                    AccountType = AccountType.Patient,
                    Username = context.PendingUsername!,
                    Password = answer
                }, httpContext, cancellationToken);

                if (!loginResult.Success)
                {
                    return BuildResponse(session, context,
                        loginResult.Error ?? "That password didn't work. Please try again.",
                        stage: NuviConversationStage.AccountCreation,
                        usePasswordInput: true);
                }

                var signedInPatient = await _db.Patients
                    .AsNoTracking()
                    .FirstOrDefaultAsync(p => p.Username == context.PendingUsername, cancellationToken);

                if (signedInPatient != null)
                {
                    session.PatientId = signedInPatient.Id;
                    context.PendingFullName = signedInPatient.FullName;
                    context.PatientDateOfBirth = signedInPatient.DateOfBirth;
                    ApplyPatientPhoneToContext(context, signedInPatient.Phone);
                    context.SkipAccountCreation = true;
                    await LoadReturningPatientProfileAsync(session, context, signedInPatient, cancellationToken);
                }

                context.IsExistingAccountLogin = false;
                return await BeginPostAccountFlowAsync(session, context, cancellationToken);

            case AccountCreationStep.Phone:
            {
                var interp = await InterpretRegistrationReplyAsync(session, context, answer, "phone", cancellationToken);
                ApplyRegistrationCorrections(context, interp);

                var phoneRaw = interp.Valid ? interp.Value ?? answer : answer;
                if (!TryNormalizePhone(PhoneNumberHelper.DigitsOnly(phoneRaw), out var phone)
                    && !TryNormalizePhone(PhoneNumberHelper.DigitsOnly(answer), out phone))
                {
                    var retry = LooksLikeDeclinedPhone(answer) || AckOffersPhoneSkip(interp.Ack)
                        ? NuviFlowContent.AccountPhoneRequiredMessage
                        : WithNextRegistrationQuestion(
                            interp.Ack,
                            NuviFlowContent.AccountPhoneRequiredMessage);
                    await SaveAssistantMessageAsync(session, retry, cancellationToken);
                    return BuildResponse(session, context, retry, stage: NuviConversationStage.AccountCreation);
                }

                context.PendingPhone = phone;
                context.AccountStep = AccountCreationStep.DateOfBirth;
                var dobPrompt = WithNextRegistrationQuestion(interp.Ack, NuviFlowContent.AccountDateOfBirthQuestion);
                await SaveAssistantMessageAsync(session, dobPrompt, cancellationToken);
                return BuildResponse(session, context, dobPrompt, stage: NuviConversationStage.AccountCreation);
            }

            case AccountCreationStep.DateOfBirth:
            {
                var interp = await InterpretRegistrationReplyAsync(session, context, answer, "dateOfBirth", cancellationToken);
                ApplyRegistrationCorrections(context, interp);

                var dobText = interp.Valid ? interp.Value ?? answer : answer;
                if (!TryParseDateOfBirth(dobText, out var dob) && !TryParseDateOfBirth(answer, out dob))
                {
                    var retry = WithNextRegistrationQuestion(
                        interp.Ack,
                        "What's your date of birth? MM/DD/YYYY is perfect — for example, 04/09/1980.");
                    await SaveAssistantMessageAsync(session, retry, cancellationToken);
                    return BuildResponse(session, context, retry, stage: NuviConversationStage.AccountCreation);
                }

                var today = DateOnly.FromDateTime(DateTime.Today);
                if (dob > today || dob.Year <= 1900 || dob < today.AddYears(-120))
                {
                    var retry = WithNextRegistrationQuestion(
                        interp.Ack,
                        "That date doesn't look right — please share a valid date of birth as MM/DD/YYYY.");
                    await SaveAssistantMessageAsync(session, retry, cancellationToken);
                    return BuildResponse(session, context, retry, stage: NuviConversationStage.AccountCreation);
                }

                context.PatientDateOfBirth = dob;
                context.AccountStep = AccountCreationStep.Password;
                var passwordPrompt = WithNextRegistrationQuestion(interp.Ack, NuviFlowContent.AccountPasswordQuestion);
                await SaveAssistantMessageAsync(session, passwordPrompt, cancellationToken);
                return BuildResponse(session, context, passwordPrompt, stage: NuviConversationStage.AccountCreation, usePasswordInput: true);
            }

            case AccountCreationStep.Password:
                if (answer.Trim().Length < 8)
                    return BuildResponse(session, context, "Please choose a password with at least 8 characters.", stage: NuviConversationStage.AccountCreation, usePasswordInput: true);

                context.PendingPassword = answer.Trim();
                context.AccountStep = AccountCreationStep.ConfirmPassword;
                var confirmText = "Please confirm your password.";
                await SaveAssistantMessageAsync(session, confirmText, cancellationToken);
                return BuildResponse(session, context, confirmText, stage: NuviConversationStage.AccountCreation, usePasswordInput: true);

            case AccountCreationStep.ConfirmPassword:
                if (!string.Equals(answer.Trim(), context.PendingPassword, StringComparison.Ordinal))
                {
                    context.PendingPassword = null;
                    context.AccountStep = AccountCreationStep.Password;
                    return BuildResponse(session, context,
                        "Those passwords didn't match — please create your password again.",
                        stage: NuviConversationStage.AccountCreation, usePasswordInput: true);
                }

                var registerResult = await _patientService.RegisterAsync(new PatientRegisterRequest
                {
                    SessionKey = session.SessionKey,
                    FullName = context.PendingFullName ?? "Patient",
                    DateOfBirth = context.PatientDateOfBirth,
                    Email = context.PendingEmail,
                    Phone = context.PendingPhone ?? "",
                    Username = context.PendingEmail ?? "",
                    Password = context.PendingPassword!
                }, cancellationToken);

                if (!registerResult.Success)
                {
                    if (registerResult.Message?.Contains("already exists", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        context.IsExistingAccountLogin = true;
                        context.AccountStep = AccountCreationStep.LoginPassword;
                        var existingAccountText = "You already have an account. Please enter your password and I'll sign you in.";
                        await SaveAssistantMessageAsync(session, existingAccountText, cancellationToken);
                        return BuildResponse(session, context, existingAccountText, stage: NuviConversationStage.AccountCreation, usePasswordInput: true);
                    }

                    return BuildResponse(session, context,
                        registerResult.Message ?? "Something went wrong creating your account. Could you try a different email?",
                        stage: NuviConversationStage.AccountCreation);
                }

                return await BeginPostAccountFlowAsync(session, context, cancellationToken);

            default:
                return BuildResponse(session, context, "Let's continue — what's your name?", stage: NuviConversationStage.AccountCreation);
        }
    }

    private async Task<ChatMessageResponse> BeginPostAccountFlowAsync(
        SearchSession session, SearchContextData context, CancellationToken cancellationToken)
    {
        if (ShouldStartImplantQualification(session, context))
            return await BeginImplantQualificationAsync(session, context, cancellationToken);

        if (context.SkipAccountCreation)
            return await ContinueSignedInAfterAccountAsync(session, context, cancellationToken);

        return await BeginDeepDivePermissionAsync(session, context, cancellationToken);
    }

    private async Task<ChatMessageResponse> ContinueSignedInAfterAccountAsync(
        SearchSession session, SearchContextData context, CancellationToken cancellationToken)
    {
        if (NeedsMissingPhone(context))
            return await BeginMissingPhoneAsync(session, context, cancellationToken);

        if (NeedsMissingDateOfBirth(context))
            return await BeginMissingDateOfBirthAsync(session, context, cancellationToken);

        await PrefillAgeFromPatientProfileAsync(session, context, cancellationToken);

        if (context.HasPriorDeepDiveAnswers && context.SavedDeepDiveAnswers is { Count: > 0 })
        {
            context.PollingAnswers = context.SavedDeepDiveAnswers
                .Select(a => new PollingAnswerEntry
                {
                    QuestionId = a.QuestionId,
                    Question = a.Question,
                    Answer = a.Answer,
                    MatchWeight = a.MatchWeight
                })
                .ToList();
            context.SkipDeepDive = true;
            return await BeginMatchSearchAsync(session, context, cancellationToken);
        }

        var welcome = FormatDeepDiveWelcome(GetDisplayName(context));
        return await BeginDeepDiveAfterAccountAsync(session, context, welcome, cancellationToken, signedIn: true);
    }

    private static bool NeedsMissingDateOfBirth(SearchContextData context) =>
        context.SkipAccountCreation && !HasKnownPatientAge(context);

    private static bool NeedsMissingPhone(SearchContextData context) =>
        context.SkipAccountCreation && !HasKnownPatientPhone(context);

    private static bool HasKnownPatientPhone(SearchContextData context) =>
        TryNormalizePhone(PhoneNumberHelper.DigitsOnly(context.PendingPhone), out _);

    private static void ApplyPatientPhoneToContext(SearchContextData context, string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
            return;

        if (TryNormalizePhone(PhoneNumberHelper.DigitsOnly(phone), out var normalized))
            context.PendingPhone = normalized;
    }

    private static bool IsCollectingMissingProfileFields(SearchContextData context) =>
        context.SkipAccountCreation
        && context.Stage == NuviConversationStage.AccountCreation
        && (context.AccountStep == AccountCreationStep.Phone
            || context.AccountStep == AccountCreationStep.DateOfBirth);

    private async Task<ChatMessageResponse> BeginMissingPhoneAsync(
        SearchSession session, SearchContextData context, CancellationToken cancellationToken)
    {
        context.Stage = NuviConversationStage.AccountCreation;
        context.AccountStep = AccountCreationStep.Phone;
        var text = NuviFlowContent.AccountMissingPhoneQuestion;
        await SaveAssistantMessageAsync(session, text, cancellationToken);
        return BuildResponse(session, context, text, stage: NuviConversationStage.AccountCreation);
    }

    private async Task<ChatMessageResponse> HandleMissingPhoneAsync(
        SearchSession session, SearchContextData context, string message, CancellationToken cancellationToken)
    {
        var answer = (message ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(answer) || answer == RedactedPasswordPlaceholder)
            return await BeginMissingPhoneAsync(session, context, cancellationToken);

        var interp = await InterpretRegistrationReplyAsync(session, context, answer, "phone", cancellationToken);
        var phoneRaw = interp.Valid ? interp.Value ?? answer : answer;
        if (!TryNormalizePhone(PhoneNumberHelper.DigitsOnly(phoneRaw), out var phone)
            && !TryNormalizePhone(PhoneNumberHelper.DigitsOnly(answer), out phone))
        {
            var retry = LooksLikeDeclinedPhone(answer) || AckOffersPhoneSkip(interp.Ack)
                ? NuviFlowContent.AccountPhoneRequiredMessage
                : WithNextRegistrationQuestion(
                    interp.Ack,
                    NuviFlowContent.AccountPhoneRequiredMessage);
            await SaveAssistantMessageAsync(session, retry, cancellationToken);
            return BuildResponse(session, context, retry, stage: NuviConversationStage.AccountCreation);
        }

        context.PendingPhone = phone;
        if (session.PatientId.HasValue)
        {
            var patient = await _db.Patients
                .FirstOrDefaultAsync(p => p.Id == session.PatientId.Value, cancellationToken);
            if (patient != null)
            {
                if (!string.Equals(patient.Phone, phone, StringComparison.Ordinal))
                {
                    patient.Phone = phone;
                    patient.PhoneVerified = false;
                    patient.PhoneVerificationCodeHash = null;
                    patient.PhoneVerificationExpiresAtUtc = null;
                    await _db.SaveChangesAsync(cancellationToken);
                }
            }
        }

        return await ContinueSignedInAfterAccountAsync(session, context, cancellationToken);
    }

    private async Task<ChatMessageResponse> BeginMissingDateOfBirthAsync(
        SearchSession session, SearchContextData context, CancellationToken cancellationToken)
    {
        context.Stage = NuviConversationStage.AccountCreation;
        context.AccountStep = AccountCreationStep.DateOfBirth;
        var text = NuviFlowContent.AccountMissingDateOfBirthQuestion;
        await SaveAssistantMessageAsync(session, text, cancellationToken);
        return BuildResponse(session, context, text, stage: NuviConversationStage.AccountCreation);
    }

    private async Task<ChatMessageResponse> HandleMissingDateOfBirthAsync(
        SearchSession session, SearchContextData context, string message, CancellationToken cancellationToken)
    {
        var answer = (message ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(answer) || answer == RedactedPasswordPlaceholder)
            return await BeginMissingDateOfBirthAsync(session, context, cancellationToken);

        var interp = await InterpretRegistrationReplyAsync(session, context, answer, "dateOfBirth", cancellationToken);
        var dobText = interp.Valid ? interp.Value ?? answer : answer;
        if (!TryParseDateOfBirth(dobText, out var dob) && !TryParseDateOfBirth(answer, out dob))
        {
            var retry = WithNextRegistrationQuestion(
                interp.Ack,
                NuviFlowContent.AccountMissingDateOfBirthQuestion);
            await SaveAssistantMessageAsync(session, retry, cancellationToken);
            return BuildResponse(session, context, retry, stage: NuviConversationStage.AccountCreation);
        }

        var today = DateOnly.FromDateTime(DateTime.Today);
        if (dob > today || dob.Year <= 1900 || dob < today.AddYears(-120))
        {
            var retry = WithNextRegistrationQuestion(
                interp.Ack,
                "That date doesn't look right — please share a valid date of birth as MM/DD/YYYY.");
            await SaveAssistantMessageAsync(session, retry, cancellationToken);
            return BuildResponse(session, context, retry, stage: NuviConversationStage.AccountCreation);
        }

        context.PatientDateOfBirth = dob;
        if (session.PatientId.HasValue)
        {
            var patient = await _db.Patients
                .FirstOrDefaultAsync(p => p.Id == session.PatientId.Value, cancellationToken);
            if (patient != null)
            {
                patient.DateOfBirth = dob;
                await _db.SaveChangesAsync(cancellationToken);
            }
        }

        return await ContinueSignedInAfterAccountAsync(session, context, cancellationToken);
    }

    private async Task<ChatMessageResponse> ApplySavedLocationAndContinueAsync(
        SearchSession session, SearchContextData context, CancellationToken cancellationToken)
    {
        var saved = NuviFlowContent.NormalizeSavedLocationChip(context.LastKnownLocation ?? string.Empty);
        context.LocationPreference = saved;
        session.Location = saved;
        return await ContinueLogisticsAfterLocationAsync(session, context, cancellationToken);
    }

    private static void ApplyLocationAnswer(SearchSession session, SearchContextData context, string answer)
    {
        if (IsUseLastLocationAnswer(answer) && !string.IsNullOrWhiteSpace(context.LastKnownLocation))
        {
            var saved = NuviFlowContent.NormalizeSavedLocationChip(context.LastKnownLocation);
            context.LocationPreference = saved;
            session.Location = saved;
            return;
        }

        // Legacy chip text like "Use last used (77006)" — pull ZIP from parentheses.
        var parenZip = System.Text.RegularExpressions.Regex.Match(answer ?? string.Empty, @"\((\d{5})(?:-\d{4})?\)");
        if (IsUseLastLocationAnswer(answer ?? string.Empty) && parenZip.Success)
        {
            context.LocationPreference = parenZip.Groups[1].Value;
            session.Location = parenZip.Groups[1].Value;
            return;
        }

        if (IsLocationSkipAnswer(answer))
        {
            context.LocationPreference = NuviFlowContent.DefaultLocationWhenSkipped;
            session.Location = NuviFlowContent.DefaultLocationWhenSkipped;
            return;
        }

        var cleaned = NuviFlowContent.NormalizeSavedLocationChip(answer ?? string.Empty);
        context.LocationPreference = string.IsNullOrWhiteSpace(cleaned) ? answer : cleaned;
        session.Location = context.LocationPreference;
    }

    private static bool IsUseLastLocationAnswer(string answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
            return false;

        var lower = answer.Trim().ToLowerInvariant();
        return lower.StartsWith("use last used", StringComparison.Ordinal)
            || lower is "use last" or "last used" or "last zip" or "last zip code";
    }

    private static bool IsLocationSkipAnswer(string answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
            return true;

        var lower = answer.Trim().ToLowerInvariant();
        return lower is "skip" or "skip for now" or "skip it" or "skip now" or "no" or "n" or "nope"
            || lower.Contains("skip", StringComparison.Ordinal);
    }

    private async Task<ChatMessageResponse> ContinueLogisticsAfterLocationAsync(
        SearchSession session, SearchContextData context, CancellationToken cancellationToken)
    {
        if (context.SkipAccountCreation)
            return await AskUrgencySkippingInsuranceAsync(session, context, cancellationToken);

        context.LogisticsStep = 1;
        await SaveAssistantMessageAsync(session, NuviFlowContent.LogisticsInsuranceTypeQuestion, cancellationToken);
        return BuildResponse(session, context, NuviFlowContent.LogisticsInsuranceTypeQuestion,
            stage: NuviConversationStage.Logistics,
            options: NuviFlowContent.LogisticsInsuranceTypeOptions);
    }

    private async Task<ChatMessageResponse> AskUrgencySkippingInsuranceAsync(
        SearchSession session, SearchContextData context, CancellationToken cancellationToken)
    {
        ApplySavedInsuranceToSession(session, context);
        context.LogisticsStep = 3;
        await SaveAssistantMessageAsync(session, NuviFlowContent.LogisticsUrgencyQuestion, cancellationToken);
        return BuildResponse(session, context, NuviFlowContent.LogisticsUrgencyQuestion,
            stage: NuviConversationStage.Logistics,
            options: NuviFlowContent.LogisticsUrgencyOptions);
    }

    private static void ApplySavedInsuranceToSession(SearchSession session, SearchContextData context)
    {
        if (!string.IsNullOrWhiteSpace(context.InsurancePreference))
            session.InsurancePlanText = context.InsurancePreference;
        else if (!string.IsNullOrWhiteSpace(session.InsurancePlanText))
            context.InsurancePreference = session.InsurancePlanText;

        if (string.IsNullOrWhiteSpace(context.InsuranceCategory)
            && !string.IsNullOrWhiteSpace(context.InsurancePreference))
        {
            context.InsuranceCategory = string.Equals(context.InsurancePreference, "Self-pay", StringComparison.OrdinalIgnoreCase)
                ? "self-pay"
                : "insured";
        }
    }

    private static bool IsReturningWithSavedLocation(SearchContextData context) =>
        context.SkipAccountCreation && !string.IsNullOrWhiteSpace(context.LastKnownLocation);

    private sealed class RegistrationInterpretation
    {
        public string Intent { get; init; } = "answer";
        public string? Value { get; init; }
        public bool Valid { get; init; }
        public string? Ack { get; init; }
        public string? CorrectedName { get; init; }
        public string? CorrectedEmail { get; init; }
        public string? CorrectedPhone { get; init; }
        public string? CorrectedDateOfBirth { get; init; }
    }

    private async Task<RegistrationInterpretation> InterpretRegistrationReplyAsync(
        SearchSession session,
        SearchContextData context,
        string message,
        string currentField,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey) || string.IsNullOrWhiteSpace(_options.Model))
            return InterpretRegistrationFallback(currentField, message);

        try
        {
            var history = await _db.ChatMessages
                .Where(m => m.SearchSessionId == session.Id)
                .OrderByDescending(m => m.CreatedAt)
                .Take(8)
                .Select(m => new { m.Role, m.Content })
                .ToListAsync(cancellationToken);
            history.Reverse();

            var historyBlock = history.Count == 0
                ? "(none yet)"
                : string.Join("\n", history.Select(m => $"{m.Role}: {m.Content}"));

            var systemPrompt = $$"""
                You are {{_branding.ChatBotName}}, a warm registration concierge for {{_branding.SiteName}}.
                You help patients create an account so you can match them with the right dentist.

                Personality:
                - Sound like a friendly front-desk coordinator on the phone — human, brief, never robotic
                - Acknowledge corrections gracefully ("Got it — Lama, thanks for the update.")
                - Extract the real data from natural language. Do not store filler phrases.
                  "I am lama" / "I'm Lama" / "my name is Lama" / "i am binu k vargese" → Binu K Vargese (full name, not just first name)
                  "Sorry my name is lama" while asking for email → NAME CORRECTION, not an email
                  "it's lama@gmail.com" / "i is lama@gmail.com" / "email is lama at gmail.com" → lama@gmail.com
                  "call me at 713-555-1212" / "7894561230" → phone digits
                  "April 9 1980" / "4/9/80" → 04/09/1980
                - Title-case full names (Binu K Vargese, Mary Jane). Lowercase emails.
                - Never invent data. If unclear, valid=false and ask again in one short sentence.
                - Do NOT ask more than one question. No medical advice. No password handling.

                EMAIL IS STRICT (currentField=email):
                - Email is REQUIRED. valid=true ONLY if you extract a real address with @ and a domain (e.g. name@gmail.com).
                - "I don't have email", "no email", "skip", "use my phone", "yes", a phone number, or anything without @ → valid=false.
                - NEVER offer to skip email. NEVER substitute phone for email. NEVER ask for a phone number on the email step.
                - If they refuse or send a phone number, ack briefly then ask ONLY for email again.

                PHONE IS STRICT (currentField=phone):
                - Phone is REQUIRED. valid=true ONLY if you extract a US number with at least 10 digits.
                - value must be digits only after stripping spaces, dashes, parentheses, and words
                  (e.g. "(789) 456-1230" / "call me at 789-456-1230" → "7894561230").
                - "I don't have a phone", "skip", "use email instead", "yes"/"no" → valid=false.
                - NEVER offer to skip phone. Ask again for a 10-digit number, e.g. 713-555-1212.

                Respond with ONLY JSON:
                {
                  "intent": "answer" | "correct_prior" | "unclear",
                  "value": "extracted value for the field we asked, or null",
                  "valid": true,
                  "ack": "optional short acknowledgement, no next question required",
                  "correctedName": null,
                  "correctedEmail": null,
                  "correctedPhone": null,
                  "correctedDateOfBirth": null
                }
                """;

            var userPrompt = $"""
                Current registration step: {currentField}
                Already collected:
                - name: {context.PendingFullName ?? "(none)"}
                - email: {context.PendingEmail ?? "(none)"}
                - phone: {context.PendingPhone ?? "(none)"}
                - date of birth: {(context.PatientDateOfBirth?.ToString("MM/dd/yyyy") ?? "(none)")}

                Recent chat:
                {historyBlock}

                Patient just said: {message}
                """;

            var payload = AnthropicApiHelper.BuildPayload(
                _options,
                maxTokens: 350,
                system: systemPrompt,
                messages: new[] { new { role = "user", content = userPrompt } });

            using var httpRequest = AnthropicApiHelper.CreateMessageRequest(_options, payload);
            var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Registration interpreter HTTP {Status}: {Body}", (int)response.StatusCode, responseBody);
                return InterpretRegistrationFallback(currentField, message);
            }

            var text = AnthropicApiHelper.ExtractTextContent(responseBody);
            var parsed = ParseRegistrationInterpretation(text);
            if (parsed != null)
                return SanitizeClaudePhoneFields(parsed, currentField);
            return InterpretRegistrationFallback(currentField, message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Registration interpreter failed.");
            return InterpretRegistrationFallback(currentField, message);
        }
    }

    private static RegistrationInterpretation? ParseRegistrationInterpretation(string text)
    {
        var json = ExtractJsonObjectLoose(text);
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? Str(string name) =>
                root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
                    ? p.GetString()
                    : null;

            var value = Str("value");
            var valid = root.TryGetProperty("valid", out var validProp)
                && validProp.ValueKind == JsonValueKind.True;

            return new RegistrationInterpretation
            {
                Intent = Str("intent") ?? "answer",
                Value = string.IsNullOrWhiteSpace(value) ? null : value.Trim(),
                Valid = valid,
                Ack = string.IsNullOrWhiteSpace(Str("ack")) ? null : Str("ack")!.Trim(),
                CorrectedName = NullIfBlank(Str("correctedName")),
                CorrectedEmail = NullIfBlank(Str("correctedEmail")),
                CorrectedPhone = NullIfBlank(Str("correctedPhone")),
                CorrectedDateOfBirth = NullIfBlank(Str("correctedDateOfBirth"))
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? ExtractJsonObjectLoose(string text)
    {
        var trimmed = text.Trim();
        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start >= 0 && end > start)
            return trimmed[start..(end + 1)];
        return null;
    }

    private static RegistrationInterpretation InterpretRegistrationFallback(
        string currentField,
        string message)
    {
        switch (currentField)
        {
            case "name":
            {
                var name = ExtractNameHeuristic(message);
                var ok = !string.IsNullOrWhiteSpace(name) && !AnthropicValidationService.LooksLikeGibberish(name);
                return new RegistrationInterpretation
                {
                    Intent = "answer",
                    Value = ok ? name : null,
                    Valid = ok
                };
            }
            case "email":
            {
                if (TryExtractEmail(message, out var email))
                {
                    return new RegistrationInterpretation
                    {
                        Intent = "answer",
                        Value = email,
                        Valid = true
                    };
                }

                if (LooksLikeNameCorrection(message) && !LooksLikeDeclinedEmail(message))
                {
                    var name = ExtractNameHeuristic(message);
                    return new RegistrationInterpretation
                    {
                        Intent = "correct_prior",
                        Valid = false,
                        CorrectedName = string.IsNullOrWhiteSpace(name) ? null : ToDisplayName(name),
                        Ack = string.IsNullOrWhiteSpace(name)
                            ? "No problem — thanks for the update."
                            : $"No problem — I'll use {ToDisplayName(name)}."
                    };
                }

                return new RegistrationInterpretation { Intent = "unclear", Valid = false };
            }
            case "phone":
            {
                var ok = TryNormalizePhone(PhoneNumberHelper.DigitsOnly(message), out var phone)
                    && !LooksLikeDeclinedPhone(message);
                return new RegistrationInterpretation
                {
                    Intent = "answer",
                    Value = ok ? phone : null,
                    Valid = ok
                };
            }
            case "dateOfBirth":
            {
                var ok = TryParseDateOfBirth(message, out var dob);
                return new RegistrationInterpretation
                {
                    Intent = "answer",
                    Value = ok ? dob.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture) : null,
                    Valid = ok
                };
            }
            default:
                return new RegistrationInterpretation { Intent = "unclear", Valid = false };
        }
    }

    private static void ApplyRegistrationCorrections(SearchContextData context, RegistrationInterpretation interp)
    {
        if (!string.IsNullOrWhiteSpace(interp.CorrectedName))
            context.PendingFullName = ToDisplayName(interp.CorrectedName);
        if (!string.IsNullOrWhiteSpace(interp.CorrectedEmail) && TryExtractEmail(interp.CorrectedEmail, out var email))
        {
            context.PendingEmail = email;
            context.PendingUsername = email;
        }
        if (!string.IsNullOrWhiteSpace(interp.CorrectedPhone)
            && TryNormalizePhone(PhoneNumberHelper.DigitsOnly(interp.CorrectedPhone), out var phone))
            context.PendingPhone = phone;
        if (!string.IsNullOrWhiteSpace(interp.CorrectedDateOfBirth)
            && TryParseDateOfBirth(interp.CorrectedDateOfBirth, out var dob))
            context.PatientDateOfBirth = dob;
    }

    private static string WithNextRegistrationQuestion(string? ack, string question)
    {
        var a = (ack ?? "").Trim();
        if (string.IsNullOrWhiteSpace(a))
            return question;
        if (a.Contains('?', StringComparison.Ordinal))
            return a;
        if (!a.EndsWith('.') && !a.EndsWith('!') && !a.EndsWith('…'))
            a += ".";
        return $"{a} {question}";
    }

    private static string BuildRegistrationFollowUp(string? ack, string defaultPrompt, string nextQuestionOnly)
    {
        var a = (ack ?? "").Trim();
        if (string.IsNullOrWhiteSpace(a))
            return defaultPrompt;
        if (a.Contains('?', StringComparison.Ordinal))
            return a;
        return WithNextRegistrationQuestion(a, nextQuestionOnly);
    }

    private static string ToDisplayName(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
            return trimmed;
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(trimmed.ToLowerInvariant());
    }

    private static readonly Regex NamePrefixRegex = new(
        @"^(?:hi|hello|hey)[,.\s]+(?:my name is|i am|i'm|im|i is|this is)?\s*|^sorry[,.\s]+(?:my name is|i am|i'm|im|i is)?\s*|^(?:my name is|i am|i'm|im|i is|this is|it's|its|name is)\s+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string ExtractNameHeuristic(string answer)
    {
        var s = answer.Trim().Trim('"', '\'');
        s = NamePrefixRegex.Replace(s, "");
        s = s.Trim().TrimEnd('.', '!', ',', ';');
        return s;
    }

    private static bool LooksLikeNameCorrection(string answer)
    {
        var lower = answer.Trim().ToLowerInvariant();
        if (lower.Contains('@'))
            return false;
        return lower.Contains("my name is", StringComparison.Ordinal)
            || lower.Contains("i am ", StringComparison.Ordinal)
            || lower.Contains("i'm ", StringComparison.Ordinal)
            || lower.Contains("i is ", StringComparison.Ordinal)
            || lower.StartsWith("sorry", StringComparison.Ordinal);
    }

    private static string? PreferFullerName(string? fromAnswer, string? fromInterp)
    {
        var answerOk = !string.IsNullOrWhiteSpace(fromAnswer) && !AnthropicValidationService.LooksLikeGibberish(fromAnswer);
        var interpOk = !string.IsNullOrWhiteSpace(fromInterp) && !AnthropicValidationService.LooksLikeGibberish(fromInterp);
        if (answerOk && interpOk)
        {
            var answerParts = fromAnswer!.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            var interpParts = fromInterp!.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            return answerParts >= interpParts ? fromAnswer : fromInterp;
        }

        return answerOk ? fromAnswer : interpOk ? fromInterp : fromAnswer ?? fromInterp;
    }

    private static bool LooksLikeDeclinedEmail(string answer)
    {
        var lower = answer.Trim().ToLowerInvariant();
        if (lower.Contains('@'))
            return false;
        return lower.Contains("don't have", StringComparison.Ordinal)
            || lower.Contains("dont have", StringComparison.Ordinal)
            || lower.Contains("no email", StringComparison.Ordinal)
            || lower.Contains("no mail", StringComparison.Ordinal)
            || lower.Contains("skip email", StringComparison.Ordinal)
            || lower.Contains("use my phone", StringComparison.Ordinal)
            || lower.Contains("use phone", StringComparison.Ordinal)
            || lower is "skip" or "no" or "yes" or "y" or "n";
    }

    private static bool LooksLikeDeclinedPhone(string answer)
    {
        var lower = answer.Trim().ToLowerInvariant();
        return lower.Contains("don't have", StringComparison.Ordinal)
            || lower.Contains("dont have", StringComparison.Ordinal)
            || lower.Contains("no phone", StringComparison.Ordinal)
            || lower.Contains("no number", StringComparison.Ordinal)
            || lower.Contains("skip phone", StringComparison.Ordinal)
            || lower is "skip" or "no" or "yes" or "y" or "n";
    }

    private static bool AckOffersEmailSkip(string? ack)
    {
        if (string.IsNullOrWhiteSpace(ack))
            return false;
        var lower = ack.ToLowerInvariant();
        return lower.Contains("phone")
            || lower.Contains("skip")
            || lower.Contains("instead")
            || lower.Contains("don't have")
            || lower.Contains("dont have")
            || lower.Contains("no email");
    }

    private static bool AckOffersPhoneSkip(string? ack)
    {
        if (string.IsNullOrWhiteSpace(ack))
            return false;
        var lower = ack.ToLowerInvariant();
        return lower.Contains("skip")
            || lower.Contains("instead")
            || lower.Contains("don't have")
            || lower.Contains("dont have")
            || lower.Contains("no phone")
            || lower.Contains("email instead");
    }

    private static readonly Regex EmailRegex = new(
        @"[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}",
        RegexOptions.Compiled);

    private static bool TryExtractEmail(string? text, out string email)
    {
        email = "";
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var match = EmailRegex.Match(text);
        if (!match.Success)
            return false;
        email = match.Value.Trim().ToLowerInvariant();
        return true;
    }

    private static RegistrationInterpretation SanitizeClaudePhoneFields(
        RegistrationInterpretation parsed,
        string currentField)
    {
        var value = currentField == "phone"
            ? PhoneNumberHelper.NormalizeLast10(parsed.Value) ?? PhoneNumberHelper.DigitsOnly(parsed.Value)
            : parsed.Value;
        var corrected = string.IsNullOrWhiteSpace(parsed.CorrectedPhone)
            ? parsed.CorrectedPhone
            : PhoneNumberHelper.NormalizeLast10(parsed.CorrectedPhone)
                ?? PhoneNumberHelper.DigitsOnly(parsed.CorrectedPhone);

        var valid = parsed.Valid;
        if (currentField == "phone")
            valid = value is { Length: 10 };

        return new RegistrationInterpretation
        {
            Intent = parsed.Intent,
            Value = string.IsNullOrWhiteSpace(value) ? null : value,
            Valid = valid,
            Ack = parsed.Ack,
            CorrectedName = parsed.CorrectedName,
            CorrectedEmail = parsed.CorrectedEmail,
            CorrectedPhone = string.IsNullOrWhiteSpace(corrected) ? null : corrected,
            CorrectedDateOfBirth = parsed.CorrectedDateOfBirth
        };
    }

    private static bool TryNormalizePhone(string? text, out string phone)
    {
        phone = PhoneNumberHelper.NormalizeLast10(text) ?? "";
        return phone.Length == 10;
    }

    private static bool TryParseDateOfBirth(string answer, out DateOnly dateOfBirth)
    {
        dateOfBirth = default;
        if (string.IsNullOrWhiteSpace(answer))
            return false;

        var trimmed = answer.Trim();
        var formats = new[]
        {
            "M/d/yyyy", "MM/dd/yyyy", "M-d-yyyy", "MM-dd-yyyy",
            "yyyy-MM-dd", "MMMM d, yyyy", "MMM d, yyyy"
        };

        if (DateOnly.TryParseExact(trimmed, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out dateOfBirth))
            return true;

        return DateOnly.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None, out dateOfBirth)
            || DateOnly.TryParse(trimmed, CultureInfo.CurrentCulture, DateTimeStyles.None, out dateOfBirth);
    }

    private static bool TryParseFirstVisitAnswer(string answer, out bool isFirstVisit)
    {
        isFirstVisit = false;
        var lower = answer.Trim().ToLowerInvariant();

        if (lower is "yes" or "y" or "yeah" or "yep" or "sure" or "ok")
        {
            isFirstVisit = true;
            return true;
        }

        if (lower is "no" or "n" or "nope")
        {
            isFirstVisit = false;
            return true;
        }

        if (lower.StartsWith("yes") || lower.Contains("first time") || lower.Contains("first visit")
            || lower.Contains("never been") || lower.Contains("new here"))
        {
            isFirstVisit = true;
            return true;
        }

        if (lower.StartsWith("no") || lower.Contains("visited before") || lower.Contains("been here before")
            || lower.Contains("returning") || lower.Contains("come back"))
        {
            isFirstVisit = false;
            return true;
        }

        return false;
    }

    private static bool IsYesAnswer(string answer)
    {
        var lower = answer.Trim().ToLowerInvariant();
        return lower is "yes" or "y" or "yeah" or "yep"
            || lower.StartsWith("yes ")
            || lower.Contains("same")
            || lower.Contains("still");
    }

    private static bool IsNoAnswer(string answer)
    {
        var lower = answer.Trim().ToLowerInvariant();
        return lower is "no" or "n" or "nope"
            || lower.StartsWith("no ")
            || lower.Contains("changed")
            || lower.Contains("moved")
            || lower.Contains("different");
    }

    private async Task<ChatMessageResponse> HandleDeepDiveAsync(
        SearchSession session, SearchContextData context, string message, CancellationToken cancellationToken)
    {
        if (context.CurrentPollingQuestionId.HasValue)
            return await HandleDeepDiveAnswerAsync(session, context, message, cancellationToken);

        return await AskNextDeepDiveQuestionAsync(session, context, "Tell me more about your preferences.", cancellationToken);
    }

    private async Task<ChatMessageResponse> HandleDeepDiveAnswerAsync(
        SearchSession session, SearchContextData context, string answer, CancellationToken cancellationToken)
    {
        var pollingList = await _pollingQuestions.GetActiveAsync(cancellationToken);
        var current = pollingList.FirstOrDefault(q => q.Id == context.CurrentPollingQuestionId);
        if (current == null)
        {
            context.CurrentPollingQuestionId = null;
            return await BeginMatchSearchAsync(session, context, cancellationToken);
        }

        var trimmed = answer.Trim();

        if (context.DeepDiveFollowUp == DeepDiveFollowUpStep.AwaitingLanguageSelection
            && NuviFlowContent.IsLanguageDeepDiveQuestion(current.Question))
        {
            return await CompleteLanguageSelectionAsync(session, context, current, trimmed, cancellationToken);
        }

        if (context.DeepDiveFollowUp == DeepDiveFollowUpStep.AwaitingWildcardConcern
            && NuviFlowContent.IsWildcardDeepDiveQuestion(current.Question))
        {
            return await CompleteWildcardConcernAsync(session, context, current, trimmed, cancellationToken);
        }

        if (NuviFlowContent.IsWildcardDeepDiveQuestion(current.Question))
        {
            if (IsAffirmativeAnswer(trimmed))
            {
                context.DeepDiveFollowUp = DeepDiveFollowUpStep.AwaitingWildcardConcern;
                var prompt = NuviFlowContent.DeepDiveWildcardFollowUpQuestion;
                await SaveAssistantMessageAsync(session, prompt, cancellationToken);
                return BuildDeepDivePollingResponse(session, context, prompt, current,
                    awaitingWildcardConcern: true,
                    inputPlaceholder: "Share what matters to you in a doctor...");
            }

            if (IsNegativeAnswer(trimmed))
                return await RecordPollingAnswerAndCompleteAsync(session, context, current, "No", cancellationToken);

            return RepromptDeepDive(session, context, current, "Please choose Yes or No.");
        }

        if (NuviFlowContent.IsLanguageDeepDiveQuestion(current.Question))
        {
            if (IsAffirmativeAnswer(trimmed))
            {
                context.DeepDiveFollowUp = DeepDiveFollowUpStep.AwaitingLanguageSelection;
                var languages = await _doctorLanguages.GetActiveNamesAsync(cancellationToken);
                var prompt = NuviFlowContent.DeepDiveLanguageFollowUpQuestion;
                await SaveAssistantMessageAsync(session, prompt, cancellationToken);
                return BuildDeepDivePollingResponse(session, context, prompt, current,
                    languageOptions: languages,
                    awaitingLanguageSelection: true);
            }

            if (IsNegativeAnswer(trimmed))
            {
                context.LanguagePreference = null;
                return await RecordPollingAnswerAndAdvanceAsync(session, context, current, "No", cancellationToken);
            }

            return RepromptDeepDive(session, context, current, "Please choose Yes or No.");
        }

        var lastAssistantMessage = await _db.ChatMessages
            .Where(m => m.SearchSessionId == session.Id && m.Role == "assistant")
            .OrderByDescending(m => m.CreatedAt)
            .Select(m => m.Content)
            .FirstOrDefaultAsync(cancellationToken);

        var validation = await _validationService.ValidateAnswerAsync(
            current.Question, trimmed, current.ValidationHint, lastAssistantMessage, cancellationToken);

        if (!validation.IsValid)
        {
            return RepromptDeepDive(session, context, current,
                validation.RepromptMessage ?? $"Could you answer again: {current.Question}");
        }

        return await RecordPollingAnswerAndAdvanceAsync(
            session, context, current, validation.NormalizedAnswer ?? trimmed, cancellationToken);
    }

    private async Task<ChatMessageResponse> CompleteLanguageSelectionAsync(
        SearchSession session,
        SearchContextData context,
        PollingQuestionDto current,
        string language,
        CancellationToken cancellationToken)
    {
        var activeLanguages = await _doctorLanguages.GetActiveNamesAsync(cancellationToken);
        var match = activeLanguages.FirstOrDefault(l =>
            l.Equals(language, StringComparison.OrdinalIgnoreCase));

        if (match == null)
        {
            return BuildDeepDivePollingResponse(session, context,
                "Please choose a language from the list.",
                current,
                languageOptions: activeLanguages,
                awaitingLanguageSelection: true);
        }

        context.LanguagePreference = match;
        context.DeepDiveFollowUp = DeepDiveFollowUpStep.None;
        return await RecordPollingAnswerAndAdvanceAsync(session, context, current, $"Yes — {match}", cancellationToken);
    }

    private async Task<ChatMessageResponse> CompleteWildcardConcernAsync(
        SearchSession session,
        SearchContextData context,
        PollingQuestionDto current,
        string concern,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(concern) || concern.Length < 2)
        {
            return BuildDeepDivePollingResponse(session, context,
                "Please share a short note about what else matters to you, or choose No if you're all set.",
                current,
                awaitingWildcardConcern: true,
                inputPlaceholder: "Share what matters to you in a doctor...");
        }

        context.WildcardConcern = concern.Trim();
        context.DeepDiveFollowUp = DeepDiveFollowUpStep.None;
        return await RecordPollingAnswerAndCompleteAsync(session, context, current, concern.Trim(), cancellationToken);
    }

    private async Task<ChatMessageResponse> RecordPollingAnswerAndAdvanceAsync(
        SearchSession session,
        SearchContextData context,
        PollingQuestionDto current,
        string answer,
        CancellationToken cancellationToken)
    {
        context.PollingAnswers.Add(new PollingAnswerEntry
        {
            QuestionId = current.Id,
            Question = current.Question,
            Answer = answer,
            MatchWeight = current.MatchWeight
        });
        context.CurrentPollingQuestionId = null;
        context.DeepDiveFollowUp = DeepDiveFollowUpStep.None;

        await PersistPatientAgeFromAnswerAsync(session, context, current, answer, cancellationToken);

        if (await IsDeepDiveCompleteAsync(context, cancellationToken))
            return await BeginMatchSearchAsync(session, context, cancellationToken);

        return await AskNextDeepDiveQuestionAsync(session, context, "Thanks!", cancellationToken);
    }

    private async Task<ChatMessageResponse> RecordPollingAnswerAndCompleteAsync(
        SearchSession session,
        SearchContextData context,
        PollingQuestionDto current,
        string answer,
        CancellationToken cancellationToken)
    {
        context.PollingAnswers.Add(new PollingAnswerEntry
        {
            QuestionId = current.Id,
            Question = current.Question,
            Answer = answer,
            MatchWeight = current.MatchWeight
        });
        context.CurrentPollingQuestionId = null;
        context.DeepDiveFollowUp = DeepDiveFollowUpStep.None;

        return await BeginMatchSearchAsync(session, context, cancellationToken);
    }

    private ChatMessageResponse RepromptDeepDive(
        SearchSession session,
        SearchContextData context,
        PollingQuestionDto current,
        string message) =>
        BuildDeepDivePollingResponse(session, context, message, current);

    private ChatMessageResponse BuildDeepDivePollingResponse(
        SearchSession session,
        SearchContextData context,
        string text,
        PollingQuestionDto current,
        IReadOnlyList<string>? languageOptions = null,
        bool awaitingLanguageSelection = false,
        bool awaitingWildcardConcern = false,
        string? inputPlaceholder = null) =>
        BuildResponse(session, context, text, stage: NuviConversationStage.DeepDive,
            awaitingPolling: true,
            pollingQuestionId: current.Id,
            options: languageOptions == null ? GetPollingQuestionOptions(current) : null,
            languageOptions: languageOptions,
            awaitingLanguageSelection: awaitingLanguageSelection,
            awaitingWildcardConcern: awaitingWildcardConcern,
            pollingQuestionKind: GetPollingQuestionKind(current),
            inputPlaceholder: inputPlaceholder);

    private static bool IsAffirmativeAnswer(string answer)
    {
        var lower = answer.Trim().ToLowerInvariant();
        return lower is "yes" or "y" or "yeah" or "yep" or "sure" or "ok";
    }

    private static bool IsNegativeAnswer(string answer)
    {
        var lower = answer.Trim().ToLowerInvariant();
        return lower is "no" or "n" or "nope" or "nothing else" or "skip" or "no thanks";
    }

    private static string? GetPollingQuestionKind(PollingQuestionDto question)
    {
        if (NuviFlowContent.IsWildcardDeepDiveQuestion(question.Question))
            return "wildcard";
        if (NuviFlowContent.IsLanguageDeepDiveQuestion(question.Question))
            return "language";
        return null;
    }

    private async Task<ChatMessageResponse> AskNextDeepDiveQuestionAsync(
        SearchSession session, SearchContextData context, string priorText, CancellationToken cancellationToken, bool signedIn = false)
    {
        if (await IsDeepDiveCompleteAsync(context, cancellationToken))
            return await BeginMatchSearchAsync(session, context, cancellationToken);

        var nextPolling = await GetNextPollingQuestionAsync(session, context, cancellationToken);
        if (nextPolling == null)
            return await BeginMatchSearchAsync(session, context, cancellationToken);

        context.CurrentPollingQuestionId = nextPolling.Id;
        var displayName = GetDisplayName(context);
        var pollingText = PersonalizePollingQuestion(nextPolling.Question, session);
        var question = context.PollingAnswers.Count == 0
            ? priorText.Contains(NuviFlowContent.DeepDiveWelcomeSuffix, StringComparison.OrdinalIgnoreCase)
                ? $"{priorText}\n\n{pollingText}"
                : $"{priorText}\n\n{FormatDeepDiveWelcome(displayName)}\n\n{pollingText}"
            : $"{priorText}\n\n{pollingText}";

        await SaveAssistantMessageAsync(session, question, cancellationToken);
        return BuildResponse(session, context, question, stage: NuviConversationStage.DeepDive,
            awaitingPolling: true, pollingQuestionId: nextPolling.Id, signedIn: signedIn,
            options: GetPollingQuestionOptions(nextPolling),
            pollingQuestionKind: GetPollingQuestionKind(nextPolling));
    }

    private async Task<ChatMessageResponse> BeginMatchSearchAsync(
        SearchSession session, SearchContextData context, CancellationToken cancellationToken)
    {
        context.AwaitingMatchSearch = true;
        context.Stage = NuviConversationStage.RecommendationReveal;
        context.PollingComplete = true;
        context.CurrentPollingQuestionId = null;

        ApplyDeepDivePreferences(session, context);
        if (!string.IsNullOrWhiteSpace(context.LanguagePreference))
            session.SearchNotes = (session.SearchNotes ?? "") + $" Preferred doctor language: {context.LanguagePreference}.";
        if (!string.IsNullOrWhiteSpace(context.WildcardConcern))
            session.SearchNotes = (session.SearchNotes ?? "") + $" Additional matching preference: {context.WildcardConcern}.";
        SearchContextHelper.Save(session, context);
        await _db.SaveChangesAsync(cancellationToken);
        await PersistPatientPreferenceProfileAsync(session, context, cancellationToken);

        var loadingText = NuviFlowContent.MatchSearchLoadingMessage;
        await SaveAssistantMessageAsync(session, loadingText, cancellationToken);
        return BuildResponse(session, context, loadingText, stage: NuviConversationStage.RecommendationReveal,
            showLoading: true, awaitingMatchSearch: true);
    }

    private async Task<ChatMessageResponse> ExecuteMatchSearchAsync(
        SearchSession session, SearchContextData context, CancellationToken cancellationToken)
    {
        context.AwaitingMatchSearch = false;

        var doctors = await SearchTopMatchesAsync(session, context, cancellationToken);
        context.MatchedDoctorIds = doctors.Select(d => d.Id).ToList();
        context.RecommendedDoctorIds.Clear();

        var displayName = GetDisplayName(context);
        if (doctors.Count == 0)
        {
            var noMatchText = $"{displayName}, I couldn't find an exact match in your area right now, but I'm still here to help refine your search.";
            await SaveAssistantMessageAsync(session, noMatchText, cancellationToken);
            return BuildResponse(session, context, noMatchText, stage: NuviConversationStage.RecommendationReveal,
                doctorCards: doctors);
        }

        context.Stage = NuviConversationStage.CallingConsent;
        context.CallingStep = CallingConsentStep.AskCallPermission;
        context.CallScope = CallOfficeScope.None;
        context.CallPreference = CallOfficePreference.None;

        var afterListText = NuviFlowContent.MatchRevealAfterListMessage;
        await SaveAssistantMessageAsync(session, afterListText, cancellationToken);
        return BuildResponse(session, context, afterListText, stage: NuviConversationStage.CallingConsent,
            doctorCards: doctors,
            options: NuviFlowContent.CallOfficesPermissionOptions,
            optionsOnly: true);
    }

    private async Task PersistPatientPreferenceProfileAsync(
        SearchSession session, SearchContextData context, CancellationToken cancellationToken)
    {
        if (!session.PatientId.HasValue)
            return;

        var patient = await _db.Patients.FirstOrDefaultAsync(p => p.Id == session.PatientId.Value, cancellationToken);
        if (patient == null)
            return;

        var profile = new PatientPreferenceProfile
        {
            VisitPreference = context.VisitPreference,
            LocationPreference = context.LocationPreference,
            UrgencyPreference = context.UrgencyPreference,
            InsurancePreference = context.InsurancePreference,
            InsuranceCategory = context.InsuranceCategory,
            LanguagePreference = context.LanguagePreference,
            WildcardConcern = context.WildcardConcern,
            DeepDiveAnswers = context.PollingAnswers,
            UpdatedAt = DateTime.UtcNow
        };

        patient.PreferenceProfileJson = JsonSerializer.Serialize(profile, SearchContextHelper.JsonOptions);
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task<ChatMessageResponse> HandleRecommendationRevealAsync(
        SearchSession session, SearchContextData context, ChatMessageRequest request, CancellationToken cancellationToken)
    {
        var message = (request.Message ?? string.Empty).Trim().ToLowerInvariant();
        if (message.Contains("other") || message.Contains("match") || message.Contains("back"))
        {
            var viewedDoctorId = context.SelectedDoctorId;
            context.SelectedDoctorId = null;
            var others = await LoadOtherMatchedDoctorsAsync(session, context, viewedDoctorId, cancellationToken);
            if (others.Count == 0)
            {
                var displayName = GetDisplayName(context);
                var onlyMatchText = $"That's the only doctor I found in your area right now who fits what you shared, {displayName}. They're your best match based on everything you told me.";
                await SaveAssistantMessageAsync(session, onlyMatchText, cancellationToken);
                return BuildResponse(session, context, onlyMatchText,
                    stage: NuviConversationStage.RecommendationReveal);
            }

            await SaveAssistantMessageAsync(session, "Here are your other matches:", cancellationToken);
            return BuildResponse(session, context, "Here are your other matches:",
                stage: NuviConversationStage.RecommendationReveal,
                doctorCards: others);
        }

        var doctorId = request.SelectedDoctorId ?? TryParseDoctorFromMessage(request.Message ?? string.Empty, context.MatchedDoctorIds);
        if (!doctorId.HasValue)
        {
            return BuildResponse(session, context,
                "Tap a doctor card above to learn more about them, or tell me which one interests you.",
                stage: NuviConversationStage.RecommendationReveal,
                doctorCards: await LoadMatchedDoctorsAsync(session, context, cancellationToken));
        }

        context.SelectedDoctorId = doctorId;
        context.Stage = NuviConversationStage.DoctorExplore;
        return await HandleDoctorExploreAsync(session, context, request, cancellationToken);
    }

    private async Task<ChatMessageResponse> HandleDoctorExploreAsync(
        SearchSession session, SearchContextData context, ChatMessageRequest request, CancellationToken cancellationToken)
    {
        var doctorId = context.SelectedDoctorId ?? request.SelectedDoctorId;
        if (!doctorId.HasValue)
            return await HandleRecommendationRevealAsync(session, context, request, cancellationToken);

        var doctor = await _db.Doctors.AsNoTracking().FirstOrDefaultAsync(d => d.Id == doctorId.Value, cancellationToken);
        if (doctor == null)
            return BuildResponse(session, context, "I couldn't find that doctor. Please pick from your matches above.",
                stage: NuviConversationStage.RecommendationReveal,
                doctorCards: await LoadMatchedDoctorsAsync(session, context, cancellationToken));

        var doctorDetail = MapDoctorDetail(doctor, session);
        var liveReviews = await _googleReviews.LookupAsync(doctor, cancellationToken);
        ApplyLiveGoogleReviews(doctorDetail, liveReviews);
        context.Stage = NuviConversationStage.RecommendationReveal;

        if (context.RecommendedDoctorIds.Contains(doctorId.Value))
        {
            return BuildResponse(session, context, string.Empty, stage: NuviConversationStage.RecommendationReveal,
                selectedDoctor: doctorDetail);
        }

        var chiefComplaint = await GetInitialHealthConcernAsync(session.Id, cancellationToken);
        var text = await BuildDoctorConciergeRecommendationAsync(doctor, chiefComplaint, session, context, liveReviews, cancellationToken);
        context.RecommendedDoctorIds.Add(doctorId.Value);

        await SaveAssistantMessageAsync(session, text, cancellationToken);

        if (session.PatientId.HasValue)
        {
            await _patientDoctorContacts.RecordContactViewAsync(
                session.PatientId.Value, doctor.Id, session.Id, cancellationToken);
        }

        return BuildResponse(session, context, text, stage: NuviConversationStage.RecommendationReveal,
            selectedDoctor: doctorDetail);
    }

    private async Task<ChatMessageResponse> HandleCallingConsentAsync(
        SearchSession session,
        SearchContextData context,
        ChatMessageRequest request,
        string message,
        CancellationToken cancellationToken)
    {
        if (IsDoctorCardOnlyRequest(request) && request.SelectedDoctorId.HasValue)
        {
            var explore = await HandleDoctorExploreAsync(session, context, request, cancellationToken);
            context.Stage = NuviConversationStage.CallingConsent;
            var (_, options) = GetCallingConsentPrompt(context);
            return BuildResponse(session, context, explore.Text ?? string.Empty,
                stage: NuviConversationStage.CallingConsent,
                selectedDoctor: explore.SelectedDoctor,
                options: options,
                optionsOnly: true);
        }

        if (context.CallingStep == CallingConsentStep.None)
            context.CallingStep = CallingConsentStep.AskCallPermission;

        switch (context.CallingStep)
        {
            case CallingConsentStep.AskCallPermission:
                if (IsNegativeAnswer(message) || IsCallDecline(message))
                {
                    context.Stage = NuviConversationStage.Confirmation;
                    context.CallingStep = CallingConsentStep.None;
                    var endText = NuviFlowContent.CallOfficesDeclineEndMessage;
                    await SaveAssistantMessageAsync(session, endText, cancellationToken);
                    return BuildResponse(session, context, endText,
                        stage: NuviConversationStage.Confirmation, flowComplete: true);
                }

                if (!IsAffirmativeAnswer(message) && !IsCallAccept(message))
                {
                    var reprompt = NuviFlowContent.CallOfficesPermissionQuestion;
                    await SaveAssistantMessageAsync(session, reprompt, cancellationToken);
                    return BuildResponse(session, context, reprompt,
                        stage: NuviConversationStage.CallingConsent,
                        options: NuviFlowContent.CallOfficesPermissionOptions,
                        optionsOnly: true);
                }

                context.CallingStep = CallingConsentStep.AskMoreQuestions;
                var askMore = NuviFlowContent.CallOfficesAskQuestionsPrompt;
                await SaveAssistantMessageAsync(session, askMore, cancellationToken);
                return BuildResponse(session, context, askMore,
                    stage: NuviConversationStage.CallingConsent,
                    options: NuviFlowContent.CallOfficesAskQuestionsOptions,
                    optionsOnly: true);

            case CallingConsentStep.AskMoreQuestions:
                if (IsNegativeAnswer(message) || IsCallDecline(message))
                {
                    context.CallScope = CallOfficeScope.TopOne;
                    return await StartCallingOfficesAsync(session, context, cancellationToken);
                }

                if (!IsAffirmativeAnswer(message) && !IsCallAccept(message))
                {
                    var reprompt = NuviFlowContent.CallOfficesAskQuestionsPrompt;
                    await SaveAssistantMessageAsync(session, reprompt, cancellationToken);
                    return BuildResponse(session, context, reprompt,
                        stage: NuviConversationStage.CallingConsent,
                        options: NuviFlowContent.CallOfficesAskQuestionsOptions,
                        optionsOnly: true);
                }

                context.CallingStep = CallingConsentStep.AskAllOrTop;
                var allOrTop = NuviFlowContent.CallOfficesAllOrTopQuestion;
                await SaveAssistantMessageAsync(session, allOrTop, cancellationToken);
                return BuildResponse(session, context, allOrTop,
                    stage: NuviConversationStage.CallingConsent,
                    options: NuviFlowContent.CallOfficesAllOrTopOptions,
                    optionsOnly: true);

            case CallingConsentStep.AskAllOrTop:
                if (IsTopOneScope(message))
                {
                    context.CallScope = CallOfficeScope.TopOne;
                    return await StartCallingOfficesAsync(session, context, cancellationToken);
                }

                if (IsAllScope(message))
                {
                    context.CallScope = CallOfficeScope.All;
                    context.CallingStep = CallingConsentStep.AskPreference;
                    var preferenceQ = NuviFlowContent.CallOfficesPreferenceQuestion;
                    await SaveAssistantMessageAsync(session, preferenceQ, cancellationToken);
                    return BuildResponse(session, context, preferenceQ,
                        stage: NuviConversationStage.CallingConsent,
                        options: NuviFlowContent.CallOfficesPreferenceOptions,
                        optionsOnly: true);
                }

                {
                    var reprompt = NuviFlowContent.CallOfficesAllOrTopQuestion;
                    await SaveAssistantMessageAsync(session, reprompt, cancellationToken);
                    return BuildResponse(session, context, reprompt,
                        stage: NuviConversationStage.CallingConsent,
                        options: NuviFlowContent.CallOfficesAllOrTopOptions,
                        optionsOnly: true);
                }

            case CallingConsentStep.AskPreference:
                if (IsDentistPreference(message))
                {
                    context.CallPreference = CallOfficePreference.Dentist;
                    return await StartCallingOfficesAsync(session, context, cancellationToken);
                }

                if (IsDateTimePreference(message))
                {
                    context.CallPreference = CallOfficePreference.DateAndTime;
                    return await StartCallingOfficesAsync(session, context, cancellationToken);
                }

                {
                    var reprompt = NuviFlowContent.CallOfficesPreferenceQuestion;
                    await SaveAssistantMessageAsync(session, reprompt, cancellationToken);
                    return BuildResponse(session, context, reprompt,
                        stage: NuviConversationStage.CallingConsent,
                        options: NuviFlowContent.CallOfficesPreferenceOptions,
                        optionsOnly: true);
                }

            default:
                context.CallingStep = CallingConsentStep.AskCallPermission;
                var restart = NuviFlowContent.CallOfficesPermissionQuestion;
                await SaveAssistantMessageAsync(session, restart, cancellationToken);
                return BuildResponse(session, context, restart,
                    stage: NuviConversationStage.CallingConsent,
                    options: NuviFlowContent.CallOfficesPermissionOptions,
                    optionsOnly: true);
        }
    }

    private async Task<ChatMessageResponse> HandleCallingOfficesAsync(
        SearchSession session, SearchContextData context, CancellationToken cancellationToken)
    {
        return await StartCallingOfficesAsync(session, context, cancellationToken);
    }

    private async Task<ChatMessageResponse> StartCallingOfficesAsync(
        SearchSession session, SearchContextData context, CancellationToken cancellationToken)
    {
        context.Stage = NuviConversationStage.CallingOffices;
        var doctors = await LoadMatchedDoctorsInRankOrderAsync(session, context, cancellationToken);
        var bookingWindow = BuildPacificBookingWindow(context.UrgencyPreference);
        var urgencyWindow = bookingWindow.Phrase;
        var agentDateTime = bookingWindow.Phrase;
        var preferredTimeWindow = context.CallPreference == CallOfficePreference.DateAndTime
            ? "prefer a specific date and time that works within the booking window (Pacific Time)"
            : "any available time during office hours (Pacific Time)";
        var callPreferenceLabel = context.CallPreference == CallOfficePreference.DateAndTime
            ? "date_and_time"
            : context.CallPreference == CallOfficePreference.Dentist
                ? "dentist"
                : context.CallScope == CallOfficeScope.TopOne ? "top_one" : "any_time";

        if (doctors.Count == 0)
        {
            var emptyText = "I don't have a matched office to call yet. Tap refresh to start a new search whenever you're ready.";
            context.Stage = NuviConversationStage.Confirmation;
            context.CallingStep = CallingConsentStep.None;
            await SaveAssistantMessageAsync(session, emptyText, cancellationToken);
            return BuildResponse(session, context, emptyText, stage: NuviConversationStage.Confirmation, flowComplete: true);
        }

        var overrideTo = ElevenLabsTwilioCallingService.ToE164(_twilioOptions.OutboundOverrideToNumber);
        var target = await FindFirstCallableDoctorAsync(doctors, allowMissingPhone: !string.IsNullOrWhiteSpace(overrideTo), cancellationToken);
        if (target == null)
        {
            var noPhoneText = string.IsNullOrWhiteSpace(overrideTo)
                ? "I found your matches, but none of them have an office phone number on file yet — so I can't place the call automatically. You can tap a doctor card to view their profile, or try again after numbers are updated."
                : "I found your matches, but I couldn't load a doctor to call. Please try again.";
            context.Stage = NuviConversationStage.Confirmation;
            context.CallingStep = CallingConsentStep.None;
            await SaveAssistantMessageAsync(session, noPhoneText, cancellationToken);
            return BuildResponse(session, context, noPhoneText, stage: NuviConversationStage.Confirmation, flowComplete: true);
        }

        context.SelectedDoctorId = target.Value.Doctor.Id;
        var topName = string.IsNullOrWhiteSpace(target.Value.Doctor.Name) ? "your top match" : target.Value.Doctor.Name;
        var practiceLabel = VoiceCallBookingService.FormatPracticeLabel(
            target.Value.Doctor.PracticeName,
            target.Value.Doctor.Name);
        var dialNumber = !string.IsNullOrWhiteSpace(overrideTo) ? overrideTo! : target.Value.PhoneE164;

        string planText;
        if (context.CallScope == CallOfficeScope.TopOne || doctors.Count <= 1)
        {
            planText = $"Great — I'll call {topName} now to help book your appointment.";
        }
        else if (context.CallPreference == CallOfficePreference.DateAndTime)
        {
            planText = $"Perfect — I'll call your matched doctors one by one from the top until we find a booking available {urgencyWindow}. Starting with {topName}.";
        }
        else
        {
            planText = $"Perfect — I'll call your matched doctors one by one from the top until a booking is available any time. Starting with {topName}.";
        }

        if (!string.IsNullOrWhiteSpace(overrideTo))
            planText += $"\n\n(Dev override: dialing {overrideTo} instead of the office number.)";

        if (!_voiceCalling.IsConfigured)
        {
            var notConfigured = $"{planText}\n\nVoice calling isn't fully configured on the server yet (ElevenLabs + Twilio). Your preference is saved — once keys/phone IDs are set, I'll place these calls automatically.";
            context.Stage = NuviConversationStage.Confirmation;
            context.BookingConfirmed = true;
            context.CallingStep = CallingConsentStep.None;
            await SaveAssistantMessageAsync(session, notConfigured, cancellationToken);
            return BuildResponse(session, context, notConfigured, stage: NuviConversationStage.Confirmation, flowComplete: true);
        }

        var chiefComplaint = await GetInitialHealthConcernAsync(session.Id, cancellationToken);
        var callContext = context.CallScope == CallOfficeScope.All
            ? (context.CallPreference == CallOfficePreference.DateAndTime
                ? $"Call offices in rank order until a booking is available {urgencyWindow}."
                : "Call offices in rank order until any booking is available.")
            : "Call the top matched doctor only.";

        var callResult = await _voiceCalling.PlaceOfficeCallAsync(new NuviOutboundCallRequest
        {
            ToNumber = dialNumber,
            DoctorName = target.Value.Doctor.Name,
            PracticeName = target.Value.Doctor.PracticeName,
            PracticePhone = target.Value.PhoneE164,
            PatientName = GetDisplayName(context),
            PatientPhone = context.PendingPhone,
            PatientEmail = context.PendingEmail,
            CallPreference = callPreferenceLabel,
            AvailabilityWindow = urgencyWindow,
            PreferredDate = agentDateTime,
            PreferredTimeWindow = preferredTimeWindow,
            BookingWindowStart = bookingWindow.StartDate,
            BookingWindowEnd = bookingWindow.EndDate,
            AppointmentType = string.IsNullOrWhiteSpace(chiefComplaint) ? "dental appointment" : chiefComplaint,
            InsuranceName = context.InsurancePreference ?? context.InsuranceCategory,
            ChiefComplaint = chiefComplaint,
            CallContext = callContext,
            SessionKey = session.SessionKey.ToString()
        }, cancellationToken);

        string text;
        string? conversationId = null;
        string? callSid = null;
        string? voiceStatus = null;
        if (callResult.Success)
        {
            text = $"{planText}\n\n{VoiceCallBookingService.FormatAttemptingCallChat(practiceLabel)}";

            conversationId = callResult.ConversationId;
            callSid = callResult.CallSid;
            voiceStatus = VoiceOutboundCallStatuses.Initiated;

            if (!string.IsNullOrWhiteSpace(callResult.ConversationId))
            {
                await _voiceBookings.RecordInitiatedCallAsync(new VoiceOutboundCallRecordRequest
                {
                    ConversationId = callResult.ConversationId!,
                    CallSid = callResult.CallSid,
                    SessionKey = session.SessionKey,
                    SearchSessionId = session.Id,
                    PatientId = session.PatientId,
                    DoctorId = target.Value.Doctor.Id,
                    PatientName = GetDisplayName(context),
                    PatientPhone = context.PendingPhone,
                    PatientEmail = context.PendingEmail,
                    VisitReason = string.IsNullOrWhiteSpace(chiefComplaint) ? "dental appointment" : chiefComplaint,
                    ToNumber = dialNumber
                }, cancellationToken);
                _voiceBookings.ScheduleConversationPolling(callResult.ConversationId!);
            }
        }
        else
        {
            if (context.CallScope == CallOfficeScope.All)
            {
                var chiefComplaintForCascade = string.IsNullOrWhiteSpace(chiefComplaint)
                    ? "dental appointment"
                    : chiefComplaint;
                var cascade = await _voiceCascade.TryCallNextDoctorAsync(
                    session,
                    context,
                    new VoiceOutboundCallRecordRequest
                    {
                        ConversationId = string.Empty,
                        SessionKey = session.SessionKey,
                        SearchSessionId = session.Id,
                        PatientId = session.PatientId,
                        DoctorId = target.Value.Doctor.Id,
                        PatientName = GetDisplayName(context),
                        PatientPhone = context.PendingPhone,
                        PatientEmail = context.PendingEmail,
                        VisitReason = chiefComplaintForCascade
                    },
                    [target.Value.Doctor.Id],
                    topName,
                    cancellationToken);

                if (cascade.NextCallStarted)
                {
                    text = cascade.ChatMessage ?? $"{planText}\n\nI'm calling the next matched doctor now.";
                    conversationId = cascade.ConversationId;
                    callSid = cascade.CallSid;
                    voiceStatus = VoiceOutboundCallStatuses.Initiated;
                }
                else
                {
                    text = cascade.AllDoctorsExhausted
                        ? cascade.ChatMessage ?? $"{planText}\n\nI wasn't able to reach any offices to book."
                        : $"{planText}\n\nI wasn't able to complete the dial just now: {callResult.Message}";
                    voiceStatus = VoiceOutboundCallStatuses.Failed;
                }
            }
            else
            {
                text = $"{planText}\n\nI wasn't able to complete the dial just now: {callResult.Message}";
                voiceStatus = VoiceOutboundCallStatuses.Failed;
            }
        }

        context.Stage = NuviConversationStage.Confirmation;
        context.BookingConfirmed = callResult.Success;
        context.CallingStep = CallingConsentStep.None;
        await SaveAssistantMessageAsync(session, text, cancellationToken);
        return BuildResponse(
            session,
            context,
            text,
            stage: NuviConversationStage.Confirmation,
            flowComplete: true,
            conversationId: conversationId,
            callSid: callSid,
            callingDoctorId: target.Value.Doctor.Id,
            callingDoctorName: topName,
            voiceCallStatus: voiceStatus);
    }

    private async Task<(Doctor Doctor, string PhoneE164)?> FindFirstCallableDoctorAsync(
        IReadOnlyList<DoctorDto> rankedDoctors,
        bool allowMissingPhone,
        CancellationToken cancellationToken)
    {
        foreach (var dto in rankedDoctors)
        {
            var doctor = await _db.Doctors.AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == dto.Id, cancellationToken);
            if (doctor == null)
                continue;

            var phone = ElevenLabsTwilioCallingService.ToE164(doctor.OfficePhoneNumber);
            if (string.IsNullOrWhiteSpace(phone))
            {
                var locationPhone = await _db.DoctorLocations.AsNoTracking()
                    .Where(l => l.DoctorId == doctor.Id && l.PhoneNumber != null && l.PhoneNumber != "")
                    .Select(l => l.PhoneNumber)
                    .FirstOrDefaultAsync(cancellationToken);
                phone = ElevenLabsTwilioCallingService.ToE164(locationPhone);
            }

            if (!string.IsNullOrWhiteSpace(phone))
                return (doctor, phone);

            if (allowMissingPhone)
                return (doctor, string.Empty);
        }

        return null;
    }

    private async Task<IReadOnlyList<DoctorDto>> LoadMatchedDoctorsInRankOrderAsync(
        SearchSession session, SearchContextData context, CancellationToken cancellationToken)
    {
        var doctors = await LoadMatchedDoctorsAsync(session, context, cancellationToken);
        if (context.MatchedDoctorIds == null || context.MatchedDoctorIds.Count == 0)
            return doctors;

        var order = context.MatchedDoctorIds
            .Select((id, index) => (id, index))
            .ToDictionary(x => x.id, x => x.index);

        return doctors
            .OrderBy(d => order.TryGetValue(d.Id, out var idx) ? idx : int.MaxValue)
            .ToList();
    }

    private static (string Prompt, IReadOnlyList<string> Options) GetCallingConsentPrompt(SearchContextData context) =>
        context.CallingStep switch
        {
            CallingConsentStep.AskMoreQuestions =>
                (NuviFlowContent.CallOfficesAskQuestionsPrompt, NuviFlowContent.CallOfficesAskQuestionsOptions),
            CallingConsentStep.AskAllOrTop =>
                (NuviFlowContent.CallOfficesAllOrTopQuestion, NuviFlowContent.CallOfficesAllOrTopOptions),
            CallingConsentStep.AskPreference =>
                (NuviFlowContent.CallOfficesPreferenceQuestion, NuviFlowContent.CallOfficesPreferenceOptions),
            _ =>
                (NuviFlowContent.CallOfficesPermissionQuestion, NuviFlowContent.CallOfficesPermissionOptions)
        };

    private static string FormatUrgencyBookingWindow(string? urgencyPreference) =>
        BuildPacificBookingWindow(urgencyPreference).Phrase;

    /// <summary>
    /// Phrase injected into ElevenLabs {{date_time}} / preferred_date for the office call.
    /// Uses explicit Pacific calendar dates so the agent can request real availability.
    /// ASAP → next 7 days; Within a month → 30 days; No rush → 120 days; Just exploring → 180 days.
    /// </summary>
    private static string FormatAgentDateTimePhrase(string? urgencyPreference) =>
        BuildPacificBookingWindow(urgencyPreference).Phrase;

    private static (string Phrase, string StartDate, string EndDate) BuildPacificBookingWindow(string? urgencyPreference)
    {
        var u = (urgencyPreference ?? string.Empty).Trim().ToLowerInvariant();
        var start = ElevenLabsTwilioCallingService.GetClinicNow().Date;
        int days;
        string label;

        if (u.Contains("asap") || u.Contains("this week") || u.Contains("1 week") || u.Contains("one week"))
        {
            days = 7;
            label = "within the next 7 days (this week)";
        }
        else if (u.Contains("month"))
        {
            days = 30;
            label = "within the next 30 days";
        }
        else if (u.Contains("no rush"))
        {
            days = 120;
            label = "within the next 120 days";
        }
        else if (u.Contains("explor"))
        {
            days = 180;
            label = "within the next 180 days";
        }
        else
        {
            days = 30;
            label = "within the next 30 days";
        }

        var end = start.AddDays(days);
        var phrase =
            $"{label}: any day from {start:dddd, MMMM d, yyyy} through {end:dddd, MMMM d, yyyy} (Pacific Time)";
        return (phrase, start.ToString("yyyy-MM-dd"), end.ToString("yyyy-MM-dd"));
    }

    private static bool IsCallAccept(string message)
    {
        var lower = message.Trim().ToLowerInvariant();
        return lower is "yes" or "y" or "yeah" or "yep" or "yup" or "sure" or "ok" or "okay";
    }

    private static bool IsCallDecline(string message)
    {
        var lower = message.Trim().ToLowerInvariant();
        return lower is "no" or "n" or "nope" or "nah" or "no thanks" or "no thank you";
    }

    private static bool IsTopOneScope(string message)
    {
        var lower = message.Trim().ToLowerInvariant();
        return lower is "top one" or "top" or "one" or "just top one" or "top 1";
    }

    private static bool IsAllScope(string message)
    {
        var lower = message.Trim().ToLowerInvariant();
        return lower is "all" or "all doctors" or "everyone" or "every doctor";
    }

    private static bool IsDentistPreference(string message)
    {
        var lower = message.Trim().ToLowerInvariant();
        return lower is "dentist" or "doctor" or "doctors" or "dentists";
    }

    private static bool IsDateTimePreference(string message)
    {
        var lower = message.Trim().ToLowerInvariant();
        return lower is "date and time" or "date" or "time" or "datetime" or "date & time";
    }

    private async Task<ChatMessageResponse> HandleBookingInitiationAsync(
        SearchSession session, SearchContextData context, ChatMessageRequest request, CancellationToken cancellationToken)
    {
        if (request.SelectedDoctorId.HasValue)
        {
            context.SelectedDoctorId = request.SelectedDoctorId;
            return await HandleDoctorExploreAsync(session, context, request, cancellationToken);
        }

        var message = request.Message.Trim().ToLowerInvariant();
        var displayName = GetDisplayName(context);

        if (message.Contains("other") || message.Contains("match"))
        {
            var viewedDoctorId = context.SelectedDoctorId;
            context.Stage = NuviConversationStage.RecommendationReveal;
            context.SelectedDoctorId = null;
            var others = await LoadOtherMatchedDoctorsAsync(session, context, viewedDoctorId, cancellationToken);
            if (others.Count == 0)
            {
                var onlyMatchText = $"That's the only doctor I found in your area right now who fits what you shared, {displayName}. They're your best match based on everything you told me.";
                await SaveAssistantMessageAsync(session, onlyMatchText, cancellationToken);
                return BuildResponse(session, context, onlyMatchText,
                    stage: NuviConversationStage.RecommendationReveal);
            }

            await SaveAssistantMessageAsync(session, "Here are your other matches:", cancellationToken);
            return BuildResponse(session, context, "Here are your other matches:",
                stage: NuviConversationStage.RecommendationReveal,
                doctorCards: others);
        }

        if (message.Contains("save") || message.Contains("later"))
        {
            context.Stage = NuviConversationStage.Confirmation;
            var saveText = $"No problem, {displayName}! I've saved your matches in your profile. You can come back anytime.";
            await SaveAssistantMessageAsync(session, saveText, cancellationToken);
            return BuildResponse(session, context, saveText, stage: NuviConversationStage.Confirmation, flowComplete: true);
        }

        var doctorId = context.SelectedDoctorId;
        if (!doctorId.HasValue)
            return BuildResponse(session, context, "Which doctor would you like to learn more about?",
                stage: NuviConversationStage.Confirmation);

        var doctor = await _db.Doctors.AsNoTracking().FirstOrDefaultAsync(d => d.Id == doctorId.Value, cancellationToken);
        if (doctor == null)
            return BuildResponse(session, context, "I couldn't find that doctor's contact info.");

        var chiefComplaint = await GetInitialHealthConcernAsync(session.Id, cancellationToken);
        var liveReviews = await _googleReviews.LookupAsync(doctor, cancellationToken);
        var contactText = await BuildDoctorConciergeRecommendationAsync(doctor, chiefComplaint, session, context, liveReviews, cancellationToken);
        var doctorDetail = MapDoctorDetail(doctor, session);
        ApplyLiveGoogleReviews(doctorDetail, liveReviews);

        if (session.PatientId.HasValue)
        {
            await _patientDoctorContacts.RecordContactViewAsync(
                session.PatientId.Value, doctor.Id, session.Id, cancellationToken);
        }

        context.Stage = NuviConversationStage.Confirmation;
        context.BookingConfirmed = true;
        await SaveAssistantMessageAsync(session, contactText, cancellationToken);

        return BuildResponse(session, context, contactText, stage: NuviConversationStage.Confirmation,
            selectedDoctor: doctorDetail);
    }

    private void ApplyDeepDivePreferences(SearchSession session, SearchContextData context)
    {
        foreach (var answer in context.PollingAnswers)
        {
            var q = answer.Question.ToLowerInvariant();
            var a = answer.Answer.ToLowerInvariant();

            if (q.Contains("communicat") || q.Contains("bedside") || q.Contains("personality"))
                session.CommunicationStyle = a.Contains("direct") ? "direct"
                    : a.Contains("warm") || a.Contains("reassur") || a.Contains("nurtur") ? "reassuring" : "collaborative";
            else if (q.Contains("experience") || q.Contains("practicing"))
                session.SearchNotes = (session.SearchNotes ?? "") + $" Experience preference: {a}.";
            else if (q.Contains("travel") || q.Contains("close to home"))
                session.SearchNotes = (session.SearchNotes ?? "") + $" Location priority: {a}.";
            else if (q.Contains("review") || q.Contains("healthgrades"))
                session.SearchNotes = (session.SearchNotes ?? "") + $" Reviews matter: {a}.";
            else if (q.Contains("holistic") || q.Contains("conventional"))
                session.SearchNotes = (session.SearchNotes ?? "") + $" Philosophy preference: {a}.";
            else if (q.Contains("language other than english") && !a.StartsWith("no"))
                session.SearchNotes = (session.SearchNotes ?? "") + $" Preferred doctor language: {answer.Answer}.";
            else if (q.Contains("anything else that matters"))
                session.SearchNotes = (session.SearchNotes ?? "") + $" Additional preference: {answer.Answer}.";
            else if (q.Contains("telehealth") || q.Contains("virtual"))
                session.AvailabilityPreference = a.Contains("yes") ? "telehealth" : session.AvailabilityPreference;
        }
        session.UpdatedAt = DateTime.UtcNow;
    }

    private async Task<IReadOnlyList<DoctorDto>> SearchTopMatchesAsync(
        SearchSession session, SearchContextData context, CancellationToken cancellationToken)
    {
        var results = await _doctorSearch.SearchAsync(new DoctorSearchRequest
        {
            SessionKey = session.SessionKey,
            Location = context.LocationPreference ?? session.Location ?? NuviFlowContent.DefaultLocationWhenSkipped,
            InsurancePlan = context.InsurancePreference,
            GenderPreference = "none",
            CommunicationStyle = session.CommunicationStyle,
            AvailabilityPreference = session.AvailabilityPreference,
            PreferredLanguage = context.LanguagePreference,
            AdditionalPreference = context.WildcardConcern
        }, cancellationToken);

        return results;
    }

    private async Task<IReadOnlyList<DoctorDto>> LoadMatchedDoctorsAsync(
        SearchSession session, SearchContextData context, CancellationToken cancellationToken)
    {
        if (context.MatchedDoctorIds == null || context.MatchedDoctorIds.Count == 0)
            return await SearchTopMatchesAsync(session, context, cancellationToken);

        var doctors = await _db.Doctors.AsNoTracking()
            .Where(d => context.MatchedDoctorIds.Contains(d.Id))
            .ToListAsync(cancellationToken);

        return doctors.Select(d => new DoctorDto
        {
            Id = d.Id,
            Name = d.Name,
            Specialty = d.Specialty,
            PracticeName = d.PracticeName,
            Location = $"{d.City}, {d.State}",
            AvatarInitials = d.AvatarInitials,
            MatchScore = 85,
            GoogleRating = d.GoogleRating,
            GoogleReviewCount = d.GoogleReviewCount,
            Tag = d.TagLine ?? d.Niche ?? d.SpecialtyCategory,
            OfficePhoneNumber = PhoneNumberHelper.FormatUsDisplay(d.OfficePhoneNumber),
            YearsOfPractice = d.YearsOfPractice,
            IsSponsored = d.IsSponsored
        }).ToList();
    }

    private async Task<IReadOnlyList<DoctorDto>> LoadOtherMatchedDoctorsAsync(
        SearchSession session, SearchContextData context, int? excludeDoctorId, CancellationToken cancellationToken)
    {
        var all = await LoadMatchedDoctorsAsync(session, context, cancellationToken);
        if (!excludeDoctorId.HasValue)
            return all;

        return all.Where(d => d.Id != excludeDoctorId.Value).ToList();
    }

    private async Task<string> BuildDoctorConciergeRecommendationAsync(
        Doctor doctor, string chiefComplaint, SearchSession session, SearchContextData context,
        GoogleReviewLookupResult? liveReviews,
        CancellationToken cancellationToken)
    {
        var prose = await GenerateDoctorRecommendationProseAsync(doctor, chiefComplaint, session, liveReviews, cancellationToken);
        if (string.IsNullOrWhiteSpace(prose))
            prose = BuildDoctorRecommendationFallback(doctor, chiefComplaint, session, context, liveReviews);

        return prose;
    }

    private async Task<string?> GenerateDoctorRecommendationProseAsync(
        Doctor doctor, string chiefComplaint, SearchSession session, GoogleReviewLookupResult? liveReviews,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey) || string.IsNullOrWhiteSpace(_options.Model))
            return null;

        var complaint = string.IsNullOrWhiteSpace(chiefComplaint) ? "(not clearly stated)" : chiefComplaint.Trim();
        var location = string.IsNullOrWhiteSpace(doctor.State) || doctor.State.Equals("NA", StringComparison.OrdinalIgnoreCase)
            ? doctor.City
            : $"{doctor.City}, {doctor.State}";

        var rating = liveReviews?.Found == true && liveReviews.GoogleRating > 0
            ? liveReviews.GoogleRating
            : doctor.GoogleRating;
        var reviewCount = liveReviews?.Found == true && liveReviews.GoogleReviewCount > 0
            ? liveReviews.GoogleReviewCount
            : doctor.GoogleReviewCount;
        var reviewSummary = !string.IsNullOrWhiteSpace(liveReviews?.SummaryOfReviews)
            ? liveReviews.SummaryOfReviews
            : doctor.SummaryOfReviews;

        var facts = new StringBuilder();
        facts.AppendLine($"Doctor name: {doctor.Name}");
        facts.AppendLine($"Specialty: {doctor.Specialty}");
        if (!string.IsNullOrWhiteSpace(location)) facts.AppendLine($"Location: {location}");
        if (rating > 0) facts.AppendLine($"Google rating: {rating:0.#} stars ({reviewCount} reviews)");
        if (doctor.YearsOfPractice.HasValue) facts.AppendLine($"Years of practice: {doctor.YearsOfPractice}");
        if (!string.IsNullOrWhiteSpace(doctor.Niche)) facts.AppendLine($"Focus areas: {doctor.Niche}");
        if (!string.IsNullOrWhiteSpace(doctor.Top3Procedures)) facts.AppendLine($"Top procedures: {doctor.Top3Procedures}");
        if (!string.IsNullOrWhiteSpace(reviewSummary)) facts.AppendLine($"Review summary: {reviewSummary}");
        if (liveReviews?.Reviews is { Count: > 0 })
        {
            facts.AppendLine("Recent Google review snippets:");
            foreach (var review in liveReviews.Reviews.Take(3))
                facts.AppendLine($"- {review.Rating}/5 {review.ReviewerName}: \"{review.ReviewText}\"");
        }

        var systemPrompt = $"""
            You are {_branding.ChatBotName}, a warm doctor-matching concierge for {_branding.SiteName}.
            Write a short, persuasive recommendation (2–4 sentences) telling the patient why this doctor is their best fit.

            CRITICAL RULES:
            - Tie the recommendation directly to the patient's chief complaint. This is WHY they came.
            - Only mention the doctor's strengths that are RELEVANT to that complaint. If the patient is in pain, emphasize relief, getting seen, and relevant experience.
            - Do NOT list irrelevant services (e.g. do not mention cosmetic/whitening/prevention when someone is in pain or has missing teeth).
            - You may highlight great reviews/ratings, relevant experience, and convenient location — but keep it tight and genuine, not a feature dump.
            - Warm, human, reassuring, concierge tone. Speak directly to the patient ("you").
            - Do NOT include a phone number, booking links, or promises to contact the office — that is added separately.
            - Do NOT invent facts not provided. Do NOT diagnose or give medical advice.
            - Output plain prose only. No bullet points, no headings.
            """;

        var userContent = $"Patient's chief complaint (their own words): \"{complaint}\"\n\nDoctor facts:\n{facts}";

        try
        {
            var payload = AnthropicApiHelper.BuildPayload(
                _options,
                maxTokens: 350,
                system: systemPrompt,
                messages: new[] { new { role = "user", content = userContent } });

            using var httpRequest = AnthropicApiHelper.CreateMessageRequest(_options, payload);
            var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Doctor recommendation API call failed: {Body}", responseBody);
                return null;
            }

            var text = AnthropicApiHelper.ExtractTextContent(responseBody).Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating doctor recommendation prose");
            return null;
        }
    }

    private static string BuildDoctorRecommendationFallback(
        Doctor doctor, string chiefComplaint, SearchSession session, SearchContextData context,
        GoogleReviewLookupResult? liveReviews = null)
    {
        var complaint = string.IsNullOrWhiteSpace(chiefComplaint)
            ? "what you've shared"
            : $"\"{chiefComplaint.Trim().TrimEnd('.')}\"";

        var location = string.IsNullOrWhiteSpace(doctor.State) || doctor.State.Equals("NA", StringComparison.OrdinalIgnoreCase)
            ? doctor.City
            : $"{doctor.City}, {doctor.State}";

        var rating = liveReviews?.Found == true && liveReviews.GoogleRating > 0
            ? liveReviews.GoogleRating
            : doctor.GoogleRating;

        var fitDetails = new List<string>();
        if (rating > 0)
            fitDetails.Add($"excellent reviews ({rating:0.#} stars)");

        var relevantFocus = GetComplaintRelevantFocus(chiefComplaint, doctor);
        if (!string.IsNullOrWhiteSpace(relevantFocus))
            fitDetails.Add(relevantFocus);

        if (!string.IsNullOrWhiteSpace(location)
            && (!string.IsNullOrWhiteSpace(context.LocationPreference) || !string.IsNullOrWhiteSpace(session.Location)))
            fitDetails.Add($"conveniently located in {location}");

        var fitSentence = fitDetails.Count > 0
            ? $" {doctor.Name} has {string.Join(", ", fitDetails)}."
            : (!string.IsNullOrWhiteSpace(location) ? $" {doctor.Name} practices in {location}." : "");

        var yearsText = doctor.YearsOfPractice.HasValue
            ? $" With {doctor.YearsOfPractice} years of experience,"
            : "";

        return $"Based on everything you've told me — especially that you're dealing with {complaint} — I think {doctor.Name} is your best fit.{yearsText}{fitSentence} " +
               $"They help patients get relief every day, and I'm confident they can take great care of you.";
    }

    private static void ApplyLiveGoogleReviews(DoctorDetailDto detail, GoogleReviewLookupResult? live)
    {
        if (live == null || !live.Found)
            return;

        if (live.GoogleRating > 0)
            detail.GoogleRating = live.GoogleRating;
        if (live.GoogleReviewCount > 0)
            detail.GoogleReviewCount = live.GoogleReviewCount;
        if (!string.IsNullOrWhiteSpace(live.SummaryOfReviews))
            detail.SummaryOfReviews = live.SummaryOfReviews;
    }

    private static string? GetComplaintRelevantFocus(string chiefComplaint, Doctor doctor)
    {
        var source = $"{doctor.Niche} {doctor.Top3Procedures}";
        if (string.IsNullOrWhiteSpace(source.Trim()))
            return null;

        var complaintLower = (chiefComplaint ?? string.Empty).ToLowerInvariant();
        var isPainOrDental = complaintLower.Contains("pain") || complaintLower.Contains("hurt")
            || complaintLower.Contains("tooth") || complaintLower.Contains("teeth")
            || complaintLower.Contains("missing") || complaintLower.Contains("broken")
            || complaintLower.Contains("ache");

        var items = source
            .Split(new[] { ',', ';', '/', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(i => !string.IsNullOrWhiteSpace(i))
            .ToList();

        if (isPainOrDental)
        {
            string[] irrelevant = ["cosmetic", "whiten", "prevention", "prevent", "veneer", "botox", "aesthetic", "smile makeover"];
            var relevant = items
                .Where(i => !irrelevant.Any(bad => i.ToLowerInvariant().Contains(bad)))
                .ToList();
            items = relevant.Count > 0 ? relevant : items;
        }

        var top = items.Take(2).ToList();
        if (top.Count == 0)
            return null;

        return $"experience with {string.Join(" and ", top).ToLowerInvariant()}";
    }

    private static DoctorDetailDto MapDoctorDetail(Doctor doctor, SearchSession session) => new()
    {
        Id = doctor.Id,
        Name = doctor.Name,
        Specialty = doctor.Specialty,
        PracticeName = doctor.PracticeName,
        Location = $"{doctor.City}, {doctor.State}",
        PhotoUrl = DoctorPhotoHelper.GetDisplayPhotoUrl(doctor.PhotoUrl, doctor.GmbPhotoLink),
        AvatarInitials = doctor.AvatarInitials,
        MatchScore = 90,
        MatchReason = session.SearchNotes,
        SummaryOfReviews = doctor.SummaryOfReviews,
        Niche = doctor.Niche,
        YearsOfPractice = doctor.YearsOfPractice,
        OfficePhoneNumber = doctor.OfficePhoneNumber,
        OfficeHours = "Mon–Fri 8am–5pm",
        GoogleRating = doctor.GoogleRating,
        GoogleReviewCount = doctor.GoogleReviewCount
    };

    private static int? TryParseDoctorFromMessage(string message, List<int>? matchedIds)
    {
        if (matchedIds == null) return null;
        var lower = message.ToLowerInvariant();
        if (lower.Contains("first") && matchedIds.Count > 0) return matchedIds[0];
        if (lower.Contains("second") && matchedIds.Count > 1) return matchedIds[1];
        if (lower.Contains("third") && matchedIds.Count > 2) return matchedIds[2];
        return null;
    }

    private async Task<PollingQuestionDto?> GetNextPollingQuestionAsync(
        SearchSession session, SearchContextData context, CancellationToken cancellationToken)
    {
        await PrefillAgeFromPatientProfileAsync(session, context, cancellationToken);

        var answeredIds = context.PollingAnswers.Select(a => a.QuestionId).ToHashSet();
        var active = await _pollingQuestions.GetActiveAsync(cancellationToken);
        var wildcard = active.FirstOrDefault(q => NuviFlowContent.IsWildcardDeepDiveQuestion(q.Question));
        var pending = active
            .Where(q => !answeredIds.Contains(q.Id))
            .Where(q => !ShouldSkipPollingQuestion(q, context))
            .ToList();

        if (wildcard != null && !answeredIds.Contains(wildcard.Id))
        {
            var nonWildcardAnswered = context.PollingAnswers.Count(a => a.QuestionId != wildcard.Id);
            var nonWildcardPending = pending.Where(q => q.Id != wildcard.Id).ToList();
            if (nonWildcardAnswered >= MaxDeepDiveQuestions || nonWildcardPending.Count == 0)
                return wildcard;
        }

        return pending.FirstOrDefault(q => wildcard == null || q.Id != wildcard.Id);
    }

    private static bool ShouldSkipPollingQuestion(PollingQuestionDto question, SearchContextData context)
    {
        var text = question.Question.ToLowerInvariant();

        if (!string.IsNullOrWhiteSpace(context.UrgencyPreference)
            && (text.Contains("wait time") || text.Contains("how soon do you need")))
            return true;

        if (text.Contains("gender"))
            return true;

        if (text.Contains("virtual visit") || text.Contains("telehealth"))
            return true;

        return false;
    }

    private async Task<bool> IsDeepDiveCompleteAsync(
        SearchContextData context, CancellationToken cancellationToken)
    {
        var active = await _pollingQuestions.GetActiveAsync(cancellationToken);
        var wildcard = active.FirstOrDefault(q => NuviFlowContent.IsWildcardDeepDiveQuestion(q.Question));
        if (wildcard == null)
            return context.PollingAnswers.Count >= MaxDeepDiveQuestions;

        return context.PollingAnswers.Any(a => a.QuestionId == wildcard.Id);
    }

    private async Task PrefillAgeFromPatientProfileAsync(
        SearchSession session, SearchContextData context, CancellationToken cancellationToken)
    {
        if (!context.SkipAccountCreation)
            return;

        if (!HasKnownPatientAge(context) && session.PatientId.HasValue)
        {
            var dob = await _db.Patients.AsNoTracking()
                .Where(p => p.Id == session.PatientId.Value)
                .Select(p => p.DateOfBirth)
                .FirstOrDefaultAsync(cancellationToken);

            if (dob != default)
                context.PatientDateOfBirth = dob;
        }

        if (!HasKnownPatientAge(context))
            return;

        var active = await _pollingQuestions.GetActiveAsync(cancellationToken);
        var answeredIds = context.PollingAnswers.Select(a => a.QuestionId).ToHashSet();
        var calculatedAge = CalculateAge(context.PatientDateOfBirth!.Value);

        foreach (var question in active.Where(IsPatientAgePollingQuestion))
        {
            if (answeredIds.Contains(question.Id))
                continue;

            context.PollingAnswers.Add(new PollingAnswerEntry
            {
                QuestionId = question.Id,
                Question = question.Question,
                Answer = calculatedAge.ToString()
            });
        }
    }

    private static bool IsPatientAgePollingQuestion(PollingQuestionDto question)
    {
        if (question.Question.Contains("doctor", StringComparison.OrdinalIgnoreCase))
            return false;

        return question.Question.Contains("old are you", StringComparison.OrdinalIgnoreCase)
            || question.Question.Contains("how old are you", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string>? GetPollingQuestionOptions(PollingQuestionDto question)
    {
        if (NuviFlowContent.IsWildcardDeepDiveQuestion(question.Question)
            || NuviFlowContent.IsLanguageDeepDiveQuestion(question.Question))
            return ["Yes", "No"];

        var hint = question.ValidationHint;
        if (string.IsNullOrWhiteSpace(hint))
            return null;

        if (hint.StartsWith("Required", StringComparison.OrdinalIgnoreCase))
            return null;

        if (hint.Contains("1 through 5", StringComparison.OrdinalIgnoreCase))
            return ["1", "2", "3", "4", "5"];

        if (hint.Contains('/'))
            return hint.Split('/').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

        if (!hint.Contains(',') && hint.Contains(" or ", StringComparison.OrdinalIgnoreCase))
            return hint.Split(" or ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();

        if (!hint.Contains(','))
            return null;

        return hint.Split(',')
            .Select(s => s.Trim())
            .Select(s => s.StartsWith("or ", StringComparison.OrdinalIgnoreCase) ? s[3..].Trim() : s)
            .Where(s => s.Length > 0)
            .ToList();
    }

    private static bool HasKnownPatientAge(SearchContextData context) =>
        context.PatientDateOfBirth is { } dob && !IsPlaceholderDateOfBirth(dob);

    private static bool IsPlaceholderDateOfBirth(DateOnly dateOfBirth) =>
        dateOfBirth.Year <= 1900 || dateOfBirth == PlaceholderDateOfBirth;

    private static DateOnly ApproximateDateOfBirthFromAge(int age)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var birthYear = today.Year - age;
        var month = today.Month;
        var day = today.Day;

        if (month == 2 && day == 29 && !DateTime.IsLeapYear(birthYear))
            day = 28;

        return new DateOnly(birthYear, month, day);
    }

    private async Task PersistPatientAgeFromAnswerAsync(
        SearchSession session,
        SearchContextData context,
        PollingQuestionDto question,
        string normalizedAnswer,
        CancellationToken cancellationToken)
    {
        if (!IsPatientAgePollingQuestion(question))
            return;

        if (!int.TryParse(normalizedAnswer, out var age) || age is < 1 or > 120)
            return;

        var approximateDob = ApproximateDateOfBirthFromAge(age);
        context.PatientDateOfBirth = approximateDob;

        if (!session.PatientId.HasValue)
            return;

        var patient = await _db.Patients
            .FirstOrDefaultAsync(p => p.Id == session.PatientId.Value, cancellationToken);

        if (patient == null || !IsPlaceholderDateOfBirth(patient.DateOfBirth))
            return;

        patient.DateOfBirth = approximateDob;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private static int CalculateAge(DateOnly dateOfBirth)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var age = today.Year - dateOfBirth.Year;
        if (dateOfBirth > today.AddYears(-age))
            age--;
        return age;
    }

    private static bool IsYesNoOptionSet(IReadOnlyList<string>? options)
    {
        if (options == null || options.Count is < 2 or > 3)
            return false;

        return options.All(o =>
        {
            var lower = o.Trim().ToLowerInvariant();
            return lower is "yes" or "no";
        });
    }

    private static bool IsSimpleYesNoAnswer(string answer)
    {
        var lower = answer.Trim().ToLowerInvariant();
        return lower is "yes" or "y" or "yeah" or "yep" or "yup" or "sure" or "ok" or "okay"
            or "no" or "n" or "nope" or "nah";
    }

    private static bool IsCancelBookingChip(string? message) =>
        !string.IsNullOrWhiteSpace(message)
        && string.Equals(message.Trim(), NuviFlowContent.CancelBookingChip, StringComparison.OrdinalIgnoreCase);

    private static bool IsRescheduleBookingChip(string? message) =>
        !string.IsNullOrWhiteSpace(message)
        && string.Equals(message.Trim(), NuviFlowContent.RescheduleBookingChip, StringComparison.OrdinalIgnoreCase);

    private static bool CanEnterCancelFromStage(NuviConversationStage stage) =>
        stage is NuviConversationStage.Greeting
            or NuviConversationStage.Triage
            or NuviConversationStage.Logistics
            or NuviConversationStage.MomentumBridge
            or NuviConversationStage.DeepDivePermission
            or NuviConversationStage.DeepDive
            or NuviConversationStage.RecommendationReveal
            or NuviConversationStage.DoctorExplore
            or NuviConversationStage.Confirmation
            or NuviConversationStage.Complete;

    private static IReadOnlyList<string> RegisteredQuickConcernChips() =>
    [
        "I need a dentist",
        "Tooth pain",
        "Dental implants",
        "Teeth cleaning",
        "Invisalign",
        "Emergency dental",
        NuviFlowContent.CancelBookingChip,
        NuviFlowContent.RescheduleBookingChip
    ];

    private async Task<ChatMessageResponse> BeginRescheduleBookingAsync(
        SearchSession session,
        SearchContextData context,
        CancellationToken cancellationToken)
    {
        ClearRescheduleContext(context);

        if (session.PatientId is not int patientId || patientId <= 0)
        {
            var needSignIn = "Please sign in to reschedule a booking.";
            await SaveAssistantMessageAsync(session, needSignIn, cancellationToken);
            return BuildResponse(session, context, needSignIn, stage: context.Stage);
        }

        var all = await _appointments.GetForPatientAsync(patientId, cancellationToken);
        var startOfToday = DateTime.Today;
        var upcoming = all
            .Where(a => a.StartsAt >= startOfToday
                        && AppointmentStatuses.IsActive(a.Status)
                        && a.Status != AppointmentStatuses.Completed)
            .OrderBy(a => a.StartsAt)
            .ToList();

        if (upcoming.Count == 0)
        {
            context.Stage = NuviConversationStage.Triage;
            var none = NuviFlowContent.RescheduleNoneMessage;
            await SaveAssistantMessageAsync(session, none, cancellationToken);
            return BuildResponse(
                session,
                context,
                none,
                stage: NuviConversationStage.Triage,
                options: RegisteredQuickConcernChips(),
                optionsOnly: false);
        }

        var choices = BuildAppointmentChoices(upcoming);
        context.RescheduleAppointmentChoices = choices;
        context.RescheduleStep = RescheduleBookingStep.SelectAppointment;
        context.Stage = NuviConversationStage.RescheduleBooking;

        var options = choices.Select(c => c.Label)
            .Append(NuviFlowContent.CancelBookingNeverMindOption)
            .ToList();
        var prompt = NuviFlowContent.RescheduleSelectPrompt;
        await SaveAssistantMessageAsync(session, prompt, cancellationToken);
        return BuildResponse(
            session,
            context,
            prompt,
            stage: NuviConversationStage.RescheduleBooking,
            options: options,
            optionsOnly: true);
    }

    private async Task<ChatMessageResponse> HandleRescheduleBookingAsync(
        SearchSession session,
        SearchContextData context,
        string message,
        CancellationToken cancellationToken)
    {
        return context.RescheduleStep switch
        {
            RescheduleBookingStep.SelectWindow =>
                await HandleRescheduleWindowAsync(session, context, message, cancellationToken),
            RescheduleBookingStep.ConfirmCall =>
                await HandleRescheduleConfirmCallAsync(session, context, message, cancellationToken),
            _ => await HandleRescheduleSelectAppointmentAsync(session, context, message, cancellationToken)
        };
    }

    private async Task<ChatMessageResponse> HandleRescheduleSelectAppointmentAsync(
        SearchSession session,
        SearchContextData context,
        string message,
        CancellationToken cancellationToken)
    {
        var answer = message.Trim();
        if (string.Equals(answer, NuviFlowContent.CancelBookingNeverMindOption, StringComparison.OrdinalIgnoreCase)
            || IsNoAnswer(answer))
        {
            ClearRescheduleContext(context);
            context.Stage = NuviConversationStage.Triage;
            var cancelled = "No problem — I won't reschedule anything. What else can I help with?";
            await SaveAssistantMessageAsync(session, cancelled, cancellationToken);
            return BuildResponse(
                session,
                context,
                cancelled,
                stage: NuviConversationStage.Triage,
                options: RegisteredQuickConcernChips(),
                optionsOnly: false);
        }

        var choices = context.RescheduleAppointmentChoices ?? new List<CancelAppointmentChoice>();
        var selected = MatchAppointmentChoice(choices, answer);
        if (selected == null)
        {
            var options = choices.Select(c => c.Label)
                .Append(NuviFlowContent.CancelBookingNeverMindOption)
                .ToList();
            var reprompt = "Please select one of the appointments below, or choose Never mind.";
            await SaveAssistantMessageAsync(session, reprompt, cancellationToken);
            return BuildResponse(
                session,
                context,
                reprompt,
                stage: NuviConversationStage.RescheduleBooking,
                options: options,
                optionsOnly: true);
        }

        context.RescheduleSelectedAppointmentId = selected.AppointmentId;
        context.RescheduleStep = RescheduleBookingStep.SelectWindow;
        var windowPrompt = NuviFlowContent.RescheduleWindowPrompt;
        await SaveAssistantMessageAsync(session, windowPrompt, cancellationToken);
        return BuildResponse(
            session,
            context,
            windowPrompt,
            stage: NuviConversationStage.RescheduleBooking,
            options: NuviFlowContent.LogisticsUrgencyOptions,
            optionsOnly: true);
    }

    private async Task<ChatMessageResponse> HandleRescheduleWindowAsync(
        SearchSession session,
        SearchContextData context,
        string message,
        CancellationToken cancellationToken)
    {
        var answer = message.Trim();
        var matchedWindow = NuviFlowContent.LogisticsUrgencyOptions
            .FirstOrDefault(o => string.Equals(o, answer, StringComparison.OrdinalIgnoreCase));

        if (matchedWindow == null)
        {
            var reprompt = "Please choose one of the timing options below.";
            await SaveAssistantMessageAsync(session, reprompt, cancellationToken);
            return BuildResponse(
                session,
                context,
                reprompt,
                stage: NuviConversationStage.RescheduleBooking,
                options: NuviFlowContent.LogisticsUrgencyOptions,
                optionsOnly: true);
        }

        context.RescheduleUrgencyPreference = matchedWindow;
        context.RescheduleStep = RescheduleBookingStep.ConfirmCall;

        var doctorLabel = "the same practice";
        if (context.RescheduleSelectedAppointmentId is int apptId)
        {
            var choice = context.RescheduleAppointmentChoices?
                .FirstOrDefault(c => c.AppointmentId == apptId);
            if (choice != null)
            {
                var doctorPart = choice.Label.Split('·')[0].Trim();
                if (!string.IsNullOrWhiteSpace(doctorPart) && !doctorPart.StartsWith('#'))
                    doctorLabel = doctorPart;
            }
        }

        var prompt =
            $"{NuviFlowContent.RescheduleCallPermissionPrompt} ({doctorLabel})";
        await SaveAssistantMessageAsync(session, prompt, cancellationToken);
        return BuildResponse(
            session,
            context,
            prompt,
            stage: NuviConversationStage.RescheduleBooking,
            options: NuviFlowContent.RescheduleCallPermissionOptions,
            optionsOnly: true);
    }

    private async Task<ChatMessageResponse> HandleRescheduleConfirmCallAsync(
        SearchSession session,
        SearchContextData context,
        string message,
        CancellationToken cancellationToken)
    {
        var answer = message.Trim();
        if (IsNoAnswer(answer) || string.Equals(answer, "No", StringComparison.OrdinalIgnoreCase))
        {
            ClearRescheduleContext(context);
            context.Stage = NuviConversationStage.Triage;
            var declined = "Okay — I won't call the office. You can tap Reschedule Booking anytime when you're ready.";
            await SaveAssistantMessageAsync(session, declined, cancellationToken);
            return BuildResponse(
                session,
                context,
                declined,
                stage: NuviConversationStage.Triage,
                options: RegisteredQuickConcernChips(),
                optionsOnly: false);
        }

        if (!IsYesAnswer(answer) && !string.Equals(answer, "Yes", StringComparison.OrdinalIgnoreCase))
        {
            var reprompt = "Please choose Yes or No.";
            await SaveAssistantMessageAsync(session, reprompt, cancellationToken);
            return BuildResponse(
                session,
                context,
                reprompt,
                stage: NuviConversationStage.RescheduleBooking,
                options: NuviFlowContent.RescheduleCallPermissionOptions,
                optionsOnly: true);
        }

        if (session.PatientId is not int patientId || patientId <= 0
            || context.RescheduleSelectedAppointmentId is not int appointmentId)
        {
            ClearRescheduleContext(context);
            context.Stage = NuviConversationStage.Triage;
            var err = "I couldn't start the reschedule call. Please try again.";
            await SaveAssistantMessageAsync(session, err, cancellationToken);
            return BuildResponse(
                session,
                context,
                err,
                stage: NuviConversationStage.Triage,
                options: RegisteredQuickConcernChips(),
                optionsOnly: false);
        }

        var urgency = context.RescheduleUrgencyPreference ?? NuviFlowContent.LogisticsUrgencyOptions[1];
        var result = await _appointmentReschedule.RequestRescheduleAsync(
            patientId, appointmentId, urgency, cancellationToken, session.Id);

        ClearRescheduleContext(context);
        context.Stage = NuviConversationStage.Triage;

        var text = string.IsNullOrWhiteSpace(result.Message)
            ? (result.Success
                ? "I've started calling the office to reschedule your appointment."
                : "I couldn't start the reschedule call. Please try again.")
            : result.Message;

        await SaveAssistantMessageAsync(session, text, cancellationToken);
        return BuildResponse(
            session,
            context,
            text,
            stage: NuviConversationStage.Triage,
            options: result.VoiceCallStarted ? null : RegisteredQuickConcernChips(),
            optionsOnly: false,
            conversationId: result.ConversationId,
            voiceCallStatus: result.VoiceCallStarted ? VoiceOutboundCallStatuses.Initiated : null);
    }

    private static void ClearRescheduleContext(SearchContextData context)
    {
        context.RescheduleStep = RescheduleBookingStep.None;
        context.RescheduleAppointmentChoices = null;
        context.RescheduleSelectedAppointmentId = null;
        context.RescheduleUrgencyPreference = null;
    }

    private static List<CancelAppointmentChoice> BuildAppointmentChoices(
        IReadOnlyList<PatientAppointmentDto> upcoming)
    {
        var choices = upcoming.Select(a =>
        {
            var slot = VoiceCallBookingService.FormatPstSlot(a.StartsAt, a.StartsAt.AddHours(1));
            var doctor = string.IsNullOrWhiteSpace(a.DoctorName) ? "your dentist" : a.DoctorName.Trim();
            return new CancelAppointmentChoice
            {
                AppointmentId = a.Id,
                Label = $"{doctor} · {slot}"
            };
        }).ToList();

        var dupes = choices.GroupBy(c => c.Label, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var choice in choices)
        {
            if (dupes.Contains(choice.Label))
                choice.Label = $"#{choice.AppointmentId} · {choice.Label}";
        }

        return choices;
    }

    private static CancelAppointmentChoice? MatchAppointmentChoice(
        IReadOnlyList<CancelAppointmentChoice> choices,
        string answer)
    {
        var selected = choices.FirstOrDefault(c =>
            string.Equals(c.Label, answer, StringComparison.OrdinalIgnoreCase));
        if (selected != null)
            return selected;

        if (int.TryParse(answer.TrimStart('#'), out var typedId))
            return choices.FirstOrDefault(c => c.AppointmentId == typedId);

        return null;
    }

    private async Task<ChatMessageResponse> BeginCancelBookingAsync(
        SearchSession session,
        SearchContextData context,
        CancellationToken cancellationToken)
    {
        if (session.PatientId is not int patientId || patientId <= 0)
        {
            var needSignIn = "Please sign in to cancel a booking.";
            await SaveAssistantMessageAsync(session, needSignIn, cancellationToken);
            return BuildResponse(session, context, needSignIn, stage: context.Stage);
        }

        var all = await _appointments.GetForPatientAsync(patientId, cancellationToken);
        var startOfToday = DateTime.Today;
        var upcoming = all
            .Where(a => a.StartsAt >= startOfToday
                        && AppointmentStatuses.IsActive(a.Status)
                        && a.Status != AppointmentStatuses.Completed)
            .OrderBy(a => a.StartsAt)
            .ToList();

        if (upcoming.Count == 0)
        {
            context.CancelAppointmentChoices = null;
            context.Stage = NuviConversationStage.Triage;
            var none = NuviFlowContent.CancelBookingNoneMessage;
            await SaveAssistantMessageAsync(session, none, cancellationToken);
            return BuildResponse(
                session,
                context,
                none,
                stage: NuviConversationStage.Triage,
                options: RegisteredQuickConcernChips(),
                optionsOnly: false);
        }

        var choices = upcoming.Select(a =>
        {
            var slot = VoiceCallBookingService.FormatPstSlot(a.StartsAt, a.StartsAt.AddHours(1));
            var doctor = string.IsNullOrWhiteSpace(a.DoctorName) ? "your dentist" : a.DoctorName.Trim();
            return new CancelAppointmentChoice
            {
                AppointmentId = a.Id,
                Label = $"{doctor} · {slot}"
            };
        }).ToList();

        // Disambiguate identical labels.
        var dupes = choices.GroupBy(c => c.Label, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var choice in choices)
        {
            if (dupes.Contains(choice.Label))
                choice.Label = $"#{choice.AppointmentId} · {choice.Label}";
        }

        context.CancelAppointmentChoices = choices;
        context.Stage = NuviConversationStage.CancelBooking;

        var options = choices.Select(c => c.Label)
            .Append(NuviFlowContent.CancelBookingNeverMindOption)
            .ToList();
        var prompt = NuviFlowContent.CancelBookingPrompt;
        await SaveAssistantMessageAsync(session, prompt, cancellationToken);
        return BuildResponse(
            session,
            context,
            prompt,
            stage: NuviConversationStage.CancelBooking,
            options: options,
            optionsOnly: false);
    }

    private async Task<ChatMessageResponse> HandleCancelBookingAsync(
        SearchSession session,
        SearchContextData context,
        string message,
        CancellationToken cancellationToken)
    {
        var answer = message.Trim();
        if (string.Equals(answer, NuviFlowContent.CancelBookingNeverMindOption, StringComparison.OrdinalIgnoreCase)
            || IsNoAnswer(answer))
        {
            context.CancelAppointmentChoices = null;
            context.Stage = NuviConversationStage.Triage;
            var cancelled = "No problem — I won't cancel anything. What else can I help with?";
            await SaveAssistantMessageAsync(session, cancelled, cancellationToken);
            return BuildResponse(
                session,
                context,
                cancelled,
                stage: NuviConversationStage.Triage,
                options: RegisteredQuickConcernChips(),
                optionsOnly: false);
        }

        var choices = context.CancelAppointmentChoices ?? new List<CancelAppointmentChoice>();
        var selected = choices.FirstOrDefault(c =>
            string.Equals(c.Label, answer, StringComparison.OrdinalIgnoreCase));

        if (selected == null
            && int.TryParse(answer.TrimStart('#'), out var typedId)
            && choices.Any(c => c.AppointmentId == typedId))
        {
            selected = choices.First(c => c.AppointmentId == typedId);
        }

        if (selected == null)
        {
            var options = choices.Select(c => c.Label)
                .Append(NuviFlowContent.CancelBookingNeverMindOption)
                .ToList();
            var reprompt = "Please pick one of the appointments below, or choose Never mind.";
            await SaveAssistantMessageAsync(session, reprompt, cancellationToken);
            return BuildResponse(
                session,
                context,
                reprompt,
                stage: NuviConversationStage.CancelBooking,
                options: options,
                optionsOnly: false);
        }

        if (session.PatientId is not int patientId || patientId <= 0)
        {
            context.CancelAppointmentChoices = null;
            context.Stage = NuviConversationStage.Triage;
            var needSignIn = "Please sign in to cancel a booking.";
            await SaveAssistantMessageAsync(session, needSignIn, cancellationToken);
            return BuildResponse(session, context, needSignIn, stage: NuviConversationStage.Triage);
        }

        var result = await _appointmentCancel.RequestCancelAsync(
            patientId, selected.AppointmentId, cancellationToken, session.Id);
        context.CancelAppointmentChoices = null;

        if (result.Success && result.CanceledImmediately)
        {
            context.Stage = NuviConversationStage.PostCancelNewBooking;
            var successText = NuviFlowContent.CancelSuccessNewBookingPrompt;
            await SaveAssistantMessageAsync(session, successText, cancellationToken);
            return BuildResponse(
                session,
                context,
                successText,
                stage: NuviConversationStage.PostCancelNewBooking,
                options: NuviFlowContent.YesNoOptions,
                optionsOnly: true);
        }

        context.Stage = NuviConversationStage.Triage;
        var text = string.IsNullOrWhiteSpace(result.Message)
            ? (result.Success
                ? VoiceCallBookingService.FormatCallingPracticeChat("the office")
                : "I couldn't cancel that appointment. Please try again.")
            : result.Message;

        await SaveAssistantMessageAsync(session, text, cancellationToken);
        return BuildResponse(
            session,
            context,
            text,
            stage: NuviConversationStage.Triage,
            options: result.VoiceCallStarted ? null : RegisteredQuickConcernChips(),
            optionsOnly: false,
            conversationId: result.ConversationId,
            voiceCallStatus: result.VoiceCallStarted ? VoiceOutboundCallStatuses.Initiated : null);
    }

    private async Task<ChatMessageResponse> HandlePostCancelNewBookingAsync(
        SearchSession session,
        SearchContextData context,
        string message,
        CancellationToken cancellationToken)
    {
        var answer = message.Trim();
        if (IsNoAnswer(answer) || string.Equals(answer, "No", StringComparison.OrdinalIgnoreCase))
        {
            context.Stage = NuviConversationStage.Triage;
            var declined = NuviFlowContent.PostCancelDeclineNewBooking;
            await SaveAssistantMessageAsync(session, declined, cancellationToken);
            return BuildResponse(
                session,
                context,
                declined,
                stage: NuviConversationStage.Triage,
                options: RegisteredQuickConcernChips(),
                optionsOnly: false);
        }

        if (!IsYesAnswer(answer) && !string.Equals(answer, "Yes", StringComparison.OrdinalIgnoreCase))
        {
            var reprompt = "Please choose Yes or No — do you want to start a new booking?";
            await SaveAssistantMessageAsync(session, reprompt, cancellationToken);
            return BuildResponse(
                session,
                context,
                reprompt,
                stage: NuviConversationStage.PostCancelNewBooking,
                options: NuviFlowContent.YesNoOptions,
                optionsOnly: true);
        }

        ResetBookingSearchContext(context);
        context.Stage = NuviConversationStage.Triage;
        var start = NuviFlowContent.PostCancelStartBookingPrompt;
        await SaveAssistantMessageAsync(session, start, cancellationToken);
        return BuildResponse(
            session,
            context,
            start,
            stage: NuviConversationStage.Triage,
            options: RegisteredQuickConcernChips(),
            optionsOnly: false);
    }

    private static void ResetBookingSearchContext(SearchContextData context)
    {
        context.TriageQuestionCount = 0;
        context.ImplantQualStep = 0;
        context.ImplantIntentQualified = null;
        context.ImplantTimingQualified = null;
        context.ImplantPayerType = null;
        context.ImplantFinancingQualified = null;
        context.ImplantQualificationComplete = false;
        context.LogisticsStep = 0;
        context.VisitPreference = null;
        context.UrgencyPreference = null;
        context.LocationPreference = null;
        context.InsurancePreference = null;
        context.InsuranceCategory = null;
        context.SkipDeepDive = false;
        context.DeepDiveFollowUp = DeepDiveFollowUpStep.None;
        context.LanguagePreference = null;
        context.WildcardConcern = null;
        context.PollingAnswers = new List<PollingAnswerEntry>();
        context.QuestionsAsked = 0;
        context.CurrentPollingQuestionId = null;
        context.PollingComplete = false;
        context.MatchedDoctorIds = null;
        context.RecommendedDoctorIds = new List<int>();
        context.SelectedDoctorId = null;
        context.BookingConfirmed = false;
        context.AwaitingMatchSearch = false;
        context.CallingStep = CallingConsentStep.None;
        context.CallScope = CallOfficeScope.None;
        context.CallPreference = CallOfficePreference.None;
        context.HasPriorDeepDiveAnswers = false;
        context.CancelAppointmentChoices = null;
        ClearRescheduleContext(context);
    }

    private async Task<bool> DetectCancelBookingIntentAsync(string message, CancellationToken cancellationToken)
    {
        var trimmed = message.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return false;

        if (IsCancelBookingChip(trimmed))
            return true;

        var lower = trimmed.ToLowerInvariant();
        var mentionsCancel = lower.Contains("cancel") || lower.Contains("cancelled") || lower.Contains("canceled")
            || lower.Contains("call off") || lower.Contains("don't need the appointment")
            || lower.Contains("dont need the appointment");
        if (!mentionsCancel)
            return false;

        // Clear cancel+appointment wording — no Claude needed.
        if (lower.Contains("appointment") || lower.Contains("booking") || lower.Contains("visit")
            || lower.Contains("reservation") || lower.Contains("my dentist"))
            return true;

        if (string.IsNullOrWhiteSpace(_options.ApiKey) || string.IsNullOrWhiteSpace(_options.Model))
            return false;

        var systemPrompt = """
            Classify the patient's message for a dental booking assistant.
            Return ONLY JSON: {"intent":"cancel_booking"} if they want to cancel an existing appointment/booking/visit.
            Return {"intent":"other"} for finding a dentist, symptoms, booking a NEW appointment, rescheduling, or anything else.
            Do not invent other intents.
            """;

        try
        {
            var payload = AnthropicApiHelper.BuildPayload(
                _options,
                maxTokens: 40,
                system: systemPrompt,
                messages: new[] { new { role = "user", content = trimmed } });

            using var httpRequest = AnthropicApiHelper.CreateMessageRequest(_options, payload);
            var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Cancel-intent classify failed: {Body}", TruncateForLog(responseBody, 300));
                return false;
            }

            var text = AnthropicApiHelper.ExtractTextContent(responseBody);
            if (string.IsNullOrWhiteSpace(text))
                return false;

            var jsonStart = text.IndexOf('{');
            var jsonEnd = text.LastIndexOf('}');
            if (jsonStart < 0 || jsonEnd <= jsonStart)
                return text.Contains("cancel_booking", StringComparison.OrdinalIgnoreCase);

            using var doc = JsonDocument.Parse(text[jsonStart..(jsonEnd + 1)]);
            if (doc.RootElement.TryGetProperty("intent", out var intentEl)
                && intentEl.ValueKind == JsonValueKind.String)
            {
                return string.Equals(intentEl.GetString(), "cancel_booking", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error classifying cancel booking intent");
        }

        return false;
    }

    private static string TruncateForLog(string value, int max)
        => value.Length <= max ? value : value[..max] + "…";

    private static ChatMessageResponse BuildResponse(
        SearchSession session,
        SearchContextData context,
        string text,
        NuviConversationStage? stage = null,
        IReadOnlyList<string>? options = null,
        bool showLoading = false,
        bool awaitingMatchSearch = false,
        string? followUpText = null,
        bool usePasswordInput = false,
        IReadOnlyList<DoctorDto>? doctorCards = null,
        DoctorDetailDto? selectedDoctor = null,
        bool awaitingPolling = false,
        int? pollingQuestionId = null,
        bool flowComplete = false,
        bool signedIn = false,
        IReadOnlyList<string>? languageOptions = null,
        bool awaitingLanguageSelection = false,
        bool awaitingWildcardConcern = false,
        string? pollingQuestionKind = null,
        string? inputPlaceholder = null,
        bool? optionsOnly = null,
        string? conversationId = null,
        string? callSid = null,
        int? callingDoctorId = null,
        string? callingDoctorName = null,
        string? voiceCallStatus = null)
    {
        return new ChatMessageResponse
        {
            SessionKey = session.SessionKey,
            Text = text,
            Stage = (stage ?? context.Stage).ToString(),
            Options = options,
            OptionsOnly = optionsOnly ?? IsYesNoOptionSet(options),
            ShowLoading = showLoading,
            AwaitingMatchSearch = awaitingMatchSearch,
            FollowUpText = followUpText,
            UsePasswordInput = usePasswordInput,
            SignedIn = signedIn,
            DoctorCards = doctorCards,
            SelectedDoctor = selectedDoctor,
            AwaitingPollingAnswer = awaitingPolling,
            CurrentPollingQuestionId = pollingQuestionId,
            LanguageOptions = languageOptions,
            AwaitingLanguageSelection = awaitingLanguageSelection,
            AwaitingWildcardConcern = awaitingWildcardConcern,
            PollingQuestionKind = pollingQuestionKind,
            InputPlaceholder = inputPlaceholder,
            Specialty = session.Specialty,
            Urgency = session.Urgency.ToString(),
            Notes = session.SearchNotes,
            Done = flowComplete || context.Stage == NuviConversationStage.Confirmation,
            FlowComplete = flowComplete,
            ConversationId = conversationId,
            CallSid = callSid,
            CallingDoctorId = callingDoctorId,
            CallingDoctorName = callingDoctorName,
            VoiceCallStatus = voiceCallStatus
        };
    }

    private async Task SaveAssistantMessageAsync(SearchSession session, string content, CancellationToken cancellationToken)
    {
        _db.ChatMessages.Add(new ChatMessage
        {
            SearchSessionId = session.Id,
            Role = "assistant",
            Content = content
        });
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task<List<object>> GetChatHistoryAsync(int sessionId, CancellationToken cancellationToken)
    {
        var history = await _db.ChatMessages
            .Where(m => m.SearchSessionId == sessionId)
            .OrderBy(m => m.CreatedAt)
            .Select(m => new { m.Role, m.Content })
            .ToListAsync(cancellationToken);
        return history.Select(m => (object)new { role = m.Role, content = m.Content }).ToList();
    }

    private async Task<string> GenerateGreetingEmpathyAsync(string userMessage, CancellationToken cancellationToken)
    {
        var trimmed = userMessage.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return GetGreetingEmpathyMessage(trimmed);

        if (string.IsNullOrWhiteSpace(_options.ApiKey) || string.IsNullOrWhiteSpace(_options.Model))
            return GetGreetingEmpathyMessage(trimmed);

        var systemPrompt = $"""
            You are {_branding.ChatBotName}, a warm doctor-matching concierge for {_branding.SiteName}.
            The patient just shared what's going on. Write ONLY 1–2 short sentences of genuine empathy that:
            - Echo their specific concern using their own words (e.g. if they say "elbow pain", mention elbow pain; if "missing teeth", mention missing teeth)
            - Sound human and caring — like a concierge on the phone, not a form or bot
            - Reassure them we can help find the right doctor
            Do NOT ask any questions. Do NOT mention first-time visiting. Do NOT diagnose or give medical advice.
            Emergency (chest pain, can't breathe, stroke): tell them to call 911 first, then one brief supportive sentence.
            Output plain text only — no quotes around the whole message, no bullet points.
            """;

        try
        {
            var payload = AnthropicApiHelper.BuildPayload(
                _options,
                maxTokens: 200,
                system: systemPrompt,
                messages: new[] { new { role = "user", content = trimmed } });

            using var httpRequest = AnthropicApiHelper.CreateMessageRequest(_options, payload);
            var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Greeting empathy API call failed: {Body}", responseBody);
                return GetGreetingEmpathyMessage(trimmed);
            }

            var text = AnthropicApiHelper.ExtractTextContent(responseBody).Trim();
            if (string.IsNullOrWhiteSpace(text) || text.Length > 500)
                return GetGreetingEmpathyMessage(trimmed);

            return text.Trim('"', '“', '”');
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating greeting empathy");
            return GetGreetingEmpathyMessage(trimmed);
        }
    }

    private static string GetGreetingEmpathyMessage(string userMessage)
    {
        var lower = userMessage.ToLowerInvariant();
        if (lower.Contains("skin") || lower.Contains("rash") || lower.Contains("acne"))
            return "I hear you — skin issues can be really uncomfortable, and we can absolutely help you find the right dermatologist.";
        if (lower.Contains("tooth") || lower.Contains("teeth") || lower.Contains("dental") || lower.Contains("dentist") || lower.Contains("gum"))
            return "I'm really glad you reached out — tooth pain and dental issues are absolutely something we can help you address.";
        if (lower.Contains("back") || lower.Contains("knee") || lower.Contains("joint") || lower.Contains("bone") || lower.Contains("spine"))
            return "That sounds really frustrating — back and joint pain can wear you down, and we're here to help you find the right specialist.";
        if (lower.Contains("heart") || lower.Contains("cardio"))
            return "I understand — heart concerns can be worrying, and we'll help you find someone who can take good care of you.";
        if (lower.Contains("anxiety") || lower.Contains("depression") || lower.Contains("mental"))
            return "Thank you for sharing that — it takes courage, and we can help you find the right support.";
        if (lower.Contains("eye") || lower.Contains("vision"))
            return "Eye concerns are worth taking seriously — we can help you find the right specialist.";
        if (lower.Contains("elbow") || lower.Contains("shoulder") || lower.Contains("wrist"))
            return "That kind of pain is no fun — we can help you find someone who specializes in exactly that.";
        if (IsVagueUserMessage(userMessage))
            return "Got it — that sounds like something worth addressing, and we're here to help you find the right doctor.";
        return $"Thanks for sharing — I hear you, and we can help you find the right doctor for what you're dealing with.";
    }

    private async Task<string> GetInitialHealthConcernAsync(int sessionId, CancellationToken cancellationToken)
    {
        var messages = await GetAllUserMessagesAsync(sessionId, cancellationToken);
        foreach (var msg in messages)
        {
            if (msg == RedactedPasswordPlaceholder)
                continue;
            if (IsYesAnswer(msg) || IsNoAnswer(msg) || TryParseFirstVisitAnswer(msg, out _))
                continue;
            if (msg.Contains('@') && !msg.Contains(' '))
                continue;
            return msg;
        }

        return messages.FirstOrDefault(m => m != RedactedPasswordPlaceholder) ?? string.Empty;
    }

    private static string GetTriageCompletionEmpathy(string latestMessage)
    {
        var lower = latestMessage.Trim().ToLowerInvariant();
        if (lower is "both" or "all" or "all of the above" || lower.Contains("both"))
            return "Got it — let's make sure we get you matched with the right person to handle both.";
        if (lower.Contains("pain") && (lower.Contains("first") || lower.Contains("asap") || lower.Contains("soon")))
            return "Got it — let's find someone who can get you relief soon.";
        if (lower.Contains("long") || lower.Contains("ongoing") || lower.Contains("manage"))
            return "Got it — I'll look for someone who can support you long-term.";
        return "That's really helpful — I have a good sense of what you need.";
    }

    private static string GetFollowUpQuestion(string userMessage, int turnCount)
    {
        var lower = userMessage.ToLowerInvariant();

        if (turnCount <= 1)
        {
            if (IsVagueUserMessage(userMessage))
                return "Got it — can you tell me a bit more about what's going on, or what kind of doctor you're hoping to find?";
            if (lower.Contains("back") || lower.Contains("spine"))
                return "That sounds really frustrating — ongoing back pain is exhausting. Are you looking for someone to help manage it long-term, or would you like it properly evaluated first?";
            if (lower.Contains("tooth") || lower.Contains("teeth") || lower.Contains("dental") || lower.Contains("dentist") || lower.Contains("gum"))
                return "That sounds really frustrating — tooth pain and missing teeth can affect so much of your daily life. Are you looking to get the pain taken care of first, focus on replacing missing teeth, or both?";
            if (lower.Contains("skin") || lower.Contains("rash") || lower.Contains("acne"))
                return "I hear you — skin issues can be really stressful. Are you looking for a quick evaluation, or someone to help manage this longer-term?";
            if (lower.Contains("anxiety") || lower.Contains("depression") || lower.Contains("mental"))
                return "Thank you for sharing that — it takes courage. Are you looking for ongoing support, or more of an initial evaluation to figure out next steps?";
            return "Thanks for sharing — I want to make sure we find the right fit. Are you looking for someone to help manage this long-term, or would you like it properly evaluated first?";
        }

        if (turnCount == 2)
            return "Do you already have a specialty in mind, or would you like my recommendation?";

        return "Is there anything else that would help me find the best doctor for you — like timing, or what matters most in a provider?";
    }

    private static bool IsDoctorCardOnlyRequest(ChatMessageRequest request) =>
        request.SelectedDoctorId.HasValue
        && (string.IsNullOrWhiteSpace(request.Message)
            || string.Equals(request.Message.Trim(), "continue", StringComparison.OrdinalIgnoreCase));

    private static bool LooksLikeAlreadySeeingQuestion(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var lower = text.ToLowerInvariant();
        string[] patterns =
        [
            "already seeing", "already have a dentist", "already have a doctor",
            "currently seeing", "currently working with", "working with a dentist",
            "working with a doctor", "do you have a dentist", "do you have a doctor",
            "are you seeing someone", "existing dentist", "existing doctor",
            "first visit for this"
        ];
        return patterns.Any(lower.Contains);
    }

    private static bool LooksLikeDiagnosticQuestion(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var lower = text.ToLowerInvariant();
        string[] patterns =
        [
            "sharp pain", "dull pain", "throbbing", "stabbing", "radiat", "is it sharp",
            "sharp or", "constant or", "come and go",
            "swelling", "numbness", "tingling", "hot or cold", "worse when",
            "better when", "rate your pain", "pain scale", "how severe",
            "any fever", "any bleeding", "describe the pain", "type of pain",
            "location of the pain", "on a scale", "diagnos",
            "come up recently", "come on recently", "something new", "ongoing issue", "bothering you for a while",
            "building up", "building up for a while", "for a while",
            "just started recently", "started recently", "how long has", "when did it start", "when did this start",
            "been going on", "first noticed", "spread", "itchy", "oozing", "blister"
        ];
        return patterns.Any(lower.Contains);
    }

    private static string BuildNotesFromConversation(IEnumerable<string> userMessages)
    {
        var text = string.Join(" ", userMessages).ToLowerInvariant();
        if (text.Contains("anxious") || text.Contains("nervous"))
            return "Patient prefers a gentle, reassuring approach";
        return "Based on your description";
    }

    private static string FormatDeepDiveWelcome(string displayName) =>
        $"{displayName}, {NuviFlowContent.DeepDiveWelcomeSuffix}";

    private static string PersonalizePollingQuestion(string question, SearchSession session)
    {
        if (question.Contains("accept your insurance", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(session.InsurancePlanText))
        {
            return $"You mentioned {session.InsurancePlanText}. Shall I only show doctors who accept it?";
        }

        return question;
    }

    private static bool IsVagueUserMessage(string message)
    {
        var lower = message.Trim().ToLowerInvariant();
        if (lower.Length > 80)
            return false;

        string[] vaguePhrases =
        [
            "need a doctor", "new doctor", "find a doctor", "not feeling well",
            "health issues", "medical help", "see someone", "not sure"
        ];
        return vaguePhrases.Any(lower.Contains)
            && !lower.Contains("pain") && !lower.Contains("rash") && !lower.Contains("tooth");
    }

    private static string MapUrgencyToAvailability(string answer)
    {
        var lower = answer.ToLowerInvariant();
        if (lower.Contains("asap") || lower.Contains("this week")) return "asap";
        if (lower.Contains("month")) return "week";
        if (lower.Contains("explor")) return "flexible";
        if (lower.Contains("soon")) return "asap";
        if (lower.Contains("virtual") || lower.Contains("telehealth")) return "telehealth";
        return "flexible";
    }

    private async Task<List<string>> GetAllUserMessagesAsync(int sessionId, CancellationToken cancellationToken) =>
        await _db.ChatMessages
            .Where(m => m.SearchSessionId == sessionId && m.Role == "user")
            .OrderBy(m => m.CreatedAt)
            .Select(m => m.Content)
            .ToListAsync(cancellationToken);

    private async Task<SearchSession> GetOrCreateSessionAsync(Guid? sessionKey, CancellationToken cancellationToken)
    {
        if (sessionKey.HasValue)
        {
            var existing = await _db.SearchSessions.FirstOrDefaultAsync(s => s.SessionKey == sessionKey.Value, cancellationToken);
            if (existing != null)
                return existing;
        }

        var session = new SearchSession();
        _db.SearchSessions.Add(session);
        await _db.SaveChangesAsync(cancellationToken);
        return session;
    }

    private static string InferSpecialtyFromText(string text)
    {
        var lower = text.ToLowerInvariant();
        if (lower.Contains("implant")) return "Oral Surgeon";
        if (lower.Contains("tooth") || lower.Contains("dental") || lower.Contains("dentist") || lower.Contains("gum") || lower.Contains("cavity"))
            return "General Dentist";
        if (lower.Contains("back") || lower.Contains("knee") || lower.Contains("joint") || lower.Contains("bone"))
            return "Orthopedic Surgeon";
        if (lower.Contains("skin") || lower.Contains("rash") || lower.Contains("acne"))
            return "Dermatologist";
        if (lower.Contains("heart") || lower.Contains("cardio"))
            return "Cardiologist";
        if (lower.Contains("anxiety") || lower.Contains("depression") || lower.Contains("mental"))
            return "Psychiatrist";
        return "Family Medicine";
    }

    private static bool ShouldStartImplantQualification(SearchSession session, SearchContextData context)
    {
        if (context.ImplantQualificationComplete)
            return false;

        if (!string.Equals(session.Specialty, "Oral Surgeon", StringComparison.OrdinalIgnoreCase))
            return false;

        return IsImplantIntent(session.MedicalIssuesSummary)
               || IsImplantIntent(session.SearchNotes);
    }

    private async Task PrepareImplantSessionAsync(
        SearchSession session,
        SearchContextData context,
        CancellationToken cancellationToken)
    {
        var allUserText = await GetAllUserMessagesAsync(session.Id, cancellationToken);
        session.Specialty = "Oral Surgeon";
        session.Urgency = UrgencyLevel.Routine;
        session.SearchNotes = "Dental implant inquiry";
        session.MedicalIssuesSummary = string.Join(" | ", allUserText);
        session.UpdatedAt = DateTime.UtcNow;
    }

    private static bool IsImplantConcern(string message) => IsImplantIntent(message);

    private static bool IsGuestImplantWelcomeYes(string answer) =>
        IsYesAnswer(answer) || string.Equals(answer.Trim(), "Yes", StringComparison.OrdinalIgnoreCase);

    private static bool IsGuestImplantWelcomeNo(string answer) =>
        IsNoAnswer(answer) || string.Equals(answer.Trim(), "No", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> GetImplantQualificationQuestion1Options(SearchContextData context) =>
        context.SkipAccountCreation
            ? NuviFlowContent.ImplantQualificationQuestion1ReturningOptions
            : NuviFlowContent.ImplantQualificationQuestion1Options;

    private static bool IsImplantQualificationPassAnswer(string answer)
    {
        if (IsCancelBookingChip(answer) || IsRescheduleBookingChip(answer))
            return false;

        if (string.Equals(answer, "Implants / missing teeth / denture replacement", StringComparison.OrdinalIgnoreCase))
            return true;

        return IsImplantIntentPass(answer) || IsImplantIntent(answer);
    }

    private static bool IsImplantIntent(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var lower = text.ToLowerInvariant();
        return lower.Contains("implant")
               || lower.Contains("missing teeth")
               || lower.Contains("missing tooth")
               || lower.Contains("failing teeth")
               || lower.Contains("failing tooth")
               || lower.Contains("denture")
               || lower.Contains("dentures");
    }

    private static bool IsImplantIntentPass(string answer)
        => answer.Contains("implant", StringComparison.OrdinalIgnoreCase)
           || answer.Contains("missing teeth", StringComparison.OrdinalIgnoreCase)
           || answer.Contains("denture", StringComparison.OrdinalIgnoreCase);

    private static bool IsImplantTimingPass(string answer)
        => answer.Contains("60 days", StringComparison.OrdinalIgnoreCase)
           || answer.Contains("asap", StringComparison.OrdinalIgnoreCase)
           || answer.Contains("this month", StringComparison.OrdinalIgnoreCase);

    private static bool IsImplantPayerDisqualified(string answer)
        => answer.Contains("medicaid", StringComparison.OrdinalIgnoreCase)
           || answer.Contains("medicare", StringComparison.OrdinalIgnoreCase);

    private static bool MatchesOption(IReadOnlyList<string> options, string answer)
        => options.Any(option => string.Equals(option, answer, StringComparison.OrdinalIgnoreCase));

    private static bool IsPasswordSubmission(SearchContextData context) =>
        (context.Stage == NuviConversationStage.Greeting && context.GreetingStep == 3)
        || (context.Stage == NuviConversationStage.AccountCreation
            && (context.AccountStep == AccountCreationStep.Password
                || context.AccountStep == AccountCreationStep.ConfirmPassword
                || context.AccountStep == AccountCreationStep.LoginPassword));

    private static string GetDisplayName(SearchContextData context) =>
        context.PendingFullName?.Trim()
        ?? context.PendingUsername
        ?? "there";

    private async Task ApplyAuthenticatedPatientAsync(
        SearchSession session,
        SearchContextData context,
        HttpContext? httpContext,
        CancellationToken cancellationToken)
    {
        var patientId = GetAuthenticatedPatientId(httpContext);
        if (!patientId.HasValue)
            return;

        var patient = await _db.Patients.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == patientId.Value, cancellationToken);
        if (patient == null)
            return;

        session.PatientId = patient.Id;
        context.PendingFullName ??= patient.FullName;
        context.PendingUsername ??= patient.Username;
        context.PatientDateOfBirth = patient.DateOfBirth;
        ApplyPatientPhoneToContext(context, patient.Phone);
        context.SkipAccountCreation = true;

        await LoadReturningPatientProfileAsync(session, context, patient, cancellationToken);
    }

    private async Task LoadReturningPatientProfileAsync(
        SearchSession session,
        SearchContextData context,
        Patient patient,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(patient.PreferenceProfileJson))
        {
            try
            {
                var profile = JsonSerializer.Deserialize<PatientPreferenceProfile>(
                    patient.PreferenceProfileJson,
                    SearchContextHelper.JsonOptions);

                if (profile != null)
                {
                    context.LastKnownLocation = NuviFlowContent.NormalizeSavedLocationChip(
                        profile.LocationPreference ?? string.Empty);
                    if (string.IsNullOrWhiteSpace(context.LastKnownLocation))
                        context.LastKnownLocation = null;
                    context.InsuranceCategory ??= profile.InsuranceCategory;
                    context.InsurancePreference ??= profile.InsurancePreference;
                    context.VisitPreference ??= profile.VisitPreference;
                    context.LanguagePreference ??= profile.LanguagePreference;
                    context.WildcardConcern ??= profile.WildcardConcern;

                    if (profile.DeepDiveAnswers is { Count: > 0 })
                    {
                        context.HasPriorDeepDiveAnswers = true;
                        context.SavedDeepDiveAnswers = profile.DeepDiveAnswers;
                    }
                }
            }
            catch
            {
                // Ignore malformed profile JSON.
            }
        }

        await TryLoadPriorDeepDiveFromSessionsAsync(session, context, patient.Id, cancellationToken);

        if (string.IsNullOrWhiteSpace(context.LastKnownLocation))
        {
            var lastSession = await _db.SearchSessions.AsNoTracking()
                .Where(s => s.PatientId == patient.Id && s.Location != null && s.Location != "")
                .OrderByDescending(s => s.UpdatedAt)
                .Select(s => new { s.Location, s.InsurancePlanText })
                .FirstOrDefaultAsync(cancellationToken);

            if (lastSession != null)
            {
                context.LastKnownLocation = NuviFlowContent.NormalizeSavedLocationChip(
                    lastSession.Location ?? string.Empty);
                if (string.IsNullOrWhiteSpace(context.LastKnownLocation))
                    context.LastKnownLocation = null;
                context.InsurancePreference ??= lastSession.InsurancePlanText;
            }
        }

        if (string.IsNullOrWhiteSpace(context.InsurancePreference)
            && !string.IsNullOrWhiteSpace(session.InsurancePlanText))
        {
            context.InsurancePreference = session.InsurancePlanText;
        }
    }

    private async Task TryLoadPriorDeepDiveFromSessionsAsync(
        SearchSession session,
        SearchContextData context,
        int patientId,
        CancellationToken cancellationToken)
    {
        if (context.HasPriorDeepDiveAnswers)
            return;

        var priorContextJsonList = await _db.SearchSessions.AsNoTracking()
            .Where(s => s.PatientId == patientId
                && s.Id != session.Id
                && s.SearchContextJson != null
                && s.SearchContextJson != "")
            .OrderByDescending(s => s.UpdatedAt)
            .Select(s => s.SearchContextJson)
            .Take(15)
            .ToListAsync(cancellationToken);

        foreach (var json in priorContextJsonList)
        {
            SearchContextData? prior;
            try
            {
                prior = JsonSerializer.Deserialize<SearchContextData>(json!, SearchContextHelper.JsonOptions);
            }
            catch
            {
                continue;
            }

            if (prior?.PollingAnswers is not { Count: > 0 } || !HasAttendedDeepDive(prior))
                continue;

            context.HasPriorDeepDiveAnswers = true;
            context.SavedDeepDiveAnswers = prior.PollingAnswers;
            context.LanguagePreference ??= prior.LanguagePreference;
            context.WildcardConcern ??= prior.WildcardConcern;
            return;
        }
    }

    private static bool HasAttendedDeepDive(SearchContextData context)
    {
        if (context.PollingAnswers.Count == 0)
            return false;

        if (context.PollingComplete)
            return true;

        if (context.SkipDeepDive)
            return false;

        if (HasCompletedDeepDiveAnswers(context.PollingAnswers))
            return true;

        return context.Stage is NuviConversationStage.RecommendationReveal
            or NuviConversationStage.DoctorExplore
            or NuviConversationStage.CallingConsent
            or NuviConversationStage.CallingOffices
            or NuviConversationStage.BookingInitiation
            or NuviConversationStage.Confirmation
            or NuviConversationStage.Complete;
    }

    private static bool HasCompletedDeepDiveAnswers(IReadOnlyList<PollingAnswerEntry> answers)
    {
        if (answers.Any(a => NuviFlowContent.IsWildcardDeepDiveQuestion(a.Question)))
            return true;

        return answers.Count >= MaxDeepDiveQuestions;
    }

    private static int? GetAuthenticatedPatientId(HttpContext? httpContext)
    {
        if (httpContext?.User.Identity?.IsAuthenticated != true)
            return null;
        if (!httpContext.User.IsInRole(AuthRoles.Patient))
            return null;

        var idClaim = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return int.TryParse(idClaim, out var id) ? id : null;
    }

    private static UrgencyLevel ParseUrgency(string urgency) =>
        urgency.ToLowerInvariant() switch
        {
            "urgent" => UrgencyLevel.Urgent,
            "emergency" => UrgencyLevel.Emergency,
            _ => UrgencyLevel.Routine
        };

    private async Task<ChatMessageResponse?> TryValidateIncomingMessageAsync(
        SearchSession session,
        SearchContextData context,
        string message,
        CancellationToken cancellationToken)
    {
        if (context.Stage == NuviConversationStage.Greeting)
            return await TryValidateGreetingMessageAsync(session, context, message, cancellationToken);

        var stage = context.Stage;

        if (stage is not (NuviConversationStage.Triage or NuviConversationStage.Logistics))
            return null;

        var trimmed = message.Trim();
        var lastAssistantMessage = await GetLastAssistantMessageAsync(session.Id, cancellationToken);
        var (question, hint, options) = stage == NuviConversationStage.Triage
            ? GetTriageValidationTarget(lastAssistantMessage)
            : GetLogisticsValidationTarget(context);

        // Skip AI validation for button-choice (yes/no) questions and clear yes/no replies —
        // buttons already constrain the input, so re-validating just causes frustrating loops.
        if (IsYesNoOptionSet(options) || IsSimpleYesNoAnswer(trimmed))
        {
            context.PendingNormalizedAnswer = trimmed;
            return null;
        }

        var conversationContext = stage == NuviConversationStage.Triage
            ? lastAssistantMessage ?? NuviFlowContent.GreetingMessage
            : lastAssistantMessage ?? question;

        var validation = await _validationService.ValidateAnswerAsync(
            question, trimmed, hint, conversationContext, cancellationToken);

        if (!validation.IsValid)
        {
            var reprompt = validation.RepromptMessage ?? $"Could you try again? {question}";
            await SaveAssistantMessageAsync(session, reprompt, cancellationToken);
            return BuildResponse(session, context, reprompt, stage: stage, options: options);
        }

        context.PendingNormalizedAnswer = validation.NormalizedAnswer ?? trimmed;
        return null;
    }

    private async Task<ChatMessageResponse?> TryValidateGreetingMessageAsync(
        SearchSession session,
        SearchContextData context,
        string message,
        CancellationToken cancellationToken)
    {
        if (context.GreetingStep is not (1 or 2))
            return null;

        var trimmed = message.Trim();

        if (context.GreetingStep == 1)
        {
            if (!TryParseFirstVisitAnswer(trimmed, out var isFirstVisit))
            {
                var reprompt = "Please choose Yes or No — is this your first time visiting us?";
                await SaveAssistantMessageAsync(session, reprompt, cancellationToken);
                return BuildResponse(session, context, reprompt, stage: NuviConversationStage.Greeting,
                    options: NuviFlowContent.FirstVisitOptions);
            }

            context.PendingNormalizedAnswer = isFirstVisit ? "Yes" : "No";
            return null;
        }

        var (question, hint, options) = (
            NuviFlowContent.ReturningUsernameQuestion,
            "a username or email address",
            (IReadOnlyList<string>?)null);

        var validation = await _validationService.ValidateAnswerAsync(
            question, trimmed, hint, question, cancellationToken);

        if (!validation.IsValid)
        {
            var reprompt = validation.RepromptMessage ?? $"Could you try again? {question}";
            await SaveAssistantMessageAsync(session, reprompt, cancellationToken);
            return BuildResponse(session, context, reprompt, stage: NuviConversationStage.Greeting, options: options);
        }

        context.PendingNormalizedAnswer = validation.NormalizedAnswer ?? trimmed;
        return null;
    }

    private async Task<string?> GetLastAssistantMessageAsync(int sessionId, CancellationToken cancellationToken) =>
        await _db.ChatMessages
            .Where(m => m.SearchSessionId == sessionId && m.Role == "assistant")
            .OrderByDescending(m => m.CreatedAt)
            .Select(m => m.Content)
            .FirstOrDefaultAsync(cancellationToken);

    private static (string Question, string Hint, IReadOnlyList<string>? Options) GetTriageValidationTarget(
        string? lastAssistantMessage)
    {
        if (string.IsNullOrWhiteSpace(lastAssistantMessage))
        {
            return (
                NuviFlowContent.GreetingMessage,
                "what health concern, symptoms, or type of doctor they are looking for",
                null);
        }

        return (
            lastAssistantMessage,
            "their care goals, timing, specialty preference, or health situation — at least one clear point is enough",
            null);
    }

    private static (string Question, string Hint, IReadOnlyList<string>? Options) GetLogisticsValidationTarget(
        SearchContextData context) =>
        context.LogisticsStep switch
        {
            0 when IsReturningWithSavedLocation(context) => (
                NuviFlowContent.LogisticsLocationQuestion,
                "their ZIP code in Houston (including the suggested previous ZIP chip), or skip",
                NuviFlowContent.FormatLogisticsLocationOptionsWithSaved(context.LastKnownLocation!)),
            0 => (
                NuviFlowContent.LogisticsLocationQuestion,
                "their ZIP code in Houston, or skip if they prefer not to share it",
                NuviFlowContent.LogisticsLocationOptions),
            LogisticsStepNewLocation => (
                NuviFlowContent.LogisticsLocationQuestion,
                "their ZIP code in Houston, or skip if they prefer not to share it",
                NuviFlowContent.LogisticsLocationOptions),
            1 => (
                NuviFlowContent.LogisticsInsuranceTypeQuestion,
                "whether they have insurance, want self-pay/cash-pay, or are not sure yet",
                NuviFlowContent.LogisticsInsuranceTypeOptions),
            2 => (
                NuviFlowContent.LogisticsInsurancePlanQuestion,
                "an insurance plan name, or skip if they are unsure",
                NuviFlowContent.LogisticsInsurancePlanOptions),
            3 => (
                NuviFlowContent.LogisticsUrgencyQuestion,
                "how soon they want to be seen: ASAP/this week, within a month, no rush, or just exploring",
                NuviFlowContent.LogisticsUrgencyOptions),
            _ => (
                NuviFlowContent.LogisticsUrgencyQuestion,
                "how soon they want to be seen",
                NuviFlowContent.LogisticsUrgencyOptions)
        };
}
