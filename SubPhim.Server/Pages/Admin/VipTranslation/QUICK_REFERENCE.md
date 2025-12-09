# VIP Translation API - Quick Reference

## Authentication
```
Authorization: Bearer AIO_xxxxxxxxxxxxxxxxxxxxxxxxx
```

## Base URL
```
https://your-domain.com/api/v1/external
```

## Quick Start (Python)
```python
import requests
import time

API_KEY = "AIO_xxx"
BASE_URL = "https://your-domain.com/api/v1/external"
headers = {"Authorization": f"Bearer {API_KEY}"}

# Start job
resp = requests.post(f"{BASE_URL}/translation/start", 
    headers=headers,
    json={
        "targetLanguage": "Vietnamese",
        "lines": [{"index": 1, "text": "Hello world"}]
    })
session_id = resp.json()["sessionId"]

# Poll for results
while True:
    result = requests.get(f"{BASE_URL}/translation/result/{session_id}", 
        headers=headers).json()
    if result["status"] == "Completed":
        print(result["result"]["lines"])
        break
    time.sleep(3)
```

## Endpoints

| Method | Endpoint | Purpose |
|--------|----------|---------|
| GET | `/account/info` | Get balance & limits |
| POST | `/estimate` | Estimate cost |
| POST | `/translation/start` | Start translation |
| GET | `/translation/result/{id}` | Get results |
| POST | `/translation/cancel/{id}` | Cancel job |
| GET | `/account/usage` | Usage history |
| GET | `/account/transactions` | Transaction history |

## Key Limits

- **RPM**: Check `rpmLimit` in account info (default: 100)
- **Max chars/line**: 3000
- **Credit charge**: 1 credit = 1 output character

## Common Status Codes

| Code | Meaning |
|------|---------|
| 200 | Success |
| 202 | Job accepted |
| 400 | Invalid request |
| 401 | Invalid API key |
| 402 | Insufficient credits |
| 429 | Rate limit exceeded |
| 500 | Server error |

## Translation Job Flow

```
1. POST /translation/start → get sessionId
2. Poll GET /translation/result/{sessionId} every 2-5s
3. Wait for status: "Completed" or "Failed"
4. Retrieve translated lines
```

## Response Status Values

- `"Processing"` - Job in progress
- `"Completed"` - Job finished successfully
- `"Failed"` - Job failed (credits refunded)
- `"Cancelled"` - Job cancelled (credits refunded)

## Error Handling

```python
try:
    response = requests.post(url, headers=headers, json=data)
    response.raise_for_status()
except requests.exceptions.HTTPError as e:
    status_code = e.response.status_code
    if status_code == 402:
        print("Insufficient credits")
    elif status_code == 429:
        print("Rate limit exceeded")
    else:
        print(f"Error: {e.response.json()}")
```

## Best Practices

✅ Check credit balance before large jobs  
✅ Poll results every 2-5 seconds  
✅ Implement exponential backoff for retries  
✅ Handle all error codes properly  
✅ Monitor credit usage regularly  
✅ Never hardcode API keys  
✅ Log all API interactions  

## Need Help?

📖 Full documentation: See `API_DOCUMENTATION.md` or `API_DOCUMENTATION_EN.md`  
👨‍💼 Admin support: Contact system administrator  
💰 Add credits: Request through admin  

---
*For complete documentation with detailed examples, see API_DOCUMENTATION.md (Vietnamese) or API_DOCUMENTATION_EN.md (English)*
