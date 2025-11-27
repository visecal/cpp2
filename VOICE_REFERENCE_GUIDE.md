# Voice Reference Guide

## Quick Reference: Voice IDs by Model Type

### 🔤 Standard / WaveNet / Neural2 / Studio Voices

**Voice IDs:** Single letters `A` through `J` (varies by language and model)

**Gender distribution:** Generally alternates between Male/Female, but check via API for specific language

**Example API call to check:**
```bash
curl -X GET "https://your-domain.com/api/aiolauncher-tts/list-voices?languageCode=en-US&modelType=2" \
  -H "Authorization: Bearer YOUR_TOKEN"
```

---

### ⭐ Chirp3-HD Voices (30 Astronomical Names)

**Voice IDs:** Astronomical star names

| Voice ID | Typical Gender | Voice ID | Typical Gender | Voice ID | Typical Gender |
|----------|----------------|----------|----------------|----------|----------------|
| Achernar | Male | Adhara | Female | Aldebaran | Male |
| Altair | Female | Antares | Male | Arcturus | Female |
| Betelgeuse | Male | Canopus | Female | Capella | Male |
| Deneb | Female | Fomalhaut | Male | Hadar | Female |
| Mimosa | Male | Pollux | Female | Procyon | Male |
| Regulus | Female | Rigel | Male | Spica | Female |
| Vega | Male | Acrux | Female | Alnilam | Male |
| Bellatrix | Female | Castor | Male | Elnath | Female |
| Gacrux | Male | Kaus | Female | Miaplacidus | Male |
| Mintaka | Female | Peacock | Male | Rasalhague | Female |

**Note:** Gender varies by language. Always check via API for specific language.

---

## Common Languages - Voice Examples

### 🇺🇸 English (US) - `en-US`

#### Standard (4M chars/month free)
```
en-US-Standard-A (Female)
en-US-Standard-B (Male)
en-US-Standard-C (Female)
en-US-Standard-D (Male)
en-US-Standard-E (Female)
en-US-Standard-F (Female)
en-US-Standard-G (Female)
en-US-Standard-H (Female)
en-US-Standard-I (Male)
en-US-Standard-J (Male)
```

#### WaveNet (1M chars/month free)
```
en-US-Wavenet-A (Male)
en-US-Wavenet-B (Male)
en-US-Wavenet-C (Female)
en-US-Wavenet-D (Male)
en-US-Wavenet-E (Female)
en-US-Wavenet-F (Female)
en-US-Wavenet-G (Female)
en-US-Wavenet-H (Female)
en-US-Wavenet-I (Male)
en-US-Wavenet-J (Male)
```

#### Neural2 (1M chars/month free)
```
en-US-Neural2-A (Male)
en-US-Neural2-C (Female)
en-US-Neural2-D (Male)
en-US-Neural2-E (Female)
en-US-Neural2-F (Female)
en-US-Neural2-G (Female)
en-US-Neural2-H (Female)
en-US-Neural2-I (Male)
en-US-Neural2-J (Male)
```

#### Chirp3-HD (1M chars/month free, $30/1M after)
```
en-US-Chirp3-HD-Achernar (Male)
en-US-Chirp3-HD-Adhara (Female)
en-US-Chirp3-HD-Aldebaran (Male)
en-US-Chirp3-HD-Altair (Female)
... (all 30 astronomical names available)
```

#### Studio (1M chars/month free)
```
en-US-Studio-O (Female)
en-US-Studio-Q (Male)
en-US-Studio-M (Male)
```

---

### 🇬🇧 English (UK) - `en-GB`

#### Standard
```
en-GB-Standard-A (Female)
en-GB-Standard-B (Male)
en-GB-Standard-C (Female)
en-GB-Standard-D (Male)
en-GB-Standard-F (Female)
```

#### WaveNet
```
en-GB-Wavenet-A (Female)
en-GB-Wavenet-B (Male)
en-GB-Wavenet-C (Female)
en-GB-Wavenet-D (Male)
en-GB-Wavenet-F (Female)
```

#### Neural2
```
en-GB-Neural2-A (Female)
en-GB-Neural2-B (Male)
en-GB-Neural2-C (Female)
en-GB-Neural2-D (Male)
en-GB-Neural2-F (Female)
```

#### Chirp3-HD
```
en-GB-Chirp3-HD-Achernar
en-GB-Chirp3-HD-Adhara
... (all 30 names)
```

---

### 🇻🇳 Vietnamese - `vi-VN`

#### Standard
```
vi-VN-Standard-A (Female)
vi-VN-Standard-B (Male)
vi-VN-Standard-C (Female)
vi-VN-Standard-D (Male)
```

