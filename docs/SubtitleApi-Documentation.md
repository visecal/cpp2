# SubtitleApi - API Dịch Phụ Đề Phân Tán

> **Base URL:** `https://your-server.com/api/subtitle`

---

## 📋 So sánh với LocalApi (AioLauncher)

| Đặc điểm | LocalApi (`/api/AioLauncher`) | SubtitleApi (`/api/subtitle`) |
|----------|-------------------------------|-------------------------------|
| **Xác thực** | ✅ JWT Token (bắt buộc) | ❌ Không yêu cầu (hiện tại) |
| **Input format** | Content string (toàn bộ SRT) | Array các dòng `{index, text}` |
| **Xử lý** | Server đơn | Phân tán nhiều server |
| **Tracking** | SessionId từ server | SessionId từ client |
| **Kết quả** | `translatedContent` (string) | `results[]` (array có index) |
| **Callback** | ❌ Không có | ✅ Hỗ trợ webhook |
| **Giới hạn** | `DailyLocalSrtLimit` | Dùng chung `DailyLocalSrtLimit` |

---

## 🔐 Xác thực (Authentication)

**Hiện tại:** API chưa yêu cầu xác thực.

**Khuyến nghị:** Nên thêm header Authorization nếu muốn tính lượt user:
```http
Authorization: Bearer <jwt_token>
```

---

## 📤 1. Gửi yêu cầu dịch

### Endpoint
```
POST /api/subtitle/translate
```

### Request Headers
```http
Content-Type: application/json
```

### Request Body

```json
{
  "sessionId": "job-20231211-143000-abc123",
  "prompt": "Dịch các dòng phụ đề sau sang tiếng Việt.\nFormat output: index|text đã dịch\n\nVí dụ:\n1|Xin chào thế giới\n2|Bạn khỏe không?",
  "systemInstruction": "Bạn là dịch giả phụ đề phim chuyên nghiệp. Dịch tự nhiên, giữ nguyên cảm xúc và ngữ cảnh.",
  "lines": [
    {"index": 1, "text": "Hello world"},
    {"index": 2, "text": "How are you?"},
    {"index": 3, "text": "I'm fine, thank you."}
  ],
  "model": "gemini-2.5-flash",
  "thinkingBudget": 0,
  "callbackUrl": "https://your-server.com/webhook/translation-complete"
}
```

### Mô tả các field

| Field | Kiểu | Bắt buộc | Mô tả |
|-------|------|----------|-------|
| `sessionId` | string | ✅ | ID phiên dịch unique, **do client tự tạo** |
| `prompt` | string | ✅ | Prompt hướng dẫn dịch cho AI |
| `systemInstruction` | string | ✅ | System instruction cho AI |
| `lines` | array | ✅ | Danh sách dòng phụ đề cần dịch |
| `lines[].index` | int | ✅ | Số thứ tự dòng (giữ nguyên từ file SRT) |
| `lines[].text` | string | ✅ | Nội dung dòng cần dịch |
| `model` | string | ❌ | Model AI (mặc định: `gemini-2.5-flash`) |
| `thinkingBudget` | int | ❌ | Token budget cho thinking (0 = tắt) |
| `callbackUrl` | string | ❌ | URL webhook khi hoàn thành |

### ⚠️ KHÁC BIỆT với LocalApi

