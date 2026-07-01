using Docovee.DS.Models;

namespace Docovee.BLL.Data;

public static class DoctorOnboardingQuestions
{
    public static IReadOnlyList<DoctorOnboardingQuestion> All { get; } = new List<DoctorOnboardingQuestion>
    {
        new(1, "Professional Background", "What is your full name and preferred professional title?", "Short Text", "e.g. Dr. Sarah Kim, MD", true),
        new(2, "Professional Background", "What is your medical specialty?", "Dropdown", "Primary care, Cardiology, Dermatology, Orthopedics, Pediatrics, OB/GYN, Neurology, Psychiatry, Dentistry, Other", true),
        new(3, "Professional Background", "Do you have any sub-specialties or areas of focused practice?", "Short Text", "e.g. Sports medicine, Cosmetic dermatology", true),
        new(4, "Professional Background", "What medical school did you graduate from?", "Short Text", "Name of institution", true),
        new(5, "Professional Background", "What year did you graduate from medical school?", "Number", "4-digit year (e.g. 2005)", true),
        new(6, "Professional Background", "Where did you complete your residency?", "Short Text", "Institution name + city", false),
        new(7, "Professional Background", "Did you complete a fellowship? If so, in what specialty and where?", "Short Text", "Specialty + institution", false),
        new(8, "Professional Background", "Are you board certified? In which board(s)?", "Multi-select + Text", "Yes/No + Board name(s)", true),
        new(9, "Professional Background", "How many total years have you been in practice?", "Number", "Whole number", true),
        new(10, "Professional Background", "What languages do you speak fluently (including English)?", "Multi-select", "English, Spanish, Mandarin, Tagalog, Vietnamese, Korean, Arabic, Other", true),
        new(11, "The Practice", "What is your practice name?", "Short Text", "Official practice name", true),
        new(12, "The Practice", "What is your practice address (city & state minimum)?", "Address", "Street, City, State, ZIP", true),
        new(13, "The Practice", "How long has your practice been at its current location?", "Number + Unit", "Years (e.g. 12 years)", true),
        new(14, "The Practice", "Is your practice solo, group, or part of a health system?", "Dropdown", "Solo / Small group (2–5) / Larger group (6+) / Hospital-affiliated / Concierge", true),
        new(15, "The Practice", "What are your standard office hours?", "Text / Check Grid", "Days + time ranges (e.g. Mon–Fri 8am–5pm, Sat 9am–1pm)", true),
        new(16, "The Practice", "Do you offer telemedicine / virtual visits?", "Yes/No + Detail", "Yes – all visits / Yes – follow-ups only / No", true),
        new(17, "The Practice", "What insurances do you accept?", "Multi-select + Text", "Major carriers list + Other text field", true),
        new(18, "The Practice", "Do you accept self-pay / cash-pay patients?", "Yes/No", "", true),
        new(19, "The Practice", "What is your average new patient wait time?", "Dropdown", "Same week / 1–2 weeks / 3–4 weeks / 1–2 months / 3+ months", true),
        new(20, "The Practice", "Who is your longest-standing employee and how long have they been with you?", "Short Text", "Role + years (e.g. Office Manager Maria – 14 years)", false),
        new(21, "The Practice", "How many patients do you typically see per day?", "Number", "Approximate number", false),
        new(22, "The Practice", "Do you have a patient portal or app?", "Yes/No + Name", "Name of portal if applicable", false),
        new(23, "Clinical Identity", "What are your top 3 procedures or treatments that define your practice?", "Ranked Text", "Procedure 1, Procedure 2, Procedure 3", true),
        new(24, "Clinical Identity", "What condition or patient population do you most love treating?", "Short Text", "Free text (e.g. 'complex chronic pain patients')", true),
        new(25, "Clinical Identity", "What sets your clinical approach apart from other doctors in your specialty?", "Long Text", "Free text, 2–4 sentences", true),
        new(26, "Clinical Identity", "How would you describe your bedside manner?", "Dropdown + Text", "Warm & nurturing / Direct & efficient / Collaborative / Educational / Other", true),
        new(27, "Clinical Identity", "Do you take a holistic / integrative approach, conventional, or a blend?", "Dropdown", "Conventional / Integrative / Functional / Blend", true),
        new(28, "Clinical Identity", "How many continuing education (CE) hours have you completed beyond the minimum required?", "Number", "Approximate total extra hours", false),
        new(29, "Clinical Identity", "Any special certifications, courses, or training worth highlighting?", "Long Text", "e.g. 'Harvard CME leadership course, 2022'", false),
        new(30, "Clinical Identity", "Have you published research, spoken at conferences, or received notable awards?", "Long Text", "Free text list", false),
        new(31, "Personal Profile", "How old are you? (or age range if preferred)", "Number or Dropdown", "Exact age OR: 30s / 40s / 50s / 60s+", false),
        new(32, "Personal Profile", "Do you have a family? (spouse/partner, kids?)", "Short Text", "e.g. 'Married with 3 kids'", false),
        new(33, "Personal Profile", "Do you have any pets?", "Short Text + Photo", "Type + name (e.g. 'Golden Retriever named Biscuit')", false),
        new(34, "Personal Profile", "Where did you grow up?", "Short Text", "City, State / Country", false),
        new(35, "Personal Profile", "What is your favorite color?", "Short Text", "Free text", false),
        new(36, "Personal Profile", "What is your favorite food or cuisine?", "Short Text", "Free text (e.g. 'Thai food, specifically pad see ew')", false),
        new(37, "Personal Profile", "What are your hobbies or interests outside of medicine?", "Multi-select + Text", "Golf, Cooking, Running, Travel, Music, Art, Hiking, Other", false),
        new(38, "Personal Profile", "Do you play any sports or have an active lifestyle?", "Short Text", "Free text", false),
        new(39, "Personal Profile", "What is a fun fact about you that your patients would be surprised to know?", "Long Text", "Free text, 1–2 sentences", false),
        new(40, "Personal Profile", "What is your personal mission as a doctor?", "Long Text", "1–3 sentences", true),
        new(41, "Personal Profile", "If you weren't a doctor, what career would you have chosen?", "Short Text", "Free text", false),
        new(42, "Personal Profile", "What book, podcast, or show are you currently into?", "Short Text", "Free text", false),
        new(43, "Patient Experience", "What do your patients say they love most about coming to see you?", "Long Text", "Free text (can pull from reviews)", true),
        new(44, "Patient Experience", "What is the #1 complaint or concern you hear from new patients about their previous doctor?", "Long Text", "Free text", true),
        new(45, "Patient Experience", "How do you handle patients who are anxious or scared?", "Long Text", "Free text, 2–3 sentences", true),
        new(46, "Patient Experience", "Do you personally follow up with patients after significant procedures or diagnoses?", "Yes/No + Detail", "", true),
        new(51, "Social & Online Presence", "Are you active on social media? Which platforms?", "Multi-select + Handle", "Instagram, TikTok, Facebook, YouTube, LinkedIn + username", false),
        new(52, "Social & Online Presence", "Do you create any educational content (videos, blog, podcast)?", "Yes/No + Link", "", false),
    };

    public static DoctorOnboardingQuestion? GetById(int id) =>
        All.FirstOrDefault(q => q.Id == id);
}
