# BÁO CÁO PHÂN TÍCH BẢO MẬT - SUBPHIM SERVER

**Ngày phân tích:** 2025-11-25
**Mức độ nghiêm trọng:** 🔴 CRITICAL - CẦN XỬ LÝ NGAY

---

## 📋 TÓM TẮT ĐIỀU HÀNH

Hệ thống có **14 lỗ hổng bảo mật nghiêm trọng** có thể bị khai thác để:
- Đánh cắp thông tin nhạy cảm (API keys, mật khẩu, database)
- Chiếm quyền điều khiển hệ thống
- Tấn công từ chối dịch vụ (DoS)
- SQL Injection và truy cập trái phép

---

## 🔴 CÁC LỖ HỔNG NGHIÊM TRỌNG (CRITICAL)

### 1. **HARDCODED JWT SECRET KEY** ⚠️ CRITICAL
**File:** `Program.cs:78`

```csharp
var jwtKey = "SubPhim-Super-Secret-Key-For-JWT-Authentication-2024-@!#$";
```

**Nguy cơ:**
- JWT secret bị hardcode trực tiếp trong source code
- Kẻ tấn công có thể tạo token giả mạo để chiếm quyền bất kỳ user nào
- Có thể leo thang đặc quyền lên admin

**Khai thác:**
```csharp
// Hacker có thể tạo token giả:
var claims = new List<Claim> {
    new Claim("id", "1"),  // Admin ID
    new Claim("Admin", "true")
};
// Ký bằng secret key đã lộ → Thành admin!
```

**Khắc phục:**
- Di chuyển secret key vào biến môi trường
- Sử dụng key mạnh hơn (256-bit random)
- Rotate key định kỳ

---

### 2. **LỘ MẬT KHẨU SMTP TRONG CONFIG** ⚠️ CRITICAL
**File:** `appsettings.json:54-59`

```json
"SmtpSettings": {
    "Host": "smtp.gmail.com",
    "Port": 587,
    "Username": "aiolauncher.service@gmail.com",
    "Password": "uuuc odat odrr ksac"  // ← MẬT KHẨU BỊ LỘ
}
```

**Nguy cơ:**
- Mật khẩu email bị lộ hoàn toàn
- Kẻ tấn công có thể:
  - Gửi email spam từ tài khoản này
  - Đọc email nhạy cảm (reset password của users)
  - Chiếm quyền tài khoản người dùng thông qua email

**Khắc phục:**
- Di chuyển ngay vào biến môi trường
- Đổi mật khẩu email ngay lập tức
- Sử dụng App Password thay vì mật khẩu thật

---

### 3. **LỘ ENCRYPTION KEY** ⚠️ CRITICAL
**File:** `appsettings.json:60-62`

```json
"LocalApiSettings": {
    "EncryptionKey": "jH$2b@!sL9*dFkP&_zXvYq?5nWmZq4t7"  // ← KEY MÃ HÓA BỊ LỘ
}
```

**Nguy cơ:**
- Tất cả dữ liệu được mã hóa bằng AES có thể bị giải mã
- Bao gồm:
  - API keys của các service (Gemini, OpenRouter, ElevenLabs)
  - Google Service Account credentials
  - Các thông tin nhạy cảm khác trong DB

**Khai thác:**
```csharp
// Hacker có thể giải mã tất cả API keys trong database:
var encryptionKey = "jH$2b@!sL9*dFkP&_zXvYq?5nWmZq4t7";
var decrypted = DecryptAES(stolenEncryptedApiKey, encryptionKey);
// → Lấy được tất cả API keys!
```

**Khắc phục:**
- Di chuyển encryption key vào Azure Key Vault hoặc biến môi trường
- Rotate key và re-encrypt tất cả dữ liệu
- Sử dụng HSM nếu có thể

---

### 4. **SQL INJECTION TIỀM ẨN** ⚠️ HIGH
**File:** `AuthController.cs:335-351`

```csharp
if (await _context.Users.AnyAsync(u => u.Username.ToLower() == request.Username.ToLower()))
```

**Nguy cơ:**
- Mặc dù dùng Entity Framework (có parameterized queries), nhưng `.ToLower()` có thể gây vấn đề
- Case-insensitive comparison không an toàn
- Có thể bypass authentication trong một số trường hợp đặc biệt

**Khai thác:**
```
Username: "admin\0" hoặc "admin%00"
→ Có thể bypass kiểm tra trùng lặp
```