| LocalApi | SubtitleApi |
|----------|-------------|
| `content`: "1\n00:00:01...\nHello\n\n2\n..."` (toàn bộ SRT) | `lines`: `[{index: 1, text: "Hello"}, ...]` |
| Server tạo sessionId | Client tạo sessionId |
| `targetLanguage`: "Vietnamese" | Không có, định nghĩa trong prompt |

### Response thành công (200 OK)

```json
{
  "sessionId": "job-20231211-143000-abc123",
  "status": "pending",
  "totalLines": 3,
  "batchCount": 1,
  "serversAssigned": 1,
  "message": "Job đã được tạo và đang phân phối đến các server."
}
```

### Response lỗi (400/500)

```json
{
  "error": "Thông báo lỗi",
  "detail": "Chi tiết lỗi (nếu có)"
}
```

### Các lỗi thường gặp

| HTTP Code | Error | Nguyên nhân |
|-----------|-------|-------------|
| 400 | `sessionId là bắt buộc` | Thiếu sessionId |
| 400 | `Session {id} đã tồn tại` | sessionId trùng |
| 400 | `Bạn đã hết lượt dịch SRT Local hôm nay` | Hết quota |
| 400 | `Không có server dịch nào khả dụng` | Tất cả server đang bận |
| 400 | `Không có API key nào khả dụng` | Tất cả key đang cooldown |

---

## 📊 2. Kiểm tra trạng thái

### Endpoint
```
GET /api/subtitle/status/{sessionId}
```

### Response (200 OK)

```json
{
  "sessionId": "job-20231211-143000-abc123",
  "status": "processing",
  "progress": 66.67,
  "totalLines": 3,
  "completedLines": 2,
  "error": null,
  "taskStats": {
    "Completed": 2,
    "Processing": 1,
    "Failed": 0
  }
}
```

### Các giá trị status

| Status | Mô tả |
|--------|-------|
| `pending` | Job đã tạo, chưa bắt đầu |
| `distributing` | Đang phân phối đến các server |
| `processing` | Đang xử lý |
| `completed` | Hoàn thành 100% |
| `partialcompleted` | Hoàn thành một phần (có batch lỗi) |
| `failed` | Thất bại hoàn toàn |

### ⚠️ KHÁC BIỆT với LocalApi

| LocalApi | SubtitleApi |
|----------|-------------|
| Status: `Pending`, `Running`, `Completed`, `Failed` | Status: `pending`, `processing`, `completed`, `partialcompleted`, `failed` |
| Không có progress % | Có `progress`: 0-100 |
| Không có taskStats | Có `taskStats` chi tiết |

---

## 📥 3. Lấy kết quả dịch

### Endpoint
```
GET /api/subtitle/results/{sessionId}
```

### Response (200 OK)

```json
{
  "sessionId": "job-20231211-143000-abc123",
  "status": "completed",
  "totalLines": 3,
  "completedLines": 3,
  "results": [
    {
      "index": 1,
      "original": "Hello world",
      "translated": "Xin chào thế giới"
    },
    {
      "index": 2,
      "original": "How are you?",
      "translated": "Bạn khỏe không?"
    },
    {
      "index": 3,
      "original": "I'm fine, thank you.",
      "translated": "Tôi khỏe, cảm ơn bạn."
    }
  ],
  "error": null,
  "createdAt": "2023-12-11T14:30:00Z",
  "completedAt": "2023-12-11T14:31:25Z"
}
```

### ⚠️ KHÁC BIỆT QUAN TRỌNG với LocalApi

| LocalApi | SubtitleApi |
|----------|-------------|
| `translatedContent`: string (toàn bộ SRT đã dịch) | `results`: array objects |
| Client phải parse toàn bộ SRT | Client parse từng object theo index |
| Không có `original` | Có cả `original` và `translated` |
| Không có timestamp | Có `createdAt`, `completedAt` |

---

## 🔔 4. Webhook Callback (Optional)

Nếu bạn cung cấp `callbackUrl`, server sẽ POST đến URL đó khi job hoàn thành:

### Callback Payload

```json
{
  "sessionId": "job-20231211-143000-abc123",
  "status": "completed",
  "totalLines": 3,
  "completedLines": 3,
  "progress": 100,
  "error": null
}
```

> **Lưu ý:** Callback chỉ chứa thông tin tổng quan. Sau khi nhận callback, client cần gọi `/results/{sessionId}` để lấy kết quả chi tiết.

---

## 💻 Code Examples

### Python Client

```python
import requests
import time
import uuid

BASE_URL = "https://your-server.com/api/subtitle"

