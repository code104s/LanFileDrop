# LanFileDrop

<p align="center">
  <img src="LanFileDrop.Desktop/Assets/Logo/lanfiledrop-256.png" width="128" alt="LanFileDrop logo">
</p>

<p align="center">
  Truyền file, thư mục và văn bản trực tiếp giữa Windows và điện thoại trong LAN — không cần Internet hoặc máy chủ trung gian.
</p>

## Tổng quan

LanFileDrop là ứng dụng Windows viết bằng C#/.NET 8 và WPF. Hai máy Windows có thể tự phát hiện nhau bằng UDP và truyền dữ liệu qua TCP. Điện thoại truy cập giao diện web nội bộ bằng QR để gửi file, tải file hoặc trao đổi văn bản mà không cần cài ứng dụng riêng.

## Tính năng chính

- Tự phát hiện nhiều máy Windows trong LAN, làm mới danh sách mỗi 5 giây.
- Nhận diện nhiều trình duyệt mobile độc lập qua tên thiết bị và heartbeat.
- Gửi file, nhiều file, thư mục ZIP và văn bản UTF-8.
- Kéo thả file/thư mục trực tiếp vào cửa sổ.
- Xác nhận **Chấp nhận/Từ chối** trước khi nhận dữ liệu.
- Hiển thị phần trăm, tốc độ MB/s, ETA, tạm dừng và hủy.
- Resume theo checkpoint 1 MB khi kết nối bị gián đoạn.
- Tự retry/reconnect tối đa 5 lần với thời gian chờ tăng dần.
- Kiểm tra SHA-256 trước khi đổi file `.partial` thành file hoàn chỉnh.
- Kiểm tra dung lượng trống trước khi nhận file lớn.
- Lịch sử SQLite với giao diện card, trạng thái trực quan và bộ lọc ngày/chiều truyền/dung lượng.
- Light/Dark mode được lưu lại cho lần khởi động tiếp theo.
- System tray và badge số transfer đang chạy.
- Logo/icon riêng cho cửa sổ, taskbar, system tray và executable.

## Yêu cầu

- Windows 10/11.
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) để build từ source.
- Hai thiết bị cùng LAN, hoặc có thể kết nối trực tiếp qua VPN ảo như Radmin VPN.
- Windows Firewall cho phép LanFileDrop trên mạng Private.

## Build và chạy

```powershell
git clone https://github.com/code104s/LanFileDrop.git
cd LanFileDrop
dotnet restore LanFileDrop.sln --configfile NuGet.Config
dotnet run --project LanFileDrop.Desktop\LanFileDrop.Desktop.csproj
```

Build bản Release:

```powershell
dotnet build LanFileDrop.sln -c Release
```

Executable sau khi build nằm tại:

```text
LanFileDrop.Desktop\bin\Release\net8.0-windows\LanFileDrop.Desktop.exe
```

## Truyền giữa hai máy Windows

1. Chạy cùng phiên bản LanFileDrop trên cả hai máy.
2. Cho phép ứng dụng qua Windows Firewall khi được hỏi.
3. Chọn máy đích trong danh sách **Thiết bị**.
4. Thả file/thư mục hoặc dùng nút **Chọn file**.
5. Xác nhận yêu cầu trên máy nhận.

Nếu UDP broadcast bị chặn, vào **Cài đặt → Kết nối IP thủ công** và nhập:

```text
192.168.1.15
```

Hoặc chỉ định port:

```text
26.10.20.30:49495
```

Địa chỉ dạng `26.x.x.x` thường là IP của Radmin VPN. Hai phía vẫn phải chạy LanFileDrop và cho phép TCP `49495` qua firewall của adapter VPN.

> Giao thức resume TCP hiện tại là phiên bản 2; nên cập nhật cả hai máy lên cùng bản mới nhất.

## Dùng với iPhone hoặc Android

1. Mở LanFileDrop trên Windows.
2. Chọn **Kết nối điện thoại**.
3. Đảm bảo điện thoại cùng Wi-Fi với máy tính và quét QR bằng Camera.
4. Đặt tên thiết bị, ví dụ `iPhone của An`. Tên được lưu trong trình duyệt.
5. Chọn file hoặc nhập văn bản trên trang web.
6. Nhấn **Chấp nhận** trên Windows để bắt đầu upload.

Trang web mobile hỗ trợ:

- Hiển thị thiết bị Safari/Chrome trong danh sách Windows.
- Upload nhiều file và tiếp tục từ block 1 MB còn thiếu nếu mất kết nối.
- Retry upload tối đa 5 lần.
- Gửi, xem và sao chép văn bản trực tiếp ở cả mobile và desktop.
- Tải xuống file đã được chia sẻ từ Windows với HTTP Range.
- Không giới hạn request body cố định từ Kestrel; giới hạn thực tế phụ thuộc dung lượng ổ đĩa, trình duyệt và hệ thống file.

Phiên upload web được giữ tối đa khoảng 30 phút khi đang chờ nối lại. Tab mobile gửi heartbeat mỗi 5 giây và biến mất khỏi danh sách thiết bị khoảng 16 giây sau khi đóng tab.

## Độ an toàn dữ liệu

- File được stream theo buffer 1 MB, không đọc toàn bộ file vào RAM.
- Dữ liệu đang nhận dùng hậu tố `.partial`.
- Chỉ sau khi nhận đủ và SHA-256 khớp, file mới được đổi sang tên cuối cùng.
- File tạm bị xóa khi checksum sai, hết dung lượng, phiên upload hết hạn hoặc ứng dụng đóng.
- File trùng tên được lưu với hậu tố `(1)`, `(2)`, ...
- Thư mục được nén ZIP vào file tạm trên đĩa trước khi truyền.

## Cổng mạng

| Giao thức | Cổng | Mục đích |
|---|---:|---|
| UDP | `49494` | Tự phát hiện máy Windows trong LAN |
| TCP | `49495` | Truyền file trực tiếp giữa hai ứng dụng |
| TCP/HTTP | `49500` | Giao diện web LAN cho điện thoại |

Web LAN sử dụng HTTP nội bộ, không mã hóa TLS. Chỉ nên sử dụng trên LAN hoặc VPN đáng tin cậy; không mở port `49500` trực tiếp ra Internet.

## Dữ liệu cục bộ

| Dữ liệu | Vị trí |
|---|---|
| File nhận | `%USERPROFILE%\Downloads\LanFileDrop` |
| Lịch sử SQLite | `%LOCALAPPDATA%\LanFileDrop\history.db` |
| Cấu hình giao diện | `%LOCALAPPDATA%\LanFileDrop\settings.json` |

## Cấu trúc solution

```text
LanFileDrop.Core       Models và SQLite history repository
LanFileDrop.Network    UDP discovery, TCP protocol, web LAN, streaming và checksum
LanFileDrop.Desktop    WPF UI/MVVM, theme, toast, QR và system tray
LanFileDrop.Tests      Unit/integration tests cho SQLite, TCP resume và web resume
```

## Kiểm thử

```powershell
dotnet test LanFileDrop.Tests\LanFileDrop.Tests.csproj -c Release
```

Bộ test hiện kiểm tra:

- Tính phần trăm tiến trình.
- Lưu và lọc lịch sử SQLite.
- Truyền TCP nhiều chunk và xác minh SHA-256.
- TCP reconnect/resume từ offset đã nhận.
- Đăng ký nhiều mobile client, upload web, resume web và gửi văn bản hai chiều.
