# Google TTS API Documentation

## Tổng quan

Hệ thống hỗ trợ đầy đủ 9 loại model Google Cloud Text-to-Speech với quản lý quota riêng biệt cho từng model. Mỗi Service Account (SA) được gán cho một model type cụ thể và tự động theo dõi giới hạn miễn phí hàng tháng.

---

## 📋 Model Types và Giới hạn

| Model Type | Enum Value | Giới hạn miễn phí/tháng | Giá sau giới hạn | SSML | Speaking Rate | Pitch |
|------------|------------|-------------------------|------------------|------|---------------|-------|
| **Standard** | `1` | 4,000,000 ký tự | $4.00/1M | ✅ | ✅ | ✅ |
| **WaveNet** | `2` | 1,000,000 ký tự | $16.00/1M | ✅ | ✅ | ✅ |
| **Neural2** | `3` | 1,000,000 ký tự | $16.00/1M | ✅ | ✅ | ✅ |
| **Chirp3HD** | `4` | 1,000,000 ký tự | $30.00/1M | ❌ | ❌ | ❌ |
| **ChirpHD** | `5` | 1,000,000 ký tự | $30.00/1M | ❌ | ❌ | ❌ |
| **Studio** | `6` | 1,000,000 ký tự | $16.00/1M | ✅ | ✅ | ✅ |
| **Polyglot** | `7` | 1,000,000 ký tự | $16.00/1M | ✅ | ✅ | ✅ |
| **News** | `8` | 1,000,000 ký tự | $16.00/1M | ✅ | ✅ | ✅ |
| **Casual** | `9` | 1,000,000 ký tự | $16.00/1M | ✅ | ✅ | ✅ |

---

## 🎯 API Endpoints

### 1. List Available Voices

Lấy danh sách tất cả các voices có sẵn từ Google Cloud TTS.

**Endpoint:** `GET /api/aiolauncher-tts/list-voices`

**Authentication:** Required (Bearer Token)

**Query Parameters:**

| Parameter | Type | Required | Description | Example |
|-----------|------|----------|-------------|---------|
| `languageCode` | string | ❌ | Mã ngôn ngữ BCP-47 để filter | `en-US`, `vi-VN`, `ja-JP` |
| `modelType` | int | ❌ | Enum value của model type để filter | `1` (Standard), `4` (Chirp3HD) |

**Response Example:**

```json
{
  "voices": [
    {
      "name": "en-US-Standard-A",
      "languageCodes": ["en-US"],
      "ssmlGender": "Female",
      "naturalSampleRateHertz": 24000,
      "modelType": "Standard",
      "voiceId": "A"
    },
    {
      "name": "en-US-Chirp3-HD-Achernar",
      "languageCodes": ["en-US"],
      "ssmlGender": "Male",
      "naturalSampleRateHertz": 24000,
      "modelType": "Chirp3HD",
      "voiceId": "Achernar"
    }
  ],
  "totalCount": 2,
  "filteredBy": {
    "languageCode": "en-US",
    "modelType": "all"
  }
}
```

**cURL Example:**

```bash
# List tất cả voices
curl -X GET "https://your-domain.com/api/aiolauncher-tts/list-voices" \
  -H "Authorization: Bearer YOUR_TOKEN"

# List voices cho tiếng Anh Mỹ
curl -X GET "https://your-domain.com/api/aiolauncher-tts/list-voices?languageCode=en-US" \
  -H "Authorization: Bearer YOUR_TOKEN"

# List chỉ Chirp3HD voices cho tiếng Việt
curl -X GET "https://your-domain.com/api/aiolauncher-tts/list-voices?languageCode=vi-VN&modelType=4" \
  -H "Authorization: Bearer YOUR_TOKEN"
```

---

### 2. Voice Map (Model ⇄ Language ⇄ Voice ID)

Trả về bản đồ đầy đủ các model → ngôn ngữ → voice ID để client có thể hiển thị danh sách lựa chọn.

**Endpoint:** `GET /api/aiolauncher-tts/voice-map`

**Authentication:** Required (Bearer Token)

**Query Parameters:**

| Parameter | Type | Required | Description | Example |
|-----------|------|----------|-------------|---------|
| `languageCode` | string | ❌ | Giới hạn kết quả cho một mã ngôn ngữ (BCP-47) | `vi-VN`, `en-US` |

**Response Shape (rút gọn):**

