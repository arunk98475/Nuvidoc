using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Docovee.BLL.Configuration;

namespace Docovee.BLL.Services;

public interface IPatientFileService
{
    Task<string?> SaveInsuranceCardPhotoAsync(IFormFile file, CancellationToken cancellationToken = default);
    Task<string?> SaveIdCardPhotoAsync(IFormFile file, CancellationToken cancellationToken = default);
}

public sealed class PatientFileService : IPatientFileService
{
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".gif"
    };

    private const long MaxBytes = 10L * 1024 * 1024;

    private readonly UploadOptions _options;

    public PatientFileService(IOptions<UploadOptions> options)
    {
        _options = options.Value;
    }

    private string InsuranceCardsPath => Path.Combine(_options.PatientsPhysicalPath, "insurance-cards");
    private string IdCardsPath => Path.Combine(_options.PatientsPhysicalPath, "id-cards");
    private string InsuranceCardsPublicPath => $"{_options.PatientsPublicPath.TrimEnd('/')}/insurance-cards";
    private string IdCardsPublicPath => $"{_options.PatientsPublicPath.TrimEnd('/')}/id-cards";

    public Task<string?> SaveInsuranceCardPhotoAsync(IFormFile file, CancellationToken cancellationToken = default) =>
        SaveAsync(file, InsuranceCardsPath, InsuranceCardsPublicPath, cancellationToken);

    public Task<string?> SaveIdCardPhotoAsync(IFormFile file, CancellationToken cancellationToken = default) =>
        SaveAsync(file, IdCardsPath, IdCardsPublicPath, cancellationToken);

    private static async Task<string?> SaveAsync(
        IFormFile file,
        string physicalDir,
        string publicDir,
        CancellationToken cancellationToken)
    {
        if (file == null || file.Length == 0)
            return null;

        if (file.Length > MaxBytes)
            return null;

        var ext = Path.GetExtension(file.FileName);
        if (string.IsNullOrEmpty(ext) || !AllowedExtensions.Contains(ext))
            return null;

        Directory.CreateDirectory(physicalDir);

        var fileName = $"{Guid.NewGuid():N}{ext.ToLowerInvariant()}";
        var fullPath = Path.Combine(physicalDir, fileName);

        await using var stream = new FileStream(fullPath, FileMode.Create);
        await file.CopyToAsync(stream, cancellationToken);

        return $"{publicDir}/{fileName}";
    }
}
