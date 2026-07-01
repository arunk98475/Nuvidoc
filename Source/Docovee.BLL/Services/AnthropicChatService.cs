using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Docovee.BLL.Auth;
using Docovee.BLL.Configuration;
using Docovee.BLL.Data;
using Docovee.BLL.Models;
using Docovee.DS;
using Docovee.DS.Entities;
using Docovee.DS.Enums;
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
        IPatientDoctorContactService patientDoctorContacts)
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
        - "Is this something new, or have you already been working with a doctor on it?"
        - "Do you already have a specialty in mind, or would you like my recommendation?"
        - For tooth/dental issues: acknowledge and move toward matching — e.g. "Sounds like you're looking for a dentist — let's get you taken care of."

        Do NOT ask how soon they want to be seen or about urgency — that is asked later in logistics.

        EXAMPLE tone:
        "That sounds really frustrating — ongoing back pain is exhausting. Are you looking for someone to help manage it long-term, or would you like it properly evaluated first?"

        FORBIDDEN — never ask about:
        - Pain quality (sharp, dull, throbbing), severity scales, or symptom characterization
        - Swelling, fever, triggers, hot/cold sensitivity, or other clinical detail
        - Medications, test results, or anything that narrows a diagnosis

        IF the patient's message is vague (e.g. "I need a doctor", "not feeling well", "health issues") use this style of clarifying question:
        "Got it — that sounds like something worth addressing. Are you looking more for a doctor to help manage something ongoing, or do you have a specific concern you'd like evaluated?"

        If the patient already named a clear specialty or symptom (e.g. tooth pain, back pain, rash), respond with empathy and route quickly — do not ask clinical follow-ups.

        Ask ONE question per turn.

        RULES:
        - SHORT responses — empathy + one question (2–3 sentences max)
        - Warm and calm — patients may be anxious
        - ONE question per turn, never multiple
        - Do NOT output the routing signal on your first response — always ask at least one clarifying question first
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

        var effectiveMessage = request.Message ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(request.Message) && !IsPasswordSubmission(context))
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

        if (!string.IsNullOrWhiteSpace(request.Message))
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

        var response = context.Stage switch
        {
            NuviConversationStage.Greeting => await HandleGreetingAsync(session, context, effectiveMessage, cancellationToken),
            NuviConversationStage.Triage => await HandleTriageAsync(session, context, effectiveMessage, cancellationToken),
            NuviConversationStage.Logistics => await HandleLogisticsAsync(session, context, effectiveMessage, cancellationToken),
            NuviConversationStage.MomentumBridge => await HandleMomentumBridgeAsync(session, context, effectiveMessage, httpContext, cancellationToken),
            NuviConversationStage.DeepDivePermission => await HandleDeepDivePermissionAsync(session, context, effectiveMessage, httpContext, cancellationToken),
            NuviConversationStage.AccountCreation => await HandleAccountCreationAsync(session, context, effectiveMessage, httpContext, cancellationToken),
            NuviConversationStage.DeepDive => await HandleDeepDiveAsync(session, context, effectiveMessage, cancellationToken),
            NuviConversationStage.RecommendationReveal => await HandleRecommendationRevealAsync(session, context, request, cancellationToken),
            NuviConversationStage.DoctorExplore => await HandleDoctorExploreAsync(session, context, request, cancellationToken),
            NuviConversationStage.BookingInitiation => await HandleBookingInitiationAsync(session, context, request, cancellationToken),
            NuviConversationStage.Confirmation or NuviConversationStage.Complete => BuildResponse(session, context,
                "You're all set! I'm here whenever you need to find another doctor.", flowComplete: true),
            _ => await HandleGreetingAsync(session, context, effectiveMessage, cancellationToken)
        };

        SearchContextHelper.Save(session, context);
        await _db.SaveChangesAsync(cancellationToken);
        return response;
    }

    private async Task<ChatMessageResponse> HandleGreetingAsync(
        SearchSession session, SearchContextData context, string message, CancellationToken cancellationToken)
    {
        context.Stage = NuviConversationStage.Triage;
        SearchContextHelper.Save(session, context);
        return await HandleTriageAsync(session, context, message, cancellationToken);
    }

    private async Task<ChatMessageResponse> HandleTriageAsync(
        SearchSession session, SearchContextData context, string message, CancellationToken cancellationToken)
    {
        context.TriageQuestionCount++;

        if (context.TriageQuestionCount >= 2 && await ShouldFastRouteToLogisticsAsync(session, cancellationToken))
        {
            return await CompleteTriageWithInferenceAsync(
                session, context, "Got it — let's find you the right doctor.", cancellationToken);
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
                _logger.LogInformation("Triage response looked diagnostic; substituting matching question.");
                aiText = GetFollowUpQuestion(message, context.TriageQuestionCount);
            }

            var routingMatch = RoutingRegex.Match(aiText);
            if (routingMatch.Success && context.TriageQuestionCount >= 1)
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

        return await BeginLogisticsAsync(session, context, text, cancellationToken);
    }

    private async Task<ChatMessageResponse> BeginLogisticsAsync(
        SearchSession session, SearchContextData context, string priorText, CancellationToken cancellationToken)
    {
        context.Stage = NuviConversationStage.Logistics;
        context.LogisticsStep = 0;

        var logisticsQuestion = NuviFlowContent.LogisticsVisitQuestion;
        var combined = string.IsNullOrWhiteSpace(priorText) ? logisticsQuestion : $"{priorText}\n\n{logisticsQuestion}";

        await SaveAssistantMessageAsync(session, combined, cancellationToken);
        return BuildResponse(session, context, combined, stage: NuviConversationStage.Logistics,
            options: NuviFlowContent.LogisticsVisitOptions);
    }

    private async Task<ChatMessageResponse> HandleLogisticsAsync(
        SearchSession session, SearchContextData context, string message, CancellationToken cancellationToken)
    {
        var answer = message.Trim();

        switch (context.LogisticsStep)
        {
            case 0:
                context.VisitPreference = answer;
                context.LogisticsStep = 1;
                await SaveAssistantMessageAsync(session, NuviFlowContent.LogisticsLocationQuestion, cancellationToken);
                return BuildResponse(session, context, NuviFlowContent.LogisticsLocationQuestion,
                    stage: NuviConversationStage.Logistics);

            case 1:
                context.LocationPreference = answer;
                session.Location = answer;
                context.LogisticsStep = 2;
                await SaveAssistantMessageAsync(session, NuviFlowContent.LogisticsInsuranceTypeQuestion, cancellationToken);
                return BuildResponse(session, context, NuviFlowContent.LogisticsInsuranceTypeQuestion,
                    stage: NuviConversationStage.Logistics,
                    options: NuviFlowContent.LogisticsInsuranceTypeOptions);

            case 2:
                context.InsuranceCategory = ClassifyInsuranceCategory(answer);
                if (IsInsuredCategory(context.InsuranceCategory))
                {
                    context.LogisticsStep = 3;
                    await SaveAssistantMessageAsync(session, NuviFlowContent.LogisticsInsurancePlanQuestion, cancellationToken);
                    return BuildResponse(session, context, NuviFlowContent.LogisticsInsurancePlanQuestion,
                        stage: NuviConversationStage.Logistics,
                        options: NuviFlowContent.LogisticsInsurancePlanOptions);
                }

                context.InsurancePreference = context.InsuranceCategory == "self-pay" ? "Self-pay" : null;
                session.InsurancePlanText = context.InsuranceCategory == "self-pay" ? null : session.InsurancePlanText;
                context.LogisticsStep = 4;
                await SaveAssistantMessageAsync(session, NuviFlowContent.LogisticsUrgencyQuestion, cancellationToken);
                return BuildResponse(session, context, NuviFlowContent.LogisticsUrgencyQuestion,
                    stage: NuviConversationStage.Logistics,
                    options: NuviFlowContent.LogisticsUrgencyOptions);

            case 3:
                if (!answer.Contains("skip", StringComparison.OrdinalIgnoreCase))
                {
                    context.InsurancePreference = answer;
                    session.InsurancePlanText = answer;
                }
                context.LogisticsStep = 4;
                await SaveAssistantMessageAsync(session, NuviFlowContent.LogisticsUrgencyQuestion, cancellationToken);
                return BuildResponse(session, context, NuviFlowContent.LogisticsUrgencyQuestion,
                    stage: NuviConversationStage.Logistics,
                    options: NuviFlowContent.LogisticsUrgencyOptions);

            case 4:
                context.UrgencyPreference = answer;
                session.AvailabilityPreference = MapUrgencyToAvailability(answer);
                session.UpdatedAt = DateTime.UtcNow;
                return await BeginMomentumBridgeAsync(session, context, cancellationToken);

            default:
                return await BeginMomentumBridgeAsync(session, context, cancellationToken);
        }
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
        context.Stage = NuviConversationStage.MomentumBridge;
        var text = NuviFlowContent.MomentumBridgeMessage;
        await SaveAssistantMessageAsync(session, text, cancellationToken);
        return BuildResponse(session, context, text, stage: NuviConversationStage.MomentumBridge,
            showLoading: true, options: ["Yes, let's do it!"]);
    }

    private async Task<ChatMessageResponse> HandleMomentumBridgeAsync(
        SearchSession session, SearchContextData context, string message, HttpContext? httpContext, CancellationToken cancellationToken)
    {
        var lower = message.Trim().ToLowerInvariant();
        if (!lower.Contains("yes") && !lower.Contains("sure") && !lower.Contains("ok") && !lower.Contains("let"))
        {
            return BuildResponse(session, context,
                "No pressure — whenever you're ready, just say yes and we'll keep going.",
                stage: NuviConversationStage.MomentumBridge,
                options: ["Yes, let's do it!"]);
        }

        return await BeginAccountCreationAsync(session, context, cancellationToken);
    }

    private async Task<ChatMessageResponse> BeginAccountCreationAsync(
        SearchSession session, SearchContextData context, CancellationToken cancellationToken)
    {
        if (context.SkipAccountCreation)
            return await BeginDeepDivePermissionAsync(session, context, cancellationToken);

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
        return await CompleteDeepDiveAsync(session, context, cancellationToken);
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
            return await BeginDeepDivePermissionAsync(session, context, cancellationToken);

        var answer = message.Trim();

        switch (context.AccountStep)
        {
            case AccountCreationStep.Name:
                context.PendingFullName = answer;
                context.AccountStep = AccountCreationStep.Email;
                var emailPrompt = $"Nice to meet you, {answer}! What's the best email address for you?";
                await SaveAssistantMessageAsync(session, emailPrompt, cancellationToken);
                return BuildResponse(session, context, emailPrompt, stage: NuviConversationStage.AccountCreation);

            case AccountCreationStep.Email:
                if (!answer.Contains('@'))
                    return BuildResponse(session, context, "That doesn't look like an email — could you try again?", stage: NuviConversationStage.AccountCreation);

                context.PendingEmail = answer;
                context.PendingUsername = answer.Trim();
                var existingPatient = await _db.Patients
                    .AsNoTracking()
                    .FirstOrDefaultAsync(p => p.Username == answer.Trim(), cancellationToken);

                if (existingPatient != null)
                {
                    context.IsExistingAccountLogin = true;
                    context.AccountStep = AccountCreationStep.LoginPassword;
                    var loginText = "You already have an account with that email. Please enter your password and I'll sign you in.";
                    await SaveAssistantMessageAsync(session, loginText, cancellationToken);
                    return BuildResponse(session, context, loginText, stage: NuviConversationStage.AccountCreation, usePasswordInput: true);
                }

                context.AccountStep = AccountCreationStep.Phone;
                await SaveAssistantMessageAsync(session, NuviFlowContent.AccountPhoneQuestion, cancellationToken);
                return BuildResponse(session, context, NuviFlowContent.AccountPhoneQuestion, stage: NuviConversationStage.AccountCreation);

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
                    context.SkipAccountCreation = true;
                }

                context.IsExistingAccountLogin = false;
                return await BeginDeepDivePermissionAsync(session, context, cancellationToken);

            case AccountCreationStep.Phone:
                context.PendingPhone = answer;
                context.AccountStep = AccountCreationStep.Password;
                await SaveAssistantMessageAsync(session, NuviFlowContent.AccountPasswordQuestion, cancellationToken);
                return BuildResponse(session, context, NuviFlowContent.AccountPasswordQuestion, stage: NuviConversationStage.AccountCreation, usePasswordInput: true);

            case AccountCreationStep.Password:
                if (answer.Length < 8)
                    return BuildResponse(session, context, "Please choose a password with at least 8 characters.", stage: NuviConversationStage.AccountCreation, usePasswordInput: true);

                context.PendingPassword = answer;
                context.AccountStep = AccountCreationStep.ConfirmPassword;
                var confirmText = "Please confirm your password.";
                await SaveAssistantMessageAsync(session, confirmText, cancellationToken);
                return BuildResponse(session, context, confirmText, stage: NuviConversationStage.AccountCreation, usePasswordInput: true);

            case AccountCreationStep.ConfirmPassword:
                if (answer != context.PendingPassword)
                    return BuildResponse(session, context, "Passwords don't match — please try again.", stage: NuviConversationStage.AccountCreation, usePasswordInput: true);

                var registerResult = await _patientService.RegisterAsync(new PatientRegisterRequest
                {
                    SessionKey = session.SessionKey,
                    FullName = context.PendingFullName ?? "Patient",
                    Email = context.PendingEmail,
                    Phone = context.PendingPhone ?? "",
                    Username = context.PendingEmail ?? "",
                    Password = answer
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

                return await BeginDeepDivePermissionAsync(session, context, cancellationToken);

            default:
                return BuildResponse(session, context, "Let's continue — what's your name?", stage: NuviConversationStage.AccountCreation);
        }
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
            return await CompleteDeepDiveAsync(session, context, cancellationToken);
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
            return await CompleteDeepDiveAsync(session, context, cancellationToken);

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

        return await CompleteDeepDiveAsync(session, context, cancellationToken);
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
            return await CompleteDeepDiveAsync(session, context, cancellationToken);

        var nextPolling = await GetNextPollingQuestionAsync(session, context, cancellationToken);
        if (nextPolling == null)
            return await CompleteDeepDiveAsync(session, context, cancellationToken);

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

    private async Task<ChatMessageResponse> CompleteDeepDiveAsync(
        SearchSession session, SearchContextData context, CancellationToken cancellationToken)
    {
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

        var doctors = await SearchTopMatchesAsync(session, context, cancellationToken);
        context.MatchedDoctorIds = doctors.Select(d => d.Id).ToList();

        var displayName = GetDisplayName(context);
        var revealText = doctors.Count > 0
            ? $"{displayName}, based on everything you've shared, I've personally matched you with {doctors.Count} doctor{(doctors.Count == 1 ? "" : "s")} I think could be a great fit. Here's who I found — and here's WHY I think each one could be the one."
            : $"{displayName}, I couldn't find an exact match in your area right now, but I'm still here to help refine your search.";

        if (doctors.Count == 0)
        {
            await SaveAssistantMessageAsync(session, revealText, cancellationToken);
            return BuildResponse(session, context, revealText, stage: NuviConversationStage.RecommendationReveal,
                doctorCards: doctors, showLoading: false);
        }

        var loadingText = NuviFlowContent.MatchSearchLoadingMessage;
        await SaveAssistantMessageAsync(session, loadingText, cancellationToken);
        await SaveAssistantMessageAsync(session, revealText, cancellationToken);
        return BuildResponse(session, context, loadingText, stage: NuviConversationStage.RecommendationReveal,
            followUpText: revealText, doctorCards: doctors, showLoading: true);
    }

    private async Task PersistPatientPreferenceProfileAsync(
        SearchSession session, SearchContextData context, CancellationToken cancellationToken)
    {
        if (!session.PatientId.HasValue)
            return;

        var patient = await _db.Patients.FirstOrDefaultAsync(p => p.Id == session.PatientId.Value, cancellationToken);
        if (patient == null)
            return;

        var profile = new
        {
            context.VisitPreference,
            context.LocationPreference,
            context.UrgencyPreference,
            context.InsurancePreference,
            context.InsuranceCategory,
            context.LanguagePreference,
            context.WildcardConcern,
            DeepDiveAnswers = context.PollingAnswers,
            UpdatedAt = DateTime.UtcNow
        };

        patient.PreferenceProfileJson = JsonSerializer.Serialize(profile);
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task<ChatMessageResponse> HandleRecommendationRevealAsync(
        SearchSession session, SearchContextData context, ChatMessageRequest request, CancellationToken cancellationToken)
    {
        var doctorId = request.SelectedDoctorId ?? TryParseDoctorFromMessage(request.Message, context.MatchedDoctorIds);
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

        var yearsText = doctor.YearsOfPractice.HasValue ? $" for {doctor.YearsOfPractice} years" : "";
        var reviewSnippet = !string.IsNullOrWhiteSpace(doctor.SummaryOfReviews)
            ? doctor.SummaryOfReviews
            : "patients consistently say they feel completely heard";
        var niche = !string.IsNullOrWhiteSpace(doctor.Niche) ? $" {doctor.Niche}" : "";

        var intro = $"{doctor.Name} has been practicing{yearsText} and {reviewSnippet}.{niche}";
        var text = $"{intro}\n\n{string.Format(NuviFlowContent.BookingInitiationPrompt, doctor.Name)}";
        context.Stage = NuviConversationStage.BookingInitiation;

        await SaveAssistantMessageAsync(session, text, cancellationToken);
        return BuildResponse(session, context, text, stage: NuviConversationStage.BookingInitiation,
            options: ["Yes, contact their office", "Save for later", "Show my other matches"]);
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
            context.Stage = NuviConversationStage.RecommendationReveal;
            context.SelectedDoctorId = null;
            return BuildResponse(session, context, "Here are your other matches:",
                stage: NuviConversationStage.RecommendationReveal,
                doctorCards: await LoadMatchedDoctorsAsync(session, context, cancellationToken));
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
            return BuildResponse(session, context, "Which doctor would you like to contact?",
                options: ["Yes, contact their office", "Save for later", "Show my other matches"],
                stage: NuviConversationStage.BookingInitiation);

        var doctor = await _db.Doctors.AsNoTracking().FirstOrDefaultAsync(d => d.Id == doctorId.Value, cancellationToken);
        if (doctor == null)
            return BuildResponse(session, context, "I couldn't find that doctor's contact info.");

        var contactConfirmed = message.Contains("yes") || message.Contains("contact") || request.Action == "book";
        var contactText = contactConfirmed
            ? $"Done! 🎉 {doctor.Name}'s office will reach out to {context.PendingPhone ?? context.PendingEmail ?? "you"} within 1 business day to confirm your appointment. In the meantime, I've saved your other matches in your profile in case you want to compare. You're in good hands, {displayName}."
            : string.Format(NuviFlowContent.BookingInitiationPrompt, doctor.Name);

        if (contactConfirmed && session.PatientId.HasValue)
        {
            await _patientDoctorContacts.RecordContactViewAsync(
                session.PatientId.Value, doctor.Id, session.Id, cancellationToken);
        }

        context.Stage = NuviConversationStage.Confirmation;
        context.BookingConfirmed = true;
        await SaveAssistantMessageAsync(session, contactText, cancellationToken);

        return BuildResponse(session, context, contactText, stage: NuviConversationStage.Confirmation,
            selectedDoctor: contactConfirmed ? MapDoctorDetail(doctor, session) : null,
            flowComplete: contactConfirmed);
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
            Location = context.LocationPreference ?? session.Location ?? "Renton, WA",
            InsurancePlan = context.InsurancePreference,
            GenderPreference = "none",
            CommunicationStyle = session.CommunicationStyle,
            AvailabilityPreference = session.AvailabilityPreference,
            PreferredLanguage = context.LanguagePreference,
            AdditionalPreference = context.WildcardConcern
        }, cancellationToken);

        return results.Take(3).ToList();
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
            OfficePhoneNumber = d.OfficePhoneNumber,
            YearsOfPractice = d.YearsOfPractice
        }).ToList();
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

    private async Task<bool> ShouldFastRouteToLogisticsAsync(
        SearchSession session, CancellationToken cancellationToken)
    {
        var allUserText = string.Join(" ", await GetAllUserMessagesAsync(session.Id, cancellationToken));
        return HasClearSpecialtyIntent(allUserText);
    }

    private static bool HasClearSpecialtyIntent(string combinedUserText) =>
        !string.Equals(InferSpecialtyFromText(combinedUserText), "Family Medicine", StringComparison.Ordinal);

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
        dateOfBirth == PlaceholderDateOfBirth;

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

    private static ChatMessageResponse BuildResponse(
        SearchSession session,
        SearchContextData context,
        string text,
        NuviConversationStage? stage = null,
        IReadOnlyList<string>? options = null,
        bool showLoading = false,
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
        string? inputPlaceholder = null)
    {
        return new ChatMessageResponse
        {
            SessionKey = session.SessionKey,
            Text = text,
            Stage = (stage ?? context.Stage).ToString(),
            Options = options,
            ShowLoading = showLoading,
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
            FlowComplete = flowComplete
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

    private static string GetFollowUpQuestion(string userMessage, int turnCount)
    {
        var lower = userMessage.ToLowerInvariant();

        if (turnCount <= 1)
        {
            if (IsVagueUserMessage(userMessage))
                return "Got it — can you tell me a bit more about what's going on, or what kind of doctor you're hoping to find?";
            if (lower.Contains("back") || lower.Contains("spine"))
                return "That sounds really frustrating — ongoing back pain is exhausting. Are you looking for someone to help manage it long-term, or would you like it properly evaluated first?";
            if (lower.Contains("tooth") || lower.Contains("dental") || lower.Contains("dentist") || lower.Contains("gum"))
                return "That sounds really frustrating — tooth pain is no fun. Are you looking for a new dentist for this, or do you already have one you see?";
            if (lower.Contains("skin") || lower.Contains("rash") || lower.Contains("acne"))
                return "I hear you — skin issues can be really stressful. Are you looking for a quick evaluation, or someone to help manage this longer-term?";
            if (lower.Contains("anxiety") || lower.Contains("depression") || lower.Contains("mental"))
                return "Thank you for sharing that — it takes courage. Are you looking for ongoing support, or more of an initial evaluation to figure out next steps?";
            return "Thanks for sharing — I want to make sure we find the right fit. Are you looking for someone to help manage this long-term, or would you like it properly evaluated first?";
        }

        if (turnCount == 2)
            return "Is this your first visit for this, or are you already seeing someone for it?";

        return "Is this something you've dealt with before with another doctor, or would this be your first visit for it?";
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
            "location of the pain", "on a scale", "diagnos"
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

    private static bool IsPasswordSubmission(SearchContextData context) =>
        context.Stage == NuviConversationStage.AccountCreation
        && (context.AccountStep == AccountCreationStep.Password
            || context.AccountStep == AccountCreationStep.ConfirmPassword
            || context.AccountStep == AccountCreationStep.LoginPassword);

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
        context.SkipAccountCreation = true;
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
        var stage = context.Stage == NuviConversationStage.Greeting
            ? NuviConversationStage.Triage
            : context.Stage;

        if (stage is not (NuviConversationStage.Triage or NuviConversationStage.Logistics))
            return null;

        var trimmed = message.Trim();
        var lastAssistantMessage = await GetLastAssistantMessageAsync(session.Id, cancellationToken);
        var (question, hint, options) = stage == NuviConversationStage.Triage
            ? GetTriageValidationTarget(lastAssistantMessage)
            : GetLogisticsValidationTarget(context.LogisticsStep);

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

    private static (string Question, string Hint, IReadOnlyList<string>? Options) GetLogisticsValidationTarget(int step) =>
        step switch
        {
            0 => (
                NuviFlowContent.LogisticsVisitQuestion,
                "in-person only, telehealth/virtual only, or either/both",
                NuviFlowContent.LogisticsVisitOptions),
            1 => (
                NuviFlowContent.LogisticsLocationQuestion,
                "a city, ZIP code, neighborhood, or general area where they want care",
                null),
            2 => (
                NuviFlowContent.LogisticsInsuranceTypeQuestion,
                "whether they have insurance, want self-pay/cash-pay, or are not sure yet",
                NuviFlowContent.LogisticsInsuranceTypeOptions),
            3 => (
                NuviFlowContent.LogisticsInsurancePlanQuestion,
                "an insurance plan name, or skip if they are unsure",
                NuviFlowContent.LogisticsInsurancePlanOptions),
            4 => (
                NuviFlowContent.LogisticsUrgencyQuestion,
                "how soon they want to be seen: ASAP/this week, within a month, no rush, or just exploring",
                NuviFlowContent.LogisticsUrgencyOptions),
            _ => (
                NuviFlowContent.LogisticsUrgencyQuestion,
                "how soon they want to be seen",
                NuviFlowContent.LogisticsUrgencyOptions)
        };
}
