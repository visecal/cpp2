# Subtitle Translation API - Hướng dẫn sử dụng

## Tổng quan

API dịch phụ đề phân tán cho phép bạn gửi file SRT lên server chính, server sẽ tự động phân phối công việc đến nhiều server dịch (fly.io) để xử lý song song, sau đó tổng hợp kết quả trả về.

### Kiến trúc hệ thống

```
┌─────────────┐     ┌────────────────┐     ┌─────────────────┐
│   Client    │────►│  Server Chính  │────►│  Server Dịch 1  │
│  (User App) │     │   (SubPhim)    │     │    (fly.io)     │
└─────────────┘     │                │     └─────────────────┘
                    │                │────►│  Server Dịch 2  │
                    │                │     │    (fly.io)     │
                    │                │     └─────────────────┘
                    │                │────►│  Server Dịch N  │
                    │                │     │    (fly.io)     │
                    └────────────────┘     └─────────────────┘
```

## Base URL

```
https://your-subphim-server.com/api/subtitle
```

---

## Endpoints

### 1. Submit Translation Job

Gửi job dịch phụ đề mới.

```http
POST /api/subtitle/translate
Content-Type: application/json
```

**Request Body:**

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `sessionId` | string | **Yes** | - | ID phiên dịch (unique, do client tạo) |
| `prompt` | string | **Yes** | - | Prompt hướng dẫn dịch |
| `systemInstruction` | string | **Yes** | - | System instruction cho AI |
| `lines` | array | **Yes** | - | Danh sách dòng cần dịch |
| `lines[].index` | int | **Yes** | - | Số thứ tự dòng (từ SRT) |
| `lines[].text` | string | **Yes** | - | Nội dung cần dịch |
| `model` | string | No | `gemini-2.5-flash` | Model AI sử dụng |
| `thinkingBudget` | int | No | `0` | Token budget cho thinking (0 = tắt) |
| `callbackUrl` | string | No | `null` | URL callback khi hoàn thành |

**Ví dụ Request:**

```json
{
  "sessionId": "job-20231211-143000-abc123",
  "prompt": "Dịch phụ đề sau sang tiếng Việt.\nGiữ nguyên format: index|text đã dịch\nChỉ trả về kết quả dịch, không giải thích.",
  "systemInstruction": "Bạn là dịch giả phụ đề phim chuyên nghiệp.\n- Dịch tự nhiên, phù hợp ngữ cảnh\n- Giữ nguyên tên riêng phổ biến\n- Không thêm bớt ý nghĩa",
  "lines": [
    {"index": 1, "text": "Hello world"},
    {"index": 2, "text": "How are you?"},
    {"index": 3, "text": "Nice to meet you"}
  ],
  "model": "gemini-2.5-flash",
  "thinkingBudget": 0,
  "callbackUrl": "https://your-server.com/api/translation-callback"
}
```

**Response (Success - 200):**

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

**Response (Error - 400):**

```json
{
  "error": "sessionId là bắt buộc"
}
```

---

### 2. Get Job Status

Polling để kiểm tra tiến trình job.

```http
GET /api/subtitle/status/{sessionId}
```

**Response:**

```json
{
  "sessionId": "job-20231211-143000-abc123",
  "status": "processing",
  "progress": 66.67,
  "totalLines": 100,
  "completedLines": 67,
  "error": null,
  "taskStats": {
    "Completed": 2,
    "Processing": 1,
    "Pending": 0
  }
}
```

**Status Values:**

| Status | Description |
|--------|-------------|
| `pending` | Job đã tạo, chờ phân phối |
| `distributing` | Đang phân phối đến các server |
| `processing` | Các server đang dịch |
| `aggregating` | Đang tổng hợp kết quả |
| `completed` | Hoàn thành |
| `failed` | Thất bại |
| `partialcompleted` | Hoàn thành một phần (có lỗi) |

---

### 3. Get Full Results

Lấy kết quả đầy đủ khi job hoàn thành.

```http
GET /api/subtitle/results/{sessionId}
```

