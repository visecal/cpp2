using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace SubPhim.Server.Services
{

    public class EncryptionService : IEncryptionService
    {
        private readonly byte[] _key;

        public EncryptionService(IConfiguration configuration)
        {
            var keyString = configuration["LocalApiSettings:EncryptionKey"] ?? "jH$2b@!sL9*dFkP&_zXvYq?5nWmZq4t7";
            if (keyString.Length != 32) throw new ArgumentException("EncryptionKey phải có 32 ký tự.");
            _key = Encoding.UTF8.GetBytes(keyString);
        }

        public (string encryptedText, string iv) Encrypt(string plainText)
        {
            using (var aes = Aes.Create())
            {
                aes.Key = _key;
                aes.GenerateIV();
                var iv = aes.IV;

                using (var encryptor = aes.CreateEncryptor(aes.Key, iv))
                using (var ms = new MemoryStream())
                {
                    using (var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
                    using (var sw = new StreamWriter(cs))
                    {
                        sw.Write(plainText);
                    }
                    var encryptedBytes = ms.ToArray();
                    return (Convert.ToBase64String(encryptedBytes), Convert.ToBase64String(iv));
                }
            }
        }

        // === BẮT ĐẦU SỬA LỖI: VIẾT CODE HOÀN CHỈNH CHO HÀM DECRYPT ===
        public string Decrypt(string encryptedText, string iv)
        {
            if (string.IsNullOrEmpty(encryptedText) || string.IsNullOrEmpty(iv))
            {
                return string.Empty;
            }

            try
            {
                var encryptedBytes = Convert.FromBase64String(encryptedText);
                var ivBytes = Convert.FromBase64String(iv);

                using (var aes = Aes.Create())
                {
                    aes.Key = _key;
                    aes.IV = ivBytes;

                    using (var decryptor = aes.CreateDecryptor(aes.Key, aes.IV))
                    using (var ms = new MemoryStream(encryptedBytes))
                    {
                        using (var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read))
                        using (var sr = new StreamReader(cs))
                        {
                            return sr.ReadToEnd();
                        }
                    }
                }
            }
            catch (FormatException)
            {
                // Lỗi khi chuỗi Base64 không hợp lệ
                return "!!! LỖI ĐỊNH DẠNG KEY !!!";
            }
            catch (CryptographicException)
            {
                // Lỗi khi giải mã (ví dụ: key sai, padding sai)
                return "!!! LỖI GIẢI MÃ (CRYPTO) !!!";
            }
            catch (Exception)
            {
                // Bắt các lỗi khác
                return "!!! LỖI KHÔNG XÁC ĐỊNH !!!";
            }
        }
        // === KẾT THÚC SỬA LỖI ===
    }
}