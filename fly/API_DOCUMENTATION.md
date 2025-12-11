# 📚 Subtitle Translation Server - API Documentation

## 🌐 Base URL

```
Url 1: ,,: https://serverdich.fly.dev
Url 2: ............
```

---

## 📡 Endpoints

### 1. Health Check

Kiểm tra server đang hoạt động.

```http
GET /
```

**Response:**
```json
{
  "service": "Subtitle Translation Server",
  "status": "running",
  "config": {
    "rpm": 5,
    "maxRetries": 5
  },
  "activeJobs": 2,
  "totalJobs": 10
}
```

---

### 2. Submit Translation Job ⭐

Gửi job dịch phụ đề mới.

```http
POST /translate
Content-Type: application/json
```

**Request Body:**

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `model` | string | No | `gemini-2.5-flash` | Model Gemini sử dụng |
| `prompt` | string | **Yes** | - | Prompt hướng dẫn dịch |
| `lines` | array | **Yes** | - | Danh sách dòng cần dịch |
| `lines[].index` | int | **Yes** | - | Số thứ tự dòng (từ SRT) |
| `lines[].text` | string | **Yes** | - | Nội dung cần dịch |
| `systemInstruction` | string | **Yes** | - | System instruction cho AI |
| `sessionId` | string | **Yes** | - | ID định danh job (unique) |
| `apiKeys` | array | **Yes** | - | Danh sách Gemini API keys |
| `batchSize` | int | No | `30` | Số dòng mỗi batch |
| `thinkingBudget` | int | No | `null` | Token budget cho thinking (0-24576) |
| `callbackUrl` | string | No | `null` | URL nhận callback khi hoàn thành |

**Ví dụ Request:**

```json
{
  "model": "gemini-2.5-flash",
  "prompt": "Dịch phụ đề sau sang tiếng Việt.\nGiữ nguyên format: index|text đã dịch\nChỉ trả về kết quả dịch.",
  "lines": [
    {"index": 1, "text": "Hello world"},
    {"index": 2, "text": "How are you?"},
    {"index": 3, "text": "Nice to meet you"}
  ],
  "systemInstruction": "Bạn là dịch giả phụ đề chuyên nghiệp. Dịch tự nhiên, phù hợp ngữ cảnh.",
  "sessionId": "job-20231211-143000-abc123",
  "apiKeys": [
    "AIzaSyBOVp86_LdfFKam4WUxi7U_LRroVav04ws",
    "AIzaSyB9UyXmplRSP5ZeNFZSbml4UhjLF1dCsvU"
  ],
  "batchSize": 30,
  "thinkingBudget": 8192,
  "callbackUrl": "https://your-server.com/api/translation-callback"
}
```

**Response (Success - 200):**

```json
{
  "sessionId": "job-20231211-143000-abc123",
  "status": "pending",
  "totalLines": 3,
  "batchSize": 30,
  "thinkingBudget": 8192,
  "callbackUrl": "https://your-server.com/api/translation-callback",
  "message": "Job submitted successfully"
}
```

**Response (Error - 400):**

```json
{
  "detail": "Job job-xxx is already processing"
}
```

---

### 3. Get Job Status

Polling để kiểm tra tiến trình job.

```http
GET /status/{sessionId}
```

**Response:**

```json
{
  "sessionId": "job-20231211-143000-abc123",
  "status": "processing",
  "progress": 66.67,
  "totalLines": 100,
  "completedLines": 67,
  "results": [],
  "error": null,
  "apiKeyUsage": [
    {
      "maskedKey": "AIzaSyBO****04ws",
      "requestCount": 3,
      "successCount": 2,
      "failureCount": 1
    },
    {
      "maskedKey": "AIzaSyB9****csvU",
      "requestCount": 2,
      "successCount": 2,
      "failureCount": 0
    }
  ],
  "totalRequests": 5
}
```

**Status Values:**

| Status | Description |
|--------|-------------|
| `pending` | Job đã nhận, chờ xử lý |
| `processing` | Đang dịch |
| `completed` | Hoàn thành |
| `failed` | Lỗi |

---

### 4. Get Full Results

Lấy kết quả đầy đủ khi job hoàn thành.

```http
GET /results/{sessionId}
```

**Response (Completed):**

```json
{
  "sessionId": "job-20231211-143000-abc123",
  "status": "completed",
  "totalLines": 3,
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
  "apiKeyUsage": [
    {
      "maskedKey": "AIzaSyBO****04ws",
      "requestCount": 3,
      "successCount": 3,
      "failureCount": 0
    }
  ],
  "totalRequests": 3,
  "totalSuccess": 3,
  "totalFailure": 0
}
```