```json
{
  "models": [
    {
      "modelType": "Chirp3HD",
      "languages": [
        {
          "languageCode": "en-US",
          "voices": [
            {
              "name": "en-US-Chirp3-HD-Achernar",
              "voiceId": "Achernar",
              "ssmlGender": "Male",
              "naturalSampleRateHertz": 24000
            },
            {
              "name": "en-US-Chirp3-HD-Adhara",
              "voiceId": "Adhara",
              "ssmlGender": "Female",
              "naturalSampleRateHertz": 24000
            }
          ]
        }
      ]
    },
    {
      "modelType": "WaveNet",
      "languages": [
        {
          "languageCode": "en-US",
          "voices": [
            {
              "name": "en-US-Wavenet-A",
              "voiceId": "A",
              "ssmlGender": "Male",
              "naturalSampleRateHertz": 24000
            }
          ]
        }
      ]
    }
  ],
  "totalModels": 2,
  "totalVoices": 3,
  "filter": {
    "languageCode": "en-US"
  }
}
```

**cURL Example:**

```bash
# Lấy full map tất cả ngôn ngữ và model
curl -X GET "https://your-domain.com/api/aiolauncher-tts/voice-map" \
  -H "Authorization: Bearer YOUR_TOKEN"

# Lọc theo một ngôn ngữ cụ thể
curl -X GET "https://your-domain.com/api/aiolauncher-tts/voice-map?languageCode=vi-VN" \
  -H "Authorization: Bearer YOUR_TOKEN"
```

**Lưu ý:** `modelType` trong response được suy ra tự động từ tên voice bằng logic `DetectModelTypeFromVoiceName`, giúp client không cần tự phân tích.

---

### 3. Generate TTS

Tạo audio từ văn bản sử dụng model và voice được chỉ định.

**Endpoint:** `POST /api/aiolauncher-tts/generate`

**Authentication:** Required (Bearer Token)

**Request Body:**

```json
{
  "language": "en-US",
  "voiceId": "A",
  "rate": 1.0,
  "text": "Hello, this is a test.",
  "modelType": 4
}
```

**Request Parameters:**

| Field | Type | Required | Description | Default |
|-------|------|----------|-------------|---------|
| `language` | string | ✅ | Mã ngôn ngữ BCP-47 | - |
| `voiceId` | string | ✅ | ID của voice (phần cuối của voice name) | - |
| `rate` | number | ✅ | Tốc độ đọc (0.25 - 4.0) | 1.0 |
| `text` | string | ✅ | Văn bản cần chuyển đổi | - |
| `modelType` | int | ❌ | Enum value của model type | `4` (Chirp3HD) |

**Voice ID Examples:**

- Standard/WaveNet/Neural2: `A`, `B`, `C`, `D`, `E`, `F`, `G`, `H`, `I`, `J`
- Chirp3-HD: `Achernar`, `Adhara`, `Aldebaran`, `Altair`, `Antares`, `Arcturus`, `Betelgeuse`, `Canopus`, `Capella`, `Deneb`, etc.
- Studio: `O`, `Q`, `M`

**Response:** Audio file (audio/mpeg)

**cURL Example:**

```bash
curl -X POST "https://your-domain.com/api/aiolauncher-tts/generate" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "language": "en-US",
    "voiceId": "Achernar",
    "rate": 1.0,
    "text": "Hello, welcome to our service!",
    "modelType": 4
  }' \
  --output output.mp3
```

**Error Responses:**

```json
{
  "message": "Không đủ ký tự TTS. Yêu cầu: 100, còn lại: 50."
}
```

```json
{
  "message": "Server đang bận hoặc đã hết quota cho model Chirp3HD. Vui lòng thử lại sau."
}
```

---

### 4. Batch Upload SRT

Upload file SRT và tạo audio cho từng dòng subtitle.

**Endpoint:** `POST /api/aiolauncher-tts/batch/upload`

**Authentication:** Required (Bearer Token)

**Content-Type:** `multipart/form-data`

**Form Data:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `srtFile` | file | ✅ | File SRT (max 50MB) |
| `language` | string | ✅ | Mã ngôn ngữ BCP-47 |
| `voiceId` | string | ✅ | ID của voice |
| `rate` | number | ✅ | Tốc độ đọc |
| `audioFormat` | string | ✅ | Format audio: `MP3`, `WAV`, `OGG_OPUS` |
| `modelType` | int | ❌ | Enum value của model type (default: 4) |

**Response:**

```json
{
  "jobId": "3fa85f64-5717-4562-b3fc-2c963f66afa6"
}
```

**cURL Example:**

```bash
curl -X POST "https://your-domain.com/api/aiolauncher-tts/batch/upload" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -F "srtFile=@subtitle.srt" \
  -F "language=en-US" \
  -F "voiceId=Achernar" \
  -F "rate=1.0" \
  -F "audioFormat=MP3" \
  -F "modelType=4"
```

