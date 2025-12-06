# Hướng Dẫn Sử Dụng API Gemini Pro/Flash Models

## Tổng Quan

Hệ thống hiện hỗ trợ phân biệt giữa hai loại Gemini models với giới hạn RPD (Requests Per Day) riêng biệt:

- **Pro Models** (vd: gemini-2.5-pro, gemini-exp-1206): Mạnh hơn, chậm hơn, giới hạn RPD thấp hơn
- **Flash Models** (vd: gemini-2.5-flash, gemini-2.0-flash-exp): Nhanh hơn, nhẹ hơn, giới hạn RPD cao hơn

## Cấu Hình Trong Admin Panel

### 1. Cài Đặt Chung (General Settings)

Truy cập `Admin/LocalApi` để cấu hình:

#### Model Settings
- **Model Pro mặc định**: Tên model Pro mặc định (mặc định: `gemini-2.5-pro`)
- **RPD cho Model Pro**: Giới hạn requests/ngày cho mỗi API key khi dùng Pro models (mặc định: 250)
- **RPD cho Model Flash**: Giới hạn requests/ngày cho mỗi API key khi dùng Flash models (mặc định: 250)

### 2. Thêm Model Mới

Khi thêm model mới trong phần "Quản lý Model Trả Phí" hoặc "Quản lý Model Miễn Phí":

1. Nhập tên model (vd: `gemini-2.5-pro` hoặc `gemini-2.5-flash`)
2. Chọn loại model:
   - **Pro**: Cho các models dòng Pro
   - **Flash**: Cho các models dòng Flash
3. Click "Thêm"

### 3. Theo Dõi RPD

Trong bảng "Danh sách Key Trả Phí", bạn sẽ thấy:

- **Requests/Ngày (Tổng)**: Tổng số requests trong ngày
- **Pro RPD**: Số requests Pro model đã dùng (badge màu xanh)
- **Flash RPD**: Số requests Flash model đã dùng (badge màu xanh nhạt)

## Cách Hoạt Động

### Rate Limiting Logic

Hệ thống áp dụng 2 loại giới hạn:

1. **RPM (Requests Per Minute)**: Áp dụng chung cho tất cả requests
   - Được cấu hình trong "Request/Phút (RPM)" ở Cài đặt chung
   
2. **RPD (Requests Per Day)**: Áp dụng riêng theo loại model
   - Pro models: Kiểm tra `ProRequestsToday` với giới hạn `ProRpdPerKey`
   - Flash models: Kiểm tra `FlashRequestsToday` với giới hạn `FlashRpdPerKey`

### Chọn API Key

Khi xử lý request, hệ thống:

1. Xác định model type (Pro hoặc Flash) từ model đang active
2. Lọc các API keys:
   - Đang enable
   - Không trong cooldown
   - Chưa vượt giới hạn RPD tương ứng với model type
3. Chọn key theo round-robin
4. Kiểm tra RPM limit
5. Gửi request

### Tracking Usage

Sau mỗi request thành công:

1. Tăng `RequestsToday` (tổng)
2. Tăng `ProRequestsToday` (nếu dùng Pro model)
3. Tăng `FlashRequestsToday` (nếu dùng Flash model)
4. Cập nhật `TotalTokensUsed`

Các counter này được reset về 0 vào 00:00 giờ Việt Nam mỗi ngày.

## Ví Dụ Cấu Hình

### Scenario 1: Phân Bổ RPD Cân Bằng

```
Pro RPD Per Key: 250
Flash RPD Per Key: 250
```

Mỗi key có thể xử lý tối đa 250 requests/ngày cho Pro models và 250 requests/ngày cho Flash models (tổng 500 requests/ngày nếu dùng cả hai loại).

### Scenario 2: Ưu Tiên Flash Models

```
Pro RPD Per Key: 100
Flash RPD Per Key: 500
```

Key có thể xử lý nhiều requests Flash hơn Pro, phù hợp cho workload cần tốc độ.

### Scenario 3: Ưu Tiên Pro Models

```
Pro RPD Per Key: 500
Flash RPD Per Key: 100
```

Key có thể xử lý nhiều requests Pro hơn Flash, phù hợp cho workload cần chất lượng cao.

## Best Practices

1. **Monitoring**: Theo dõi cột Pro RPD và Flash RPD trong Admin panel để biết khi nào cần thêm keys
2. **Model Selection**: 
   - Dùng Pro models cho tasks phức tạp, cần độ chính xác cao
   - Dùng Flash models cho tasks đơn giản, cần tốc độ
3. **RPD Tuning**: Điều chỉnh RPD limits dựa trên:
   - Giới hạn của Google API
   - Số lượng keys có sẵn
   - Nhu cầu workload
4. **Key Pool**: Duy trì đủ keys để đảm bảo không bị gián đoạn khi một số keys đạt giới hạn RPD

## Thay Đổi So Với Phiên Bản Trước

### Đã Loại Bỏ

- **Giới hạn tốc độ toàn server (Global Rate Limit)**: Không còn giới hạn số requests đồng thời trên toàn server

### Đã Thêm

- **Pro/Flash Model Type**: Phân loại models rõ ràng
- **Separate RPD Tracking**: Theo dõi RPD riêng cho Pro và Flash
- **Per-Model-Type RPD Limits**: Giới hạn RPD riêng cho từng loại model

## Troubleshooting

### Lỗi: "No eligible keys available"

**Nguyên nhân**: Tất cả keys đã vượt giới hạn RPD cho model type đang dùng

**Giải pháp**:
1. Kiểm tra cột Pro RPD hoặc Flash RPD trong Admin panel
2. Thêm keys mới
3. Tăng RPD limit (nếu phù hợp với giới hạn của Google)
4. Chờ đến ngày mới để counter được reset

### Keys không được sử dụng đều

**Nguyên nhân**: Round-robin có thể bị ảnh hưởng bởi RPM hoặc RPD limits

**Giải pháp**:
1. Đảm bảo RPM limit phù hợp
2. Đảm bảo RPD limit được cài đặt hợp lý
3. Kiểm tra xem có keys nào bị cooldown hay disabled

## API Endpoint

API endpoint vẫn giữ nguyên. Model type được tự động xác định dựa trên model đang active trong database.

Không cần thay đổi gì trong code client khi gọi API.