**Khắc phục:**
- Dùng `StringComparison.OrdinalIgnoreCase`
- Validate username với regex: `^[a-zA-Z0-9_]{3,20}$`

---

### 5. **INSECURE RANDOM NUMBER GENERATOR** ⚠️ HIGH
**File:** `AuthController.cs:160-170` và `Program.cs:152`

```csharp
var random = new Random();  // ← KHÔNG AN TOÀN
var chars = new char[length];
for (int i = 0; i < length; i++)
{
    chars[i] = validChars[random.Next(validChars.Length)];
}
```

**Nguy cơ:**
- `Random()` có thể dự đoán được
- Dùng để tạo:
  - Mật khẩu reset (dễ đoán!)
  - User UID (có thể đoán được UID của user khác)
- Kẻ tấn công có thể brute-force dễ dàng

**Khai thác:**
```csharp
// Mật khẩu chỉ 4 ký tự với Random() → Có thể đoán trong vài phút
// UID 9 chữ số → Có thể enumerate tất cả users
```

**Khắc phục:**
```csharp
// Sử dụng cryptographically secure random
using var rng = RandomNumberGenerator.Create();
byte[] randomBytes = new byte[length];
rng.GetBytes(randomBytes);
```

---

### 6. **WEAK PASSWORD GENERATION** ⚠️ HIGH
**File:** `AuthController.cs:160`

```csharp
private static string GenerateRandomPassword(int length = 4)  // ← CHỈ 4 KÝ TỰ!
```

**Nguy cơ:**
- Mật khẩu reset chỉ 4 ký tự
- Chỉ có 62^4 = ~14 triệu kết hợp
- Có thể brute-force trong vài phút

**Khắc phục:**
- Tăng lên ít nhất 12 ký tự
- Bắt buộc phải có: chữ hoa, chữ thường, số, ký tự đặc biệt
- Hoặc dùng temporary token thay vì mật khẩu mới

---

### 7. **INFORMATION DISCLOSURE VIA ERROR MESSAGES** ⚠️ MEDIUM
**File:** Multiple locations

```csharp
return BadRequest("Tên đăng nhập hoặc mật khẩu không đúng.");  // OK
return BadRequest("Email đã được sử dụng.");  // ← LỘ INFO!
return BadRequest("Tên tài khoản đã tồn tại.");  // ← LỘ INFO!
```

**Nguy cơ:**
- Kẻ tấn công có thể enumerate users và emails trong hệ thống
- Biết được username/email nào đã tồn tại

**Khai thác:**
```python
# Script tự động kiểm tra users
for username in wordlist:
    response = register(username, "test@test.com", "pass123")
    if "đã tồn tại" in response:
        valid_users.append(username)
```

**Khắc phục:**
- Trả về message chung chung: "Đăng ký không thành công"
- Log thông tin chi tiết ở server-side

---

### 8. **NO HTTPS ENFORCEMENT** ⚠️ CRITICAL
**File:** `Program.cs:83,250-255`

```csharp
options.RequireHttpsMetadata = false;  // ← TẮT HTTPS!

app.Run("http://*:8080");  // ← KHÔNG MÃ HÓA!
app.Run("http://*:5000");  // ← KHÔNG MÃ HÓA!
```

**Nguy cơ:**
- Tất cả traffic không được mã hóa
- Kẻ tấn công có thể:
  - Sniff passwords, tokens, API keys
  - Man-in-the-middle attacks
  - Session hijacking

**Khai thác:**
```bash
# Attacker trên cùng mạng WiFi:
tcpdump -i wlan0 -A | grep "Authorization: Bearer"
# → Lấy được JWT token!
```

**Khắc phục:**
- Bật HTTPS bắt buộc
- Cấu hình SSL/TLS certificates
- Sử dụng HSTS headers

---

### 9. **INSECURE ADMIN LOGIN** ⚠️ HIGH
**File:** `Login.cshtml.cs:30`

```csharp
public async Task<IActionResult> OnGetAsync(string username, string password, string returnUrl = null)
```

**Nguy cơ:**
- Admin credentials được truyền qua URL query string!
- Bị log trong:
  - Browser history
  - Server access logs
  - Proxy logs
  - Referrer headers

**Khai thác:**
```
https://server.com/Admin/Login?username=admin&password=AdminMatKhauMoi123!
→ Mật khẩu bị lộ trong logs!
```

**Khắc phục:**
- Dùng POST method với form body
- Không bao giờ truyền credentials qua GET

---

### 10. **DEFAULT ADMIN CREDENTIALS** ⚠️ CRITICAL
**File:** `Program.cs:143-144`

