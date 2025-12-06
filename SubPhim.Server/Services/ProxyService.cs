using Microsoft.EntityFrameworkCore;
using SubPhim.Server.Data;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;

namespace SubPhim.Server.Services
{
    /// <summary>
    /// Service for managing and rotating proxies for Google API HTTP requests.
    /// Supports SOCKS4, SOCKS5 and HTTP proxies.
    /// </summary>
    public class ProxyService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<ProxyService> _logger;
        
        // Configuration constants
        private const int MaxConsecutiveFailuresBeforeDisable = 5;
        private const int MaxFailureReasonLength = 500;
        private const int ProxyConnectTimeoutSeconds = 10; // Gi?m t? 30s xu?ng 10s ?? fail fast
        private const int HttpClientTimeoutMinutes = 3; // Gi?m t? 5 phút xu?ng 3 phút
        
        // Round-robin index for proxy selection
        private static int _proxyRoundRobinIndex = 0;
        private static readonly object _proxyLock = new();
        
        // Proxy cache to avoid excessive DB queries
        private static List<Proxy> _cachedProxies = new();
        private static DateTime _lastCacheUpdate = DateTime.MinValue;
        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(1);

        public ProxyService(IServiceProvider serviceProvider, ILogger<ProxyService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        /// <summary>
        /// Get the next proxy using round-robin selection.
        /// </summary>
        /// <param name="excludeProxyIds">Optional set of proxy IDs to exclude (e.g., proxies that failed in current request)</param>
        public async Task<Proxy?> GetNextProxyAsync(HashSet<int>? excludeProxyIds = null)
        {
            var proxies = await GetEnabledProxiesAsync();
            
            // Filter out excluded proxies
            if (excludeProxyIds != null && excludeProxyIds.Count > 0)
            {
                proxies = proxies.Where(p => !excludeProxyIds.Contains(p.Id)).ToList();
            }
            
            if (!proxies.Any())
            {
                if (excludeProxyIds != null && excludeProxyIds.Count > 0)
                {
                    _logger.LogWarning("No proxies available after excluding {ExcludeCount} failed proxies. Will use direct connection.", 
                        excludeProxyIds.Count);
                }
                else
                {
                    _logger.LogWarning("No proxies are enabled. Requests will be sent directly.");
                }
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
        /// Get list of enabled proxies (with caching).
        /// </summary>
        private async Task<List<Proxy>> GetEnabledProxiesAsync()
        {
            // Check cache validity
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
        /// Invalidate proxy cache (call when changes are made from Admin panel).
        /// </summary>
        public void RefreshCache()
        {
            _lastCacheUpdate = DateTime.MinValue;
            _logger.LogInformation("Proxy cache invalidated, will refresh on next request");
        }

        /// <summary>
        /// Create HttpClient with the configured proxy.
        /// </summary>
        public HttpClient CreateHttpClientWithProxy(Proxy? proxy)
        {
            if (proxy == null)
            {
                _logger.LogDebug("No proxy specified, creating direct HttpClient");
                return new HttpClient { Timeout = TimeSpan.FromMinutes(HttpClientTimeoutMinutes) };
            }

            var handler = CreateHttpMessageHandler(proxy);
            var httpClient = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = TimeSpan.FromMinutes(HttpClientTimeoutMinutes)
            };

            _logger.LogDebug("Created HttpClient with proxy {ProxyId}: {Type}://{Host}:{Port}", 
                proxy.Id, proxy.Type, proxy.Host, proxy.Port);

            return httpClient;
        }

        /// <summary>
        /// Create HttpMessageHandler based on proxy type.
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
        /// Check if proxy has valid authentication credentials.
        /// </summary>
        private static bool HasCredentials(Proxy proxy)
        {
            return !string.IsNullOrEmpty(proxy.Username) && !string.IsNullOrEmpty(proxy.Password);
        }

        /// <summary>
        /// Build proxy URI with optional embedded credentials.
        /// </summary>
        private static Uri BuildProxyUri(string scheme, Proxy proxy)
        {
            if (HasCredentials(proxy))
            {
                // URL-encode username and password to handle special characters
                var encodedUsername = Uri.EscapeDataString(proxy.Username!);
                var encodedPassword = Uri.EscapeDataString(proxy.Password!);
                return new Uri($"{scheme}://{encodedUsername}:{encodedPassword}@{proxy.Host}:{proxy.Port}");
            }
            return new Uri($"{scheme}://{proxy.Host}:{proxy.Port}");
        }

        /// <summary>
        /// Create handler for HTTP proxy.
        /// Uses SocketsHttpHandler with DefaultProxyCredentials for proper HTTPS tunneling
        /// authentication on Linux/Google Cloud VM environments.
        /// </summary>
        private SocketsHttpHandler CreateHttpProxyHandler(Proxy proxy)
        {
            // Build proxy URI with embedded credentials for HTTPS CONNECT tunnel authentication
            // This approach works better on Linux environments and Google Cloud VMs
            var proxyUri = BuildProxyUri("http", proxy);
            var webProxy = new WebProxy(proxyUri);
            
            // Also set credentials on WebProxy for compatibility with some proxy servers
            if (HasCredentials(proxy))
            {
                webProxy.Credentials = new NetworkCredential(proxy.Username, proxy.Password);
            }

            var handler = new SocketsHttpHandler
            {
                Proxy = webProxy,
                UseProxy = true,
                ConnectTimeout = TimeSpan.FromSeconds(ProxyConnectTimeoutSeconds),
                // Set default proxy credentials for CONNECT tunnel authentication
                DefaultProxyCredentials = HasCredentials(proxy)
                    ? new NetworkCredential(proxy.Username, proxy.Password)
                    : null,
                // Bypass SSL certificate validation when using proxy
                // This is needed because some proxies may cause certificate chain issues
                SslOptions = CreateSslOptionsForProxy()
            };

            return handler;
        }

        /// <summary>
        /// Create handler for SOCKS proxy (SOCKS4/SOCKS5).
        /// </summary>
        private SocketsHttpHandler CreateSocksProxyHandler(Proxy proxy)
        {
            var socksScheme = proxy.Type == ProxyType.Socks5 ? "socks5" : "socks4";
            var proxyUri = BuildProxyUri(socksScheme, proxy);

            return new SocketsHttpHandler
            {
                Proxy = new WebProxy(proxyUri),
                UseProxy = true,
                ConnectTimeout = TimeSpan.FromSeconds(ProxyConnectTimeoutSeconds),
                // Bypass SSL certificate validation when using proxy
                SslOptions = CreateSslOptionsForProxy()
            };
        }
        
        /// <summary>
        /// Create SSL options that bypass certificate validation for proxy connections.
        /// This is necessary because some proxies may intercept SSL traffic or cause
        /// certificate chain validation issues (RemoteCertificateNameMismatch, RemoteCertificateChainErrors).
        /// </summary>
        private static System.Net.Security.SslClientAuthenticationOptions CreateSslOptionsForProxy()
        {
            return new System.Net.Security.SslClientAuthenticationOptions
            {
                // Accept all certificates when using proxy
                // This handles cases where proxy causes certificate mismatch or chain errors
                RemoteCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) => true
            };
        }

        /// <summary>
        /// Record a successful proxy usage and reset failure count.
        /// This helps with intermittent proxies (sometimes work, sometimes don't).
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
                    
                    // Reset failure count on success - this helps with intermittent proxies
                    if (proxy.FailureCount > 0)
                    {
                        _logger.LogInformation("Proxy {ProxyId} ({Host}:{Port}) succeeded after {FailureCount} failures. Resetting failure count.",
                            proxyId, proxy.Host, proxy.Port, proxy.FailureCount);
                        proxy.FailureCount = 0;
                        proxy.LastFailureReason = null;
                    }
                    
                    await context.SaveChangesAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to record proxy success for ID {ProxyId}", proxyId);
            }
        }