def translate_srt(lines: list[dict], prompt: str, system_instruction: str) -> dict:
    """
    Dịch phụ đề qua SubtitleApi

    Args:
        lines: [{"index": 1, "text": "Hello"}, ...]
        prompt: Prompt hướng dẫn dịch
        system_instruction: System instruction cho AI

    Returns:
        {"results": [{"index": 1, "original": "...", "translated": "..."}, ...]}
    """
    # 1. Tạo sessionId unique
    session_id = f"job-{uuid.uuid4().hex[:12]}"

    # 2. Gửi request dịch
    payload = {
        "sessionId": session_id,
        "prompt": prompt,
        "systemInstruction": system_instruction,
        "lines": lines,
        "model": "gemini-2.5-flash"
    }

    response = requests.post(f"{BASE_URL}/translate", json=payload)
    response.raise_for_status()

    print(f"Job created: {session_id}")

    # 3. Polling status
    while True:
        status_response = requests.get(f"{BASE_URL}/status/{session_id}")
        status_data = status_response.json()

        print(f"Progress: {status_data['progress']:.1f}% ({status_data['completedLines']}/{status_data['totalLines']})")

        if status_data["status"] in ["completed", "partialcompleted", "failed"]:
            break

        time.sleep(2)  # Poll every 2 seconds

    # 4. Lấy kết quả
    results_response = requests.get(f"{BASE_URL}/results/{session_id}")
    return results_response.json()


# Ví dụ sử dụng
if __name__ == "__main__":
    # Parse SRT thành lines
    lines = [
        {"index": 1, "text": "Hello world"},
        {"index": 2, "text": "How are you?"},
        {"index": 3, "text": "I'm fine, thank you."}
    ]

    prompt = """Dịch các dòng phụ đề sau sang tiếng Việt.
Format output: index|text đã dịch

Ví dụ:
1|Xin chào thế giới"""

    system_instruction = "Bạn là dịch giả phụ đề phim chuyên nghiệp."

    result = translate_srt(lines, prompt, system_instruction)

    # Parse kết quả
    for item in result["results"]:
        print(f"{item['index']}: {item['original']} -> {item['translated']}")
```

### JavaScript/TypeScript Client

```typescript
interface SubtitleLine {
  index: number;
  text: string;
}

interface TranslatedLine {
  index: number;
  original: string;
  translated: string;
}

interface TranslationResult {
  sessionId: string;
  status: string;
  totalLines: number;
  completedLines: number;
  results: TranslatedLine[];
  error?: string;
}

async function translateSubtitles(
  lines: SubtitleLine[],
  prompt: string,
  systemInstruction: string
): Promise<TranslationResult> {
  const BASE_URL = "https://your-server.com/api/subtitle";
  const sessionId = `job-${Date.now()}-${Math.random().toString(36).slice(2, 10)}`;

  // 1. Submit job
  const submitResponse = await fetch(`${BASE_URL}/translate`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      sessionId,
      prompt,
      systemInstruction,
      lines,
      model: "gemini-2.5-flash"
    })
  });

  if (!submitResponse.ok) {
    const error = await submitResponse.json();
    throw new Error(error.error);
  }

  console.log(`Job created: ${sessionId}`);

  // 2. Poll status
  while (true) {
    const statusResponse = await fetch(`${BASE_URL}/status/${sessionId}`);
    const status = await statusResponse.json();

    console.log(`Progress: ${status.progress.toFixed(1)}%`);

    if (["completed", "partialcompleted", "failed"].includes(status.status)) {
      break;
    }

    await new Promise(resolve => setTimeout(resolve, 2000));
  }

  // 3. Get results
  const resultsResponse = await fetch(`${BASE_URL}/results/${sessionId}`);
  return resultsResponse.json();
}

// Sử dụng
const lines = [
  { index: 1, text: "Hello world" },
  { index: 2, text: "How are you?" }
];

const result = await translateSubtitles(
  lines,
  "Dịch sang tiếng Việt. Format: index|text",
  "Bạn là dịch giả chuyên nghiệp."
);

result.results.forEach(item => {
  console.log(`${item.index}: ${item.original} -> ${item.translated}`);
});
```

### C# Client

```csharp
using System.Net.Http.Json;
using System.Text.Json.Serialization;

