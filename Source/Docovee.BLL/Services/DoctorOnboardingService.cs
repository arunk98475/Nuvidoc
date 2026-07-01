using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using Docovee.BLL.Auth;
using Docovee.BLL.Configuration;
using Docovee.BLL.Data;
using Docovee.DS.Models;
using Docovee.DS;
using Docovee.DS.Entities;
using Docovee.logging;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Docovee.BLL.Services;

public interface IDoctorOnboardingService
{
    Task<DoctorOnboardingMessageResponse> SendMessageAsync(
        DoctorOnboardingMessageRequest request,
        HttpContext httpContext,
        CancellationToken cancellationToken = default);
}

public class DoctorOnboardingService : IDoctorOnboardingService
{
    private readonly DocoveeDbContext _db;
    private readonly IAccountRegistrationService _registration;
    private readonly IAccountAuthService _auth;
    private readonly IDocoveeLogger _logger;
    private readonly string _siteName;
    private readonly IAnthropicValidationService _validationService;

    public DoctorOnboardingService(
        DocoveeDbContext db,
        IAccountRegistrationService registration,
        IAccountAuthService auth,
        IDocoveeLogger logger,
        IOptions<SiteOptions> siteOptions,
        IAnthropicValidationService validationService)
    {
        _db = db;
        _registration = registration;
        _auth = auth;
        _logger = logger;
        _siteName = siteOptions.Value.Name;
        _validationService = validationService;
    }

    public async Task<DoctorOnboardingMessageResponse> SendMessageAsync(
        DoctorOnboardingMessageRequest request,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        var session = await GetOrCreateSessionAsync(request.SessionKey, cancellationToken);
        var context = DoctorOnboardingContextHelper.Load(session);
        var message = request.Message?.Trim() ?? string.Empty;

        await TryLoadLoggedInDoctorAsync(context, session, httpContext, cancellationToken);

        var isFreshStart = string.IsNullOrEmpty(message)
            && context.Stage == DoctorOnboardingStage.Name
            && context.CurrentQuestionIndex == 0
            && context.Answers.Count == 0
            && !context.DoctorId.HasValue;

        if (!string.IsNullOrEmpty(message) && !isFreshStart)
        {
            var response = await ProcessAnswerAsync(session, context, message, httpContext, cancellationToken);
            DoctorOnboardingContextHelper.Save(session, context);
            session.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            return response;
        }

        var intro = await BuildIntroResponseAsync(session, context, httpContext, cancellationToken);
        DoctorOnboardingContextHelper.Save(session, context);
        session.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return intro;
    }

    private async Task TryLoadLoggedInDoctorAsync(
        DoctorOnboardingContextData context,
        DoctorOnboardingSession session,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (context.DoctorId.HasValue)
            return;

        var doctorId = GetLoggedInDoctorId(httpContext);
        if (!doctorId.HasValue)
            return;

        var doctor = await _db.Doctors.FirstOrDefaultAsync(d => d.Id == doctorId.Value, cancellationToken);
        if (doctor == null)
            return;

        context.DoctorId = doctor.Id;
        context.Answers = DoctorOnboardingProgress.LoadAnswers(doctor);
        context.CurrentQuestionIndex = DoctorOnboardingProgress.ResumeQuestionIndex(doctor);
        context.Stage = DoctorOnboardingProgress.IsOnboardingComplete(doctor)
            ? DoctorOnboardingStage.Complete
            : DoctorOnboardingStage.Questions;
        context.PendingName = doctor.Name;
        context.PendingEmail = doctor.Username;
        context.PendingPhone = doctor.OfficePhoneNumber;

        session.DoctorId = doctor.Id;
    }

    private async Task<DoctorOnboardingSession> GetOrCreateSessionAsync(Guid? sessionKey, CancellationToken cancellationToken)
    {
        if (sessionKey.HasValue)
        {
            var existing = await _db.DoctorOnboardingSessions
                .FirstOrDefaultAsync(s => s.SessionKey == sessionKey.Value, cancellationToken);
            if (existing != null)
                return existing;
        }

        var session = new DoctorOnboardingSession();
        _db.DoctorOnboardingSessions.Add(session);
        await _db.SaveChangesAsync(cancellationToken);
        return session;
    }

