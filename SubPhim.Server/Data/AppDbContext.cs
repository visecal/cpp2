using Microsoft.EntityFrameworkCore;

namespace SubPhim.Server.Data
{
    // Lớp này là cầu nối trực tiếp giữa code C# và database
    public class AppDbContext : DbContext
    {
        public DbSet<TierDefaultSetting> TierDefaultSettings { get; set; }
        public DbSet<LocalApiSetting> LocalApiSettings { get; set; }
        public DbSet<User> Users { get; set; }
        public DbSet<Device> Devices { get; set; }
        public DbSet<BannedDevice> BannedDevices { get; set; }
        public DbSet<ManagedApiKey> ManagedApiKeys { get; set; }
        public DbSet<TranslationLog> TranslationLogs {get; set;}
        public DbSet<AvailableApiModel> AvailableApiModels { get; set; }
        public DbSet<TranslationJobDb> TranslationJobs { get; set; }
        public DbSet<OriginalSrtLineDb> OriginalSrtLines { get; set; }
        public DbSet<TranslatedSrtLineDb> TranslatedSrtLines { get; set; }
        public DbSet<TtsApiKey> TtsApiKeys { get; set; }

        public DbSet<TtsModelSetting> TtsModelSettings { get; set; }
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options){}
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Cấu hình mối quan hệ giữa TranslationJobDb và OriginalSrtLineDb
            modelBuilder.Entity<TranslationJobDb>()
                .HasMany(j => j.OriginalLines) // Một Job có nhiều Dòng Gốc
                .WithOne(l => l.Job)           // Một Dòng Gốc thuộc về một Job
                .HasForeignKey(l => l.SessionId) // Khóa ngoại là SessionId
                .OnDelete(DeleteBehavior.Cascade); // <<< ĐÂY LÀ DÒNG QUAN TRỌNG NHẤT

            // Cấu hình mối quan hệ giữa TranslationJobDb và TranslatedSrtLineDb
            modelBuilder.Entity<TranslationJobDb>()
                .HasMany(j => j.TranslatedLines) // Một Job có nhiều Dòng Đã Dịch
                .WithOne(l => l.Job)              // Một Dòng Đã Dịch thuộc về một Job
                .HasForeignKey(l => l.SessionId)  // Khóa ngoại là SessionId
                .OnDelete(DeleteBehavior.Cascade); // <<< VÀ DÒNG NÀY
            modelBuilder.Entity<User>()
                .HasMany(u => u.Devices)
                .WithOne(d => d.User)
                .HasForeignKey(d => d.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        }

    }
}