        /// <summary>
        /// Record a proxy failure and optionally disable if too many consecutive failures.
        /// For intermittent failures (HTML response, etc.), this increments failure count but doesn't immediately disable.
        /// </summary>
        /// <param name="proxyId">The proxy ID</param>
        /// <param name="reason">Failure reason</param>
        /// <param name="isIntermittent">True if this is an intermittent failure (proxy may work next time)</param>
        public async Task RecordProxyFailureAsync(int proxyId, string reason, bool isIntermittent = false)
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
                    
                    // Safely truncate reason to max length
                    if (!string.IsNullOrEmpty(reason))
                    {
                        proxy.LastFailureReason = reason.Length > MaxFailureReasonLength 
                            ? reason.Substring(0, MaxFailureReasonLength) 
                            : reason;
                    }
                    
                    // For intermittent failures, use higher threshold before disabling
                    int disableThreshold = isIntermittent 
                        ? MaxConsecutiveFailuresBeforeDisable * 2  // 10 failures for intermittent
                        : MaxConsecutiveFailuresBeforeDisable;     // 5 failures for regular
                    
                    // Auto-disable proxy after too many consecutive failures
                    if (proxy.FailureCount >= disableThreshold)
                    {
                        proxy.IsEnabled = false;
                        _logger.LogWarning("Proxy {ProxyId} ({Host}:{Port}) disabled due to {FailureCount} consecutive failures (threshold: {Threshold}, intermittent: {IsIntermittent})", 
                            proxyId, proxy.Host, proxy.Port, proxy.FailureCount, disableThreshold, isIntermittent);
                        RefreshCache();
                    }
                    else if (isIntermittent)
                    {
                        _logger.LogInformation("Proxy {ProxyId} ({Host}:{Port}) intermittent failure #{FailureCount}/{Threshold}: {Reason}",
                            proxyId, proxy.Host, proxy.Port, proxy.FailureCount, disableThreshold, reason);
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
        /// Immediately disable a proxy due to critical connection error.
        /// This is called when proxy is completely unreachable (timeout, connection refused, host down, etc.)
        /// </summary>
        public async Task DisableProxyImmediatelyAsync(int proxyId, string reason)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                
                var proxy = await context.Proxies.FindAsync(proxyId);
                if (proxy != null && proxy.IsEnabled)
                {
                    proxy.IsEnabled = false;
                    proxy.FailureCount++;
                    proxy.LastFailedAt = DateTime.UtcNow;
                    
                    // Safely truncate reason to max length
                    if (!string.IsNullOrEmpty(reason))
                    {
                        proxy.LastFailureReason = reason.Length > MaxFailureReasonLength 
                            ? reason.Substring(0, MaxFailureReasonLength) 
                            : reason;
                    }
                    
                    await context.SaveChangesAsync();
                    RefreshCache();
                    
                    _logger.LogWarning("?? Proxy {ProxyId} ({Host}:{Port}) PERMANENTLY DISABLED due to critical error: {Reason}", 
                        proxyId, proxy.Host, proxy.Port, reason);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to disable proxy for ID {ProxyId}", proxyId);
            }
        }
        
