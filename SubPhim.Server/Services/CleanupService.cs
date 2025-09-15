using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SubPhim.Server.Data;

namespace SubPhim.Server.Services
{
    public class CleanupService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<CleanupService> _logger;
        private readonly TimeSpan _period = TimeSpan.FromMinutes(5);

        public CleanupService(IServiceProvider serviceProvider, ILogger<CleanupService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);

            using var timer = new PeriodicTimer(_period);

            while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    _logger.LogInformation("Cleanup Service is running.");

                    using var scope = _serviceProvider.CreateScope();
                    var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var srtThreshold = DateTime.UtcNow.AddMinutes(-10);
                    var srtJobsToDelete = await context.TranslationJobs
                        .Where(j => (j.Status == JobStatus.Completed || j.Status == JobStatus.Failed) && j.CreatedAt < srtThreshold)
                        .Select(j => j.SessionId)
                        .ToListAsync(stoppingToken);

                    if (srtJobsToDelete.Any())
                    {
                        var deletedRows = await context.TranslationJobs
                            .Where(j => srtJobsToDelete.Contains(j.SessionId))
                            .ExecuteDeleteAsync(stoppingToken);
                        _logger.LogInformation("Cleanup Service successfully deleted {Count} old SRT job(s).", deletedRows);
                    }
                    else
                    {
                        _logger.LogInformation("No old SRT jobs found to delete.");
                    }
                    _logger.LogInformation("Checking for old AIO translation jobs to delete...");
                    var aioThreshold = DateTime.UtcNow.AddMinutes(-5);

                    var aioDeletedRows = await context.AioTranslationJobs
                        .Where(j => (j.Status == AioJobStatus.Completed || j.Status == AioJobStatus.Failed)
                                    && j.CompletedAt != null 
                                    && j.CompletedAt < aioThreshold)
                        .ExecuteDeleteAsync(stoppingToken);

                    if (aioDeletedRows > 0)
                    {
                        _logger.LogInformation("Cleanup Service successfully deleted {Count} old AIO job(s).", aioDeletedRows);
                    }
                    else
                    {
                        _logger.LogInformation("No old AIO jobs found to delete.");
                    }

                    _logger.LogInformation("Performing database WAL checkpoint...");
                    var connection = context.Database.GetDbConnection() as SqliteConnection;
                    if (connection != null)
                    {
                        await using var command = connection.CreateCommand();
                        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                        try
                        {
                            await connection.OpenAsync(stoppingToken);
                            await command.ExecuteNonQueryAsync(stoppingToken);
                            _logger.LogInformation("WAL checkpoint completed successfully.");
                        }
                        finally
                        {
                            await connection.CloseAsync();
                        }
                    }
                    else
                    {
                        _logger.LogWarning("Could not perform WAL checkpoint because the DB connection is not a SqliteConnection.");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "An error occurred in the Cleanup Service.");
                }
            }
        }
    }
}