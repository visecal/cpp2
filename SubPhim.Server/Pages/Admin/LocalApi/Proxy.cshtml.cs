using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SubPhim.Server.Data;
using SubPhim.Server.Services;

namespace SubPhim.Server.Pages.Admin.LocalApi
{
    public class ProxyModel : PageModel
    {
        private readonly AppDbContext _context;
        private readonly ProxyService _proxyService;
        private readonly ILogger<ProxyModel> _logger;

        public ProxyModel(AppDbContext context, ProxyService proxyService, ILogger<ProxyModel> logger)
        {
            _context = context;
            _proxyService = proxyService;
            _logger = logger;
        }

        public List<Proxy> Proxies { get; set; } = new();
        public int ActiveProxyCount { get; set; }
        
        /// <summary>
        /// Kết quả test tốc độ gần nhất (hiển thị trong thông báo)
        /// </summary>
        public string? SpeedTestResult { get; set; }

        [TempData] public string SuccessMessage { get; set; }
        [TempData] public string ErrorMessage { get; set; }

        public async Task OnGetAsync()
        {
            await LoadDataAsync();
        }

        private async Task LoadDataAsync()
        {
            Proxies = await _context.Proxies
                .OrderByDescending(p => p.IsEnabled)
                .ThenBy(p => p.SpeedMs > 0 ? p.SpeedMs : int.MaxValue) // Sắp xếp theo tốc độ (nhanh nhất trước)
                .ThenByDescending(p => p.CreatedAt)
                .ToListAsync();
            
            ActiveProxyCount = Proxies.Count(p => p.IsEnabled);
        }