        /// <summary>
        /// Check if an exception indicates a critical proxy failure that should result in immediate disable.
        /// Critical failures include: connection timeout, host unreachable, connection refused, etc.
        /// </summary>
        public static bool IsCriticalProxyError(Exception ex)
        {
            // Check for SocketException with specific error codes
            if (ex is SocketException socketEx)
            {
                return socketEx.SocketErrorCode switch
                {
                    SocketError.TimedOut => true,           // 10060 - Connection timed out
                    SocketError.ConnectionRefused => true,  // 10061 - Connection refused
                    SocketError.HostUnreachable => true,    // 10065 - Host unreachable
                    SocketError.NetworkUnreachable => true, // 10051 - Network unreachable
                    SocketError.HostNotFound => true,       // 11001 - Host not found
                    SocketError.HostDown => true,           // Host is down
                    SocketError.NetworkDown => true,        // Network is down
                    SocketError.AddressNotAvailable => true,// Address not available
                    _ => false
                };
            }
            
            // Check for HttpRequestException that wraps SocketException
            if (ex is HttpRequestException httpEx)
            {
                // Check inner exception
                if (httpEx.InnerException is SocketException innerSocketEx)
                {
                    return IsCriticalProxyError(innerSocketEx);
                }
                
                // Check message patterns for critical errors
                var message = httpEx.Message ?? string.Empty;
                return message.Contains("10060") ||  // Connection timed out
                       message.Contains("10061") ||  // Connection refused
                       message.Contains("10065") ||  // Host unreachable
                       message.Contains("10051") ||  // Network unreachable
                       message.Contains("failed to respond", StringComparison.OrdinalIgnoreCase) ||
                       message.Contains("connection attempt failed", StringComparison.OrdinalIgnoreCase) ||
                       message.Contains("No connection could be made", StringComparison.OrdinalIgnoreCase) ||
                       message.Contains("actively refused", StringComparison.OrdinalIgnoreCase);
            }
            
            // Check inner exceptions recursively
            if (ex.InnerException != null)
            {
                return IsCriticalProxyError(ex.InnerException);
            }
            
            return false;
        }
        
