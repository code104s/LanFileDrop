# LanFileDrop

Ứng dụng WPF dành cho Windows để truyền file, thư mục và văn bản trực tiếp trong LAN, không cần Internet hay server trung gian.

## Tính năng

- Tự phát hiện thiết bị bằng UDP broadcast, làm mới mỗi 5 giây.
- Gửi/nhận bằng `TcpListener` / `TcpClient`, đọc theo chunk 1 MB.
- Xác nhận nhận file bằng toast trước khi bên gửi bắt đầu truyền.
- SHA-256 được tính trong lúc stream; bên nhận xác minh và trả acknowledgement cho bên gửi.
- File lỗi checksum bị xóa. File trùng tên được lưu với hậu tố `(1)`, `(2)`, ...
- Thư mục được ZIP vào file tạm trên đĩa trước khi truyền.
- Tiến trình, tốc độ MB/s, ETA, pause/resume và cancel phía gửi.
- Lịch sử SQLite có lọc theo ngày, chiều truyền và dung lượng tối thiểu.
- Light/Dark mode, system tray và badge số transfer đang chạy.
- Web LAN responsive cho iPhone/Android: quét QR để upload và tải file mà không cần cài ứng dụng mobile.

## Cấu trúc

- `LanFileDrop.Core`: model và SQLite history repository.
- `LanFileDrop.Network`: UDP discovery, TCP protocol, streaming và checksum.
- `LanFileDrop.Desktop`: WPF UI/MVVM, toast, tray và theme.
- `LanFileDrop.Tests`: test model, SQLite và transfer loopback nhiều chunk.

## Chạy ứng dụng

Yêu cầu Windows và .NET 8 Desktop Runtime/SDK.

```powershell
dotnet restore LanFileDrop.sln --configfile NuGet.Config
dotnet run --project LanFileDrop.Desktop\LanFileDrop.Desktop.csproj
```

## Dùng với iPhone hoặc Android

1. Mở LanFileDrop trên Windows.
2. Chọn **Kết nối điện thoại**.
3. Đảm bảo điện thoại dùng cùng Wi-Fi, rồi quét QR bằng Camera.
4. Đặt tên thiết bị trên trang web, ví dụ `iPhone của An`. Tên được lưu trong Safari và thiết bị sẽ xuất hiện trong danh sách của Windows.
5. Chọn file trên trang web. Cửa sổ Windows sẽ hiện lên và chờ **Chấp nhận/Từ chối** tối đa 30 giây trước khi upload bắt đầu.

Trang web gửi heartbeat mỗi 5 giây; thiết bị biến mất khỏi danh sách sau khoảng 16 giây khi đóng tab. Trang web cho phép upload nhiều file, gửi văn bản UTF-8, xem và sao chép trực tiếp từng tin nhắn, hiển thị tiến trình và tải xuống các file trong `Downloads\LanFileDrop`. Văn bản được lưu thành file `.txt`. Token upload chỉ dùng một lần và hết hạn sau 2 phút.

Để hai máy nhìn thấy và kết nối được, Windows Firewall cần cho phép ứng dụng trên mạng Private. Các cổng mặc định:

- UDP `49494`: discovery.
- TCP `49495`: truyền dữ liệu.
- TCP `49500`: web LAN cho điện thoại.

File nhận được lưu tại `%USERPROFILE%\Downloads\LanFileDrop`. Lịch sử nằm tại `%LOCALAPPDATA%\LanFileDrop\history.db`.

## Kiểm thử

```powershell
dotnet test LanFileDrop.Tests\LanFileDrop.Tests.csproj
dotnet build LanFileDrop.sln -c Release
```
