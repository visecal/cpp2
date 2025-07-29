using Microsoft.Data.Sqlite; // << THÊM DÒNG USING NÀY
using Microsoft.EntityFrameworkCore;
using SubPhim.Server.Data;

namespace SubPhim.Server.Services
{
    public class CleanupService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<CleanupService> _logger;
        // Chuyển về 1 giờ để chạy ở môi trường production
        private readonly TimeSpan _period = TimeSpan.FromSeconds(10);

        public CleanupService(IServiceProvider serviceProvider, ILogger<CleanupService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(_period);

            while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    _logger.LogInformation("Cleanup Service is running.");

                    using var scope = _serviceProvider.CreateScope();
                    var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                    var threshold = DateTime.UtcNow.AddSeconds(-10);
                    var jobsToDelete = await context.TranslationJobs
                        .Where(j => (j.Status == JobStatus.Completed || j.Status == JobStatus.Failed) && j.CreatedAt < threshold)
                        .Select(j => j.SessionId)
                        .ToListAsync(stoppingToken);

                    if (jobsToDelete.Any())
                    {
                        _logger.LogInformation("Found {Count} old jobs to delete.", jobsToDelete.Count);
                        var deletedRows = await context.TranslationJobs
                            .Where(j => jobsToDelete.Contains(j.SessionId))
                            .ExecuteDeleteAsync(stoppingToken);
                        _logger.LogInformation("Cleanup Service successfully deleted {Count} job(s) and their associated data.", deletedRows);
                    }
                    else
                    {
                        _logger.LogInformation("No old jobs found to delete.");
                    }


                    // === PHẦN 2: THỰC HIỆN CHECKPOINT ĐỂ DỌN DẸP FILE WAL ===
                    _logger.LogInformation("Performing database WAL checkpoint...");

                    // Lấy kết nối DB thô từ DbContext
                    var connection = context.Database.GetDbConnection() as SqliteConnection;
                    if (connection != null)
                    {
                        // Tạo một command để thực thi lệnh PRAGMA của SQLite
                        await using var command = connection.CreateCommand();
                        // TRUNCATE là chế độ mạnh nhất: gộp dữ liệu từ WAL vào DB và thu nhỏ file WAL về 0.
                        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";

                        try
                        {
                            await connection.OpenAsync(stoppingToken);
                            await command.ExecuteNonQueryAsync(stoppingToken);
                            _logger.LogInformation("WAL checkpoint completed successfully. The .db-wal file should be truncated.");
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