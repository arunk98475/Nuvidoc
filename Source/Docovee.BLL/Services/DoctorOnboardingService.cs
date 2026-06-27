using System.Text;
using System.Text.RegularExpressions;
using Docovee.BLL.Configuration;
using Docovee.BLL.Data;
using Docovee.BLL.Models;
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

    public DoctorOnboardingService(
        DocoveeDbContext db,
        IAccountRegistrationService registration,
        IAccountAuthService auth,
        IDocoveeLogger logger,
        IOptions<SiteOptions> siteOptions)
    {
        _db = db;
        _registration = registration;
        _auth = auth;
        _logger = logger;
        _siteName = siteOptions.Value.Name;
    }

    public async Task<DoctorOnboardingMessageResponse> SendMessageAsync(
        DoctorOnboardingMessageRequest request,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        var session = await GetOrCreateSessionAsync(request.SessionKey, cancellationToken);
        var context = DoctorOnboardingContextHelper.Load(session);
        var message = request.Message?.Trim() ?? string.Empty;
        var isStart = string.IsNullOrEmpty(message) && context.Stage == DoctorOnboardingStage.Questions
            && context.CurrentQuestionIndex == 0 && context.Answers.Count == 0;

        if (!string.IsNullOrEmpty(message) && !isStart)
        {
            var response = await ProcessAnswerAsync(session, context, message, httpContext, cancellationToken);
            DoctorOnboardingContextHelper.Save(session, context);
            session.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            return response;
        }

        var intro = await BuildIntroResponseAsync(session, context, cancellationToken);
        DoctorOnboardingContextHelper.Save(session, context);
        session.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return intro;
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
        CancellationToken cancellationToken)
    {
        var welcome = $"""
            Welcome to {_siteName} doctor registration! 👋

            I'll walk you through our doctor profile questionnaire — about {DoctorOnboardingQuestions.All.Count} questions. This usually takes 10–15 minutes.

            You can type **skip** on optional questions.

            Let's get started!
            """;

        await SaveAssistantMessageAsync(session, welcome, cancellationToken);
        return await AskCurrentQuestionAsync(session, context, welcome, cancellationToken);
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
            DoctorOnboardingStage.Questions => await HandleQuestionAnswerAsync(session, context, message, cancellationToken),
            DoctorOnboardingStage.Username => await HandleUsernameAsync(session, context, message, cancellationToken),
            DoctorOnboardingStage.Password => await HandlePasswordAsync(session, context, message, cancellationToken),
            DoctorOnboardingStage.ConfirmPassword => await HandleConfirmPasswordAsync(session, context, message, httpContext, cancellationToken),
            DoctorOnboardingStage.Complete => BuildCompleteResponse(session, context),
            _ => await HandleQuestionAnswerAsync(session, context, message, cancellationToken)
        };
    }

    private async Task<DoctorOnboardingMessageResponse> HandleQuestionAnswerAsync(
        DoctorOnboardingSession session,
        DoctorOnboardingContextData context,
        string message,
        CancellationToken cancellationToken)
    {
        var questions = DoctorOnboardingQuestions.All;
        if (context.CurrentQuestionIndex >= questions.Count)
            return await BeginCredentialsAsync(session, context, cancellationToken);

        var question = questions[context.CurrentQuestionIndex];
        var validationError = ValidateAnswer(question, message);
        if (validationError != null)
            return await RepromptQuestionAsync(session, context, validationError, cancellationToken);

        if (!string.IsNullOrWhiteSpace(message) &&
            (!message.Equals("skip", StringComparison.OrdinalIgnoreCase) || question.Required))
        {
            context.Answers[question.Id] = message.Trim();
        }

        context.CurrentQuestionIndex++;
        if (context.CurrentQuestionIndex >= questions.Count)
            return await BeginCredentialsAsync(session, context, cancellationToken);

        return await AskCurrentQuestionAsync(session, context, "Got it — thanks!", cancellationToken);
    }

    private async Task<DoctorOnboardingMessageResponse> BeginCredentialsAsync(
        DoctorOnboardingSession session,
        DoctorOnboardingContextData context,
        CancellationToken cancellationToken)
    {
        context.Stage = DoctorOnboardingStage.Username;
        var text = "Great — your profile questionnaire is complete! 🎉\n\nNow let's create your login. What username would you like to use?";
        await SaveAssistantMessageAsync(session, text, cancellationToken);
        return BuildResponse(session, context, text, usePasswordInput: false);
    }

    private async Task<DoctorOnboardingMessageResponse> HandleUsernameAsync(
        DoctorOnboardingSession session,
        DoctorOnboardingContextData context,
        string message,
        CancellationToken cancellationToken)
    {
        var username = message.Trim();
        if (string.IsNullOrWhiteSpace(username))
            return BuildResponse(session, context, "Please enter a username to continue.");

        if (await _db.Doctors.AnyAsync(d => d.Username == username, cancellationToken))
            return BuildResponse(session, context, "That username is already taken. Please choose another.");

        context.PendingUsername = username;
        context.Stage = DoctorOnboardingStage.Password;
        var text = $"Perfect — **{username}** it is. Now choose a password (at least 6 characters).";
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
        var text = "Please confirm your password.";
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

        var carriers = await _db.InsuranceCarriers.AsNoTracking()
            .Where(c => c.IsActive)
            .ToListAsync(cancellationToken);

        var data = DoctorOnboardingMapper.BuildRegistration(
            context.Answers,
            context.PendingUsername!,
            context.PendingPassword!,
            carriers);

        var result = await _registration.RegisterAsync(data.RegisterRequest, cancellationToken: cancellationToken);
        if (!result.Success)
        {
            context.Stage = DoctorOnboardingStage.Username;
            context.PendingPassword = null;
            var failText = $"{result.Message}\n\nLet's pick a different username — what would you like to use?";
            await SaveAssistantMessageAsync(session, failText, cancellationToken);
            return BuildResponse(session, context, failText, usePasswordInput: false);
        }

        var doctor = await _db.Doctors.FirstAsync(
            d => d.Username == context.PendingUsername, cancellationToken);
        DoctorOnboardingMapper.ApplyProfileFields(doctor, data);
        session.DoctorId = doctor.Id;
        await _db.SaveChangesAsync(cancellationToken);

        var loginResult = await _auth.LoginAsync(new AccountLoginRequest
        {
            AccountType = AccountType.Doctor,
            Username = context.PendingUsername!,
            Password = context.PendingPassword!
        }, httpContext, cancellationToken);

        context.Stage = DoctorOnboardingStage.Complete;
        var text = loginResult.Success
            ? $"You're all set, Dr. {doctor.Name.Split(' ').FirstOrDefault() ?? "there"}! 🎉 Your {_siteName} doctor profile has been created. Taking you to your profile now…"
            : $"Your account was created! Please sign in with username **{context.PendingUsername}**.";

        await SaveAssistantMessageAsync(session, text, cancellationToken);
        _logger.LogInformation("Doctor onboarding completed: {Username}", doctor.Username);

        return BuildResponse(session, context, text, flowComplete: true, signedIn: loginResult.Success);
    }

    private async Task<DoctorOnboardingMessageResponse> AskCurrentQuestionAsync(
        DoctorOnboardingSession session,
        DoctorOnboardingContextData context,
        string? prefix,
        CancellationToken cancellationToken)
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

        if (!string.IsNullOrWhiteSpace(question.OptionsHint) &&
            !question.AnswerType.Contains("e.g.", StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine();
            sb.AppendLine($"_{question.OptionsHint}_");
        }
        else if (!string.IsNullOrWhiteSpace(question.OptionsHint))
        {
            sb.AppendLine();
            sb.AppendLine($"_{question.OptionsHint}_");
        }

        var text = sb.ToString().Trim();
        await SaveAssistantMessageAsync(session, text, cancellationToken);
        return BuildResponse(session, context, text, options: GetOptions(question));
    }

    private async Task<DoctorOnboardingMessageResponse> RepromptQuestionAsync(
        DoctorOnboardingSession session,
        DoctorOnboardingContextData context,
        string error,
        CancellationToken cancellationToken)
    {
        var question = DoctorOnboardingQuestions.All[context.CurrentQuestionIndex];
        var text = $"{error}\n\n{question.Question}";
        await SaveAssistantMessageAsync(session, text, cancellationToken);
        return BuildResponse(session, context, text, options: GetOptions(question));
    }

    private static DoctorOnboardingMessageResponse BuildCompleteResponse(
        DoctorOnboardingSession session,
        DoctorOnboardingContextData context) =>
        BuildResponse(session, context, "Your registration is complete.", flowComplete: true);

    private static DoctorOnboardingMessageResponse BuildResponse(
        DoctorOnboardingSession session,
        DoctorOnboardingContextData context,
        string text,
        IReadOnlyList<string>? options = null,
        bool usePasswordInput = false,
        bool flowComplete = false,
        bool signedIn = false)
    {
        var questions = DoctorOnboardingQuestions.All;
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
            TotalQuestions = context.Stage == DoctorOnboardingStage.Questions ? questions.Count : null
        };
    }

    private static Task SaveAssistantMessageAsync(
        DoctorOnboardingSession session,
        string text,
        CancellationToken cancellationToken) => Task.CompletedTask;

    private static string? ValidateAnswer(DoctorOnboardingQuestion question, string message)
    {
        var answer = message.Trim();
        if (!question.Required && (string.IsNullOrWhiteSpace(answer) || answer.Equals("skip", StringComparison.OrdinalIgnoreCase)))
            return null;

        if (question.Required && string.IsNullOrWhiteSpace(answer))
            return "This question is required. Please provide an answer.";

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
        if (hint.Contains('/'))
            return hint.Split('/').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

        return hint.Split(',')
            .Select(s => s.Trim())
            .Where(s => s.Length > 0 && !s.StartsWith("e.g.", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }
}
