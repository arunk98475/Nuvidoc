namespace Docovee.DS.Entities;

/// <summary>
/// Editable homepage (landing page) marketing copy. Single row (Id = 1).
/// Null/empty fields fall back to built-in defaults at runtime.
/// </summary>
public class HomePageContent
{
    public int Id { get; set; } = 1;

    public string? MetaDescription { get; set; }

    public string? HeroEyebrow { get; set; }
    public string? HeroHeadlineHtml { get; set; }
    public string? HeroSubtext { get; set; }

    public string? Stat1Num { get; set; }
    public string? Stat1Label { get; set; }
    public string? Stat2Num { get; set; }
    public string? Stat2Label { get; set; }
    public string? Stat3Num { get; set; }
    public string? Stat3Label { get; set; }
    public string? Stat4Num { get; set; }
    public string? Stat4Label { get; set; }

    public string? InsuranceTitle { get; set; }

    public string? WhyEyebrow { get; set; }
    public string? WhyHeadlineHtml { get; set; }
    public string? Why1Title { get; set; }
    public string? Why1Body { get; set; }
    public string? Why2Title { get; set; }
    public string? Why2Body { get; set; }
    public string? Why3Title { get; set; }
    public string? Why3Body { get; set; }

    public string? VisitEyebrow { get; set; }
    public string? VisitHeadlineHtml { get; set; }

    public string? SpecialtyEyebrow { get; set; }
    public string? SpecialtyHeadlineHtml { get; set; }
    public string? SpecialtyBody { get; set; }

    public string? DoctorsEyebrow { get; set; }
    public string? DoctorsHeadlineHtml { get; set; }
    public string? DoctorsSubtitle { get; set; }

    public string? HowEyebrow { get; set; }
    public string? HowHeadlineHtml { get; set; }
    public string? How1Title { get; set; }
    public string? How1Body { get; set; }
    public string? How2Title { get; set; }
    public string? How2Body { get; set; }
    public string? How3Title { get; set; }
    public string? How3Body { get; set; }

    public string? CtaEyebrow { get; set; }
    public string? CtaHeadlineHtml { get; set; }
    public string? CtaSubtext { get; set; }
    public string? CtaButtonText { get; set; }
    public string? CtaNote { get; set; }

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
