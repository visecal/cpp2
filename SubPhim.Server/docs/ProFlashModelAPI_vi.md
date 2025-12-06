# Tài liệu API Model Pro/Flash

## Tổng quan

Dịch vụ LocalApi hiện hỗ trợ hai loại mô hình Gemini chạy song song:
- **Model Pro** (ví dụ: `gemini-2.5-pro`): Mô hình chuyên nghiệp chất lượng cao
- **Model Flash** (ví dụ: `gemini-2.5-flash`): Mô hình nhanh, hiệu quả

Mỗi loại model có cài đặt giới hạn tốc độ độc lập cho:
- RPM (Request/Phút) mỗi API key
- RPD (Request/Ngày) mỗi API key  
- RPM mỗi Proxy

## API Endpoint

### POST `/api/launcheraio/start-translation`

Bắt đầu một công việc dịch mới sử dụng loại model được chỉ định.

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

#### Tham số

| Tham số | Kiểu | Bắt buộc | Mặc định | Mô tả |
|---------|------|----------|----------|-------|
| Genre | string | Có | - | Thể loại hoặc ngữ cảnh cho dịch |
| TargetLanguage | string | Có | - | Mã ngôn ngữ đích (vd: "vi", "en") |
| Lines | array | Có | - | Mảng các dòng văn bản cần dịch |
| SystemInstruction | string | Có | - | Hướng dẫn hệ thống tùy chỉnh cho AI |
| AcceptPartial | boolean | Không | false | Chấp nhận dịch một phần nếu không đủ quota |
| **ModelType** | integer | Không | 2 | **Loại model: 1 = Flash, 2 = Pro** |

#### Giá trị Model Type

| Giá trị | Loại | Mô tả | Trường hợp sử dụng |
|---------|------|-------|-------------------|
| 1 | Flash | Model nhanh, hiệu quả (gemini-2.5-flash) | Dịch nhanh, xử lý khối lượng lớn |
| 2 | Pro | Model chuyên nghiệp chất lượng cao (gemini-2.5-pro) | Dịch phức tạp, yêu cầu độ chính xác cao |

#### Phản hồi

```json
{
  "Status": "Accepted",
  "Message": null,
  "SessionId": "unique-session-id",
  "RemainingLines": 0
}
```

## Ví dụ

### Ví dụ 1: Sử dụng Model Flash (Dịch nhanh)

```bash
curl -X POST https://your-server.com/api/launcheraio/start-translation \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer YOUR_JWT_TOKEN" \
  -d '{
    "Genre": "Phim hành động",
    "TargetLanguage": "vi",
    "Lines": [
      {"LineIndex": 1, "OriginalText": "Hello"},
      {"LineIndex": 2, "OriginalText": "How are you?"}
    ],
    "SystemInstruction": "Dịch tự nhiên cho khán giả Việt Nam",
    "AcceptPartial": false,
    "ModelType": 1
  }'
```

### Ví dụ 2: Sử dụng Model Pro (Dịch chất lượng cao)

```bash
curl -X POST https://your-server.com/api/launcheraio/start-translation \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer YOUR_JWT_TOKEN" \
  -d '{
    "Genre": "Phim tài liệu",
    "TargetLanguage": "en",
    "Lines": [
      {"LineIndex": 1, "OriginalText": "Xin chào"},
      {"LineIndex": 2, "OriginalText": "Bạn khỏe không?"}
    ],
    "SystemInstruction": "Dịch chuyên nghiệp cho nội dung tài liệu",
    "AcceptPartial": false,
    "ModelType": 2
  }'
```

### Ví dụ 3: Hành vi mặc định (Model Pro)

Nếu không chỉ định `ModelType`, mặc định sẽ dùng Pro (2) để tương thích ngược:

```bash
curl -X POST https://your-server.com/api/launcheraio/start-translation \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer YOUR_JWT_TOKEN" \
  -d '{
    "Genre": "Chung",
    "TargetLanguage": "vi",
    "Lines": [
      {"LineIndex": 1, "OriginalText": "Hello world"}
    ],
    "SystemInstruction": "Dịch tự nhiên"
  }'
```

## Cấu hình Admin

### Thiết lập Models

1. Truy cập Admin → Local API
2. Trong phần "Quản lý Model Trả Phí" hoặc "Quản lý Model Miễn Phí"
3. Thêm model mới:
   - Nhập tên model (vd: `gemini-2.5-pro` hoặc `gemini-2.5-flash`)
   - Chọn loại model:
     - **Pro (Chuyên nghiệp)** cho các model chuyên nghiệp
     - **Flash (Nhanh)** cho các model nhanh
4. Click "Thêm" để thêm model
5. Kích hoạt model bạn muốn sử dụng

### Cấu hình Giới hạn tốc độ

Trong phần "Cài đặt chung", bạn có thể cấu hình giới hạn tốc độ riêng cho từng loại model:

#### Cài đặt Model Pro
- **Request/Phút (RPM)**: Request mỗi phút trên tất cả các key
- **Request/Ngày/Key (RPD)**: Giới hạn request hàng ngày mỗi API key
- **Request/Phút/Proxy**: Request mỗi phút mỗi proxy

#### Cài đặt Model Flash
- **Request/Phút (RPM)**: Request mỗi phút trên tất cả các key
- **Request/Ngày/Key (RPD)**: Giới hạn request hàng ngày mỗi API key
- **Request/Phút/Proxy**: Request mỗi phút mỗi proxy

#### Model Chuyên nghiệp
- **Model Chuyên Nghiệp (Pro)**: Tên model chuyên nghiệp mặc định (vd: `gemini-2.5-pro`)

### Giá trị mặc định

| Cài đặt | Mặc định Pro | Mặc định Flash |
|---------|--------------|----------------|
| RPM | 100 | 100 |
| RPD mỗi Key | 250 | 250 |
| RPM mỗi Proxy | 10 | 10 |

## Cơ chế Giới hạn tốc độ

### Giới hạn theo từng loại Model

- Mỗi loại model (Pro/Flash) có giới hạn tốc độ độc lập
- API keys được chia sẻ giữa các loại model
- RPD (Request/Ngày) được theo dõi riêng cho từng loại model trên mỗi key
- Proxy có thể được chia sẻ miễn là không vi phạm giới hạn RPM proxy của từng loại

### Ví dụ Kịch bản

Nếu bạn có một API key với:
- Giới hạn RPD Pro: 250
- Giới hạn RPD Flash: 250

Key này có thể xử lý:
- Tối đa 250 request Pro mỗi ngày
- Tối đa 250 request Flash mỗi ngày
- Tổng: tối đa 500 request mỗi ngày (250 Pro + 250 Flash)

### Theo dõi Sử dụng

Trong panel Admin, bảng API key hiển thị các cột riêng cho:
- **Req Pro/Ngày**: Số request Pro đã dùng hôm nay
- **Req Flash/Ngày**: Số request Flash đã dùng hôm nay

## Thực hành tốt nhất

1. **Lựa chọn Model**:
   - Dùng **Flash (1)** cho dịch khối lượng lớn, cần tốc độ
   - Dùng **Pro (2)** cho dịch phức tạp, cần độ chính xác cao

2. **Quản lý Giới hạn tốc độ**:
   - Theo dõi sử dụng hàng ngày trong panel Admin
   - Điều chỉnh giới hạn RPD dựa trên quota API key của bạn
   - Cấu hình giới hạn khác nhau cho Pro và Flash theo nhu cầu

3. **Tương thích ngược**:
   - Client hiện có không chỉ định `ModelType` sẽ mặc định dùng Pro (2)
   - Không cần thay đổi gì cho các tích hợp hiện có

4. **Kiểm thử**:
   - Thử nghiệm với cả hai loại model để so sánh tốc độ và chất lượng
   - Điều chỉnh chiến lược lựa chọn model dựa trên kết quả

## Xử lý sự cố

### Lỗi: "Không có model nào đang hoạt động"

**Giải pháp**: Đảm bảo bạn đã kích hoạt ít nhất một model của loại được yêu cầu trong panel Admin.

### Lỗi: Vượt quá quota API key

**Giải pháp**: Kiểm tra cột "Req Pro/Ngày" hoặc "Req Flash/Ngày" trong panel Admin và điều chỉnh giới hạn RPD nếu cần.

### Thời gian phản hồi chậm

**Giải pháp**: 
- Cân nhắc sử dụng model Flash (ModelType: 1) để phản hồi nhanh hơn
- Kiểm tra giới hạn RPM proxy nếu bạn đang chạm giới hạn proxy
- Theo dõi số lượng API key đang hoạt động

## Hướng dẫn Migration

### Cho Client hiện có

Không cần thay đổi gì! Các API call hiện có sẽ tiếp tục hoạt động với model Pro làm mặc định.

### Để sử dụng Model Flash

Chỉ cần thêm `"ModelType": 1` vào request body hiện có.

### Để sử dụng Model Pro rõ ràng

Thêm `"ModelType": 2` vào request body, hoặc bỏ qua tham số này (Pro là mặc định).

## Tài nguyên bổ sung

- Panel Admin: `/Admin/LocalApi`
- Quản lý Model: Cấu hình model hoạt động cho từng loại
- Quản lý API Key: Theo dõi sử dụng theo loại model
- Cấu hình Giới hạn tốc độ: Cài đặt riêng cho Pro và Flash

## Hỗ trợ

Để được hỗ trợ hoặc có câu hỏi về cấu hình model Pro/Flash, vui lòng tham khảo tài liệu panel Admin hoặc liên hệ hỗ trợ.
