# Pro/Flash Model API Documentation

## Overview

The LocalApi translation service now supports two types of Gemini models running in parallel:
- **Pro Models** (e.g., `gemini-2.5-pro`): High-quality professional models
- **Flash Models** (e.g., `gemini-2.5-flash`): Fast, efficient models

Each model type has independent rate limiting settings for:
- RPM (Requests Per Minute) per API key
- RPD (Requests Per Day) per API key
- RPM per Proxy

## API Endpoint

### POST `/api/launcheraio/start-translation`

Starts a new translation job using the specified model type.

#### Request Body

```json
{
  "Genre": "string",
  "TargetLanguage": "string",
  "Lines": [
    {
      "LineIndex": 1,
      "OriginalText": "Hello world"
    }
  ],
  "SystemInstruction": "string",
  "AcceptPartial": false,
  "ModelType": 2
}
```

#### Parameters

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| Genre | string | Yes | - | Genre or context for translation |
| TargetLanguage | string | Yes | - | Target language code (e.g., "vi", "en") |
| Lines | array | Yes | - | Array of text lines to translate |
| SystemInstruction | string | Yes | - | Custom system instruction for the AI model |
| AcceptPartial | boolean | No | false | Accept partial translation if quota is insufficient |
| **ModelType** | integer | No | 2 | **Model type to use: 1 = Flash, 2 = Pro** |

#### Model Type Values

| Value | Type | Description | Use Case |
|-------|------|-------------|----------|
| 1 | Flash | Fast, efficient model (gemini-2.5-flash) | Quick translations, high-volume processing |
| 2 | Pro | High-quality professional model (gemini-2.5-pro) | Complex translations, high accuracy requirements |

#### Response

```json
{
  "Status": "Accepted",
  "Message": null,
  "SessionId": "unique-session-id",
  "RemainingLines": 0
}
```

## Examples

### Example 1: Using Flash Model (Fast Translation)

```bash
curl -X POST https://your-server.com/api/launcheraio/start-translation \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer YOUR_JWT_TOKEN" \
  -d '{
    "Genre": "Action Movie",
    "TargetLanguage": "vi",
    "Lines": [
      {"LineIndex": 1, "OriginalText": "Hello"},
      {"LineIndex": 2, "OriginalText": "How are you?"}
    ],
    "SystemInstruction": "Translate naturally for Vietnamese audience",
    "AcceptPartial": false,
    "ModelType": 1
  }'
```

### Example 2: Using Pro Model (High-Quality Translation)

```bash
curl -X POST https://your-server.com/api/launcheraio/start-translation \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer YOUR_JWT_TOKEN" \
  -d '{
    "Genre": "Documentary",
    "TargetLanguage": "en",
    "Lines": [
      {"LineIndex": 1, "OriginalText": "Xin chào"},
      {"LineIndex": 2, "OriginalText": "Bạn khỏe không?"}
    ],
    "SystemInstruction": "Professional translation for documentary content",
    "AcceptPartial": false,
    "ModelType": 2
  }'
```

### Example 3: Default Behavior (Pro Model)

If `ModelType` is not specified, it defaults to Pro (2) for backward compatibility:

```bash
curl -X POST https://your-server.com/api/launcheraio/start-translation \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer YOUR_JWT_TOKEN" \
  -d '{
    "Genre": "General",
    "TargetLanguage": "vi",
    "Lines": [
      {"LineIndex": 1, "OriginalText": "Hello world"}
    ],
    "SystemInstruction": "Translate naturally"
  }'
```

## Admin Configuration

### Setting Up Models

1. Navigate to Admin → Local API
2. In "Quản lý Model Trả Phí" or "Quản lý Model Miễn Phí" section
3. Add a new model:
   - Enter model name (e.g., `gemini-2.5-pro` or `gemini-2.5-flash`)
   - Select model type:
     - **Pro (Chuyên nghiệp)** for professional models
     - **Flash (Nhanh)** for fast models
4. Click "Thêm" to add the model
5. Activate the model you want to use

### Configuring Rate Limits

In the "Cài đặt chung" section, you can configure separate rate limits for each model type:

#### Pro Model Settings
- **Request/Phút (RPM)**: Requests per minute across all keys
- **Request/Ngày/Key (RPD)**: Daily request limit per API key
- **Request/Phút/Proxy**: Requests per minute per proxy

#### Flash Model Settings
- **Request/Phút (RPM)**: Requests per minute across all keys
- **Request/Ngày/Key (RPD)**: Daily request limit per API key
- **Request/Phút/Proxy**: Requests per minute per proxy

#### Professional Model
- **Model Chuyên Nghiệp (Pro)**: Default professional model name (e.g., `gemini-2.5-pro`)

### Default Values

| Setting | Pro Default | Flash Default |
|---------|-------------|---------------|
| RPM | 100 | 100 |
| RPD per Key | 250 | 250 |
| RPM per Proxy | 10 | 10 |

## Rate Limiting Behavior

### Per-Model-Type Rate Limiting

- Each model type (Pro/Flash) has independent rate limiting
- API keys are shared between model types
- RPD (Requests Per Day) is tracked separately per model type per key
- Proxies can be shared as long as per-model-type proxy RPM limits are respected

### Example Scenario

If you have an API key with:
- Pro RPD limit: 250
- Flash RPD limit: 250

The key can handle:
- Up to 250 Pro requests per day
- Up to 250 Flash requests per day
- Total: up to 500 requests per day (250 Pro + 250 Flash)

### Monitoring Usage

In the Admin panel, the API key table shows separate columns for:
- **Req Pro/Ngày**: Number of Pro requests used today
- **Req Flash/Ngày**: Number of Flash requests used today

## Best Practices

1. **Model Selection**:
   - Use **Flash (1)** for high-volume, speed-critical translations
   - Use **Pro (2)** for complex, accuracy-critical translations

2. **Rate Limit Management**:
   - Monitor daily usage in the Admin panel
   - Adjust RPD limits based on your API key quotas
   - Configure different limits for Pro and Flash based on your needs

3. **Backward Compatibility**:
   - Existing clients not specifying `ModelType` will default to Pro (2)
   - No changes required for existing integrations

4. **Testing**:
   - Test with both model types to compare speed and quality
   - Adjust your model selection strategy based on results

## Troubleshooting

### Error: "Không có model nào đang hoạt động"

**Solution**: Ensure you have activated at least one model of the requested type in the Admin panel.

### Error: API key quota exceeded

**Solution**: Check the "Req Pro/Ngày" or "Req Flash/Ngày" columns in the Admin panel and adjust RPD limits if needed.

### Slow response times

**Solution**: 
- Consider using Flash model (ModelType: 1) for faster responses
- Check proxy RPM limits if you're hitting proxy rate limits
- Monitor the number of active API keys

## Migration Guide

### For Existing Clients

No changes required! Existing API calls will continue to work with Pro model as default.

### To Use Flash Model

Simply add `"ModelType": 1` to your existing request body.

### To Use Pro Model Explicitly

Add `"ModelType": 2` to your request body, or omit the parameter (Pro is default).

## Additional Resources

- Admin Panel: `/Admin/LocalApi`
- Model Management: Configure active models for each type
- API Key Management: Monitor usage per model type
- Rate Limit Configuration: Separate settings for Pro and Flash

## Support

For issues or questions about Pro/Flash model configuration, please refer to the Admin panel documentation or contact support.
