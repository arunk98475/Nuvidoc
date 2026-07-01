using System.Collections.Concurrent;
using Docovee.DS.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Docovee.BLL.Services;

public interface IDoctorImportJobService
{
    Task<string> StartImportAsync(byte[] fileBytes, string fileName, CancellationToken cancellationToken = default);
    ImportJobStatus? GetStatus(string jobId);
}

public class DoctorImportJobService : IDoctorImportJobService
{
    private readonly ConcurrentDictionary<string, ImportJobStatus> _jobs = new();
    private readonly IServiceScopeFactory _scopeFactory;

    public DoctorImportJobService(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    public Task<string> StartImportAsync(byte[] fileBytes, string fileName, CancellationToken cancellationToken = default)
    {
        var jobId = Guid.NewGuid().ToString("N");
        var status = new ImportJobStatus { JobId = jobId };
        _jobs[jobId] = status;

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<IAdminDoctorService>();

                var progress = new Progress<ImportProgress>(p =>
                {
                    status.TotalRows = p.TotalRows;
                    status.ProcessedRows = p.ProcessedRows;
                    status.Imported = p.Imported;
                    status.Failed = p.Failed;
                    status.Errors = p.Errors.ToList();
                    status.Complete = p.Complete;
                });

                await using var stream = new MemoryStream(fileBytes);
                await service.ImportAsync(stream, fileName, progress, CancellationToken.None);
            }
            catch (Exception ex)
            {
                status.ErrorMessage = ex.Message;
                status.Complete = true;
            }
        }, cancellationToken);

        return Task.FromResult(jobId);
    }

    public ImportJobStatus? GetStatus(string jobId) =>
        _jobs.TryGetValue(jobId, out var status) ? status : null;
}