        /// <summary>
        /// Thêm proxy từ danh sách text - CÓ KIỂM TRA TỐC ĐỘ
        /// </summary>
        public async Task<IActionResult> OnPostAddProxiesAsync([FromForm] string proxyList)
        {
            if (string.IsNullOrWhiteSpace(proxyList))
            {
                ErrorMessage = "Vui lòng nhập ít nhất một proxy.";
                return RedirectToPage();
            }

            try
            {
                var parsedProxies = _proxyService.ParseProxyList(proxyList);
                
                if (!parsedProxies.Any())
                {
                    ErrorMessage = "Không có proxy hợp lệ nào được tìm thấy. Vui lòng kiểm tra định dạng.";
                    return RedirectToPage();
                }

                int addedCount = 0;
                int duplicateCount = 0;
                var newProxies = new List<Proxy>();

                foreach (var proxy in parsedProxies)
                {
                    // Kiểm tra trùng lặp
                    var exists = await _context.Proxies.AnyAsync(p => 
                        p.Host == proxy.Host && p.Port == proxy.Port);
                    
                    if (exists)
                    {
                        duplicateCount++;
                        continue;
                    }

                    _context.Proxies.Add(proxy);
                    newProxies.Add(proxy);
                    addedCount++;
                }

                await _context.SaveChangesAsync();
                
                // Kiểm tra tốc độ các proxy mới thêm
                if (newProxies.Any())
                {
                    _logger.LogInformation("Testing speed for {Count} newly added proxies...", newProxies.Count);
                    
                    // Tạo HashSet để lookup hiệu quả hơn
                    var newProxyKeys = newProxies.Select(np => $"{np.Host}:{np.Port}").ToHashSet();
                    
                    // Reload để có ID - sử dụng AsEnumerable để filter client-side với HashSet
                    var addedProxiesWithIds = (await _context.Proxies.ToListAsync())
                        .Where(p => newProxyKeys.Contains($"{p.Host}:{p.Port}"))
                        .ToList();
                    
                    var speedResults = await _proxyService.TestMultipleProxiesSpeedAsync(addedProxiesWithIds);
                    
                    int workingCount = speedResults.Count(r => r.SpeedMs > 0);
                    int deadCount = speedResults.Count(r => r.SpeedMs == 0);
                    
                    if (duplicateCount > 0)
                    {
                        SuccessMessage = $"Đã thêm {addedCount} proxy ({workingCount} hoạt động, {deadCount} không kết nối được). Bỏ qua {duplicateCount} proxy trùng lặp.";
                    }
                    else
                    {
                        SuccessMessage = $"Đã thêm {addedCount} proxy ({workingCount} hoạt động, {deadCount} không kết nối được).";
                    }
                }
                else
                {
                    if (duplicateCount > 0)
                    {
                        SuccessMessage = $"Bỏ qua {duplicateCount} proxy trùng lặp. Không có proxy mới nào được thêm.";
                    }
                }
                
                // Refresh cache
                _proxyService.RefreshCache();

                _logger.LogInformation("Added {Count} proxies, skipped {Duplicates} duplicates", addedCount, duplicateCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding proxies");
                ErrorMessage = $"Lỗi khi thêm proxy: {ex.Message}";
            }

            return RedirectToPage();
        }

        /// <summary>
        /// Kiểm tra tốc độ tất cả proxy đang enabled
        /// </summary>
        public async Task<IActionResult> OnPostTestAllSpeedAsync()
        {
            try
            {
                var enabledProxies = await _context.Proxies.Where(p => p.IsEnabled).ToListAsync();
                
                if (!enabledProxies.Any())
                {
                    ErrorMessage = "Không có proxy nào đang enabled để kiểm tra.";
                    return RedirectToPage();
                }
                
                _logger.LogInformation("Starting speed test for {Count} enabled proxies", enabledProxies.Count);
                
                var results = await _proxyService.TestMultipleProxiesSpeedAsync(enabledProxies);
                
                int workingCount = results.Count(r => r.SpeedMs > 0);
                int deadCount = results.Count(r => r.SpeedMs == 0);
                var avgSpeed = results.Where(r => r.SpeedMs > 0).Select(r => r.SpeedMs).DefaultIfEmpty(0).Average();
                
                SuccessMessage = $"Đã kiểm tra {results.Count} proxy: {workingCount} hoạt động (TB: {avgSpeed:F0}ms), {deadCount} không kết nối được (đã tắt).";
                
                _logger.LogInformation("Speed test completed: {Working} working, {Dead} dead", workingCount, deadCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error testing proxy speeds");
                ErrorMessage = $"Lỗi khi kiểm tra tốc độ: {ex.Message}";
            }
            
            return RedirectToPage();
        }

        /// <summary>
        /// Bật/tắt proxy
        /// </summary>
        public async Task<IActionResult> OnPostToggleProxyAsync(int id)
        {
            var proxy = await _context.Proxies.FindAsync(id);
            if (proxy != null)
            {
                proxy.IsEnabled = !proxy.IsEnabled;
                await _context.SaveChangesAsync();
                _proxyService.RefreshCache();
                
                _logger.LogInformation("Proxy {Id} ({Host}:{Port}) toggled to {Status}", 
                    id, proxy.Host, proxy.Port, proxy.IsEnabled ? "ON" : "OFF");
            }
            return RedirectToPage();
        }

        /// <summary>
        /// Xóa một proxy
        /// </summary>
        public async Task<IActionResult> OnPostDeleteProxyAsync(int id)
        {
            var proxy = await _context.Proxies.FindAsync(id);
            if (proxy != null)
            {
                _context.Proxies.Remove(proxy);
                await _context.SaveChangesAsync();
                _proxyService.RefreshCache();
                
                SuccessMessage = $"Đã xóa proxy {proxy.Host}:{proxy.Port}";
                _logger.LogInformation("Deleted proxy {Id} ({Host}:{Port})", id, proxy.Host, proxy.Port);
            }
            return RedirectToPage();
        }

        /// <summary>
        /// Reset thống kê proxy
        /// </summary>
        public async Task<IActionResult> OnPostResetProxyStatsAsync(int id)
        {
            var proxy = await _context.Proxies.FindAsync(id);
            if (proxy != null)
            {
                proxy.UsageCount = 0;
                proxy.FailureCount = 0;
                proxy.LastUsedAt = null;
                proxy.LastFailedAt = null;
                proxy.LastFailureReason = null;
                
                await _context.SaveChangesAsync();
                _proxyService.RefreshCache();
                
                SuccessMessage = $"Đã reset thống kê proxy {proxy.Host}:{proxy.Port}";
                _logger.LogInformation("Reset stats for proxy {Id} ({Host}:{Port})", id, proxy.Host, proxy.Port);
            }
            return RedirectToPage();
        }

        /// <summary>
        /// Xóa nhiều proxy đã chọn
        /// </summary>
        public async Task<IActionResult> OnPostDeleteSelectedProxiesAsync([FromForm] int[] selectedProxyIds)
        {
            if (selectedProxyIds == null || !selectedProxyIds.Any())
            {
                ErrorMessage = "Vui lòng chọn ít nhất một proxy để xóa.";
                return RedirectToPage();
            }

            var proxiesToDelete = await _context.Proxies
                .Where(p => selectedProxyIds.Contains(p.Id))
                .ToListAsync();

            if (proxiesToDelete.Any())
            {
                _context.Proxies.RemoveRange(proxiesToDelete);
                await _context.SaveChangesAsync();
                _proxyService.RefreshCache();
                
                SuccessMessage = $"Đã xóa {proxiesToDelete.Count} proxy.";
                _logger.LogInformation("Deleted {Count} proxies", proxiesToDelete.Count);
            }

            return RedirectToPage();
        }

        /// <summary>
        /// Tắt nhiều proxy đã chọn
        /// </summary>
        public async Task<IActionResult> OnPostDisableSelectedProxiesAsync([FromForm] int[] selectedProxyIds)
        {
            if (selectedProxyIds == null || !selectedProxyIds.Any())
            {
                ErrorMessage = "Vui lòng chọn ít nhất một proxy để tắt.";
                return RedirectToPage();
            }

            var proxiesToDisable = await _context.Proxies
                .Where(p => selectedProxyIds.Contains(p.Id))
                .ToListAsync();

            if (proxiesToDisable.Any())
            {
                foreach (var proxy in proxiesToDisable)
                {
                    proxy.IsEnabled = false;
                }
                await _context.SaveChangesAsync();
                _proxyService.RefreshCache();
                
                SuccessMessage = $"Đã tắt {proxiesToDisable.Count} proxy.";
                _logger.LogInformation("Disabled {Count} proxies", proxiesToDisable.Count);
            }

            return RedirectToPage();
        }

        /// <summary>
        /// Bật nhiều proxy đã chọn
        /// </summary>
        public async Task<IActionResult> OnPostEnableSelectedProxiesAsync([FromForm] int[] selectedProxyIds)
        {
            if (selectedProxyIds == null || !selectedProxyIds.Any())
            {
                ErrorMessage = "Vui lòng chọn ít nhất một proxy để bật.";
                return RedirectToPage();
            }

            var proxiesToEnable = await _context.Proxies
                .Where(p => selectedProxyIds.Contains(p.Id))
                .ToListAsync();

            if (proxiesToEnable.Any())
            {
                foreach (var proxy in proxiesToEnable)
                {
                    proxy.IsEnabled = true;
                    // Reset failure count khi bật lại
                    proxy.FailureCount = 0;
                    proxy.LastFailureReason = null;
                }
                await _context.SaveChangesAsync();
                _proxyService.RefreshCache();
                
                SuccessMessage = $"Đã bật {proxiesToEnable.Count} proxy.";
                _logger.LogInformation("Enabled {Count} proxies", proxiesToEnable.Count);
            }

            return RedirectToPage();
        }

        /// <summary>
        /// Xóa tất cả proxy
        /// </summary>
        public async Task<IActionResult> OnPostDeleteAllProxiesAsync()
        {
            var allProxies = await _context.Proxies.ToListAsync();
            
            if (allProxies.Any())
            {
                _context.Proxies.RemoveRange(allProxies);
                await _context.SaveChangesAsync();
                _proxyService.RefreshCache();
                
                SuccessMessage = $"Đã xóa tất cả {allProxies.Count} proxy.";
                _logger.LogInformation("Deleted all {Count} proxies", allProxies.Count);
            }
            else
            {
                ErrorMessage = "Không có proxy nào để xóa.";
            }

            return RedirectToPage();
        }
    }
}