**Response (Completed):**

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
      "original": "Nice to meet you",
      "translated": "Rất vui được gặp bạn"
    }
  ],
  "error": null,
  "createdAt": "2023-12-11T14:30:00Z",
  "completedAt": "2023-12-11T14:31:30Z"
}
```

---

### 4. Health Check

```http
GET /api/subtitle/health
```

**Response:**

```json
{
  "service": "Subtitle Translation API (Distributed)",
  "status": "running",
  "timestamp": "2023-12-11T14:30:00Z"
}
```

---

## Callback System

Khi job hoàn thành (success hoặc failed), server sẽ gửi POST request đến `callbackUrl` với payload:

```json
{
  "sessionId": "job-20231211-143000-abc123",
  "status": "completed",
  "totalLines": 100,
  "completedLines": 100,
  "progress": 100,
  "error": null
}
```

---

## Complete Workflow

### 1. Basic Flow (Polling)

```
Client                          Server Chính
  │                                  │
  ├─── POST /api/subtitle/translate ►│
  │◄── {sessionId, status} ──────────┤
  │                                  │
  │    (wait 3s)                     │
  ├─── GET /status/{id} ────────────►│
  │◄── {progress: 33%} ──────────────┤
  │                                  │
  │    (wait 3s)                     │
  ├─── GET /status/{id} ────────────►│
  │◄── {progress: 66%} ──────────────┤
  │                                  │
  │    (wait 3s)                     │
  ├─── GET /status/{id} ────────────►│
  │◄── {status: completed} ──────────┤
  │                                  │
  ├─── GET /results/{id} ───────────►│
  │◄── {results: [...]} ─────────────┤
```

### 2. With Callback (Recommended)

```
Client                          Server Chính                   Your Backend
  │                                  │                              │
  ├─── POST /translate ─────────────►│                              │
  │    (with callbackUrl)            │                              │
  │◄── {sessionId} ──────────────────┤                              │
  │                                  │                              │
  │    (optional polling)            │                              │
  │                                  │── (job completed) ──────────►│
  │                                  │   POST callbackUrl           │
  │                                  │◄── {received: true} ─────────┤
  │                                  │                              │
  │◄── (receive notification) ───────────────────────────────────────┤
```

---

## Code Examples

### Python Client

```python
import requests
import time
import uuid
from datetime import datetime

SERVER = "https://your-subphim-server.com"

def create_session_id():
    return f"job-{datetime.now().strftime('%Y%m%d-%H%M%S')}-{uuid.uuid4().hex[:6]}"

def translate_srt(srt_lines, callback_url=None):
    """
    Dịch phụ đề qua API phân tán

    Args:
        srt_lines: List of {"index": int, "text": str}
        callback_url: Optional callback URL

    Returns:
        List of translated lines
    """
    session_id = create_session_id()

    # 1. Submit job
    payload = {
        "sessionId": session_id,
        "prompt": """Dịch phụ đề sau sang tiếng Việt.
Giữ nguyên format: index|text đã dịch
Chỉ trả về kết quả dịch, không giải thích.""",
        "systemInstruction": """Bạn là dịch giả phụ đề phim chuyên nghiệp.
- Dịch tự nhiên, phù hợp ngữ cảnh
- Giữ nguyên tên riêng phổ biến
- Không thêm bớt ý nghĩa""",
        "lines": srt_lines,
        "model": "gemini-2.5-flash",
        "callbackUrl": callback_url
    }

    response = requests.post(f"{SERVER}/api/subtitle/translate", json=payload)
    if response.status_code != 200:
        raise Exception(f"Submit failed: {response.text}")

    print(f"Job submitted: {session_id}")

    # 2. Poll for results
    while True:
        time.sleep(3)

        status_response = requests.get(f"{SERVER}/api/subtitle/status/{session_id}")
        status = status_response.json()

        print(f"Progress: {status['progress']:.1f}%")

        if status['status'] == 'completed':
            results = requests.get(f"{SERVER}/api/subtitle/results/{session_id}").json()
            return results['results']
        elif status['status'] in ['failed', 'partialcompleted']:
            raise Exception(f"Job failed: {status.get('error', 'Unknown error')}")

# Usage
srt_lines = [
    {"index": 1, "text": "Hello world"},
    {"index": 2, "text": "How are you?"},
    {"index": 3, "text": "Nice to meet you"}
]

results = translate_srt(srt_lines)
for r in results:
    print(f"{r['index']}: {r['original']} -> {r['translated']}")
```

### JavaScript/Node.js Client

```javascript
const axios = require('axios');

const SERVER = 'https://your-subphim-server.com';

function createSessionId() {
    const now = new Date();
    const dateStr = now.toISOString().replace(/[-:T.]/g, '').slice(0, 14);
    const randomStr = Math.random().toString(36).substring(2, 8);
    return `job-${dateStr}-${randomStr}`;
}