        /// <summary>
        /// Get a user-friendly description of the proxy error for logging.
        /// </summary>
        public static string GetProxyErrorDescription(Exception ex)
        {
            if (ex is SocketException socketEx)
            {
                return socketEx.SocketErrorCode switch
                {
                    SocketError.TimedOut => "Connection timed out - proxy service may be down or blocked",
                    SocketError.ConnectionRefused => "Connection refused - proxy service not running or port blocked",
                    SocketError.HostUnreachable => "Host unreachable - proxy server not accessible",
                    SocketError.NetworkUnreachable => "Network unreachable - routing issue or firewall block",
                    SocketError.HostNotFound => "Host not found - invalid hostname or DNS issue",
                    SocketError.HostDown => "Host is down - proxy server offline",
                    SocketError.NetworkDown => "Network is down - network connectivity issue",
                    _ => $"Socket error: {socketEx.SocketErrorCode}"
                };
            }
            
            if (ex is HttpRequestException httpEx && httpEx.InnerException is SocketException innerSocketEx)
            {
                return GetProxyErrorDescription(innerSocketEx);
            }
            
            return ex.Message.Length > MaxFailureReasonLength 
                ? ex.Message.Substring(0, MaxFailureReasonLength) 
                : ex.Message;
        }

        /// <summary>
        /// Parse proxy list from text (one proxy per line).
        /// Supported formats:
        /// - host:port (defaults to SOCKS5)
        /// - type://host:port (type = http, socks4, socks5)
        /// - type://username:password@host:port (with authentication)
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
                
                // Determine proxy type from scheme
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
                    // Default to SOCKS5
                    proxy.Type = ProxyType.Socks5;
                }

                // Check for authentication (username:password@host:port)
                var atIndex = line.LastIndexOf('@');
                if (atIndex > 0)
                {
                    var auth = line.Substring(0, atIndex);
                    var hostPort = line.Substring(atIndex + 1);
                    
                    var authParts = auth.Split(':');
                    if (authParts.Length >= 2)
                    {
                        proxy.Username = authParts[0];
                        proxy.Password = string.Join(":", authParts.Skip(1)); // Password may contain ':'
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
        /// Get the count of active proxies.
        /// </summary>
        public async Task<int> GetActiveProxyCountAsync()
        {
            var proxies = await GetEnabledProxiesAsync();
            return proxies.Count;
        }
    }
}