---

### 5. Check Batch Status

Kiểm tra trạng thái của batch job.

**Endpoint:** `GET /api/aiolauncher-tts/batch/status/{jobId}`

**Response:**

```json
{
  "jobId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "status": "Completed",
  "totalLines": 100,
  "processedLines": 100,
  "errorMessage": null,
  "createdAt": "2025-01-27T10:00:00Z",
  "completedAt": "2025-01-27T10:05:00Z"
}
```

**Job Status Values:**
- `Pending`: Chờ xử lý
- `Processing`: Đang xử lý
- `Completed`: Hoàn thành
- `Failed`: Thất bại

---

### 6. Download Batch Result

Tải về file ZIP chứa tất cả audio đã tạo.

**Endpoint:** `GET /api/aiolauncher-tts/batch/download/{jobId}`

**Response:** ZIP file

**cURL Example:**

```bash
curl -X GET "https://your-domain.com/api/aiolauncher-tts/batch/download/{jobId}" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  --output result.zip
```

---

## 🗣️ Voice Naming Conventions

### Standard Voices
**Format:** `{language}-Standard-{letter}`

**Examples:**
- `en-US-Standard-A` → Female
- `en-US-Standard-B` → Male
- `vi-VN-Standard-A` → Female

### WaveNet Voices
**Format:** `{language}-Wavenet-{letter}`

**Examples:**
- `en-US-Wavenet-A` → Male
- `ja-JP-Wavenet-A` → Female
- `fr-FR-Wavenet-A` → Female

### Neural2 Voices
**Format:** `{language}-Neural2-{letter}`

**Examples:**
- `en-US-Neural2-A` → Male
- `en-US-Neural2-C` → Female
- `en-GB-Neural2-A` → Female

### Chirp3-HD Voices
**Format:** `{language}-Chirp3-HD-{AstronomicalName}`

**Examples:**
- `en-US-Chirp3-HD-Achernar` → Male
- `en-US-Chirp3-HD-Adhara` → Female
- `vi-VN-Chirp3-HD-Aldebaran` → Male

**Available Astronomical Names:**
- Achernar, Adhara, Aldebaran, Altair, Antares, Arcturus
- Betelgeuse, Canopus, Capella, Deneb, Fomalhaut, Hadar
- Mimosa, Pollux, Procyon, Regulus, Rigel, Spica, Vega

### Studio Voices
**Format:** `{language}-Studio-{letter}`

**Examples:**
- `en-US-Studio-O` → Female
- `en-US-Studio-Q` → Male
- `fr-FR-Studio-A` → Female

### Polyglot Voices
**Format:** `{language}-Polyglot-{number}`

**Example:**
- `cmn-CN-Polyglot-1` → Female

---

## 🌍 Supported Languages (Examples)

### Tiếng Anh
- `en-US` - English (United States)
- `en-GB` - English (United Kingdom)
- `en-AU` - English (Australia)
- `en-IN` - English (India)

### Tiếng Việt
- `vi-VN` - Vietnamese

### Tiếng Nhật
- `ja-JP` - Japanese

### Tiếng Trung
- `cmn-CN` - Mandarin Chinese (Simplified)
- `cmn-TW` - Mandarin Chinese (Traditional)
- `yue-HK` - Cantonese

### Tiếng Hàn
- `ko-KR` - Korean

### Tiếng Pháp
- `fr-FR` - French (France)
- `fr-CA` - French (Canada)

### Tiếng Đức
- `de-DE` - German

### Tiếng Tây Ban Nha
- `es-ES` - Spanish (Spain)
- `es-US` - Spanish (United States)

**Total:** 75+ languages và variants

---

## 💡 Usage Examples

### Example 1: Tạo TTS cho tiếng Anh với Chirp3-HD

```bash
curl -X POST "https://your-domain.com/api/aiolauncher-tts/generate" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "language": "en-US",
    "voiceId": "Achernar",
    "rate": 1.2,
    "text": "Welcome to our advanced text-to-speech service powered by Google Cloud.",
    "modelType": 4
  }' \
  --output welcome.mp3
```

### Example 2: Tạo TTS cho tiếng Việt với Standard

```bash
curl -X POST "https://your-domain.com/api/aiolauncher-tts/generate" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "language": "vi-VN",
    "voiceId": "A",
    "rate": 1.0,
    "text": "Xin chào, đây là dịch vụ chuyển văn bản thành giọng nói.",
    "modelType": 1
  }' \
  --output vietnamese.mp3
```

