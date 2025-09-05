using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection; 
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using SubPhim.Server.Data;
using SubPhim.Server.Models;
using SubPhim.Server.Services;
using SubPhim.Server.Settings;
using System.Security.Claims;
using System.Text;
using System.Globalization; 
using Microsoft.AspNetCore.Localization;
using SubPhim.Server.Middleware;


var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<LocalApiSettings>(
    builder.Configuration.GetSection(LocalApiSettings.SectionName)
);
builder.Services.Configure<UsageLimitsSettings>(
    builder.Configuration.GetSection("UsageLimits")
);
builder.Services.AddScoped<ITtsOrchestratorService, TtsOrchestratorService>();
builder.Services.AddScoped<ITtsSettingsService, TtsSettingsService>();
builder.Services.AddHostedService<TtsKeyResetService>();
builder.Services.AddHttpClient();
builder.Services.AddHostedService<CleanupService>();
builder.Services.AddScoped<IEncryptionService, EncryptionService>();
builder.Services.AddScoped<ITierSettingsService, TierSettingsService>();
builder.Services.AddControllers();
builder.Services.AddSingleton<TranslationOrchestratorService>();
builder.Services.AddMemoryCache();
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    var supportedCultures = new[] { new CultureInfo("en-US") };
    options.DefaultRequestCulture = new RequestCulture("en-US");
    options.SupportedCultures = supportedCultures;
    options.SupportedUICultures = supportedCultures;
});
builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/Admin", "AdminPolicy");
    options.Conventions.AllowAnonymousToPage("/Admin/Login");
});
var dbConnectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FLY_APP_NAME")))
{
    dbConnectionString = "Data Source=/data/subphim.db";
}
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(dbConnectionString));

if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FLY_APP_NAME")))
{
    var dataProtectionPath = new DirectoryInfo("/data/keys");
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(dataProtectionPath)
        .SetApplicationName("SubPhimApp");
}
else
{
    builder.Services.AddDataProtection();
}
var jwtKey = "SubPhim-Super-Secret-Key-For-JWT-Authentication-2024-@!#$";
var key = Encoding.ASCII.GetBytes(jwtKey);
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.RequireHttpsMetadata = false;
        options.SaveToken = true;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(key),
            ValidateIssuer = false,
            ValidateAudience = false
        };
    })
    .AddCookie("AdminCookie", options =>
    {
        options.Cookie.Name = "SubPhim.AdminAuth";
        options.LoginPath = "/Admin/Login";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
    });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminPolicy", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.AddAuthenticationSchemes("AdminCookie");
        policy.RequireClaim("Admin", "true");
    });
});
var app = builder.Build();
app.UseMiddleware<RequestLoggingMiddleware>();
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

app.UseRouting();
app.UseRequestLocalization();
app.UseAuthentication();

app.UseAuthorization();

app.MapControllers();
app.MapRazorPages();