#### WaveNet
```
vi-VN-Wavenet-A (Female)
vi-VN-Wavenet-B (Male)
vi-VN-Wavenet-C (Female)
vi-VN-Wavenet-D (Male)
```

#### Neural2
```
vi-VN-Neural2-A (Female)
vi-VN-Neural2-D (Male)
```

#### Chirp3-HD
```
vi-VN-Chirp3-HD-Achernar
vi-VN-Chirp3-HD-Adhara
... (all 30 names)
```

---

### 🇯🇵 Japanese - `ja-JP`

#### Standard
```
ja-JP-Standard-A (Female)
ja-JP-Standard-B (Female)
ja-JP-Standard-C (Male)
ja-JP-Standard-D (Male)
```

#### WaveNet
```
ja-JP-Wavenet-A (Female)
ja-JP-Wavenet-B (Female)
ja-JP-Wavenet-C (Male)
ja-JP-Wavenet-D (Male)
```

#### Neural2
```
ja-JP-Neural2-B (Female)
ja-JP-Neural2-C (Male)
ja-JP-Neural2-D (Male)
```

#### Chirp3-HD
```
ja-JP-Chirp3-HD-Achernar
ja-JP-Chirp3-HD-Adhara
... (all 30 names)
```

---

### 🇰🇷 Korean - `ko-KR`

#### Standard
```
ko-KR-Standard-A (Female)
ko-KR-Standard-B (Female)
ko-KR-Standard-C (Male)
ko-KR-Standard-D (Male)
```

#### WaveNet
```
ko-KR-Wavenet-A (Female)
ko-KR-Wavenet-B (Female)
ko-KR-Wavenet-C (Male)
ko-KR-Wavenet-D (Male)
```

#### Neural2
```
ko-KR-Neural2-A (Female)
ko-KR-Neural2-B (Female)
ko-KR-Neural2-C (Male)
```

#### Chirp3-HD
```
ko-KR-Chirp3-HD-Achernar
ko-KR-Chirp3-HD-Adhara
... (all 30 names)
```

---

### 🇨🇳 Chinese (Mandarin, Simplified) - `cmn-CN`

#### Standard
```
cmn-CN-Standard-A (Female)
cmn-CN-Standard-B (Male)
cmn-CN-Standard-C (Male)
cmn-CN-Standard-D (Female)
```

#### WaveNet
```
cmn-CN-Wavenet-A (Female)
cmn-CN-Wavenet-B (Male)
cmn-CN-Wavenet-C (Male)
cmn-CN-Wavenet-D (Female)
```

#### Chirp3-HD
```
cmn-CN-Chirp3-HD-Achernar
cmn-CN-Chirp3-HD-Adhara
... (all 30 names)
```

#### Polyglot
```
cmn-CN-Polyglot-1 (Female)
```

---

### 🇹🇼 Chinese (Mandarin, Traditional) - `cmn-TW`

#### Standard
```
cmn-TW-Standard-A (Female)
cmn-TW-Standard-B (Male)
cmn-TW-Standard-C (Male)
```

#### WaveNet
```
cmn-TW-Wavenet-A (Female)
cmn-TW-Wavenet-B (Male)
cmn-TW-Wavenet-C (Male)
```

#### Chirp3-HD
```
cmn-TW-Chirp3-HD-Achernar
cmn-TW-Chirp3-HD-Adhara
... (all 30 names)
```

---

### 🇫🇷 French (France) - `fr-FR`

#### Standard
```
fr-FR-Standard-A (Female)
fr-FR-Standard-B (Male)
fr-FR-Standard-C (Female)
fr-FR-Standard-D (Male)
fr-FR-Standard-E (Female)
```

#### WaveNet
```
fr-FR-Wavenet-A (Female)
fr-FR-Wavenet-B (Male)
fr-FR-Wavenet-C (Female)
fr-FR-Wavenet-D (Male)
fr-FR-Wavenet-E (Female)
```

#### Neural2
```
fr-FR-Neural2-A (Female)
fr-FR-Neural2-B (Male)
fr-FR-Neural2-C (Female)
fr-FR-Neural2-D (Male)
fr-FR-Neural2-E (Female)
```

#### Chirp3-HD
```
fr-FR-Chirp3-HD-Achernar
fr-FR-Chirp3-HD-Adhara
... (all 30 names)
```

#### Studio
```
fr-FR-Studio-A (Female)
fr-FR-Studio-D (Male)
```

---

### 🇩🇪 German - `de-DE`

#### Standard
```
de-DE-Standard-A (Female)
de-DE-Standard-B (Male)
de-DE-Standard-C (Female)
de-DE-Standard-D (Male)
de-DE-Standard-E (Male)
de-DE-Standard-F (Female)
```

