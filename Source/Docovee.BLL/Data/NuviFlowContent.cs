namespace Docovee.BLL.Data;



/// <summary>

/// Conversation copy and deep-dive questions from Docs/Nuvidoc_Onboarding_Questionnaire_29_06_2026.xlsx

/// (💬 User Intake Flow, 🔍 User Deep-Dive Questions, 🤖 Nuvi AI Conversation Flow).

/// </summary>

public static class NuviFlowContent

{

    public const string GreetingMessage =

        "Hi! I'm Nuvi 👋 I'm here to personally match you with the right doctor — not just any doctor, the right one for YOU. What's going on? Tell me what's been on your mind health-wise, or what kind of doctor you're looking for.";



    public const string LogisticsVisitQuestion =

        "And are you looking for someone local you can visit in person, or would a virtual/telehealth option work for you — or both?";



    public static readonly string[] LogisticsVisitOptions =

        ["In-person only", "Telehealth only", "Either works"];



    public const string LogisticsLocationQuestion =

        "Where are you looking for care? Share your city, ZIP code, or general area — I'll use it to find doctors near you.";



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

        "Perfect — I think I already have a few ideas forming. Based on what you've shared, I want to find you someone who truly fits. Give me just a moment… ✨ I've identified some strong doctors who could be a great fit — but before I show you, I want to make sure they're a personal fit for you, not just a generic list. Ready to set up your free profile so I can save your matches?";



    public const string DeepDivePermissionQuestionTemplate =

        "Thanks for creating your account, {0}! To get you the best match possible, may I ask a few more quick questions? Totally optional — I can show your matches now if you prefer.";



    public static readonly string[] DeepDivePermissionOptions =

        ["Yes, ask away", "No thanks, show my matches"];



    public const string MatchSearchLoadingMessage =

        "Please wait for a while — I'm searching for the best matches for you.";



    public const string AccountNameQuestion =

        "To save your matches and get in touch with your top picks, I just need a quick second to set up your free profile. What's your name?";



    public const string AccountPhoneQuestion =

        "And a phone number? (We'll only use this if your matched doctor needs to confirm your appointment.)";



    public const string AccountPasswordQuestion =

        "Last step for your profile — create a password so you can come back anytime and manage your matches. (Your login will be your email + this password.)";



    public const string DeepDiveWelcomeSuffix =

        "now that I know what you're dealing with — let me get to know what matters most to YOU in a doctor.";



    public const string BookingInitiationPrompt =

        "Want me to send a booking request to {0}'s office on your behalf? I can reach out with your info so they're expecting you — all you'd need to do is confirm the time.";



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



    public static IReadOnlyList<(string Question, string ValidationHint)> DeepDiveQuestions { get; } =

    [

        ("How important is it that your doctor is close to home or work?", "Very important, somewhat, or doesn't matter"),

        ("Would you travel 30+ minutes for the right doctor?", "Yes or no"),

        ("Does experience level matter to you — do you prefer a doctor who's been practicing for many years?", "Yes, no, or neutral"),

        ("Is training at a top-ranked medical school or residency program important to you?", "Yes, no, or neutral"),

        ("Shall I only show doctors who accept your insurance plan?", "Yes or no"),

        ("Do online reviews (Google, Healthgrades) matter to you when choosing a doctor?", "Yes or no"),

        ("Would you consider a newer doctor with fewer reviews if everything else felt right?", "Yes or no"),

        ("Is it important that your doctor speaks a language other than English?", "Yes or no"),

        ("On a scale of 1–5, how much does the doctor's personality and bedside manner matter to you vs. just their credentials?", "A number 1 through 5"),

        ("Do you value a doctor who takes a holistic or integrative approach, or do you prefer strictly conventional medicine?", "Holistic, conventional, or doesn't matter"),

        ("Would you feel more comfortable with a doctor who shares some of your personal interests or lifestyle?", "Yes or no"),

        ("Do you have a preference for your doctor's approximate age group?", "30s, 40s–50s, 60s+, or no preference"),

        (DeepDiveWildcardQuestion, "Yes or no"),

    ];

}