async function translateSrt(srtLines, callbackUrl = null) {
    const sessionId = createSessionId();

    // 1. Submit job
    const payload = {
        sessionId,
        prompt: `Dịch phụ đề sau sang tiếng Việt.
Giữ nguyên format: index|text đã dịch
Chỉ trả về kết quả dịch.`,
        systemInstruction: `Bạn là dịch giả phụ đề phim chuyên nghiệp.
- Dịch tự nhiên, phù hợp ngữ cảnh
- Giữ nguyên tên riêng phổ biến`,
        lines: srtLines,
        model: 'gemini-2.5-flash',
        callbackUrl
    };

    const submitResponse = await axios.post(`${SERVER}/api/subtitle/translate`, payload);
    console.log(`Job submitted: ${sessionId}`);

    // 2. Poll for results
    while (true) {
        await new Promise(resolve => setTimeout(resolve, 3000));

        const statusResponse = await axios.get(`${SERVER}/api/subtitle/status/${sessionId}`);
        const status = statusResponse.data;

        console.log(`Progress: ${status.progress.toFixed(1)}%`);

        if (status.status === 'completed') {
            const resultsResponse = await axios.get(`${SERVER}/api/subtitle/results/${sessionId}`);
            return resultsResponse.data.results;
        } else if (['failed', 'partialcompleted'].includes(status.status)) {
            throw new Error(`Job failed: ${status.error || 'Unknown error'}`);
        }
    }
}

// Usage
const srtLines = [
    { index: 1, text: 'Hello world' },
    { index: 2, text: 'How are you?' },
    { index: 3, text: 'Nice to meet you' }
];

translateSrt(srtLines)
    .then(results => {
        results.forEach(r => {
            console.log(`${r.index}: ${r.original} -> ${r.translated}`);
        });
    })
    .catch(err => console.error(err));
```

---

## Best Practices

### 1. Session ID

Tạo session ID unique cho mỗi job:

```python
session_id = f"job-{datetime.now().strftime('%Y%m%d-%H%M%S')}-{uuid.uuid4().hex[:6]}"
# Example: job-20231211-143052-a1b2c3
```

### 2. Batch Size Recommendations

Server sẽ tự động chia batch theo cài đặt Admin panel:
- **LinesPerServer**: Số dòng gửi cho mỗi server dịch (mặc định: 120)
- **BatchSizePerServer**: Số dòng mỗi request đến Gemini API (mặc định: 40)

Ví dụ: 2000 dòng với 5 server hoạt động:
```
2000 dòng ÷ 120 dòng/server = 17 batch
Mỗi server nhận 120 dòng, dịch theo 3 request (40 + 40 + 40)
Server cuối nhận 80 dòng, dịch theo 2 request
```

### 3. Polling Interval

```python
poll_interval = 3   # seconds cho job nhỏ (<500 lines)
poll_interval = 5   # seconds cho job lớn (>500 lines)
poll_interval = 10  # seconds cho job rất lớn (>2000 lines)
```

### 4. Error Handling

```python
def translate_with_retry(srt_lines, max_retries=3):
    for attempt in range(max_retries):
        try:
            return translate_srt(srt_lines)
        except Exception as e:
            if attempt == max_retries - 1:
                raise
            print(f"Attempt {attempt + 1} failed: {e}")
            time.sleep(5 * (attempt + 1))  # Exponential backoff
```

---

## Error Codes

| HTTP Code | Meaning |
|-----------|---------|
| 200 | Success |
| 400 | Bad request (missing fields, duplicate sessionId) |
| 404 | Job not found |
| 500 | Server error |

---

## Admin Panel

Truy cập `/Admin/SubtitleApi` để quản lý:

1. **Cài đặt chung**: Lines/Server, Batch size, Timeout, Cooldown...
2. **Bể API Key**: Thêm/Xóa/Bật/Tắt các Gemini API key
3. **Server dịch**: Thêm/Xóa/Test các server fly.io
4. **Thống kê job**: Xem các job gần đây, dọn dẹp job đã hoàn thành

---

## Flow xử lý chi tiết

1. **Client gửi request** với danh sách dòng phụ đề
2. **Server chính** nhận request và tạo job trong database
3. **Tính toán batch** thông minh:
   - Chia lines thành các batch theo `LinesPerServer`
   - Gộp batch cuối nếu quá nhỏ (< `MergeBatchThreshold`)
4. **Lấy server khả dụng** (IsEnabled và không bận)
5. **Lấy API key khả dụng** (IsEnabled và không trong cooldown)
6. **Phân phối batch** đến các server dịch song song
7. **Mỗi server dịch** xử lý và gửi callback về
8. **Server chính** tổng hợp kết quả từ các callback
9. **Cập nhật trạng thái key**: Nếu lỗi 429 → đưa vào cooldown
10. **Hoàn thành**: Gửi callback đến client (nếu có) và lưu kết quả