using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    var logger = services.GetRequiredService<ILogger<Program>>();
    try
    {
        var context = services.GetRequiredService<AppDbContext>();
        logger.LogInformation("Attempting to connect to the database...");
        if (context.Database.CanConnect())
        {
            logger.LogInformation(">>>>>> DATABASE CONNECTION SUCCESSFUL. <<<<<<");
        }
        else
        {
            logger.LogCritical("!!!!!!!! DATABASE CONNECTION FAILED. CHECK CONNECTION STRING AND FILE PERMISSIONS. !!!!!!!");
        }
        context.Database.Migrate();

        var adminUsername = "admin";
        var defaultAdminPassword = "AdminMatKhauMoi123!";
        var adminUser = context.Users.FirstOrDefault(u => u.Username == adminUsername);

        if (adminUser == null)
        {
            logger.LogInformation("Admin user not found. Creating a new one.");
            adminUser = new User
            {
                Uid = new Random().Next(100_000_000, 1_000_000_000).ToString(),
                Username = adminUsername,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(defaultAdminPassword),
                Tier = SubscriptionTier.Lifetime,
                SubscriptionExpiry = DateTime.UtcNow.AddYears(100),
                IsBlocked = false,
                CreatedAt = DateTime.UtcNow,
                MaxDevices = 99,
                GrantedFeatures = (GrantedFeatures)Enum.GetValues(typeof(GrantedFeatures)).Cast<int>().Sum(),
                AllowedApiAccess = (AllowedApis)Enum.GetValues(typeof(AllowedApis)).Cast<int>().Sum(),
                IsAdmin = true // Đảm bảo quyền admin
            };
            context.Users.Add(adminUser);
        }
        else
        {
            logger.LogInformation("Admin user found. Ensuring permissions are correctly set.");
            adminUser.IsAdmin = true; 
            adminUser.Tier = SubscriptionTier.Lifetime; 
            adminUser.IsBlocked = false; 
        }

        context.SaveChanges(); 
        logger.LogInformation("Admin user seeding/update completed successfully.");
        try
        {
            if (!context.TierDefaultSettings.Any())
            {
                logger.LogInformation("TierDefaultSettings table is empty. Seeding from appsettings.json...");
                var usageLimitsSettings = services.GetRequiredService<IOptions<UsageLimitsSettings>>().Value;

                foreach (SubscriptionTier tierValue in Enum.GetValues(typeof(SubscriptionTier)))
                {
                    var tierConfigFromFile = tierValue switch
                    {
                        SubscriptionTier.Free => usageLimitsSettings.Free,
                        SubscriptionTier.Daily => usageLimitsSettings.Daily,
                        SubscriptionTier.Monthly => usageLimitsSettings.Monthly,
                        SubscriptionTier.Yearly => usageLimitsSettings.Yearly,
                        SubscriptionTier.Lifetime => usageLimitsSettings.Lifetime,
                        _ => null
                    };

                    if (tierConfigFromFile != null)
                    {
                        var newDefaultSetting = new TierDefaultSetting
                        {
                            Tier = tierValue,
                            VideoDurationMinutes = tierConfigFromFile.VideoDurationMinutes,
                            DailyVideoCount = tierConfigFromFile.DailyVideoCount,
                            DailyTranslationRequests = tierConfigFromFile.DailyTranslationRequests,
                            DailySrtLineLimit = tierConfigFromFile.DailySrtLineLimit,
                            AllowedApis = Enum.TryParse<AllowedApis>(tierConfigFromFile.AllowedApis, true, out var apis) ? apis : AllowedApis.None,
                            GrantedFeatures = Enum.TryParse<GrantedFeatures>(tierConfigFromFile.GrantedFeatures, true, out var features) ? features : GrantedFeatures.None
                        };
                        context.TierDefaultSettings.Add(newDefaultSetting);
                    }
                }
                context.SaveChanges();
                logger.LogInformation("Successfully seeded TierDefaultSettings.");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "An error occurred during TierDefaultSettings seeding.");
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "An error occurred during DB migration or admin user seeding.");
    }
    try
    {
        var context = services.GetRequiredService<AppDbContext>();
        if (!context.TtsModelSettings.Any())
        {
            logger.LogInformation("Seeding default TTS Model Settings...");

            var defaultSettings = new List<TtsModelSetting>
            {
                new TtsModelSetting { Provider = TtsProvider.Gemini, Identifier = "Pro", ModelName = "gemini-2.5-pro-preview-tts", MaxRequestsPerDay = 100, MaxRequestsPerMinute = 10 },
                new TtsModelSetting { Provider = TtsProvider.Gemini, Identifier = "Flash", ModelName = "gemini-2.5-flash-preview-tts", MaxRequestsPerDay = 15, MaxRequestsPerMinute = 3 },
                new TtsModelSetting { Provider = TtsProvider.ElevenLabs, Identifier = "ElevenLabs", ModelName = "eleven_multilingual_v2", MaxRequestsPerDay = -1, MaxRequestsPerMinute = -1 }
            };

            context.TtsModelSettings.AddRange(defaultSettings);
            context.SaveChanges();
            logger.LogInformation("Successfully seeded TTS Model Settings.");
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "An error occurred during TTS Model Settings seeding.");
    }
}



// --- 6. Chạy ứng dụng ---
// Cấu hình cổng lắng nghe linh hoạt
if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FLY_APP_NAME")))
{
    app.Run("http://*:8080");
}
else
{
    app.Run();
}