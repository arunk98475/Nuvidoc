namespace Docovee.BLL.Data;



/// <summary>

/// Conversation copy and deep-dive questions from Docs/Nuvidoc_Onboarding_Questionnaire_29_06_2026.xlsx

/// (💬 User Intake Flow, 🔍 User Deep-Dive Questions, 🤖 Nuvi AI Conversation Flow).

/// </summary>

public static class NuviFlowContent

{

    public const string GreetingMessage =

        "Hi! I'm Nuvi 👋 I'm here to personally match you with the right doctor — not just any doctor, the right one for YOU. What's going on? Tell me what's been on your mind health-wise, or what kind of doctor you're looking for.";



    public const string FirstVisitQuestionTemplate =

        "Is this your first time visiting {0}?";



    public static readonly string[] FirstVisitOptions =

        ["Yes", "No"];



    public static string FormatFirstVisitQuestion(string siteName) =>

        string.Format(FirstVisitQuestionTemplate, siteName.Trim());



    public const string ReturningUsernameQuestion =

        "Welcome back! Please enter your username or email address.";



    public const string ReturningPasswordQuestion =

        "Thanks — now enter your password.";



    public const string LogisticsVisitQuestion =

        "And are you looking for someone local you can visit in person, or would a virtual/telehealth option work for you — or both?";



    public static readonly string[] LogisticsVisitOptions =

        ["In-person only", "Telehealth only", "Either works"];



    public const string LogisticsLocationQuestion =

        "So I can find the best-fit doctor near you — what's your ZIP code, city, or general area?";



    public const string LogisticsLocationChangeQuestionTemplate =

        "Are you looking for doctors near {0}?";



    public static readonly string[] LogisticsLocationChangeOptions =

        ["Yes", "No"];



    public static string FormatLogisticsLocationChangeQuestion(string cityName) =>

        string.Format(LogisticsLocationChangeQuestionTemplate, cityName.Trim());



    public const string LogisticsInsuranceTypeQuestion =

        "Do you have health insurance, or are you looking for self-pay / cash-pay options?";



    public static readonly string[] LogisticsInsuranceTypeOptions =

        ["I have insurance", "Self-pay", "Not sure yet"];



    public const string LogisticsInsurancePlanQuestion =

        "What insurance plan are you on? (Don't worry if you're not sure — you can skip this for now)";



    public static readonly string[] LogisticsInsurancePlanOptions =

        ["Aetna PPO", "Blue Cross Blue Shield", "Cigna", "United Healthcare", "Medicare", "Humana", "Skip for now"];



    public const string LogisticsUrgencyQuestion =

        "One more quick thing — roughly how soon are you hoping to be seen?";



    public static readonly string[] LogisticsUrgencyOptions =

        ["ASAP (this week)", "Within a month", "No rush", "Just exploring"];



    public const string MomentumBridgeMessage =

        "I've identified some doctors who could be a great fit for you. To help with booking and save your matches, I just need a quick second to set up your profile.";



    public const string DeepDivePermissionQuestionTemplate =

        "Nice to meet you, {0}! May I ask a few follow-up questions just to make sure they're the best fit for you? Totally optional — I can show your matches now if you prefer.";



    public static readonly string[] DeepDivePermissionOptions =

        ["Yes, ask away", "No thanks, show my matches"];



    public const string MatchSearchLoadingMessage =

        "Please wait for a while — I'm searching for the best matches for you.";



    public const string AccountNameQuestion =

        "What's your name?";



    public const string AccountPhoneQuestion =

        "And a phone number? (We'll only use this if your matched doctor needs to confirm your appointment.)";



    public const string AccountPasswordQuestion =

        "Last step for your profile — create a password so you can come back anytime and manage your matches. (Your login will be your email + this password.)";



    public const string DeepDiveWelcomeSuffix =

        "now that I know what you're dealing with — let me get to know what matters most to YOU in a doctor.";



    public const string BookingInitiationPrompt =

        "Give {0}'s office a call when you're ready — they're the best next step to get you taken care of.";



    public const string DeepDiveWildcardQuestion =

        "Is there anything else that matters to you when finding your perfect doctor that we haven't asked yet?";



    public const string DeepDiveLanguageFollowUpQuestion =

        "Which language would you prefer your doctor to speak?";



    public const string DeepDiveWildcardFollowUpQuestion =

        "Please tell me what else matters to you when choosing a doctor.";



    public static string FormatDeepDivePermissionQuestion(string displayName) =>

        string.Format(DeepDivePermissionQuestionTemplate, displayName);



    public static bool IsWildcardDeepDiveQuestion(string question) =>

        question.Contains("anything else that matters", StringComparison.OrdinalIgnoreCase);



    public static bool IsLanguageDeepDiveQuestion(string question) =>

        question.Contains("speaks a language other than English", StringComparison.OrdinalIgnoreCase);



    public static IReadOnlyList<(string Question, string ValidationHint, int MatchWeight, string MatchWeightLabel)> DeepDiveQuestions { get; } =

    [

        ("How important is it that your doctor is close to home or work?", "Very important, somewhat, or doesn't matter", 8, "High"),

        ("Would you travel 30+ minutes for the right doctor?", "Yes or no", 5, "Medium"),

        ("Does experience level matter to you — do you prefer a doctor who's been practicing for many years?", "Yes, no, or neutral", 8, "High"),

        ("Is training at a top-ranked medical school or residency program important to you?", "Yes, no, or neutral", 5, "Medium"),

        ("Shall I only show doctors who accept your insurance plan?", "Yes or no", 10, "Critical"),

        ("Do online reviews (Google, Healthgrades) matter to you when choosing a doctor?", "Yes or no", 5, "Medium"),

        ("Would you consider a newer doctor with fewer reviews if everything else felt right?", "Yes or no", 3, "Low-Medium"),

        ("Is it important that your doctor speaks a language other than English?", "Yes or no", 8, "High"),

        ("On a scale of 1–5, how much does the doctor's personality and bedside manner matter to you vs. just their credentials?", "A number 1 through 5", 8, "High"),

        ("Do you value a doctor who takes a holistic or integrative approach, or do you prefer strictly conventional medicine?", "Holistic, conventional, or doesn't matter", 5, "Medium"),

        ("Would you feel more comfortable with a doctor who shares some of your personal interests or lifestyle?", "Yes or no", 3, "Low-Medium"),

        ("Do you have a preference for your doctor's approximate age group?", "30s, 40s–50s, 60s+, or no preference", 1, "Low"),

        (DeepDiveWildcardQuestion, "Yes or no", 5, "Variable"),

    ];

}