    private async Task<DoctorOnboardingMessageResponse> BuildIntroResponseAsync(
        DoctorOnboardingSession session,
        DoctorOnboardingContextData context,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (context.Stage == DoctorOnboardingStage.Complete)
        {
            var doneText = "Your doctor profile questionnaire is already complete. Taking you to your profile…";
            await SaveAssistantMessageAsync(session, doneText, cancellationToken);
            return BuildResponse(session, context, doneText, flowComplete: true, signedIn: httpContext.User.IsInRole(AuthRoles.Doctor));
        }

        if (context.Stage == DoctorOnboardingStage.Questions && context.DoctorId.HasValue)
        {
            var doctor = await _db.Doctors.AsNoTracking()
                .FirstAsync(d => d.Id == context.DoctorId.Value, cancellationToken);
            var pct = doctor.ProfileCompletionPercent;
            var welcomeBack = $"""
                Welcome back, Dr. {GetFirstName(doctor.Name)}! 👋

                You're **{pct}%** through your profile questionnaire ({DoctorOnboardingProgress.TotalQuestions} questions total). Pick up right where you left off — your answers are saved.

                You can type **skip** on optional questions.
                """;
            await SaveAssistantMessageAsync(session, welcomeBack, cancellationToken);
            return await AskCurrentQuestionAsync(session, context, welcomeBack, cancellationToken);
        }

        if (context.Stage is DoctorOnboardingStage.Email or DoctorOnboardingStage.Phone
            or DoctorOnboardingStage.Password or DoctorOnboardingStage.ConfirmPassword)
        {
            var (resumeText, usePassword) = context.Stage switch
            {
                DoctorOnboardingStage.Email => ("Welcome back! What's your **email address**? You'll use this to log in.", false),
                DoctorOnboardingStage.Phone => ("Welcome back! What's your **phone number**?", false),
                DoctorOnboardingStage.Password => ("Welcome back! Choose a **password** for your account (at least 6 characters).", true),
                DoctorOnboardingStage.ConfirmPassword => ("Welcome back! Please **confirm your password**.", true),
                _ => ("Let's continue setting up your account.", false)
            };
            await SaveAssistantMessageAsync(session, resumeText, cancellationToken);
            return BuildResponse(session, context, resumeText, usePasswordInput: usePassword);
        }

        var welcome = $"""
            Welcome to {_siteName} doctor registration! 👋

            First I'll create your account — I need your name, email, phone number, and a password.

            Then we'll walk through our doctor profile questionnaire ({DoctorOnboardingProgress.TotalQuestions} questions). You can log in anytime to continue where you left off.

            Let's start with your **full name** (include your professional title if you'd like, e.g. Dr. Sarah Kim, MD).
            """;

        await SaveAssistantMessageAsync(session, welcome, cancellationToken);
        return BuildResponse(session, context, welcome);
    }

    private async Task<DoctorOnboardingMessageResponse> ProcessAnswerAsync(
        DoctorOnboardingSession session,
        DoctorOnboardingContextData context,
        string message,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        return context.Stage switch
        {
            DoctorOnboardingStage.Name => await HandleNameAsync(session, context, message, cancellationToken),
            DoctorOnboardingStage.Email => await HandleEmailAsync(session, context, message, cancellationToken),
            DoctorOnboardingStage.Phone => await HandlePhoneAsync(session, context, message, cancellationToken),
            DoctorOnboardingStage.Password => await HandlePasswordAsync(session, context, message, cancellationToken),
            DoctorOnboardingStage.ConfirmPassword => await HandleConfirmPasswordAsync(session, context, message, httpContext, cancellationToken),
            DoctorOnboardingStage.Questions => await HandleQuestionAnswerAsync(session, context, message, cancellationToken),
            DoctorOnboardingStage.Complete => BuildCompleteResponse(session, context, httpContext),
            _ => await HandleNameAsync(session, context, message, cancellationToken)
        };
    }