public class SubtitleApiClient
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;

    public SubtitleApiClient(string baseUrl)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _httpClient = new HttpClient();
    }

    public async Task<TranslationResult> TranslateAsync(
        List<SubtitleLine> lines,
        string prompt,
        string systemInstruction,
        CancellationToken cancellationToken = default)
    {
        var sessionId = $"job-{Guid.NewGuid():N}";

        // 1. Submit
        var request = new
        {
            sessionId,
            prompt,
            systemInstruction,
            lines = lines.Select(l => new { index = l.Index, text = l.Text }),
            model = "gemini-2.5-flash"
        };

        var submitResponse = await _httpClient.PostAsJsonAsync(
            $"{_baseUrl}/translate", request, cancellationToken);
        submitResponse.EnsureSuccessStatusCode();

        // 2. Poll
        while (true)
        {
            var statusResponse = await _httpClient.GetFromJsonAsync<StatusResponse>(
                $"{_baseUrl}/status/{sessionId}", cancellationToken);

            Console.WriteLine($"Progress: {statusResponse.Progress:F1}%");

            if (statusResponse.Status is "completed" or "partialcompleted" or "failed")
                break;

            await Task.Delay(2000, cancellationToken);
        }

        // 3. Get results
        return await _httpClient.GetFromJsonAsync<TranslationResult>(
            $"{_baseUrl}/results/{sessionId}", cancellationToken);
    }
}

public record SubtitleLine(int Index, string Text);

public record TranslatedLine(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("original")] string Original,
    [property: JsonPropertyName("translated")] string Translated
);

public record StatusResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("progress")] double Progress
);

public record TranslationResult(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("results")] List<TranslatedLine> Results
);
```

---

## 🔄 Flow Diagram

```
┌──────────────────────────────────────────────────────────────────┐
│                          CLIENT                                   │
└───────────────────────────────┬──────────────────────────────────┘
                                │
    1. POST /translate          │
       {sessionId, lines, ...}  │
                                ▼
┌───────────────────────────────────────────────────────────────────┐
│                      MAIN SERVER                                   │
│  ┌─────────────────────────────────────────────────────────────┐  │
│  │ • Validate request                                           │  │
│  │ • Check user quota (DailyLocalSrtLimit)                     │  │
│  │ • Deduct lines from user quota                              │  │
│  │ • Split lines into batches (LinesPerServer)                 │  │
│  │ • Distribute to fly.io servers                               │  │
│  └─────────────────────────────────────────────────────────────┘  │
│                                │                                   │
│                ┌───────────────┼───────────────┐                  │
│                ▼               ▼               ▼                  │
│         ┌──────────┐    ┌──────────┐    ┌──────────┐             │
│         │ Server 1 │    │ Server 2 │    │ Server N │             │
│         │ Fly.io   │    │ Fly.io   │    │ Fly.io   │             │
│         └────┬─────┘    └────┬─────┘    └────┬─────┘             │
│              │               │               │                    │
│              └───────────────┼───────────────┘                    │
│                              │ Callbacks                          │
│                              ▼                                    │
│  ┌─────────────────────────────────────────────────────────────┐  │
│  │ • Aggregate results                                          │  │
│  │ • Handle failed batches (retry with new API keys)           │  │
│  │ • Refund quota if failed                                     │  │
│  │ • Send callback to client (if callbackUrl provided)         │  │
│  └─────────────────────────────────────────────────────────────┘  │
└───────────────────────────────┬──────────────────────────────────┘
                                │
    2. GET /status/{sessionId}  │  (Polling)
    3. GET /results/{sessionId} │
                                ▼
┌───────────────────────────────────────────────────────────────────┐
│                          CLIENT                                    │
│  Parse results:                                                    │
│  results.forEach(item => {                                        │
│    srtLines[item.index].translated = item.translated;             │
│  });                                                               │
└───────────────────────────────────────────────────────────────────┘
```

---

## ⚡ Best Practices

1. **SessionId Format:** Sử dụng format `job-{timestamp}-{random}` để dễ debug
2. **Polling Interval:** 2-5 giây cho file nhỏ, 5-10 giây cho file lớn
3. **Retry Logic:** Nếu polling timeout, thử lại vài lần trước khi báo lỗi
4. **Callback URL:** Sử dụng HTTPS và xác thực request từ server

---

## 📝 Notes

- **Quota:** SubtitleApi sử dụng **chung quota** với LocalApi (`DailyLocalSrtLimit`)
- **Retry:** Server tự động retry batch thất bại với API key mới (tối đa 3 lần)
- **Refund:** Nếu batch thất bại hoàn toàn, quota sẽ được hoàn lại
- **Partial Results:** Nếu status là `partialcompleted`, một số dòng có thể thiếu trong `results`