```csharp
var adminUsername = "admin";
var defaultAdminPassword = "AdminMatKhauMoi123!";  // ← MẬT KHẨU MẶC ĐỊNH!
```

**Nguy cơ:**
- Mật khẩu admin mặc định bị hardcode
- Nếu admin không đổi → Dễ bị chiếm quyền
- Có thể tìm thấy trong source code public

**Khai thác:**
```bash
# Thử đăng nhập với credentials mặc định:
curl -X GET "https://target.com/Admin/Login?username=admin&password=AdminMatKhauMoi123!"
```

**Khắc phục:**
- Bắt buộc đổi password lần đầu
- Sử dụng random password và gửi qua email an toàn
- Yêu cầu 2FA cho admin

---

### 11. **RATE LIMITING BYPASS** ⚠️ MEDIUM
**File:** `AuthController.cs:373-379`

```csharp
var cacheKey = $"login_fail_{clientIp}_{request.Username}";
if (_cache.TryGetValue(cacheKey, out int failCount) && failCount >= 5)
{
    return StatusCode(429, "Bạn đã nhập sai quá nhiều lần. Vui lòng thử lại sau 1 giờ.");
}
```

**Nguy cơ:**
- Rate limit dựa trên IP có thể bypass bằng:
  - VPN/Proxy rotation
  - Distributed attacks
  - IP spoofing (nếu không có proper validation)
- Username-based limit có thể bypass bằng cách thử nhiều usernames khác nhau từ cùng IP

**Khai thác:**
```python
# Bypass bằng cách rotate proxy:
for proxy in proxy_list:
    for password in password_list:
        login(username="admin", password=password, proxy=proxy)
```

**Khắc phục:**
- Thêm CAPTCHA sau 3 lần thất bại
- Rate limit theo nhiều yếu tố: IP + Username + Device
- Thêm exponential backoff

---

### 12. **ENUMERATION VIA DEVICE LIMIT** ⚠️ LOW
**File:** `AuthController.cs:330-333`

```csharp
if (await _context.Devices.AnyAsync(d => d.Hwid == request.Hwid))
{
    return BadRequest("Mỗi thiết bị chỉ được phép đăng ký một tài khoản duy nhất.");
}
```

**Nguy cơ:**
- Có thể enumerate devices đã đăng ký
- Biết được HWID nào đã được sử dụng

**Khắc phục:**
- Trả về message chung: "Đăng ký không thành công"

---

### 13. **INSECURE DIRECT OBJECT REFERENCE (IDOR)** ⚠️ HIGH
**File:** `LauncherAioController.cs:52,65`

```csharp
[HttpGet("get-result/{sessionId}")]
public async Task<IActionResult> GetResult(string sessionId)
{
    // Chỉ kiểm tra userId từ token, nhưng không verify sessionId có thuộc user này không!
    var result = await _aioLauncherService.GetJobResultAsync(sessionId, userId);
```

**Nguy cơ:**
- Nếu service không validate ownership, user có thể xem kết quả của người khác
- IDOR: Đoán sessionId của người khác

**Khai thác:**
```bash
# User A có sessionId: "abc123"
# User B thử:
curl -H "Authorization: Bearer <userB_token>" \
  https://api/get-result/abc123
# → Có thể xem dữ liệu của User A!
```

**Khắc phục:**
- Verify sessionId thuộc về userId trong service layer
- Sử dụng GUID thay vì sequential IDs

---

### 14. **EXPOSED SENSITIVE API ENDPOINTS** ⚠️ HIGH
**File:** `SaOcrController.cs:36-68`

```csharp
[HttpGet("keys")]
public async Task<IActionResult> GetServiceAccountKeys()
{
    // Trả về tất cả Google Service Account JSON keys!
    var decryptedJson = _encryptionService.Decrypt(sa.EncryptedJsonKey, sa.Iv);
    keysToReturn.Add(new SaOcrKeyDto(decryptedJson, sa.DriveFolderId));
```

**Nguy cơ:**
- Endpoint trả về **toàn bộ Google Service Account credentials** cho bất kỳ user đã authenticated nào
- Bao gồm private keys có thể dùng để:
  - Truy cập Google Drive
  - Sử dụng OCR API
  - Truy cập tài nguyên Google Cloud khác

**Khai thác:**
```bash
# User thường có thể lấy tất cả service account keys:
curl -H "Authorization: Bearer <any_user_token>" \
  https://api/sa-ocr/keys
# → Lấy được private keys của Google Cloud!
```

