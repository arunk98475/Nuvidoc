using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using CsvHelper;
using CsvHelper.Configuration;
using Docovee.DS.Models;
using Docovee.DS;
using Docovee.DS.Entities;
using Docovee.DS.Enums;
using Docovee.logging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Docovee.BLL.Services;

public interface IAdminDoctorService
{
    Task<PagedResult<DoctorAdminDto>> ListAsync(int page, int pageSize, string? search, CancellationToken cancellationToken = default);
    Task<DoctorAdminEditModel?> GetForEditAsync(int id, CancellationToken cancellationToken = default);
    Task<(bool Success, string? Error)> CreateAsync(DoctorAdminEditModel model, IFormFile? photo, CancellationToken cancellationToken = default);
    Task<(bool Success, string? Error)> UpdateAsync(DoctorAdminEditModel model, IFormFile? photo, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default);
    Task<DoctorImportResult> ImportAsync(IFormFile file, IProgress<ImportProgress>? progress = null, CancellationToken cancellationToken = default);
    Task<DoctorImportResult> ImportAsync(Stream fileStream, string fileName, IProgress<ImportProgress>? progress = null, CancellationToken cancellationToken = default);
}

public class AdminDoctorService : IAdminDoctorService
{
    private readonly DocoveeDbContext _db;
    private readonly IDoctorFileService _fileService;
    private readonly IDocoveeLogger _logger;
    private readonly PasswordHasher<Doctor> _passwordHasher = new();

    public AdminDoctorService(DocoveeDbContext db, IDoctorFileService fileService, IDocoveeLogger logger)
    {
        _db = db;
        _fileService = fileService;
        _logger = logger;
    }

