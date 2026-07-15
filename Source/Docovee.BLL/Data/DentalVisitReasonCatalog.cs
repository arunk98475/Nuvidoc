namespace Docovee.BLL.Data;

public sealed record VisitReasonPopularItem(string Key, string Name);

public sealed record VisitReasonCategoryDef(
    string Key,
    string Title,
    string Description,
    int DefaultNewMinutes,
    int DefaultExistingMinutes,
    bool DefaultEnabled,
    IReadOnlyList<VisitReasonPopularItem> PopularItems);

/// <summary>
/// Dental visit-reason categories for Settings (Zocdoc-style preference UI).
/// </summary>
public static class DentalVisitReasonCatalog
{
    public static IReadOnlyList<VisitReasonCategoryDef> Categories { get; } =
    [
        new(
            "routine",
            "Routine Dental Care and Exams",
            "Preventive cleanings, checkups, and exams that keep patients on a healthy schedule.",
            45, 45, true,
            [
                new("dental-cleaning", "Dental Cleaning"),
                new("routine-exam", "Routine Dental Exam")
            ]),
        new(
            "tests-xrays",
            "Dental Tests and X-rays",
            "Imaging and diagnostic visits used to detect issues early.",
            30, 20, false,
            [
                new("dental-xray", "Dental X-ray"),
                new("oral-cancer-exam", "Oral Cancer Exam")
            ]),
        new(
            "common-problems",
            "Common Dental Problems",
            "Everyday concerns like cavities, pain, sensitivity, and gum issues.",
            45, 30, true,
            [
                new("cavities", "Cavities"),
                new("tooth-decay", "Tooth Decay"),
                new("sensitive-teeth", "Sensitive Teeth"),
                new("bleeding-gums", "Bleeding Gums")
            ]),
        new(
            "restorative",
            "Restorative Dentistry",
            "Fillings, crowns, bridges, root canals, and other repairs.",
            60, 45, true,
            [
                new("filling", "Filling"),
                new("crown", "Crown"),
                new("root-canal", "Root Canal"),
                new("bridge", "Bridge")
            ]),
        new(
            "cosmetic",
            "Cosmetic Dentistry",
            "Whitening, veneers, bonding, and smile-focused treatments.",
            45, 30, false,
            [
                new("teeth-whitening", "Teeth Whitening"),
                new("veneers", "Veneer(s)"),
                new("teeth-bonding", "Teeth Bonding")
            ]),
        new(
            "orthodontics",
            "Orthodontics",
            "Braces, clear aligners, retainers, and related checkups.",
            45, 30, false,
            [
                new("clear-aligners-consult", "Clear Aligners / Invisalign Consultation"),
                new("retainer-checkup", "Retainer Checkup")
            ]),
        new(
            "periodontal",
            "Periodontal Care",
            "Gum disease treatment, maintenance, and scaling services.",
            60, 45, false,
            [
                new("periodontal-maintenance", "Periodontal Maintenance"),
                new("scaling-root-planing", "Scaling and Root Planing")
            ]),
        new(
            "implants",
            "Dental Implants",
            "Implant consultations, placements, and restorations.",
            60, 45, false,
            [
                new("dental-implants", "Dental Implant(s)"),
                new("implant-restoration", "Dental Implant Restoration")
            ]),
        new(
            "dentures",
            "Dentures and Partials",
            "Complete dentures, partials, repairs, and hybrid options.",
            45, 30, false,
            [
                new("dentures", "Dentures"),
                new("partial-dentures", "Partial Dentures")
            ]),
        new(
            "oral-surgery",
            "Oral Surgery and Extractions",
            "Extractions, surgical procedures, and related follow-ups.",
            60, 30, false,
            [
                new("tooth-extraction", "Tooth Extraction"),
                new("dental-surgery", "Dental Surgery")
            ]),
        new(
            "pediatric",
            "Pediatric Dental Care",
            "Child-focused cleanings, exams, and preventive visits.",
            45, 30, false,
            [
                new("pediatric-cleaning", "Pediatric Dental Cleaning"),
                new("new-patient-child", "New Patient Dental Exam (Child)")
            ]),
        new(
            "emergency",
            "Dental Emergencies",
            "Urgent pain, broken teeth, abscesses, and same-day concerns.",
            45, 30, true,
            [
                new("dental-emergency", "Dental Emergency"),
                new("broken-tooth", "Broken Tooth"),
                new("dental-pain", "Dental Pain")
            ]),
        new(
            "consultations",
            "Consultations",
            "General and specialty consults for new and returning patients.",
            30, 20, true,
            [
                new("dental-consultation", "Dental Consultation"),
                new("new-patient-adult", "New Patient Dental Exam (Adult)")
            ])
    ];

    public static VisitReasonCategoryDef? Find(string key) =>
        Categories.FirstOrDefault(c => c.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
}
