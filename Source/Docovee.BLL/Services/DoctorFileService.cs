using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Docovee.BLL.Configuration;

namespace Docovee.BLL.Services;

public interface IDoctorFileService
{
    long MaxVideoBytes { get; }
    int MaxVideoMb { get; }
    Task<string?> SaveUploadedPhotoAsync(int doctorId, IFormFile file, CancellationToken cancellationToken = default);
    Task<string?> SaveUploadedVideoAsync(int doctorId, IFormFile file, long? maxBytes = null, CancellationToken cancellationToken = default);
    Task<string?> DownloadAndSavePhotoAsync(int doctorId, string url, CancellationToken cancellationToken = default);
}

public class DoctorFileService : IDoctorFileService
{
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".gif"
    };

    private static readonly HashSet<string> AllowedVideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".webm", ".ogg", ".mov", ".m4v"
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly UploadOptions _options;

    public DoctorFileService(IHttpClientFactory httpClientFactory, IOptions<UploadOptions> options)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
    }

    public long MaxVideoBytes => _options.MaxUploadBytes;
    public int MaxVideoMb => _options.MaxUploadMb;

    private string DoctorPhysicalPath(int doctorId) =>
        Path.Combine(_options.DoctorsPhysicalPath, doctorId.ToString());

    private string DoctorPublicPath(int doctorId) =>
        $"{_options.DoctorsPublicPath.TrimEnd('/')}/{doctorId}";

    private string VideoPhysicalPath(int doctorId) =>
        Path.Combine(DoctorPhysicalPath(doctorId), "videos");

    private string VideoPublicPath(int doctorId) =>
        $"{DoctorPublicPath(doctorId)}/videos";

    public async Task<string?> SaveUploadedVideoAsync(
        int doctorId,
        IFormFile file,
        long? maxBytes = null,
        CancellationToken cancellationToken = default)
    {
        if (doctorId <= 0 || file == null || file.Length == 0)
            return null;

        var limit = maxBytes ?? MaxVideoBytes;
        if (limit > 0 && file.Length > limit)
            return null;

        var ext = Path.GetExtension(file.FileName);
        if (string.IsNullOrEmpty(ext) || !AllowedVideoExtensions.Contains(ext))
            return null;

        var videoDir = VideoPhysicalPath(doctorId);
        Directory.CreateDirectory(videoDir);

        var fileName = $"{Guid.NewGuid():N}{ext.ToLowerInvariant()}";
        var fullPath = Path.Combine(videoDir, fileName);

        await using var stream = new FileStream(fullPath, FileMode.Create);
        await file.CopyToAsync(stream, cancellationToken);

        return $"{VideoPublicPath(doctorId)}/{fileName}";
    }

    public async Task<string?> SaveUploadedPhotoAsync(
        int doctorId,
        IFormFile file,
        CancellationToken cancellationToken = default)
    {
        if (doctorId <= 0 || file == null || file.Length == 0)
            return null;

        var ext = Path.GetExtension(file.FileName);
        if (string.IsNullOrEmpty(ext) || !AllowedExtensions.Contains(ext))
            return null;

        var doctorDir = DoctorPhysicalPath(doctorId);
        Directory.CreateDirectory(doctorDir);

        var fileName = $"{Guid.NewGuid():N}{ext.ToLowerInvariant()}";
        var fullPath = Path.Combine(doctorDir, fileName);

        await using var stream = new FileStream(fullPath, FileMode.Create);
        await file.CopyToAsync(stream, cancellationToken);

        return $"{DoctorPublicPath(doctorId)}/{fileName}";
    }

    public async Task<string?> DownloadAndSavePhotoAsync(
        int doctorId,
        string url,
        CancellationToken cancellationToken = default)
    {
        if (doctorId <= 0 || string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        try
        {
            var client = _httpClientFactory.CreateClient("DoctorPhotoDownload");
            using var response = await client.GetAsync(uri, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;

            var contentType = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
            var ext = contentType switch
            {
                "image/png" => ".png",
                "image/webp" => ".webp",
                "image/gif" => ".gif",
                _ => ".jpg"
            };

            var doctorDir = DoctorPhysicalPath(doctorId);
            Directory.CreateDirectory(doctorDir);

            var fileName = $"{Guid.NewGuid():N}{ext}";
            var fullPath = Path.Combine(doctorDir, fileName);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var fileStream = new FileStream(fullPath, FileMode.Create);
            await stream.CopyToAsync(fileStream, cancellationToken);

            return $"{DoctorPublicPath(doctorId)}/{fileName}";
        }
        catch
        {
            return null;
        }
    }
}
