using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
// Không cần dùng SharpCompress nữa

namespace subphimv1.Services
{
    public static class JianyingPatcher
    {
        // --- Cấu hình (giữ nguyên) ---
        private const string DownloadUrl = "https://github.com/visecal/Cracker/releases/download/crack/JianyingPro.rar";
        private const string DownloadedFileName = "JianyingPro_Download.tmp";
        private const string AppFolder = "JianyingPro";
        private const string ProcessName = "JianyingPro";
        private const string DllArchiveName = "Dll.rar";
        private const string DllPassword = "0365912903";
        private const string DllName = "VECreator.dll";
        public static event EventHandler CleanupCompleted;
        private static CancellationTokenSource _monitorCts;
        public static bool IsActive { get; private set; } = false;
        private static string AppExePath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, AppFolder, "JianyingPro.exe");
        private static string OriginalDllPath => Path.Combine(Path.GetDirectoryName(AppExePath), DllName);
        private static string DllArchivePath => Path.Combine(Path.GetDirectoryName(AppExePath), DllArchiveName);
        private static string SevenZipExePath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "7z.exe");

        /// <summary>
        /// Helper function để giải nén bằng 7z.exe từ dòng lệnh
        /// </summary>
        private static async Task<bool> ExtractWith7zAsync(string archivePath, string destinationPath, string password, IProgress<string> progress)
        {
            if (!File.Exists(SevenZipExePath))
            {
                return false;
            }

            var processStartInfo = new ProcessStartInfo
            {
                FileName = SevenZipExePath,
                Arguments = $"x \"{archivePath}\" -o\"{destinationPath}\" -p\"{password}\" -y",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using (var process = new Process { StartInfo = processStartInfo })
            {
                process.Start();
                await process.WaitForExitAsync(); // Dùng phiên bản async

                if (process.ExitCode != 0)
                {
                    string error = await process.StandardError.ReadToEndAsync();
                    return false;
                }
            }
            return true;
        }

        private static async Task<bool> EnsureAppIsReadyAsync(IProgress<string> progress, IProgress<double> downloadProgress)
        {
            var appDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, AppFolder);
            var downloadedFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, DownloadedFileName);

            if (File.Exists(AppExePath))
            {
                return true;
            }

            progress.Report($"Đang tải JianyingPro khoảng 700mb...");
            try
            {
                // Logic tải file bằng WebClient (giữ nguyên)
                var tcs = new TaskCompletionSource<bool>();
                using (var client = new WebClient())
                {
                    client.DownloadProgressChanged += (s, e) => downloadProgress.Report(e.ProgressPercentage);
                    client.DownloadFileCompleted += (s, e) =>
                    {
                        if (e.Cancelled) tcs.SetCanceled();
                        else if (e.Error != null) tcs.SetException(e.Error);
                        else tcs.SetResult(true);
                    };
                    client.DownloadFileAsync(new Uri(DownloadUrl), downloadedFilePath);
                    await tcs.Task;
                }

                progress.Report("Tải file thành công.");
                downloadProgress.Report(0);

                // === SỬA LỖI: DÙNG 7-ZIP ĐỂ GIẢI NÉN ===
                progress.Report($"Đang giải nén file tải về..");
                if (Directory.Exists(appDirectory)) Directory.Delete(appDirectory, true);
                Directory.CreateDirectory(appDirectory);

                if (!await ExtractWith7zAsync(downloadedFilePath, appDirectory, DllPassword, progress))
                {
                    return false;
                }

                progress.Report("Giải nén thành công.");
                File.Delete(downloadedFilePath);
                return true;
            }
            catch (Exception ex)
            {
                progress.Report($"LỖI trong quá trình tải/giải nén: {ex.Message}");
                if (File.Exists(downloadedFilePath)) File.Delete(downloadedFilePath);
                return false;
            }
        }

        public static async Task<bool> ActivateAsync(IProgress<string> progress, IProgress<double> downloadProgress)
        {
            try
            {
                if (!await EnsureAppIsReadyAsync(progress, downloadProgress)) return false;

                await KillProcessByName(ProcessName);

                progress.Report($"Đang crackkkkkkkkk...");
                string destinationDir = Path.GetDirectoryName(AppExePath);
                if (!await ExtractWith7zAsync(DllArchivePath, destinationDir, DllPassword, progress))
                {
                    return false;
                }
                if (!File.Exists(OriginalDllPath))
                {
                    return false;
                }

                progress.Report("Kích hoạt thành công!");
                IsActive = true;

                progress.Report("Đang khởi động JianyingPro...");
                var process = Process.Start(AppExePath);
                StartMonitoringProcess(process, progress);

                return true;
            }
            catch (Exception ex)
            {
                progress.Report($"LỖI KÍCH HOẠT NGOẠI LỆ: {ex.Message}");
                return false;
            }
        }
        private static void StartMonitoringProcess(Process process, IProgress<string> progress)
        {
            _monitorCts = new CancellationTokenSource();
            var token = _monitorCts.Token;

            Task.Run(async () =>
            {
                try
                {
                    await process.WaitForExitAsync(token);
                    if (!token.IsCancellationRequested)
                    {
                        await CleanupAsync();
                    }
                }
                catch (TaskCanceledException)
                {
                }
                catch (Exception ex)
                {
                }
            }, token);
        }

        public static async Task CleanupAsync(IProgress<string> progress = null)
        {
            if (!IsActive) return;
            _monitorCts?.Cancel();
            _monitorCts?.Dispose();
            _monitorCts = null;
            await KillProcessByName(ProcessName);
            try
            {
                if (File.Exists(OriginalDllPath))
                {
                    File.Delete(OriginalDllPath);
                }
                else
                {
                }
            }
            catch (Exception ex)
            {
            }
            finally
            {
                IsActive = false;
                CleanupCompleted?.Invoke(null, EventArgs.Empty);
            }
        }

        private static async Task KillProcessByName(string processName)
        {
            await Task.Run(() =>
            {
                foreach (var process in Process.GetProcessesByName(processName))
                {
                    try { if (!process.HasExited) { process.Kill(); process.WaitForExit(); } }
                    catch { }
                    finally { process.Dispose(); }
                }
            });
        }
    }
}