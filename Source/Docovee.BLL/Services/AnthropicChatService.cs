using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Docovee.BLL.Auth;
using Docovee.BLL.Configuration;
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
    private const int MaxDeepDiveQuestions = 8;
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
        IBrandingService branding)
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
    }

    private string TriageSystemPrompt => $"""
        You are {_branding.ChatBotName}, {_branding.SiteName}'s AI doctor-matching concierge. Your job is to understand a patient's situation deeply enough to match them with the RIGHT doctor — not just the right specialty.

        You are NOT a doctor. Never diagnose. Never recommend treatment. Only navigate.

        PHASE — TRIAGE (ask targeted follow-up questions, one at a time)
        When a patient describes their issue, respond with empathy and ask ONE clarifying question to understand:
        - Likely specialty direction
        - Whether this is new or ongoing
        - Urgency level if unclear

        Ask ONE question at a time. Keep each response to 1-2 sentences + the question.

        When you have enough context (usually after 1-2 exchanges), summarize warmly in one sentence, then output the routing signal.

        RULES:
        - SHORT responses only — 1-3 sentences max
        - Warm and calm — patients may be anxious
        - ONE question per turn, never multiple
        - If someone describes emergency symptoms (chest pain, difficulty breathing, numbness, stroke signs) — immediately say call 911, set URGENCY: emergency
        - Never diagnose, never recommend specific treatments

        Valid specialties: General Dentist, Oral Surgeon, Periodontist, Orthodontist, Family Medicine, Internal Medicine, Dermatologist, Orthopedic Surgeon, Neurologist, Cardiologist, OB/GYN, Pediatrician, Psychiatrist, Physical Therapist, Urgent Care

        ROUTING SIGNAL — output this on its own line only when ready:
        SPECIALTY: [name] | URGENCY: [routine/urgent/emergency] | NOTES: [1 sentence about patient context]
        """;

    public async Task<ChatMessageResponse> SendMessageAsync(ChatMessageRequest request, HttpContext? httpContext = null, CancellationToken cancellationToken = default)
    {
        var session = await GetOrCreateSessionAsync(request.SessionKey, cancellationToken);
        var context = SearchContextHelper.Load(session);
        await ApplyAuthenticatedPatientAsync(session, context, httpContext, cancellationToken);

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
            NuviConversationStage.Greeting => await HandleGreetingAsync(session, context, request.Message, cancellationToken),
            NuviConversationStage.Triage => await HandleTriageAsync(session, context, request.Message, cancellationToken),
            NuviConversationStage.Logistics => await HandleLogisticsAsync(session, context, request.Message, cancellationToken),
            NuviConversationStage.MomentumBridge => await HandleMomentumBridgeAsync(session, context, request.Message, httpContext, cancellationToken),
            NuviConversationStage.AccountCreation => await HandleAccountCreationAsync(session, context, request.Message, httpContext, cancellationToken),
            NuviConversationStage.DeepDive => await HandleDeepDiveAsync(session, context, request.Message, cancellationToken),
            NuviConversationStage.RecommendationReveal => await HandleRecommendationRevealAsync(session, context, request, cancellationToken),
            NuviConversationStage.DoctorExplore => await HandleDoctorExploreAsync(session, context, request, cancellationToken),
            NuviConversationStage.BookingInitiation => await HandleBookingInitiationAsync(session, context, request, cancellationToken),
            NuviConversationStage.Confirmation or NuviConversationStage.Complete => BuildResponse(session, context,
                "You're all set! I'm here whenever you need to find another doctor.", flowComplete: true),
            _ => await HandleGreetingAsync(session, context, request.Message, cancellationToken)
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
            await SaveAssistantMessageAsync(session, aiText, cancellationToken);

            var routingMatch = RoutingRegex.Match(aiText);
            if (routingMatch.Success && context.TriageQuestionCount >= 1)
                return await CompleteTriageAsync(session, context, aiText, routingMatch, cancellationToken);

            if (context.TriageQuestionCount >= MaxTriageQuestions)
                return await CompleteTriageWithInferenceAsync(session, context, aiText, cancellationToken);

            return BuildResponse(session, context, RoutingRegex.Replace(aiText, string.Empty).Trim(), stage: NuviConversationStage.Triage);
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
        if (context.TriageQuestionCount < 2)
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
        await SaveAssistantMessageAsync(session, text, cancellationToken);
        return BeginLogistics(session, context, text);
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

        await SaveAssistantMessageAsync(session, cleanText, cancellationToken);
        return BeginLogistics(session, context, cleanText);
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

        await SaveAssistantMessageAsync(session, text, cancellationToken);
        return BeginLogistics(session, context, text);
    }

    private ChatMessageResponse BeginLogistics(SearchSession session, SearchContextData context, string priorText)
    {
        context.Stage = NuviConversationStage.Logistics;
        context.LogisticsStep = 0;

        var logisticsQuestion = "Got it. Real quick — do you want someone local you can visit in person, or would virtual work too?";
        var combined = string.IsNullOrWhiteSpace(priorText) ? logisticsQuestion : $"{priorText}\n\n{logisticsQuestion}";

        return BuildResponse(session, context, combined, stage: NuviConversationStage.Logistics,
            options: ["In person", "Virtual / telehealth", "Either works"]);
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
                var urgencyText = "And how soon are you hoping to be seen?";
                await SaveAssistantMessageAsync(session, urgencyText, cancellationToken);
                return BuildResponse(session, context, urgencyText, stage: NuviConversationStage.Logistics,
                    options: ["As soon as possible", "This week", "I'm flexible"]);

            case 1:
                context.UrgencyPreference = answer;
                context.LogisticsStep = 2;
                session.AvailabilityPreference = MapUrgencyToAvailability(answer);
                var locationText = "What city or zip code are you in?";
                await SaveAssistantMessageAsync(session, locationText, cancellationToken);
                return BuildResponse(session, context, locationText, stage: NuviConversationStage.Logistics);

            case 2:
                context.LocationPreference = answer;
                session.Location = answer;
                context.LogisticsStep = 3;
                var insuranceText = "Last one — what insurance do you have, if any? (You can say 'none' or 'self-pay'.)";
                await SaveAssistantMessageAsync(session, insuranceText, cancellationToken);
                return BuildResponse(session, context, insuranceText, stage: NuviConversationStage.Logistics,
                    options: ["Aetna PPO", "Blue Cross Blue Shield", "Medicare", "No insurance / self-pay"]);

            case 3:
                context.InsurancePreference = answer;
                session.InsurancePlanText = answer.Equals("none", StringComparison.OrdinalIgnoreCase) ||
                    answer.Contains("self-pay", StringComparison.OrdinalIgnoreCase) ||
                    answer.Contains("no insurance", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : answer;
                session.UpdatedAt = DateTime.UtcNow;
                return await BeginMomentumBridgeAsync(session, context, cancellationToken);

            default:
                return await BeginMomentumBridgeAsync(session, context, cancellationToken);
        }
    }

    private async Task<ChatMessageResponse> BeginMomentumBridgeAsync(
        SearchSession session, SearchContextData context, CancellationToken cancellationToken)
    {
        context.Stage = NuviConversationStage.MomentumBridge;
        var text = "Perfect. Give me just a second… ✨ I've already identified some strong candidates — but I want to make sure these are a personal fit for YOU, not just a generic list. Can I grab a few more quick things from you?";
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

        if (context.SkipAccountCreation)
            return await BeginDeepDiveForAuthenticatedPatientAsync(session, context, cancellationToken);

        context.Stage = NuviConversationStage.AccountCreation;
        context.AccountStep = AccountCreationStep.Name;
        var text = "First — what's your name? (First name is fine.)";
        await SaveAssistantMessageAsync(session, text, cancellationToken);
        return BuildResponse(session, context, text, stage: NuviConversationStage.AccountCreation);
    }

    private async Task<ChatMessageResponse> BeginDeepDiveForAuthenticatedPatientAsync(
        SearchSession session, SearchContextData context, CancellationToken cancellationToken)
    {
        context.Stage = NuviConversationStage.DeepDive;
        var displayName = GetDisplayName(context);
        var welcomeText = $"Welcome back, {displayName}! Now let me get to know what matters most to YOU in a doctor.";
        await SaveAssistantMessageAsync(session, welcomeText, cancellationToken);
        return await AskNextDeepDiveQuestionAsync(session, context, welcomeText, cancellationToken);
    }

    private async Task<ChatMessageResponse> HandleAccountCreationAsync(
        SearchSession session, SearchContextData context, string message, HttpContext? httpContext, CancellationToken cancellationToken)
    {
        if (context.SkipAccountCreation)
            return await BeginDeepDiveForAuthenticatedPatientAsync(session, context, cancellationToken);

        var answer = message.Trim();

        switch (context.AccountStep)
        {
            case AccountCreationStep.Name:
                context.PendingFullName = answer;
                context.AccountStep = AccountCreationStep.Username;
                var usernamePrompt = $"Nice to meet you, {answer}! What username would you like for your profile?";
                await SaveAssistantMessageAsync(session, usernamePrompt, cancellationToken);
                return BuildResponse(session, context, usernamePrompt, stage: NuviConversationStage.AccountCreation);

            case AccountCreationStep.Username:
                if (string.IsNullOrWhiteSpace(answer))
                    return BuildResponse(session, context, "Please enter a username to continue.", stage: NuviConversationStage.AccountCreation);

                context.PendingUsername = answer;
                var existingPatient = await _db.Patients
                    .AsNoTracking()
                    .FirstOrDefaultAsync(p => p.Username == answer, cancellationToken);

                if (existingPatient != null)
                {
                    context.IsExistingAccountLogin = true;
                    context.AccountStep = AccountCreationStep.LoginPassword;
                    var loginText = "You already have an account. Please enter your password and I'll sign you in to your profile.";
                    await SaveAssistantMessageAsync(session, loginText, cancellationToken);
                    return BuildResponse(session, context, loginText, stage: NuviConversationStage.AccountCreation, usePasswordInput: true);
                }

                context.AccountStep = AccountCreationStep.Email;
                var emailText = "Great choice! What email should I use for your profile?";
                await SaveAssistantMessageAsync(session, emailText, cancellationToken);
                return BuildResponse(session, context, emailText, stage: NuviConversationStage.AccountCreation);

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
                context.Stage = NuviConversationStage.DeepDive;
                var signedInWelcome = $"You're signed in! Welcome back, {GetDisplayName(context)}. Now let me get to know what matters most to YOU in a doctor.";
                await SaveAssistantMessageAsync(session, signedInWelcome, cancellationToken);
                return await AskNextDeepDiveQuestionAsync(session, context, signedInWelcome, cancellationToken, signedIn: true);

            case AccountCreationStep.Email:
                if (!answer.Contains('@'))
                    return BuildResponse(session, context, "That doesn't look like an email — could you try again?", stage: NuviConversationStage.AccountCreation);
                context.PendingEmail = answer;
                context.AccountStep = AccountCreationStep.Phone;
                var phoneText = "And what's the best phone number to reach you?";
                await SaveAssistantMessageAsync(session, phoneText, cancellationToken);
                return BuildResponse(session, context, phoneText, stage: NuviConversationStage.AccountCreation);

            case AccountCreationStep.Phone:
                context.PendingPhone = answer;
                context.AccountStep = AccountCreationStep.Password;
                var passwordText = "Last thing — pick a password to save your matches.";
                await SaveAssistantMessageAsync(session, passwordText, cancellationToken);
                return BuildResponse(session, context, passwordText, stage: NuviConversationStage.AccountCreation, usePasswordInput: true);

            case AccountCreationStep.Password:
                if (answer.Length < 6)
                    return BuildResponse(session, context, "Please choose a password with at least 6 characters.", stage: NuviConversationStage.AccountCreation, usePasswordInput: true);

                var registerResult = await _patientService.RegisterAsync(new PatientRegisterRequest
                {
                    SessionKey = session.SessionKey,
                    FullName = context.PendingFullName ?? "Patient",
                    Email = context.PendingEmail,
                    Phone = context.PendingPhone ?? "",
                    Username = context.PendingUsername ?? "",
                    Password = answer
                }, cancellationToken);

                if (!registerResult.Success)
                {
                    var retryPassword = registerResult.Message?.Contains("email", StringComparison.OrdinalIgnoreCase) != true;
                    if (registerResult.Message?.Contains("already exists", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        context.IsExistingAccountLogin = true;
                        context.AccountStep = AccountCreationStep.LoginPassword;
                        var existingAccountText = "You already have an account. Please enter your password and I'll sign you in to your profile.";
                        await SaveAssistantMessageAsync(session, existingAccountText, cancellationToken);
                        return BuildResponse(session, context, existingAccountText, stage: NuviConversationStage.AccountCreation, usePasswordInput: true);
                    }

                    return BuildResponse(session, context,
                        registerResult.Message ?? "Something went wrong creating your account. Could you try a different email?",
                        stage: NuviConversationStage.AccountCreation,
                        usePasswordInput: retryPassword);
                }

                context.Stage = NuviConversationStage.DeepDive;
                var welcomeText = $"You're all set, {GetDisplayName(context)}! Now let me get to know what matters most to YOU in a doctor.";
                await SaveAssistantMessageAsync(session, welcomeText, cancellationToken);
                return await AskNextDeepDiveQuestionAsync(session, context, welcomeText, cancellationToken);

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

        var lastAssistantMessage = await _db.ChatMessages
            .Where(m => m.SearchSessionId == session.Id && m.Role == "assistant")
            .OrderByDescending(m => m.CreatedAt)
            .Select(m => m.Content)
            .FirstOrDefaultAsync(cancellationToken);

        var validation = await _validationService.ValidateAnswerAsync(
            current.Question, answer, current.ValidationHint, lastAssistantMessage, cancellationToken);

        if (!validation.IsValid)
        {
            return BuildResponse(session, context,
                validation.RepromptMessage ?? $"Could you answer again: {current.Question}",
                stage: NuviConversationStage.DeepDive,
                awaitingPolling: true,
                pollingQuestionId: current.Id);
        }

        context.PollingAnswers.Add(new PollingAnswerEntry
        {
            QuestionId = current.Id,
            Question = current.Question,
            Answer = validation.NormalizedAnswer ?? answer.Trim()
        });
        context.CurrentPollingQuestionId = null;

        await PersistPatientAgeFromAnswerAsync(session, context, current, validation.NormalizedAnswer ?? answer.Trim(), cancellationToken);

        if (context.PollingAnswers.Count >= MaxDeepDiveQuestions)
            return await CompleteDeepDiveAsync(session, context, cancellationToken);

        return await AskNextDeepDiveQuestionAsync(session, context, "Thanks!", cancellationToken);
    }

    private async Task<ChatMessageResponse> AskNextDeepDiveQuestionAsync(
        SearchSession session, SearchContextData context, string priorText, CancellationToken cancellationToken, bool signedIn = false)
    {
        var nextPolling = await GetNextPollingQuestionAsync(session, context, cancellationToken);
        if (nextPolling == null || context.PollingAnswers.Count >= MaxDeepDiveQuestions)
            return await CompleteDeepDiveAsync(session, context, cancellationToken);

        context.CurrentPollingQuestionId = nextPolling.Id;
        var displayName = GetDisplayName(context);
        var question = context.PollingAnswers.Count == 0
            ? $"{priorText}\n\n{displayName}, {nextPolling.Question}"
            : $"{priorText}\n\n{nextPolling.Question}";

        await SaveAssistantMessageAsync(session, question, cancellationToken);
        return BuildResponse(session, context, question, stage: NuviConversationStage.DeepDive,
            awaitingPolling: true, pollingQuestionId: nextPolling.Id, signedIn: signedIn);
    }

    private async Task<ChatMessageResponse> CompleteDeepDiveAsync(
        SearchSession session, SearchContextData context, CancellationToken cancellationToken)
    {
        context.Stage = NuviConversationStage.RecommendationReveal;
        context.PollingComplete = true;
        context.CurrentPollingQuestionId = null;

        ApplyDeepDivePreferences(session, context);

        var loadingText = "Give me just a moment while I match you with the best doctors… ✨";
        await SaveAssistantMessageAsync(session, loadingText, cancellationToken);

        var doctors = await SearchTopMatchesAsync(session, context, cancellationToken);
        context.MatchedDoctorIds = doctors.Select(d => d.Id).ToList();

        var displayName = GetDisplayName(context);
        var revealText = doctors.Count > 0
            ? $"{displayName}, based on everything you've shared, I've personally matched you with {doctors.Count} doctor{(doctors.Count == 1 ? "" : "s")} I think could be a great fit. Here's who I found — and here's WHY I think each one could be the one."
            : $"{displayName}, I couldn't find an exact match in your area right now, but I'm still here to help refine your search.";

        await SaveAssistantMessageAsync(session, revealText, cancellationToken);
        return BuildResponse(session, context, revealText, stage: NuviConversationStage.RecommendationReveal,
            doctorCards: doctors, showLoading: doctors.Count == 0);
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

        var detail = MapDoctorDetail(doctor, session);
        var yearsText = doctor.YearsOfPractice.HasValue ? $" for {doctor.YearsOfPractice} years" : "";
        var reviewSnippet = !string.IsNullOrWhiteSpace(doctor.SummaryOfReviews)
            ? doctor.SummaryOfReviews
            : "her patients consistently say she makes you feel completely heard";
        var niche = !string.IsNullOrWhiteSpace(doctor.Niche) ? $" {doctor.Niche}" : "";

        var text = $"{doctor.Name} has been practicing{yearsText} and {reviewSnippet}.{niche} Want me to help you get in touch with their office?";
        context.Stage = NuviConversationStage.BookingInitiation;

        await SaveAssistantMessageAsync(session, text, cancellationToken);
        return BuildResponse(session, context, text, stage: NuviConversationStage.BookingInitiation,
            selectedDoctor: detail,
            options: ["Yes, contact their office", "Save for later", "Show my other matches"]);
    }

    private async Task<ChatMessageResponse> HandleBookingInitiationAsync(
        SearchSession session, SearchContextData context, ChatMessageRequest request, CancellationToken cancellationToken)
    {
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

        var phone = doctor.OfficePhoneNumber ?? "(phone not on file)";
        var hours = "Mon–Fri 8am–5pm";
        var contactText = message.Contains("yes") || message.Contains("contact") || request.Action == "book"
            ? $"Done! 🎉 {doctor.Name}'s office can be reached at {phone} ({hours}). Tap the number below to call — they'll help you schedule. I've also saved your other matches in your profile in case you want to compare. You're in good hands, {displayName}."
            : $"Want me to connect you with {doctor.Name}'s office? They're available at {phone} ({hours}).";

        context.Stage = NuviConversationStage.Confirmation;
        context.BookingConfirmed = true;
        await SaveAssistantMessageAsync(session, contactText, cancellationToken);

        return BuildResponse(session, context, contactText, stage: NuviConversationStage.Confirmation,
            selectedDoctor: MapDoctorDetail(doctor, session), flowComplete: message.Contains("yes") || message.Contains("contact") || request.Action == "book");
    }

    private void ApplyDeepDivePreferences(SearchSession session, SearchContextData context)
    {
        foreach (var answer in context.PollingAnswers)
        {
            var q = answer.Question.ToLowerInvariant();
            var a = answer.Answer.ToLowerInvariant();

            if (q.Contains("gender"))
                session.GenderPreference = a.Contains("female") ? GenderPreference.Female
                    : a.Contains("male") ? GenderPreference.Male : GenderPreference.NoPreference;
            else if (q.Contains("communicat") || q.Contains("bedside") || q.Contains("vibe"))
                session.CommunicationStyle = a.Contains("direct") ? "direct"
                    : a.Contains("warm") || a.Contains("reassur") ? "reassuring" : "collaborative";
            else if (q.Contains("experience"))
                session.SearchNotes = (session.SearchNotes ?? "") + $" Prefers {(a.Contains("experience") || a.Contains("years") ? "experienced" : "approachable")} doctors.";
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
            GenderPreference = session.GenderPreference == GenderPreference.Male ? "male"
                : session.GenderPreference == GenderPreference.Female ? "female" : "none",
            CommunicationStyle = session.CommunicationStyle,
            AvailabilityPreference = session.AvailabilityPreference
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
        return active.FirstOrDefault(q => !answeredIds.Contains(q.Id));
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

        foreach (var question in active.Where(IsAgePollingQuestion))
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

    private static bool IsAgePollingQuestion(PollingQuestionDto question) =>
        question.ValidationHint?.Contains("age", StringComparison.OrdinalIgnoreCase) == true
        || question.Question.Contains("age", StringComparison.OrdinalIgnoreCase)
        || question.Question.Contains("old are you", StringComparison.OrdinalIgnoreCase);

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
        if (!IsAgePollingQuestion(question))
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
        bool usePasswordInput = false,
        IReadOnlyList<DoctorDto>? doctorCards = null,
        DoctorDetailDto? selectedDoctor = null,
        bool awaitingPolling = false,
        int? pollingQuestionId = null,
        bool flowComplete = false,
        bool signedIn = false)
    {
        return new ChatMessageResponse
        {
            SessionKey = session.SessionKey,
            Text = text,
            Stage = (stage ?? context.Stage).ToString(),
            Options = options,
            ShowLoading = showLoading,
            UsePasswordInput = usePasswordInput,
            SignedIn = signedIn,
            DoctorCards = doctorCards,
            SelectedDoctor = selectedDoctor,
            AwaitingPollingAnswer = awaitingPolling,
            CurrentPollingQuestionId = pollingQuestionId,
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
        var isDental = lower.Contains("tooth") || lower.Contains("dental") || lower.Contains("dentist") || lower.Contains("gum");

        if (turnCount <= 1)
        {
            if (isDental)
                return "That sounds really frustrating — tooth pain is no fun. How long has it been bothering you?";
            if (lower.Contains("back") || lower.Contains("knee") || lower.Contains("joint"))
                return "Got it. Is this something that came on suddenly, or has it been building over time?";
            if (lower.Contains("skin") || lower.Contains("rash"))
                return "I understand. How long have you noticed this, and is it getting worse?";
            return "Thanks for sharing. Can you tell me a bit more — how long has this been going on?";
        }

        return isDental
            ? "Have you noticed any swelling, or is it worse with hot or cold foods?"
            : "Are you looking for someone to help manage this long-term, or would you like it properly evaluated first?";
    }

    private static string BuildNotesFromConversation(IEnumerable<string> userMessages)
    {
        var text = string.Join(" ", userMessages).ToLowerInvariant();
        if (text.Contains("anxious") || text.Contains("nervous"))
            return "Patient prefers a gentle, reassuring approach";
        if (text.Contains("sharp") || text.Contains("severe") || text.Contains("bad"))
            return "Experiencing notable pain, may need prompt care";
        return "Based on your description";
    }

    private static string MapUrgencyToAvailability(string answer)
    {
        var lower = answer.ToLowerInvariant();
        if (lower.Contains("soon") || lower.Contains("asap")) return "asap";
        if (lower.Contains("week")) return "week";
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
}
