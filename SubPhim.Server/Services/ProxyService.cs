using Microsoft.EntityFrameworkCore;
using SubPhim.Server.Data;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace SubPhim.Server.Services
{
    /// <summary>
    /// Service để quản lý và luân phiên proxy cho các request HTTP đến Google API
    /// Hỗ trợ SOCKS4, SOCKS5 và HTTP proxy
    /// </summary>
    public class ProxyService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<ProxyService> _logger;
        
        // Round-robin index cho việc chọn proxy
        private static int _proxyRoundRobinIndex = 0;
        private static readonly object _proxyLock = new();
        
        // Cache danh sách proxy để tránh query DB quá nhiều
        private static List<Proxy> _cachedProxies = new();
        private static DateTime _lastCacheUpdate = DateTime.MinValue;
        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(1);

        public ProxyService(IServiceProvider serviceProvider, ILogger<ProxyService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        /// <summary>
        /// Lấy proxy tiếp theo theo round-robin
        /// </summary>
        public async Task<Proxy?> GetNextProxyAsync()
        {
            var proxies = await GetEnabledProxiesAsync();
            
            if (!proxies.Any())
            {
                _logger.LogWarning("Không có proxy nào được kích hoạt. Request sẽ được gửi trực tiếp.");
                return null;
            }

            lock (_proxyLock)
            {
                if (_proxyRoundRobinIndex >= proxies.Count)
                    _proxyRoundRobinIndex = 0;
                
                var selectedProxy = proxies[_proxyRoundRobinIndex];
                _proxyRoundRobinIndex++;
                
                _logger.LogDebug("Selected proxy {ProxyId}: {Host}:{Port} ({Type})", 
                    selectedProxy.Id, selectedProxy.Host, selectedProxy.Port, selectedProxy.Type);
                
                return selectedProxy;
            }
        }

        /// <summary>
        /// Lấy danh sách proxy đang hoạt động (có cache)
        /// </summary>
        private async Task<List<Proxy>> GetEnabledProxiesAsync()
        {
            // Kiểm tra cache
            if (DateTime.UtcNow - _lastCacheUpdate < CacheDuration && _cachedProxies.Any())
            {
                return _cachedProxies;
            }

            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            
            _cachedProxies = await context.Proxies
                .Where(p => p.IsEnabled)
                .OrderBy(p => p.Id)
                .ToListAsync();
            
            _lastCacheUpdate = DateTime.UtcNow;
            
            _logger.LogInformation("Refreshed proxy cache: {Count} proxies available", _cachedProxies.Count);
            
            return _cachedProxies;
        }

        /// <summary>
        /// Làm mới cache proxy (gọi khi có thay đổi từ Admin panel)
        /// </summary>
        public void RefreshCache()
        {
            _lastCacheUpdate = DateTime.MinValue;
            _logger.LogInformation("Proxy cache invalidated, will refresh on next request");
        }

        /// <summary>
        /// Tạo HttpClient với proxy được cấu hình
        /// </summary>
        public HttpClient CreateHttpClientWithProxy(Proxy? proxy)
        {
            if (proxy == null)
            {
                _logger.LogDebug("No proxy specified, creating direct HttpClient");
                return new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            }

            var handler = CreateHttpMessageHandler(proxy);
            var httpClient = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = TimeSpan.FromMinutes(5)
            };

            _logger.LogDebug("Created HttpClient with proxy {ProxyId}: {Type}://{Host}:{Port}", 
                proxy.Id, proxy.Type, proxy.Host, proxy.Port);

            return httpClient;
        }

        /// <summary>
        /// Tạo HttpMessageHandler dựa trên loại proxy
        /// </summary>
        private HttpMessageHandler CreateHttpMessageHandler(Proxy proxy)
        {
            switch (proxy.Type)
            {
                case ProxyType.Http:
                    return CreateHttpProxyHandler(proxy);
                    
                case ProxyType.Socks4:
                case ProxyType.Socks5:
                    return CreateSocksProxyHandler(proxy);
                    
                default:
                    _logger.LogWarning("Unknown proxy type {Type}, using direct connection", proxy.Type);
                    return new HttpClientHandler();
            }
        }

        /// <summary>
        /// Tạo handler cho HTTP proxy
        /// </summary>
        private HttpClientHandler CreateHttpProxyHandler(Proxy proxy)
        {
            var proxyUri = new Uri($"http://{proxy.Host}:{proxy.Port}");
            var webProxy = new WebProxy(proxyUri);
            
            if (!string.IsNullOrEmpty(proxy.Username) && !string.IsNullOrEmpty(proxy.Password))
            {
                webProxy.Credentials = new NetworkCredential(proxy.Username, proxy.Password);
            }

            return new HttpClientHandler
            {
                Proxy = webProxy,
                UseProxy = true
            };
        }

        /// <summary>
        /// Tạo handler cho SOCKS proxy (SOCKS4/SOCKS5)
        /// </summary>
        private SocketsHttpHandler CreateSocksProxyHandler(Proxy proxy)
        {
            var socksScheme = proxy.Type == ProxyType.Socks5 ? "socks5" : "socks4";
            var proxyUri = !string.IsNullOrEmpty(proxy.Username) && !string.IsNullOrEmpty(proxy.Password)
                ? new Uri($"{socksScheme}://{proxy.Username}:{proxy.Password}@{proxy.Host}:{proxy.Port}")
                : new Uri($"{socksScheme}://{proxy.Host}:{proxy.Port}");

            return new SocketsHttpHandler
            {
                Proxy = new WebProxy(proxyUri),
                UseProxy = true,
                ConnectTimeout = TimeSpan.FromSeconds(30)
            };
        }

        /// <summary>
        /// Ghi nhận proxy đã được sử dụng thành công
        /// </summary>
        public async Task RecordProxySuccessAsync(int proxyId)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                
                var proxy = await context.Proxies.FindAsync(proxyId);
                if (proxy != null)
                {
                    proxy.UsageCount++;
                    proxy.LastUsedAt = DateTime.UtcNow;
                    await context.SaveChangesAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to record proxy success for ID {ProxyId}", proxyId);
            }
        }

        /// <summary>
        /// Ghi nhận proxy bị lỗi
        /// </summary>
        public async Task RecordProxyFailureAsync(int proxyId, string reason)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                
                var proxy = await context.Proxies.FindAsync(proxyId);
                if (proxy != null)
                {
                    proxy.FailureCount++;
                    proxy.LastFailedAt = DateTime.UtcNow;
                    proxy.LastFailureReason = reason?.Length > 500 ? reason.Substring(0, 500) : reason;
                    
                    // Tự động vô hiệu hóa proxy nếu lỗi quá nhiều (5 lần liên tiếp)
                    if (proxy.FailureCount >= 5)
                    {
                        proxy.IsEnabled = false;
                        _logger.LogWarning("Proxy {ProxyId} ({Host}:{Port}) disabled due to too many failures", 
                            proxyId, proxy.Host, proxy.Port);
                        RefreshCache(); // Refresh cache khi proxy bị disable
                    }
                    
                    await context.SaveChangesAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to record proxy failure for ID {ProxyId}", proxyId);
            }
        }

        /// <summary>
        /// Parse danh sách proxy từ text (mỗi dòng một proxy)
        /// Hỗ trợ các định dạng:
        /// - host:port (mặc định SOCKS5)
        /// - type://host:port (type = http, socks4, socks5)
        /// - type://username:password@host:port (có authentication)
        /// </summary>
        public List<Proxy> ParseProxyList(string proxyText)
        {
            var proxies = new List<Proxy>();
            
            if (string.IsNullOrWhiteSpace(proxyText))
                return proxies;

            var lines = proxyText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            
            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();
                if (string.IsNullOrEmpty(trimmedLine) || trimmedLine.StartsWith("#"))
                    continue;

                var proxy = ParseProxyLine(trimmedLine);
                if (proxy != null)
                {
                    proxies.Add(proxy);
                }
            }

            _logger.LogInformation("Parsed {Count} proxies from text input", proxies.Count);
            return proxies;
        }

        private Proxy? ParseProxyLine(string line)
        {
            try
            {
                var proxy = new Proxy();
                
                // Xác định loại proxy
                if (line.StartsWith("socks5://", StringComparison.OrdinalIgnoreCase))
                {
                    proxy.Type = ProxyType.Socks5;
                    line = line.Substring(9);
                }
                else if (line.StartsWith("socks4://", StringComparison.OrdinalIgnoreCase))
                {
                    proxy.Type = ProxyType.Socks4;
                    line = line.Substring(9);
                }
                else if (line.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                {
                    proxy.Type = ProxyType.Http;
                    line = line.Substring(7);
                }
                else if (line.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    proxy.Type = ProxyType.Http;
                    line = line.Substring(8);
                }
                else
                {
                    // Mặc định là SOCKS5
                    proxy.Type = ProxyType.Socks5;
                }

                // Kiểm tra authentication (username:password@host:port)
                var atIndex = line.LastIndexOf('@');
                if (atIndex > 0)
                {
                    var auth = line.Substring(0, atIndex);
                    var hostPort = line.Substring(atIndex + 1);
                    
                    var authParts = auth.Split(':');
                    if (authParts.Length >= 2)
                    {
                        proxy.Username = authParts[0];
                        proxy.Password = string.Join(":", authParts.Skip(1)); // Password có thể chứa ':'
                    }
                    
                    line = hostPort;
                }

                // Parse host:port
                var colonIndex = line.LastIndexOf(':');
                if (colonIndex <= 0)
                {
                    _logger.LogWarning("Invalid proxy format (missing port): {Line}", line);
                    return null;
                }

                proxy.Host = line.Substring(0, colonIndex).Trim();
                var portStr = line.Substring(colonIndex + 1).Trim();
                
                if (!int.TryParse(portStr, out int port) || port <= 0 || port > 65535)
                {
                    _logger.LogWarning("Invalid proxy port: {Port}", portStr);
                    return null;
                }
                
                proxy.Port = port;
                
                if (string.IsNullOrEmpty(proxy.Host))
                {
                    _logger.LogWarning("Empty proxy host");
                    return null;
                }

                return proxy;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse proxy line: {Line}", line);
                return null;
            }
        }

        /// <summary>
        /// Lấy số lượng proxy đang hoạt động
        /// </summary>
        public async Task<int> GetActiveProxyCountAsync()
        {
            var proxies = await GetEnabledProxiesAsync();
            return proxies.Count;
        }
    }
}