    public async Task<PagedResult<DoctorAdminDto>> ListAsync(int page, int pageSize, string? search, CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = _db.Doctors.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
            query = query.Where(d =>
                d.Name.ToLower().Contains(term) ||
                d.Specialty.ToLower().Contains(term) ||
                (d.PracticeName != null && d.PracticeName.ToLower().Contains(term)) ||
                d.City.ToLower().Contains(term));
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderBy(d => d.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(d => new
            {
                d.Id,
                d.Name,
                d.Specialty,
                d.PracticeName,
                d.Location,
                d.City,
                d.State,
                d.GoogleRating,
                d.GoogleReviewCount,
                d.PhotoUrl,
                d.GmbPhotoLink,
                d.IsActive,
                PatientReviewCount = d.PatientReviews.Count
            })
            .ToListAsync(cancellationToken);

        return new PagedResult<DoctorAdminDto>
        {
            Items = items.Select(d => new DoctorAdminDto
            {
                Id = d.Id,
                Name = d.Name,
                Specialty = d.Specialty,
                PracticeName = d.PracticeName,
                Location = d.Location ?? (d.City + ", " + d.State),
                City = d.City,
                State = d.State,
                GoogleRating = d.GoogleRating,
                GoogleReviewCount = d.GoogleReviewCount,
                PhotoUrl = DoctorPhotoHelper.GetDisplayPhotoUrl(d.PhotoUrl, d.GmbPhotoLink),
                IsActive = d.IsActive,
                PatientReviewCount = d.PatientReviewCount
            }).ToList(),
            TotalCount = total,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<DoctorAdminEditModel?> GetForEditAsync(int id, CancellationToken cancellationToken = default)
    {
        var doctor = await _db.Doctors.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
        return doctor == null ? null : MapToEditModel(doctor);
    }

    public async Task<(bool Success, string? Error)> CreateAsync(DoctorAdminEditModel model, IFormFile? photo, CancellationToken cancellationToken = default)
    {
        var error = ValidateModel(model);
        if (error != null) return (false, error);

        var usernameError = await ValidateUsernameAsync(model, cancellationToken);
        if (usernameError != null) return (false, usernameError);

        var doctor = new Doctor();
        ApplyModel(doctor, model);
        ApplyCredentials(doctor, model, isCreate: true);

        if (photo != null)
        {
            var photoUrl = await _fileService.SaveUploadedPhotoAsync(photo, cancellationToken);
            if (photoUrl != null)
                doctor.PhotoUrl = photoUrl;
        }

        doctor.AvatarInitials = BuildInitials(doctor.Name);
        _db.Doctors.Add(doctor);
        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Admin created doctor {Name}", doctor.Name);
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> UpdateAsync(DoctorAdminEditModel model, IFormFile? photo, CancellationToken cancellationToken = default)
    {
        var doctor = await _db.Doctors.FirstOrDefaultAsync(d => d.Id == model.Id, cancellationToken);
        if (doctor == null) return (false, "Doctor not found.");

        var error = ValidateModel(model);
        if (error != null) return (false, error);

        var usernameError = await ValidateUsernameAsync(model, cancellationToken);
        if (usernameError != null) return (false, usernameError);

        ApplyModel(doctor, model);
        ApplyCredentials(doctor, model, isCreate: false);

        if (photo != null)
        {
            var photoUrl = await _fileService.SaveUploadedPhotoAsync(photo, cancellationToken);
            if (photoUrl != null)
                doctor.PhotoUrl = photoUrl;
        }

        doctor.AvatarInitials = BuildInitials(doctor.Name);
        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Admin updated doctor {Id}", doctor.Id);
        return (true, null);
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        var doctor = await _db.Doctors.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (doctor == null) return false;

        _db.Doctors.Remove(doctor);
        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Admin deleted doctor {Id}", id);
        return true;
    }

    public Task<DoctorImportResult> ImportAsync(IFormFile file, IProgress<ImportProgress>? progress = null, CancellationToken cancellationToken = default) =>
        ImportAsync(file.OpenReadStream(), file.FileName, progress, cancellationToken);

    public async Task<DoctorImportResult> ImportAsync(Stream fileStream, string fileName, IProgress<ImportProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        var imported = 0;
        var ext = Path.GetExtension(fileName).ToLowerInvariant();

        List<Dictionary<string, string>> rows;
        try
        {
            rows = ext switch
            {
                ".csv" => await ParseCsvAsync(fileStream, cancellationToken),
                ".xlsx" or ".xls" => ParseExcel(fileStream),
                _ => throw new InvalidOperationException("Unsupported file type. Use .csv or .xlsx.")
            };
        }
        catch (Exception ex)
        {
            return new DoctorImportResult { Errors = new[] { ex.Message } };
        }

        ReportProgress(progress, rows.Count, 0, imported, errors.Count, errors, false);

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var rowNum = i + 2;
            try
            {
                var name = GetValue(row, "Doctor Name", "Name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    errors.Add($"Row {rowNum}: Doctor Name is required.");
                    ReportProgress(progress, rows.Count, i + 1, imported, errors.Count, errors, false);
                    continue;
                }

                var gmbLinkRaw = GetValue(row, "GMB Photo Link", "Gmb Photo Link");
                var gmbLink = DoctorPhotoHelper.NormalizeStoredLink(gmbLinkRaw);
                var photosColumn = GetValue(row, "Photos", "Photo");

                var doctor = new Doctor
                {
                    Name = name.Trim(),
                    Specialty = GetValue(row, "Specialty") ?? "General Practice",
                    SpecialtyCategory = GetValue(row, "Specialty Category", "Specialty") ?? GetValue(row, "Specialty") ?? "General Practice",
                    Location = GetValue(row, "Location"),
                    PracticeName = GetValue(row, "Practice Name"),
                    Address = GetValue(row, "Address"),
                    OfficePhoneNumber = GetValue(row, "Office Phone Number", "Phone"),
                    GmbPhotoLink = gmbLink,
                    SummaryOfReviews = GetValue(row, "Summary of Reviews"),
                    Top3Procedures = GetValue(row, "Top 3 Procedures", "Top3Procedures"),
                    Niche = GetValue(row, "Niche"),
                    OffersDentalImplants = ParseBool(GetValue(row, "Dental Implants")),
                    OffersTmj = ParseBool(GetValue(row, "TMJ")),
                    OffersBotox = ParseBool(GetValue(row, "Botox")),
                    GoogleRating = ParseGoogleRating(GetValue(row, "Rating", "Google Rating")),
                    GoogleReviewCount = ParseReviewCount(GetValue(row, "Reviews", "Google Review Count")),
                    TagLine = GetValue(row, "Tag Line", "TagLine"),
                    City = GetValue(row, "City") ?? ParseCityFromLocation(GetValue(row, "Location")),
                    State = GetValue(row, "State") ?? ParseStateFromLocation(GetValue(row, "Location")),
                    ZipCode = GetValue(row, "Zip Code", "ZipCode") ?? "00000",
                    Gender = ParseGender(GetValue(row, "Gender")),
                    AvatarInitials = BuildInitials(name),
                    PhotoUrl = DoctorPhotoHelper.GetDisplayPhotoUrl(photosColumn, gmbLinkRaw)
                };

                _db.Doctors.Add(doctor);
                await _db.SaveChangesAsync(cancellationToken);
                imported++;
            }
            catch (DbUpdateException ex)
            {
                _db.ChangeTracker.Clear();
                var inner = ex.InnerException?.Message ?? ex.Message;
                errors.Add($"Row {rowNum}: Could not save — {inner}");
            }
            catch (Exception ex)
            {
                _db.ChangeTracker.Clear();
                errors.Add($"Row {rowNum}: {ex.Message}");
            }

            ReportProgress(progress, rows.Count, i + 1, imported, errors.Count, errors, false);
        }

        ReportProgress(progress, rows.Count, rows.Count, imported, errors.Count, errors, true);

        _logger.LogInformation("Doctor import completed: {Imported} imported, {Failed} failed", imported, errors.Count);
        return new DoctorImportResult
        {
            Imported = imported,
            Failed = errors.Count,
            Errors = errors
        };
    }

    private static void ReportProgress(
        IProgress<ImportProgress>? progress,
        int total,
        int processed,
        int imported,
        int failed,
        List<string> errors,
        bool complete)
    {
        progress?.Report(new ImportProgress
        {
            TotalRows = total,
            ProcessedRows = processed,
            Imported = imported,
            Failed = failed,
            Errors = errors.ToList(),
            Complete = complete
        });
    }

    private static string? ValidateModel(DoctorAdminEditModel model)
    {
        if (string.IsNullOrWhiteSpace(model.Name)) return "Doctor name is required.";
        if (string.IsNullOrWhiteSpace(model.Specialty)) return "Specialty is required.";
        if (string.IsNullOrWhiteSpace(model.SpecialtyCategory)) return "Specialty category is required.";
        if (string.IsNullOrWhiteSpace(model.City)) return "City is required.";
        if (string.IsNullOrWhiteSpace(model.State)) return "State is required.";
        return null;
    }

    private static void ApplyModel(Doctor doctor, DoctorAdminEditModel model)
    {
        doctor.Name = model.Name.Trim();
        doctor.Specialty = model.Specialty.Trim();
        doctor.SpecialtyCategory = model.SpecialtyCategory.Trim();
        doctor.Location = model.Location?.Trim();
        doctor.PracticeName = model.PracticeName?.Trim();
        doctor.Address = model.Address?.Trim();
        doctor.OfficePhoneNumber = model.OfficePhoneNumber?.Trim();
        doctor.GmbPhotoLink = DoctorPhotoHelper.NormalizeStoredLink(model.GmbPhotoLink);
        doctor.PhotoUrl = DoctorPhotoHelper.GetDisplayPhotoUrl(model.PhotoUrl, doctor.GmbPhotoLink);
        doctor.SummaryOfReviews = model.SummaryOfReviews?.Trim();
        doctor.Top3Procedures = model.Top3Procedures?.Trim();
        doctor.Niche = model.Niche?.Trim();
        doctor.OffersDentalImplants = model.OffersDentalImplants;
        doctor.OffersTmj = model.OffersTmj;
        doctor.OffersBotox = model.OffersBotox;
        doctor.Age = model.Age;
        doctor.YearsOfPractice = model.YearsOfPractice;
        doctor.ProcedureCount = model.ProcedureCount;
        doctor.GraduationYear = model.GraduationYear;
        doctor.PracticeCount = model.PracticeCount;
        doctor.City = model.City.Trim();
        doctor.State = model.State.Trim();
        doctor.ZipCode = string.IsNullOrWhiteSpace(model.ZipCode) ? "00000" : model.ZipCode.Trim();
        doctor.Latitude = model.Latitude;
        doctor.Longitude = model.Longitude;
        doctor.GoogleRating = ClampGoogleRating(model.GoogleRating);
        doctor.GoogleReviewCount = Math.Max(0, model.GoogleReviewCount);
        doctor.TagLine = model.TagLine?.Trim();
        doctor.Gender = ParseGender(model.Gender);
        doctor.IsActive = model.IsActive;
    }

    private void ApplyCredentials(Doctor doctor, DoctorAdminEditModel model, bool isCreate)
    {
        if (string.IsNullOrWhiteSpace(model.Username))
        {
            if (isCreate)
            {
                doctor.Username = null;
                doctor.PasswordHash = null;
            }
            return;
        }

        doctor.Username = model.Username.Trim();

        if (!string.IsNullOrWhiteSpace(model.Password))
            doctor.PasswordHash = _passwordHasher.HashPassword(doctor, model.Password);
        else if (isCreate)
            doctor.PasswordHash = null;
    }

    private async Task<string?> ValidateUsernameAsync(DoctorAdminEditModel model, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(model.Username))
            return null;

        var username = model.Username.Trim();
        var taken = await _db.Doctors.AnyAsync(
            d => d.Username == username && d.Id != model.Id, cancellationToken);
        if (taken)
            return "Username is already taken by another doctor.";

        if (!string.IsNullOrWhiteSpace(model.Password) && model.Password.Length < 6)
            return "Password must be at least 6 characters.";

        return null;
    }

    private static DoctorAdminEditModel MapToEditModel(Doctor doctor) => new()
    {
        Id = doctor.Id,
        Name = doctor.Name,
        Specialty = doctor.Specialty,
        SpecialtyCategory = doctor.SpecialtyCategory,
        Location = doctor.Location,
        PracticeName = doctor.PracticeName,
        Address = doctor.Address,
        OfficePhoneNumber = doctor.OfficePhoneNumber,
        PhotoUrl = DoctorPhotoHelper.GetDisplayPhotoUrl(doctor.PhotoUrl, doctor.GmbPhotoLink),
        GmbPhotoLink = doctor.GmbPhotoLink,
        SummaryOfReviews = doctor.SummaryOfReviews,
        Top3Procedures = doctor.Top3Procedures,
        Niche = doctor.Niche,
        OffersDentalImplants = doctor.OffersDentalImplants,
        OffersTmj = doctor.OffersTmj,
        OffersBotox = doctor.OffersBotox,
        Age = doctor.Age,
        YearsOfPractice = doctor.YearsOfPractice,
        ProcedureCount = doctor.ProcedureCount,
        GraduationYear = doctor.GraduationYear,
        PracticeCount = doctor.PracticeCount,
        City = doctor.City,
        State = doctor.State,
        ZipCode = doctor.ZipCode,
        Latitude = doctor.Latitude,
        Longitude = doctor.Longitude,
        GoogleRating = doctor.GoogleRating,
        GoogleReviewCount = doctor.GoogleReviewCount,
        TagLine = doctor.TagLine,
        Gender = doctor.Gender.ToString(),
        IsActive = doctor.IsActive,
        Username = doctor.Username
    };

    private static async Task<List<Dictionary<string, string>>> ParseCsvAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HeaderValidated = null,
            MissingFieldFound = null,
            BadDataFound = null
        };
        using var csv = new CsvReader(reader, config);
        await csv.ReadAsync();
        csv.ReadHeader();
        var headers = csv.HeaderRecord ?? Array.Empty<string>();

        var rows = new List<Dictionary<string, string>>();
        while (await csv.ReadAsync())
        {
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in headers)
                row[header] = csv.GetField(header) ?? string.Empty;
            rows.Add(row);
        }
        return rows;
    }

    private static List<Dictionary<string, string>> ParseExcel(Stream stream)
    {
        using var workbook = new XLWorkbook(stream);
        var worksheet = workbook.Worksheet(1);
        var rows = new List<Dictionary<string, string>>();
        var headerRow = worksheet.Row(1);
        var headers = headerRow.CellsUsed().Select(c => c.GetString().Trim()).Where(h => !string.IsNullOrEmpty(h)).ToList();

        foreach (var row in worksheet.RowsUsed().Skip(1))
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < headers.Count; i++)
                dict[headers[i]] = GetCellText(row.Cell(i + 1));
            rows.Add(dict);
        }
        return rows;
    }

    private static string GetCellText(ClosedXML.Excel.IXLCell cell)
    {
        if (cell.IsEmpty())
            return string.Empty;

        return cell.DataType switch
        {
            ClosedXML.Excel.XLDataType.Number => cell.GetDouble().ToString(CultureInfo.InvariantCulture),
            ClosedXML.Excel.XLDataType.Boolean => cell.GetBoolean().ToString(),
            _ => cell.GetFormattedString().Trim()
        };
    }

    private static string? GetValue(Dictionary<string, string> row, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (row.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }
        return null;
    }

    private static bool ParseBool(string? value) =>
        value != null && (value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                          value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                          value == "1" ||
                          value.Equals("y", StringComparison.OrdinalIgnoreCase));

    private static decimal ParseGoogleRating(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return 0m;

        if (!TryParseNumber(value, out var d))
            return 0m;

        if (d < 0m || d > 5m)
            return 0m;

        return Math.Round(d, 2);
    }

    private static int ParseReviewCount(string? value)
    {
        if (!TryParseNumber(value, out var d))
            return 0;
        return Math.Max(0, (int)Math.Truncate(d));
    }

    private static decimal ClampGoogleRating(decimal rating)
    {
        if (rating < 0m || rating > 5m)
            return 0m;
        return Math.Round(rating, 2);
    }

    private static bool TryParseNumber(string? value, out decimal result)
    {
        result = 0m;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out result))
            return true;

        var cleaned = new string(value.Where(c => char.IsDigit(c) || c == '.' || c == ',').ToArray())
            .Replace(',', '.');
        return decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out result);
    }

    private static decimal ParseDecimal(string? value) =>
        TryParseNumber(value, out var d) ? d : 0m;

    private static int ParseInt(string? value) =>
        int.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var i) ? i : 0;

    private static Gender ParseGender(string? value) => value?.ToLowerInvariant() switch
    {
        "male" or "m" => Gender.Male,
        "female" or "f" => Gender.Female,
        _ => Gender.Other
    };

    private static string ParseCityFromLocation(string? location)
    {
        if (string.IsNullOrWhiteSpace(location)) return "Unknown";
        var parts = location.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[0] : location;
    }

    private static string ParseStateFromLocation(string? location)
    {
        if (string.IsNullOrWhiteSpace(location)) return "NA";
        var parts = location.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 1 ? parts[1] : "NA";
    }

    private static string BuildInitials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => !p.Equals("Dr.", StringComparison.OrdinalIgnoreCase) && !p.Equals("Dr", StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .Select(p => p[0])
            .ToArray();
        return parts.Length > 0 ? new string(parts).ToUpperInvariant() : "DR";
    }
}