    private async Task<DoctorOnboardingMessageResponse> HandleNameAsync(
        DoctorOnboardingSession session,
        DoctorOnboardingContextData context,
        string message,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(message))
            return BuildResponse(session, context, "Please enter your full name to continue.");

        if (AnthropicValidationService.LooksLikeGibberish(message))
            return BuildResponse(session, context, "Please enter your real full name (e.g. Dr. Sarah Kim, MD).");

        var nameValidation = await _validationService.ValidateAnswerAsync(
            "What is your full name and preferred professional title?",
            message.Trim(),
            "a real person's name, optionally with a title such as Dr. or MD",
            null,
            cancellationToken);
        if (!nameValidation.IsValid)
            return BuildResponse(session, context, nameValidation.RepromptMessage ?? "Please enter your full name to continue.");

        context.PendingName = nameValidation.NormalizedAnswer ?? message.Trim();
        context.Stage = DoctorOnboardingStage.Email;
        var text = $"Thanks, **{context.PendingName}**! What's your **email address**? You'll use this to log in.";
        await SaveAssistantMessageAsync(session, text, cancellationToken);
        return BuildResponse(session, context, text);
    }

    private async Task<DoctorOnboardingMessageResponse> HandleEmailAsync(
        DoctorOnboardingSession session,
        DoctorOnboardingContextData context,
        string message,
        CancellationToken cancellationToken)
    {
        var email = message.Trim().ToLowerInvariant();
        if (!IsValidEmail(email))
            return BuildResponse(session, context, "Please enter a valid email address.");

        if (await _db.Doctors.AnyAsync(d => d.Username == email, cancellationToken))
            return BuildResponse(session, context, "An account with that email already exists. Please sign in to continue your profile, or use a different email.");

        context.PendingEmail = email;
        context.Stage = DoctorOnboardingStage.Phone;
        var text = "Great. What's your **phone number**?";
        await SaveAssistantMessageAsync(session, text, cancellationToken);
        return BuildResponse(session, context, text);
    }

    private async Task<DoctorOnboardingMessageResponse> HandlePhoneAsync(
        DoctorOnboardingSession session,
        DoctorOnboardingContextData context,
        string message,
        CancellationToken cancellationToken)
    {
        var phone = message.Trim();
        if (!IsValidPhone(phone))
            return BuildResponse(session, context, "Please enter a valid phone number (at least 7 digits).");

        context.PendingPhone = phone;
        context.Stage = DoctorOnboardingStage.Password;
        var text = "Almost there — choose a **password** for your account (at least 6 characters).";
        await SaveAssistantMessageAsync(session, text, cancellationToken);
        return BuildResponse(session, context, text, usePasswordInput: true);
    }

    private async Task<DoctorOnboardingMessageResponse> HandlePasswordAsync(
        DoctorOnboardingSession session,
        DoctorOnboardingContextData context,
        string message,
        CancellationToken cancellationToken)
    {
        if (message.Length < 6)
            return BuildResponse(session, context, "Password must be at least 6 characters. Please try again.", usePasswordInput: true);

        context.PendingPassword = message;
        context.Stage = DoctorOnboardingStage.ConfirmPassword;
        var text = "Please **confirm your password**.";
        await SaveAssistantMessageAsync(session, text, cancellationToken);
        return BuildResponse(session, context, text, usePasswordInput: true);
    }

    private async Task<DoctorOnboardingMessageResponse> HandleConfirmPasswordAsync(
        DoctorOnboardingSession session,
        DoctorOnboardingContextData context,
        string message,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (message != context.PendingPassword)
            return BuildResponse(session, context, "Passwords do not match. Please re-enter your password.", usePasswordInput: true);

        var registerRequest = new AccountRegisterRequest
        {
            AccountType = AccountType.Doctor,
            Username = context.PendingEmail!,
            Password = context.PendingPassword!,
            ConfirmPassword = context.PendingPassword!,
            DoctorName = context.PendingName!,
            OfficePhoneNumber = context.PendingPhone,
            Specialty = "Pending",
            City = "Pending",
            State = "CA",
            ZipCode = "00000"
        };

        var result = await _registration.RegisterAsync(registerRequest, cancellationToken: cancellationToken);
        if (!result.Success)
        {
            context.Stage = DoctorOnboardingStage.Email;
            context.PendingPassword = null;
            var failText = $"{result.Message}\n\nPlease enter a different email address.";
            await SaveAssistantMessageAsync(session, failText, cancellationToken);
            return BuildResponse(session, context, failText);
        }

        var doctor = await _db.Doctors.FirstAsync(d => d.Username == context.PendingEmail, cancellationToken);
        session.DoctorId = doctor.Id;
        context.DoctorId = doctor.Id;

        var loginResult = await _auth.LoginAsync(new AccountLoginRequest
        {
            AccountType = AccountType.Doctor,
            Username = context.PendingEmail!,
            Password = context.PendingPassword!
        }, httpContext, cancellationToken);

        context.Answers[1] = context.PendingName!;
        context.CurrentQuestionIndex = 1;
        context.Stage = DoctorOnboardingStage.Questions;
        await PersistProgressAsync(context, cancellationToken);

        var text = loginResult.Success
            ? $"Account created — welcome, Dr. {GetFirstName(doctor.Name)}! 🎉\n\nNow let's build your profile. I've saved your name — next up is your specialty."
            : $"Account created! Please sign in with **{context.PendingEmail}** to continue.\n\nLet's build your profile — next up is your specialty.";

        await SaveAssistantMessageAsync(session, text, cancellationToken);
        return await AskCurrentQuestionAsync(session, context, text, cancellationToken, signedIn: loginResult.Success);
    }

    private async Task<DoctorOnboardingMessageResponse> HandleQuestionAnswerAsync(
        DoctorOnboardingSession session,
        DoctorOnboardingContextData context,
        string message,
        CancellationToken cancellationToken)
    {
        var questions = DoctorOnboardingQuestions.All;
        if (context.CurrentQuestionIndex >= questions.Count)
            return await FinishQuestionnaireAsync(session, context, cancellationToken);

        var question = questions[context.CurrentQuestionIndex];
        var answer = message.Trim();

        var staticError = ValidateAnswer(question, answer);
        if (staticError != null)
            return await RepromptQuestionAsync(session, context, staticError, cancellationToken);

        var isSkip = !question.Required && answer.Equals("skip", StringComparison.OrdinalIgnoreCase);
        if (!isSkip)
        {
            var options = GetOptions(question);
            var isExactOption = options?.Any(o => o.Equals(answer, StringComparison.OrdinalIgnoreCase)) == true;
            if (!isExactOption)
            {
                var validation = await _validationService.ValidateAnswerAsync(
                    question.Question,
                    answer,
                    BuildValidationHint(question),
                    BuildQuestionContext(question, context.CurrentQuestionIndex, questions.Count),
                    cancellationToken);

                if (!validation.IsValid)
                {
                    var reprompt = validation.RepromptMessage ?? "Could you give a clearer answer?";
                    return await RepromptQuestionAsync(session, context, reprompt, cancellationToken);
                }

                answer = validation.NormalizedAnswer ?? answer;

                var optionError = ValidateAgainstOptions(question, answer);
                if (optionError != null)
                    return await RepromptQuestionAsync(session, context, optionError, cancellationToken);
            }
        }

        if (!string.IsNullOrWhiteSpace(answer) && !isSkip)
        {
            context.Answers[question.Id] = answer;
        }

        context.CurrentQuestionIndex++;
        await PersistProgressAsync(context, cancellationToken);

        if (context.CurrentQuestionIndex >= questions.Count)
            return await FinishQuestionnaireAsync(session, context, cancellationToken);

        return await AskCurrentQuestionAsync(session, context, "Got it — thanks!", cancellationToken);
    }

    private async Task<DoctorOnboardingMessageResponse> FinishQuestionnaireAsync(
        DoctorOnboardingSession session,
        DoctorOnboardingContextData context,
        CancellationToken cancellationToken)
    {
        if (!context.DoctorId.HasValue)
            return BuildResponse(session, context, "Something went wrong — please sign in and try again.");

        var doctor = await _db.Doctors.FirstAsync(d => d.Id == context.DoctorId.Value, cancellationToken);
        var carriers = await _db.InsuranceCarriers.AsNoTracking()
            .Where(c => c.IsActive)
            .ToListAsync(cancellationToken);

        var data = DoctorOnboardingMapper.BuildRegistration(
            context.Answers,
            doctor.Username!,
            string.Empty,
            carriers);
        DoctorOnboardingMapper.ApplyProfileFields(doctor, data);
        doctor.ProfileCompletionPercent = 100;
        doctor.OnboardingQuestionIndex = DoctorOnboardingQuestions.All.Count;
        doctor.OnboardingProfileJson = DoctorOnboardingProgress.SerializeAnswers(context.Answers);

        context.Stage = DoctorOnboardingStage.Complete;
        await _db.SaveChangesAsync(cancellationToken);

        var text = $"You're all set, Dr. {GetFirstName(doctor.Name)}! 🎉 Your {_siteName} profile is **100% complete**. Taking you to your profile now…";
        await SaveAssistantMessageAsync(session, text, cancellationToken);
        _logger.LogInformation("Doctor onboarding completed: {Email}", doctor.Username);

        return BuildResponse(session, context, text, flowComplete: true, signedIn: true);
    }

    private async Task PersistProgressAsync(DoctorOnboardingContextData context, CancellationToken cancellationToken)
    {
        if (!context.DoctorId.HasValue)
            return;

        var doctor = await _db.Doctors.FirstAsync(d => d.Id == context.DoctorId.Value, cancellationToken);
        doctor.OnboardingProfileJson = DoctorOnboardingProgress.SerializeAnswers(context.Answers);
        doctor.OnboardingQuestionIndex = context.CurrentQuestionIndex;
        doctor.ProfileCompletionPercent = DoctorOnboardingProgress.CalculatePercent(context.CurrentQuestionIndex);
        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task<DoctorOnboardingMessageResponse> AskCurrentQuestionAsync(
        DoctorOnboardingSession session,
        DoctorOnboardingContextData context,
        string? prefix,
        CancellationToken cancellationToken,
        bool signedIn = false)
    {
        var questions = DoctorOnboardingQuestions.All;
        var question = questions[context.CurrentQuestionIndex];
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(prefix))
            sb.AppendLine(prefix).AppendLine();

        sb.AppendLine($"**{question.Category}** — Question {context.CurrentQuestionIndex + 1} of {questions.Count}");
        if (!question.Required)
            sb.AppendLine("_(Optional — type skip to skip)_");
        sb.AppendLine();
        sb.AppendLine(question.Question);

        if (!string.IsNullOrWhiteSpace(question.OptionsHint))
        {
            sb.AppendLine();
            sb.AppendLine($"_{question.OptionsHint}_");
        }

        var text = sb.ToString().Trim();
        await SaveAssistantMessageAsync(session, text, cancellationToken);
        return BuildResponse(session, context, text, options: GetOptions(question), signedIn: signedIn);
    }

    private async Task<DoctorOnboardingMessageResponse> RepromptQuestionAsync(
        DoctorOnboardingSession session,
        DoctorOnboardingContextData context,
        string error,
        CancellationToken cancellationToken)
    {
        var questions = DoctorOnboardingQuestions.All;
        var question = questions[context.CurrentQuestionIndex];
        var sb = new StringBuilder();
        sb.AppendLine(error);
        sb.AppendLine();
        sb.AppendLine($"**{question.Category}** — Question {context.CurrentQuestionIndex + 1} of {questions.Count}");
        if (!question.Required)
            sb.AppendLine("_(Optional — type skip to skip)_");
        sb.AppendLine();
        sb.AppendLine(question.Question);
        if (!string.IsNullOrWhiteSpace(question.OptionsHint))
        {
            sb.AppendLine();
            sb.AppendLine($"_{question.OptionsHint}_");
        }

        var text = sb.ToString().Trim();
        await SaveAssistantMessageAsync(session, text, cancellationToken);
        return BuildResponse(session, context, text, options: GetOptions(question));
    }

    private DoctorOnboardingMessageResponse BuildCompleteResponse(
        DoctorOnboardingSession session,
        DoctorOnboardingContextData context,
        HttpContext httpContext) =>
        BuildResponse(session, context, "Your registration is complete.", flowComplete: true,
            signedIn: httpContext.User.IsInRole(AuthRoles.Doctor),
            profileCompletionPercent: 100);

    private DoctorOnboardingMessageResponse BuildResponse(
        DoctorOnboardingSession session,
        DoctorOnboardingContextData context,
        string text,
        IReadOnlyList<string>? options = null,
        bool usePasswordInput = false,
        bool flowComplete = false,
        bool signedIn = false,
        int? profileCompletionPercent = null)
    {
        var questions = DoctorOnboardingQuestions.All;
        var pct = profileCompletionPercent ?? (context.Stage == DoctorOnboardingStage.Questions
            ? DoctorOnboardingProgress.CalculatePercent(context.CurrentQuestionIndex)
            : context.Stage == DoctorOnboardingStage.Complete ? 100 : 0);

        return new DoctorOnboardingMessageResponse
        {
            SessionKey = session.SessionKey,
            Text = text,
            Stage = context.Stage.ToString(),
            Options = options,
            UsePasswordInput = usePasswordInput,
            FlowComplete = flowComplete,
            SignedIn = signedIn,
            QuestionNumber = context.Stage == DoctorOnboardingStage.Questions
                ? context.CurrentQuestionIndex + 1
                : null,
            TotalQuestions = context.Stage == DoctorOnboardingStage.Questions ? questions.Count : null,
            ProfileCompletionPercent = pct
        };
    }

    private static Task SaveAssistantMessageAsync(
        DoctorOnboardingSession session,
        string text,
        CancellationToken cancellationToken) => Task.CompletedTask;

    private static int? GetLoggedInDoctorId(HttpContext httpContext)
    {
        if (!httpContext.User.IsInRole(AuthRoles.Doctor))
            return null;

        var idClaim = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        return int.TryParse(idClaim, out var doctorId) ? doctorId : null;
    }

    private static string GetFirstName(string name) =>
        name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(p => !p.Equals("Dr.", StringComparison.OrdinalIgnoreCase) && !p.Equals("Dr", StringComparison.OrdinalIgnoreCase))
        ?? "there";

    private static bool IsValidEmail(string email) =>
        !string.IsNullOrWhiteSpace(email)
        && email.Contains('@')
        && email.Contains('.')
        && email.Length >= 5;

    private static bool IsValidPhone(string phone) =>
        Regex.IsMatch(phone, @"\d{7,}");

    private static string? ValidateAnswer(DoctorOnboardingQuestion question, string message)
    {
        var answer = message.Trim();
        if (!question.Required && (string.IsNullOrWhiteSpace(answer) || answer.Equals("skip", StringComparison.OrdinalIgnoreCase)))
            return null;

        if (question.Required && string.IsNullOrWhiteSpace(answer))
            return "This question is required. Please provide an answer.";

        if (!answer.Equals("skip", StringComparison.OrdinalIgnoreCase)
            && AnthropicValidationService.LooksLikeGibberish(answer))
        {
            return "That doesn't look like a valid answer — please try again.";
        }

        if (question.AnswerType.Contains("Number", StringComparison.OrdinalIgnoreCase) &&
            !question.AnswerType.Contains("Number or", StringComparison.OrdinalIgnoreCase) &&
            !Regex.IsMatch(answer, @"\d"))
        {
            return "Please include a number in your answer.";
        }

        if (question.AnswerType.Contains("URL", StringComparison.OrdinalIgnoreCase) &&
            !answer.Equals("skip", StringComparison.OrdinalIgnoreCase) &&
            !answer.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
            question.Required)
        {
            return "Please enter a valid URL starting with http:// or https://, or type skip if you don't have one.";
        }

        return null;
    }

    private static string? ValidateAgainstOptions(DoctorOnboardingQuestion question, string answer)
    {
        var options = GetOptions(question);
        if (options == null || options.Count == 0)
            return null;

        if (options.Any(o => o.Equals(answer, StringComparison.OrdinalIgnoreCase)))
            return null;

        var partial = options.FirstOrDefault(o =>
            o.Contains(answer, StringComparison.OrdinalIgnoreCase) ||
            answer.Contains(o, StringComparison.OrdinalIgnoreCase));
        if (partial != null)
            return null;

        if (options.Any(o => o.Equals("Other", StringComparison.OrdinalIgnoreCase))
            && !AnthropicValidationService.LooksLikeGibberish(answer)
            && answer.Trim().Length >= 3)
        {
            return null;
        }

        return $"Please pick one of the listed options: {question.OptionsHint}";
    }

    private static string BuildValidationHint(DoctorOnboardingQuestion question)
    {
        if (!string.IsNullOrWhiteSpace(question.OptionsHint))
        {
            if (question.AnswerType.Contains("Dropdown", StringComparison.OrdinalIgnoreCase)
                || question.AnswerType.Contains("Multi-select", StringComparison.OrdinalIgnoreCase)
                || question.AnswerType.Contains("Yes/No", StringComparison.OrdinalIgnoreCase))
            {
                return $"A sensible answer matching one of these options (or close): {question.OptionsHint}";
            }

            return question.OptionsHint;
        }

        return question.AnswerType switch
        {
            var t when t.Contains("Number", StringComparison.OrdinalIgnoreCase) => "a number or numeric value",
            var t when t.Contains("URL", StringComparison.OrdinalIgnoreCase) => "a valid URL or skip",
            var t when t.Contains("Address", StringComparison.OrdinalIgnoreCase) => "a practice address with city and state",
            _ => "a clear, relevant answer to the question"
        };
    }

    private static string BuildQuestionContext(DoctorOnboardingQuestion question, int index, int total)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{question.Category} — Question {index + 1} of {total}");
        sb.AppendLine(question.Question);
        if (!string.IsNullOrWhiteSpace(question.OptionsHint))
            sb.AppendLine(question.OptionsHint);
        return sb.ToString().Trim();
    }

    private static IReadOnlyList<string>? GetOptions(DoctorOnboardingQuestion question)
    {
        if (string.IsNullOrWhiteSpace(question.OptionsHint))
        {
            if (question.AnswerType.Contains("Yes/No", StringComparison.OrdinalIgnoreCase))
                return ["Yes", "No"];
            return null;
        }

        if (!(question.AnswerType.Contains("Dropdown", StringComparison.OrdinalIgnoreCase) ||
              question.AnswerType.Contains("Multi-select", StringComparison.OrdinalIgnoreCase) ||
              question.AnswerType.Contains("Yes/No", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var hint = question.OptionsHint;

        // Comma-separated lists (items may contain slashes, e.g. OB/GYN)
        if (hint.Contains(','))
        {
            return hint.Split(',')
                .Select(s => s.Trim())
                .Where(s => s.Length > 0 && !s.StartsWith("e.g.", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        // "Option A / Option B" style (space-delimited slashes)
        if (hint.Contains(" / ", StringComparison.Ordinal))
        {
            return hint.Split(" / ", StringSplitOptions.None)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToList();
        }

        if (hint.Equals("Yes/No", StringComparison.OrdinalIgnoreCase))
            return ["Yes", "No"];

        if (hint.Contains('/'))
        {
            return hint.Split('/')
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToList();
        }

        return null;
    }
}
