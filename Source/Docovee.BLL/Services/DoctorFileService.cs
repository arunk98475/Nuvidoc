using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Docovee.BLL.Configuration;

namespace Docovee.BLL.Services;

public interface IDoctorFileService
{
    Task<string?> SaveUploadedPhotoAsync(IFormFile file, CancellationToken cancellationToken = default);
    Task<string?> DownloadAndSavePhotoAsync(string url, CancellationToken cancellationToken = default);
}

public class DoctorFileService : IDoctorFileService
{
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".gif"
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly UploadOptions _options;

    public DoctorFileService(IHttpClientFactory httpClientFactory, IOptions<UploadOptions> options)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        Directory.CreateDirectory(_options.DoctorsPhysicalPath);
    }

    public async Task<string?> SaveUploadedPhotoAsync(IFormFile file, CancellationToken cancellationToken = default)
    {
        if (file == null || file.Length == 0)
            return null;

        var ext = Path.GetExtension(file.FileName);
        if (string.IsNullOrEmpty(ext) || !AllowedExtensions.Contains(ext))
            return null;

        var fileName = $"{Guid.NewGuid():N}{ext.ToLowerInvariant()}";
        var fullPath = Path.Combine(_options.DoctorsPhysicalPath, fileName);

        await using var stream = new FileStream(fullPath, FileMode.Create);
        await file.CopyToAsync(stream, cancellationToken);

        return $"{_options.DoctorsPublicPath.TrimEnd('/')}/{fileName}";
    }

    public async Task<string?> DownloadAndSavePhotoAsync(string url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
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

            var fileName = $"{Guid.NewGuid():N}{ext}";
            var fullPath = Path.Combine(_options.DoctorsPhysicalPath, fileName);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var fileStream = new FileStream(fullPath, FileMode.Create);
            await stream.CopyToAsync(fileStream, cancellationToken);

            return $"{_options.DoctorsPublicPath.TrimEnd('/')}/{fileName}";
        }
        catch
        {
            return null;
        }
    }
}
