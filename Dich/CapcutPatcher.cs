using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace subphimv1.Services
{
    public static class CapcutPatcher
    {
        private const string ProcessName = "CapCut";
        private const string DllName = "VECreator.dll";
        private const string BackupExtension = ".bak_aio"; 
        private const string ConfigFileName = "app.cfg";
        #region
        public static string OriginalDllPath { get; private set; }
        public static string BackupDllPath { get; private set; }
        public static bool IsActive { get; private set; } = false;

        // Chuỗi byte cho .vip_entrance
        private static readonly byte[] FindBytes = { 0x00, 0x76, 0x69, 0x70, 0x5F, 0x65, 0x6E, 0x74, 0x72, 0x61, 0x6E, 0x63, 0x65, 0x00 };
        // Chuỗi byte .pro_fortnite
        private static readonly byte[] ReplaceBytes = { 0x00, 0x70, 0x72, 0x6F, 0x5F, 0x66, 0x6F, 0x72, 0x74, 0x6E, 0x69, 0x74, 0x65, 0x00 };
        private static CancellationTokenSource _monitorCts;
        public static event EventHandler CleanupCompleted;
        private static async Task<string> GetCapcutExePathAsync(IProgress<string> progress)
        {
            var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ConfigFileName);
            progress.Report("Đang kiểm tra đường dẫn CapCut đã lưu...");

            if (File.Exists(configPath))
            {
                var savedPath = await File.ReadAllTextAsync(configPath);
                if (File.Exists(savedPath))
                {
                    progress.Report($"Đã tìm thấy đường dẫn từ file cấu hình: {savedPath}");
                    return savedPath;
                }
                progress.Report("Đường dẫn đã lưu không còn hợp lệ. Đang tìm lại...");
            }

            progress.Report("Không tìm thấy cấu hình. Đang tìm tiến trình CapCut...");
            var exePath = await FindCapcutProcessPath();
            if (!string.IsNullOrEmpty(exePath))
            {
                progress.Report($"Đã tìm thấy CapCut đang chạy. Đang lưu đường dẫn: {exePath}");
                await File.WriteAllTextAsync(configPath, exePath);
                return exePath;
            }

            return null;
        }
        #endregion
        public static async Task<bool> ActivateAsync(IProgress<string> progress)
        {
            try
            { var exePath = await GetCapcutExePathAsync(progress);
                if (string.IsNullOrEmpty(exePath)) { progress.Report("LỖI: Không tìm thấy CapCut. Vui lòng mở CapCut ít nhất một lần rồi thử lại."); return false;}
                var exeFolder = Path.GetDirectoryName(exePath);
                OriginalDllPath = Path.Combine(exeFolder, DllName);
                BackupDllPath = OriginalDllPath + BackupExtension;
                if (!File.Exists(BackupDllPath)){if (!File.Exists(OriginalDllPath)){ return false; }File.Copy(OriginalDllPath, BackupDllPath, true); } else {}
                progress.Report("Đang đóng tất cả tiến trình CapCut...");
                await KillProcessByName(ProcessName);
                progress.Report("Tất cả tiến trình CapCut đã được đóng.");
                progress.Report("Bắt đầu crack các file DLL...");
                byte[] originalBytes = await File.ReadAllBytesAsync(BackupDllPath);
                byte[] modifiedBytes = BinaryReplace(originalBytes, FindBytes, ReplaceBytes);
                if (originalBytes.SequenceEqual(modifiedBytes)) {}
                await File.WriteAllBytesAsync(OriginalDllPath, modifiedBytes);
                progress.Report("Đã crack thành công!");
                var watermarkFolder = Path.Combine(exeFolder, "Resources", "watermark");
                if (Directory.Exists(watermarkFolder))
                {try{   
                        progress.Report("Đang xoá thư mục watermark...");
                        Directory.Delete(watermarkFolder, true);
                        progress.Report("Đã xoá watermark.");}
                    catch (Exception ex){ progress.Report($"Không thể xoá watermark: {ex.Message}"); } }
                IsActive = true;
                progress.Report("KÍCH HOẠT THÀNH CÔNG! KHÔNG ĐƯỢC ĐÓNG HAY TẮT CỬA SỔ NÀY!!!");
                var process = Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true });
                StartMonitoringProcess(process, progress);

                return true;
            }
            catch (Exception ex)
            {
                await CleanupAsync(progress);
                return false;
            }
        }
        private static void StartMonitoringProcess(Process process, IProgress<string> progress)
        {
            _monitorCts = new CancellationTokenSource();
            var token = _monitorCts.Token;

            Task.Run(async () =>
            {
                if (process == null) return;
                try
                {
                    await process.WaitForExitAsync(token);
                    if (!token.IsCancellationRequested)
                    {
                        await CleanupAsync();
                    }
                }
                catch (TaskCanceledException) { }
                catch (Exception ex) { }
            }, token);
        }
        public static async Task CleanupAsync(IProgress<string> progress = null)
        {
            if (!IsActive && string.IsNullOrEmpty(BackupDllPath)) return;
            _monitorCts?.Cancel();
            _monitorCts?.Dispose();
            _monitorCts = null;
            try
            {
                await KillProcessByName(ProcessName);

                if (File.Exists(BackupDllPath))
                {
                    if (File.Exists(OriginalDllPath))
                    {
                        File.Delete(OriginalDllPath);
                    }
                    File.Move(BackupDllPath, OriginalDllPath);

                }


            }
            catch (Exception ex)
            {
            }
            finally
            {
                IsActive = false;
                BackupDllPath = null;
                OriginalDllPath = null;
                CleanupCompleted?.Invoke(null, EventArgs.Empty);
            }
        }
        private static async Task<string> FindCapcutProcessPath()
        {
            return await Task.Run(() =>
            {
                var processes = Process.GetProcessesByName(ProcessName);
                if (processes.Length == 0) return null;

                try
                {
                    return processes.First().MainModule?.FileName;
                }
                catch (Exception)
                {
                    // Có thể xảy ra lỗi do xung đột 32/64bit
                    return null;
                }
            });
        }
        private static async Task KillProcessByName(string processName)
        {
            await Task.Run(() =>
            {
                var processes = Process.GetProcessesByName(processName);
                foreach (var process in processes)
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill();
                            process.WaitForExit(5000); 
                        }
                    }
                    catch (Exception) { }
                    finally
                    {
                        process.Dispose();
                    }
                }
            });
        }

        private static byte[] BinaryReplace(byte[] input, byte[] find, byte[] replace)
        {
            List<byte> result = new List<byte>(input.Length);
            int findLen = find.Length;
            int i = 0;
            while (i < input.Length)
            {
                bool found = (i + findLen <= input.Length) && input.Skip(i).Take(findLen).SequenceEqual(find);

                if (found)
                {
                    result.AddRange(replace);
                    i += findLen;
                }
                else
                {
                    result.Add(input[i]);
                    i++;
                }
            }
            return result.ToArray();
        }
    }
}