**Response (Not Completed):**

```json
{
  "sessionId": "job-xxx",
  "status": "processing",
  "message": "Job not completed yet",
  "progress": 45.5,
  "apiKeyUsage": [...]
}
```

---

### 5. Update Server Config

Cập nhật cấu hình RPM và retry.

```http
POST /config
Content-Type: application/json
```

**Request Body:**

```json
{
  "rpm": 10,
  "maxRetries": 3
}
```

**Response:**

```json
{
  "success": true,
  "changes": ["RPM: 5 -> 10", "Max retries: 5 -> 3"],
  "currentConfig": {
    "rpm": 10,
    "maxRetries": 3
  }
}
```

---

### 6. Get Current Config

```http
GET /config
```

**Response:**

```json
{
  "rpm": 5,
  "maxRetries": 5,
  "retryDelayBase": 2
}
```

---

### 7. List All Jobs

```http
GET /jobs
```

**Response:**

```json
{
  "total": 5,
  "jobs": [
    {
      "sessionId": "job-001",
      "status": "completed",
      "progress": 100,
      "totalLines": 500
    },
    {
      "sessionId": "job-002",
      "status": "processing",
      "progress": 45.5,
      "totalLines": 200
    }
  ]
}
```

---

### 8. Delete Job

```http
DELETE /job/{sessionId}
```

**Response:**

```json
{
  "success": true,
  "message": "Job job-001 deleted"
}
```

---

### 9. Cleanup Completed Jobs

Xóa tất cả jobs đã hoàn thành hoặc failed.

```http
DELETE /jobs/completed
```

**Response:**

```json
{
  "deleted": 10,
  "remaining": 2
}
```

---

## 🔔 Callback System

Khi job hoàn thành (success hoặc failed), server sẽ gửi POST request đến `callbackUrl` với payload:

```json
{
  "sessionId": "job-20231211-143000-abc123",
  "status": "completed",
  "totalLines": 100,
  "completedLines": 100,
  "error": null,
  "apiKeyUsage": [
    {
      "apiKey": "AIzaSyBOVp86_LdfFKam4WUxi7U_LRroVav04ws",
      "maskedKey": "AIzaSyBO****04ws",
      "requestCount": 3,
      "successCount": 3,
      "failureCount": 0
    },
    {
      "apiKey": "AIzaSyB9UyXmplRSP5ZeNFZSbml4UhjLF1dCsvU",
      "maskedKey": "AIzaSyB9****csvU",
      "requestCount": 2,
      "successCount": 2,
      "failureCount": 0
    }
  ],
  "totalRequests": 5,
  "totalSuccess": 5,
  "totalFailure": 0
}
```

### Callback Handler Example (Node.js Express)

```javascript
app.post('/api/translation-callback', async (req, res) => {
  const {
    sessionId,
    status,
    totalLines,
    completedLines,
    error,
    apiKeyUsage,
    totalRequests,
    totalSuccess,
    totalFailure
  } = req.body;
  
  // Lưu vào database
  await db.translationJobs.update({
    where: { sessionId },
    data: {
      status,
      completedAt: new Date(),
      error
    }
  });
  
  // Lưu API key usage statistics
  for (const usage of apiKeyUsage) {
    await db.apiKeyUsage.create({
      data: {
        sessionId,
        apiKey: usage.apiKey,
        requestCount: usage.requestCount,
        successCount: usage.successCount,
        failureCount: usage.failureCount,
        createdAt: new Date()
      }
    });
  }
  
  res.json({ received: true });
});
```

### Callback Handler Example (Python FastAPI)

```python
from fastapi import FastAPI
from pydantic import BaseModel
from typing import List, Optional

class ApiKeyUsageCallback(BaseModel):
    apiKey: str
    maskedKey: str
    requestCount: int
    successCount: int
    failureCount: int

class TranslationCallback(BaseModel):
    sessionId: str
    status: str
    totalLines: int
    completedLines: int
    error: Optional[str]
    apiKeyUsage: List[ApiKeyUsageCallback]
    totalRequests: int
    totalSuccess: int
    totalFailure: int

@app.post("/api/translation-callback")
async def handle_callback(data: TranslationCallback):
    # Lưu vào database
    await save_job_result(data.sessionId, data.status, data.error)
    
    # Lưu API key usage
    for usage in data.apiKeyUsage:
        await save_api_usage(
            session_id=data.sessionId,
            api_key=usage.apiKey,
            requests=usage.requestCount,
            success=usage.successCount,
            failure=usage.failureCount
        )
    
    return {"received": True}
```

---

## 🔄 Complete Workflow

### 1. Basic Flow (Polling)

