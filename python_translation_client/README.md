# LocalAPI Translation Client

Ứng dụng Python dịch văn bản hàng loạt sử dụng LocalAPI của SubPhim Server.

## Tính năng

- 🔐 **Đăng ký/Đăng nhập**: Xác thực với server sử dụng JWT token
- 📂 **Dịch hàng loạt**: Chọn thư mục chứa các file .txt để dịch
- 🔄 **Xử lý đồng thời**: Hỗ trợ tối đa 100 session dịch cùng lúc
- 📝 **System Instruction tùy chỉnh**: Nhập prompt hướng dẫn cho AI
- 📊 **Polling kết quả**: Tự động theo dõi và lấy kết quả dịch
- 💾 **Tự động lưu**: Tạo thư mục "Đã dịch" và đặt tên file theo nội dung
- 🖥️ **2 phiên bản**: GUI (tkinter) và Command Line

## Phiên bản

| File | Mô tả |
|------|-------|
| `localapi_translator.py` | Phiên bản GUI với tkinter |
| `localapi_translator_cli.py` | Phiên bản Command Line |

## Cài đặt

### Yêu cầu
- Python 3.8 trở lên
- tkinter (thường có sẵn với Python)

### Cài đặt dependencies

```bash
cd python_translation_client
pip install -r requirements.txt
```

Trên Linux, nếu chưa có tkinter:
```bash
sudo apt-get install python3-tk
```

## Sử dụng

### Phiên bản GUI

```bash
python localapi_translator.py
```

### Phiên bản Command Line

```bash
# Đăng ký tài khoản mới
python localapi_translator_cli.py --server http://localhost:5000 --register --username user1 --password pass123 --email user1@example.com

# Đăng nhập và dịch
python localapi_translator_cli.py --server http://localhost:5000 --username user1 --password pass123 --folder ./texts --instruction "Dịch sang tiếng Việt"

# Dịch với nhiều session đồng thời
python localapi_translator_cli.py --server http://localhost:5000 --username user1 --password pass123 --folder ./texts --concurrent 50 --instruction "Dịch tiểu thuyết sang tiếng Việt"
```

### Các bước sử dụng (GUI)

1. **Cấu hình Server**: Nhập URL của SubPhim Server
2. **Đăng nhập**: Nhập username/password hoặc đăng ký tài khoản mới
3. **Cài đặt dịch**:
   - Nhập System Instruction (prompt hướng dẫn AI cách dịch)
   - Chọn ngôn ngữ đích (mặc định: Vietnamese)
   - Điều chỉnh số session đồng thời (1-100)
4. **Chọn thư mục**: Chọn thư mục chứa các file .txt cần dịch
5. **Bắt đầu dịch**: Nhấn "Bắt đầu dịch" và theo dõi tiến trình

### Cấu trúc output

```
📁 Thư mục nguồn/
├── file1.txt
├── file2.txt
├── file3.txt
└── 📁 Đã dịch/
    ├── [Tên từ dòng đầu tiên của kết quả].txt
    ├── ...
```

## API Endpoints sử dụng

⚠️ **QUAN TRỌNG**: Ứng dụng này CHỈ sử dụng LocalAPI endpoints:

| Endpoint | Mục đích |
|----------|----------|
| `POST /api/auth/register` | Đăng ký tài khoản |
| `POST /api/auth/login` | Đăng nhập |
| `POST /api/launcheraio/start-translation` | Bắt đầu job dịch |
| `GET /api/launcheraio/get-results/{sessionId}` | Polling kết quả |

**KHÔNG sử dụng** endpoint `/api/viptranslation` (đang trong giai đoạn test).

## Cơ chế hoạt động

1. **Bypass cấu trúc SRT**: Mỗi file txt được gửi như một dòng SRT duy nhất với `Index=1` và `OriginalText` là toàn bộ nội dung file
2. **Session riêng biệt**: Mỗi file có sessionId riêng để theo dõi
3. **Polling**: Sau khi tạo job, ứng dụng polling kết quả mỗi giây (tối đa 120 lần)
4. **Đặt tên file**: Tên file output lấy từ 50 ký tự đầu tiên của kết quả dịch

## Cấu hình

### ServerConfig
- `base_url`: URL của server (mặc định: http://localhost:5000)
- `timeout`: Timeout cho mỗi request (mặc định: 60 giây)
- `max_concurrent_sessions`: Số session tối đa cùng lúc (giới hạn: 100)

## Lưu ý

- Đảm bảo tài khoản có đủ quota LocalAPI (`DailyLocalSrtLimit`)
- File txt nên có encoding UTF-8
- Văn bản quá dài có thể ảnh hưởng đến chất lượng dịch
- Nên test với vài file trước khi dịch hàng loạt

## License

MIT License - Xem file LICENSE để biết thêm chi tiết.