#### WaveNet
```
de-DE-Wavenet-A (Female)
de-DE-Wavenet-B (Male)
de-DE-Wavenet-C (Female)
de-DE-Wavenet-D (Male)
de-DE-Wavenet-E (Male)
de-DE-Wavenet-F (Female)
```

#### Neural2
```
de-DE-Neural2-A (Female)
de-DE-Neural2-B (Male)
de-DE-Neural2-C (Female)
de-DE-Neural2-D (Male)
de-DE-Neural2-F (Female)
```

#### Chirp3-HD
```
de-DE-Chirp3-HD-Achernar
de-DE-Chirp3-HD-Adhara
... (all 30 names)
```

---

### 🇪🇸 Spanish (Spain) - `es-ES`

#### Standard
```
es-ES-Standard-A (Female)
es-ES-Standard-B (Male)
es-ES-Standard-C (Female)
es-ES-Standard-D (Female)
```

#### WaveNet
```
es-ES-Wavenet-B (Male)
es-ES-Wavenet-C (Female)
es-ES-Wavenet-D (Female)
```

#### Neural2
```
es-ES-Neural2-A (Female)
es-ES-Neural2-B (Male)
es-ES-Neural2-C (Female)
es-ES-Neural2-D (Female)
es-ES-Neural2-E (Female)
es-ES-Neural2-F (Male)
```

#### Chirp3-HD
```
es-ES-Chirp3-HD-Achernar
es-ES-Chirp3-HD-Adhara
... (all 30 names)
```

---

## 🔍 How to Discover All Voices

### Method 1: Via API (Recommended)

```bash
# Get all voices for a specific language
curl -X GET "https://your-domain.com/api/aiolauncher-tts/list-voices?languageCode=en-US" \
  -H "Authorization: Bearer YOUR_TOKEN" | jq '.voices[] | {name, ssmlGender, modelType, voiceId}'

# Filter by model type
curl -X GET "https://your-domain.com/api/aiolauncher-tts/list-voices?languageCode=vi-VN&modelType=4" \
  -H "Authorization: Bearer YOUR_TOKEN"
```

### Method 2: Via Google Cloud Console

```bash
# Using gcloud CLI
gcloud auth application-default login
curl -H "Authorization: Bearer $(gcloud auth application-default print-access-token)" \
  "https://texttospeech.googleapis.com/v1/voices?languageCode=en-US"
```

---

## 💡 Tips for Choosing Voices

### For Different Content Types:

**Formal Content (Business, Documentation):**
- Use: WaveNet, Neural2, or Studio voices
- Example: `en-US-Wavenet-D` (Male, professional)

**Casual Content (Social media, Marketing):**
- Use: Casual, Chirp3-HD voices
- Example: `en-US-Chirp3-HD-Altair` (Female, friendly)

**News/Broadcast:**
- Use: Studio, News voices
- Example: `en-US-Studio-Q` (Male, authoritative)

**Conversational AI/Chatbot:**
- Use: Neural2, Chirp3-HD voices
- Example: `en-US-Chirp3-HD-Betelgeuse` (Male, conversational)

**E-Learning/Audiobook:**
- Use: WaveNet, Neural2 voices
- Example: `en-US-Wavenet-C` (Female, clear pronunciation)

**Cost-Sensitive Applications:**
- Use: Standard voices (4M free/month, only $4/1M after)
- Example: `en-US-Standard-A` (Female, economical)

---

## 🌟 Gender Distribution Tips

### General Patterns (may vary by language):

**Letters typically Female:** A, C, E, F, G, H
**Letters typically Male:** B, D, I, J

**Chirp3-HD alternates:**
- Male: Achernar, Aldebaran, Antares, Betelgeuse, Capella, ...
- Female: Adhara, Altair, Arcturus, Canopus, Deneb, ...

**Always verify via API** as gender can differ by language!

---

## 📝 Quick Reference Table

| Need | Model Type | Typical Voice ID | Cost/1M after free |
|------|------------|------------------|-------------------|
| Cheapest | Standard | A-J | $4 |
| Best Quality | Chirp3-HD | Star names | $30 |
| Natural Voice | WaveNet/Neural2 | A-J | $16 |
| News Reading | Studio | O, Q, M | $16 |
| High Free Quota | Standard | A-J | 4M free/month |
| Low Latency | Chirp3-HD | Star names | 1M free/month |
| Multi-language | Polyglot | 1 | $16 |

---

## 🔗 Quick Links

- [Main API Documentation](./GOOGLE_TTS_API_DOCUMENTATION.md)
- [Admin: Manage Service Accounts](/Admin/AioLauncherTts)
- [Admin: Model Configurations](/Admin/GoogleTtsModels)
- [Google Official Voice List](https://cloud.google.com/text-to-speech/docs/voices)