```
Client                          Server
  │                               │
  ├─── POST /translate ──────────►│
  │◄── {sessionId, status} ───────┤
  │                               │
  │    (wait 3s)                  │
  ├─── GET /status/{id} ─────────►│
  │◄── {progress: 33%} ───────────┤
  │                               │
  │    (wait 3s)                  │
  ├─── GET /status/{id} ─────────►│
  │◄── {progress: 66%} ───────────┤
  │                               │
  │    (wait 3s)                  │
  ├─── GET /status/{id} ─────────►│
  │◄── {status: completed} ───────┤
  │                               │
  ├─── GET /results/{id} ────────►│
  │◄── {results: [...]} ──────────┤
```

### 2. With Callback (Recommended)

```
Client                          Server                      Your Backend
  │                               │                              │
  ├─── POST /translate ──────────►│                              │
  │    (with callbackUrl)         │                              │
  │◄── {sessionId} ───────────────┤                              │
  │                               │                              │
  │    (optional polling)         │                              │
  ├─── GET /status/{id} ─────────►│                              │
  │◄── {progress: 50%} ───────────┤                              │
  │                               │                              │
  │                               │── (job completed) ──────────►│
  │                               │   POST callbackUrl           │
  │                               │   {apiKeyUsage, ...}         │
  │                               │◄── {received: true} ─────────┤
  │                               │                              │
  │    (receive notification)     │                              │
  │◄──────────────────────────────────────────────────────────────┤
```

---

## 💡 Best Practices

### 1. Session ID

Tạo session ID unique cho mỗi job:

```python
import uuid
from datetime import datetime

session_id = f"job-{datetime.now().strftime('%Y%m%d-%H%M%S')}-{uuid.uuid4().hex[:6]}"
# Example: job-20231211-143052-a1b2c3
```

### 2. Batch Size

| Số dòng SRT | Batch Size khuyến nghị |
|-------------|------------------------|
| < 100 | 30-50 |
| 100-500 | 30-40 |
| 500-1000 | 20-30 |
| > 1000 | 15-25 |

### 3. Thinking Budget


### 4. API Keys

- Sử dụng nhiều API keys để tăng throughput
- Server tự động rotate khi key bị rate limit
- Theo dõi usage qua callback để quản lý quota

### 5. Polling Interval

```python
# Khuyến nghị
poll_interval = 10 # seconds cho job nhỏ
poll_interval = 20  # seconds cho job lớn (>500 lines)
```


## ⚠️ Error Codes

| HTTP Code | Meaning |
|-----------|---------|
| 200 | Success |
| 400 | Bad request / Job already exists |
| 404 | Job not found |
| 500 | Server error |

---

## 📝 Complete Example (Python)

```python
import requests
import time
import uuid
from datetime import datetime

SERVER = "https://serverdich.fly.dev"

def translate_srt(srt_lines, api_keys, callback_url=None):
    # 1. Generate session ID
    session_id = f"job-{datetime.now().strftime('%Y%m%d-%H%M%S')}-{uuid.uuid4().hex[:6]}"
    
    # 2. Prepare payload
    payload = {
        "model": "gemini-2.5-flash",
        "prompt": "Dịch phụ đề sau sang tiếng Việt.\nFormat: index|text dịch",
        "lines": [{"index": i, "text": text} for i, text in enumerate(srt_lines, 1)],
        "systemInstruction": "Dịch tự nhiên, giữ ngữ cảnh.",
        "sessionId": session_id,
        "apiKeys": api_keys,
        "batchSize": 30,
        "thinkingBudget": 8192,
        "callbackUrl": callback_url
    }
    
    # 3. Submit job
    response = requests.post(f"{SERVER}/translate", json=payload)
    if response.status_code != 200:
        raise Exception(f"Submit failed: {response.text}")
    
    print(f"Job submitted: {session_id}")
    
    # 4. Poll for results (nếu không dùng callback)
    if not callback_url:
        while True:
            time.sleep(3)
            status = requests.get(f"{SERVER}/status/{session_id}").json()
            print(f"Progress: {status['progress']:.1f}%")
            
            if status['status'] == 'completed':
                results = requests.get(f"{SERVER}/results/{session_id}").json()
                return results
            elif status['status'] == 'failed':
                raise Exception(status['error'])
    
    return {"sessionId": session_id, "message": "Job submitted, waiting for callback"}

# Usage
srt_lines = ["Hello world", "How are you?", "Nice to meet you"]
api_keys = ["AIzaSyBOVp86_xxx", "AIzaSyB9UyXm_xxx"]
results = translate_srt(srt_lines, api_keys)
print(results)
```