### Example 3: List voices cho tiếng Nhật với Neural2

```bash
curl -X GET "https://your-domain.com/api/aiolauncher-tts/list-voices?languageCode=ja-JP&modelType=3" \
  -H "Authorization: Bearer YOUR_TOKEN"
```

### Example 4: Batch processing file SRT

```bash
# Upload SRT file
RESPONSE=$(curl -X POST "https://your-domain.com/api/aiolauncher-tts/batch/upload" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -F "srtFile=@movie_subtitle.srt" \
  -F "language=en-US" \
  -F "voiceId=Betelgeuse" \
  -F "rate=1.1" \
  -F "audioFormat=MP3" \
  -F "modelType=4")

# Extract jobId
JOB_ID=$(echo $RESPONSE | jq -r '.jobId')

# Check status
curl -X GET "https://your-domain.com/api/aiolauncher-tts/batch/status/$JOB_ID" \
  -H "Authorization: Bearer YOUR_TOKEN"

# Download result when completed
curl -X GET "https://your-domain.com/api/aiolauncher-tts/batch/download/$JOB_ID" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  --output audio_files.zip
```

---

## 🔒 Quota Management

### Cơ chế hoạt động

1. **Per-SA Quota Tracking**: Mỗi Service Account theo dõi quota riêng theo model type được gán
2. **Monthly Reset**: Quota tự động reset vào đầu tháng mới
3. **Automatic Stop**: SA tự động dừng khi đạt giới hạn miễn phí → tránh phát sinh chi phí
4. **Round-robin**: Hệ thống tự động chọn SA có quota khả dụng

### Ví dụ Quota

Nếu bạn có:
- 3 SA cho Chirp3HD (1M ký tự/tháng mỗi SA)
- 2 SA cho Standard (4M ký tự/tháng mỗi SA)

**Total quota/tháng:**
- Chirp3HD: 3M ký tự
- Standard: 8M ký tự

---

## 📊 Model Comparison

### Khi nào dùng model nào?

**Standard:**
- ✅ Chi phí thấp nhất ($4/1M)
- ✅ Quota miễn phí cao nhất (4M/tháng)
- ⚠️ Chất lượng cơ bản
- 💡 **Use case:** Thông báo hệ thống, nội dung không quan trọng

**WaveNet:**
- ✅ Chất lượng cao, gần giọng người
- ✅ Hỗ trợ đầy đủ SSML, rate, pitch
- ⚠️ Chi phí trung bình ($16/1M)
- 💡 **Use case:** Audiobook, e-learning, nội dung chuyên nghiệp

**Neural2:**
- ✅ Custom voice technology
- ✅ Giọng tự nhiên
- ⚠️ Chi phí trung bình ($16/1M)
- 💡 **Use case:** Voice assistant, chatbot

**Chirp3-HD:**
- ✅ 30 kiểu giọng đa dạng
- ✅ Chất lượng cao nhất
- ❌ Không hỗ trợ SSML, rate, pitch
- ⚠️ Chi phí cao nhất ($30/1M)
- 💡 **Use case:** Real-time conversation, interactive agents

**Studio:**
- ✅ Chuyên cho tin tức, phát thanh
- ✅ Giọng chuyên nghiệp
- ⚠️ Chi phí trung bình ($16/1M)
- 💡 **Use case:** News reading, podcast, broadcast

---

## ⚠️ Important Notes


### SSML Support
- Chirp3-HD và Chirp-HD **KHÔNG** hỗ trợ SSML
- Các model khác hỗ trợ đầy đủ SSML tags

### Character Limits
- Mỗi request có thể gửi tối đa ~5000 bytes
- Hệ thống tự động chia batch nếu văn bản quá dài
- Batch processing hỗ trợ file SRT lên đến 50MB

---

## 📚 References

- [Google Cloud TTS Pricing](https://cloud.google.com/text-to-speech/pricing)
- [Supported Voices and Languages](https://docs.cloud.google.com/text-to-speech/docs/list-voices-and-types)
- [Chirp 3: HD Documentation](https://docs.cloud.google.com/text-to-speech/docs/chirp3-hd)
- [Voice List API](https://cloud.google.com/text-to-speech/docs/reference/rest/v1/voices/list)

---

## 🆘 Support

Nếu gặp vấn đề, hãy kiểm tra:
1. Token authentication có hợp lệ không
2. Quota của user còn không
3. Service Account có hoạt động không
4. Model type và voice ID có match không

**Admin Panel:** `/Admin/AioLauncherTts` - Quản lý Service Accounts
**Model Config:** `/Admin/GoogleTtsModels` - Xem cấu hình models
