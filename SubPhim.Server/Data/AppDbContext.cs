using Microsoft.EntityFrameworkCore;

namespace SubPhim.Server.Data
{
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
            modelBuilder.Entity<TranslationJobDb>()
                .HasMany(j => j.OriginalLines) 
                .WithOne(l => l.Job)        
                .HasForeignKey(l => l.SessionId) 
                .OnDelete(DeleteBehavior.Cascade); 

            modelBuilder.Entity<TranslationJobDb>()
                .HasMany(j => j.TranslatedLines) 
                .WithOne(l => l.Job)   
                .HasForeignKey(l => l.SessionId)  
                .OnDelete(DeleteBehavior.Cascade); 
            modelBuilder.Entity<User>()
                .HasMany(u => u.Devices)
                .WithOne(d => d.User)
                .HasForeignKey(d => d.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        }

    }
}