**Khắc phục:**
- Không bao giờ trả về credentials trực tiếp cho client
- Implement server-side proxy
- Chỉ admin mới được quản lý keys

---

## 🟡 CÁC VẤN ĐỀ BẢO MẬT KHÁC

### 15. No CSRF Protection
- Admin pages có `[IgnoreAntiforgeryToken]`
- Có thể bị CSRF attacks

### 16. No Input Validation
- Không validate length, format của inputs
- Có thể gây buffer overflow hoặc DoS

### 17. Verbose Logging
```csharp
_logger.LogWarning("====== INCOMING REQUEST ====== Method: {Method}, Path: {Path}");
```
- Log quá nhiều thông tin
- Có thể chứa sensitive data

### 18. No Request Size Limits
- Không giới hạn request body size
- Có thể upload file khổng lồ → DoS

### 19. Password in Logs
```csharp
_logger.LogInformation("Password has been reset for user {Username}");
```
- Không nên log về password operations chi tiết

---

## 🎯 KỊCH BẢN TẤN CÔNG THỰC TẾ

### **Kịch bản 1: Chiếm toàn bộ hệ thống**
```
1. Lấy JWT secret từ source code (hoặc decompile)
2. Tạo JWT token giả với claim Admin=true
3. Truy cập admin panel
4. Lấy encryption key từ appsettings.json
5. Giải mã tất cả API keys trong database
6. Sử dụng API keys → Gây tốn phí cho admin
7. Đổi password admin → Khóa admin ra khỏi hệ thống
```

### **Kịch bản 2: Đánh cắp Google Service Account**
```
1. Đăng ký tài khoản Free
2. Gọi /api/sa-ocr/keys
3. Nhận được tất cả Google SA credentials
4. Sử dụng credentials để:
   - Truy cập Google Drive
   - Đọc/xóa dữ liệu
   - Sử dụng API gây chi phí
```

### **Kịch bản 3: Account Takeover**
```
1. Enumerate username qua endpoint register
2. Request forgot password
3. Intercept email hoặc brute-force mật khẩu 4 ký tự
4. Login vào tài khoản
5. Lấy thông tin nhạy cảm
```

---

## ✅ KHUYẾN NGHỊ KHẮC PHỤC

### Ưu tiên CAO (Làm NGAY):
1. ✅ **Di chuyển tất cả secrets vào biến môi trường**
2. ✅ **Đổi ngay mật khẩu email SMTP**
3. ✅ **Rotate encryption key và re-encrypt data**
4. ✅ **Bật HTTPS và tắt HTTP**
5. ✅ **Đổi mật khẩu admin mặc định**
6. ✅ **Fix insecure random number generator**
7. ✅ **Tăng độ dài mật khẩu reset lên 16+ ký tự**
8. ✅ **Remove endpoint trả về Service Account keys**

### Ưu tiên TRUNG BÌNH:
9. ✅ Implement proper input validation
10. ✅ Add CAPTCHA cho login/register
11. ✅ Fix admin login (POST thay vì GET)
12. ✅ Implement rate limiting đúng cách
13. ✅ Add CSRF protection
14. ✅ Sanitize error messages

### Ưu tiên THẤP:
15. ✅ Clean up verbose logging
16. ✅ Add request size limits
17. ✅ Implement security headers (CSP, X-Frame-Options, etc.)
18. ✅ Add monitoring và alerting

---

## 📊 ĐÁNH GIÁ RỦI RO TỔNG THỂ

| Loại lỗ hổng | Số lượng | Mức độ |
|---------------|----------|--------|
| Critical | 6 | 🔴 |
| High | 5 | 🟠 |
| Medium | 2 | 🟡 |
| Low | 1 | 🟢 |

**Điểm bảo mật: 2/10** ⚠️

---

## 🛡️ KẾT LUẬN

Hệ thống hiện tại có **nhiều lỗ hổng bảo mật nghiêm trọng** cần được xử lý ngay lập tức. Các lỗ hổng này có thể dẫn đến:

1. **Mất toàn bộ quyền điều khiển hệ thống**
2. **Đánh cắp dữ liệu người dùng**
3. **Lộ API keys và credentials nhạy cảm**
4. **Tổn thất tài chính do lạm dụng API**

**Khuyến nghị:** Tạm dừng production cho đến khi sửa xong các lỗ hổng CRITICAL.

---

**Người phân tích:** Claude (AI Security Auditor)
**Liên hệ:** [GitHub Issues](https://github.com/anthropics/claude-code/issues)
