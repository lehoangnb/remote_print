# Remote Print

Ứng dụng chia sẻ máy in giữa các máy Windows qua một server trung tâm. Mỗi client tự tạo tên ngẫu nhiên, ID, bí mật thiết bị và cặp khóa RSA. Quản trị viên duyệt client, gán Room; chỉ client đã duyệt trong cùng Room mới thấy nhau và gửi lệnh in cho nhau.

## Thành phần

- `client/RemotePrint.Client`: giao diện WPF và desktop agent ở khay hệ thống.
- `client/RemotePrint.Service`: Windows Service nhận, gửi, giải mã và in tài liệu; không phụ thuộc cửa sổ quản lý.
- `client/RemotePrint.Core`: mã hóa, API và giao thức named pipe có phân quyền theo tài khoản Windows.
- `server`: Node.js 20+, API, trang quản trị và hàng đợi lệnh in.
- Server lưu trạng thái trong `server/data/db.json` và payload mã hóa trong `server/data/jobs`.

Ứng dụng cài một máy in ảo tên **Remote Print** bằng driver `Microsoft Print To PDF` có sẵn của Windows. Khi người dùng in từ Word, trình duyệt hoặc phần mềm bất kỳ qua máy in này, client bắt bản PDF, mở hộp chọn máy tính/máy in trong cùng Room rồi gửi lệnh từ xa. Tài liệu được mã hóa trên máy gửi bằng AES-256-GCM. Khóa AES được bọc bằng RSA-OAEP-SHA256 của máy nhận. Server chỉ đọc được metadata như tên tệp, máy gửi, máy nhận và máy in; server không giải mã được nội dung PDF.

## Chạy server

Yêu cầu Node.js 20 trở lên.

```powershell
cd server
Copy-Item .env.example .env
notepad .env
npm install
npm start
```

Trong `.env`, bắt buộc đổi `ADMIN_PASSWORD` và `JWT_SECRET`. Có thể tạo secret bằng PowerShell:

```powershell
[Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(48))
```

Mở `http://IP-SERVER:7443` để vào trang quản trị. Khi client đăng ký, nhập tên Room rồi bấm **Duyệt**.

Để sử dụng qua Internet, đặt server sau HTTPS reverse proxy hoặc cấu hình `TLS_CERT_PATH` và `TLS_KEY_PATH` bằng chứng thư PEM hợp lệ. Không mở bản HTTP trực tiếp ra Internet. Cổng mặc định là TCP 7443.

## Chạy client Windows

Khi chạy từ mã nguồn, máy cần .NET 8 SDK. Bản phát hành là self-contained nên không cần cài .NET. Windows Service dùng PDFium được đóng gói sẵn để render PDF và gửi trang in tới Windows GDI/spooler, không cần SumatraPDF hoặc WebView2.

Chạy từ mã nguồn:

```powershell
dotnet run --project client/RemotePrint.Client/RemotePrint.Client.csproj
```

Tạo bản phát hành độc lập cho Windows x64:

```powershell
.\scripts\publish-client.ps1
```

Bản phát hành nằm trong `dist/client-win-x64`. Trên mỗi máy:

1. Giải nén toàn bộ gói, giữ nguyên thư mục `Service` và `Scripts`, mở `RemotePrint.Client.exe`.
2. Bấm **Cài dịch vụ** và chấp nhận UAC. Bộ cài tạo service `RemotePrint`, tự chạy cùng Windows và tự khởi động lại khi gặp lỗi.
3. Điền tên máy, địa chỉ server, chọn máy in muốn chia sẻ, bấm **Lưu cấu hình và kết nối**. Có thể không chọn máy in nếu máy này chỉ gửi lệnh in.
4. Quản trị viên duyệt máy và gán Room trên server.
5. Bấm **Cài máy in ảo** và chấp nhận hộp UAC của Windows.
6. Có thể đóng cửa sổ quản lý. Desktop agent thu về khay hệ thống và tự chạy khi đăng nhập; service tiếp tục nhận/in tài liệu cả khi không có người đăng nhập.
7. Trong ứng dụng bất kỳ, bấm Print, chọn **Remote Print**. Agent mở hộp chọn máy nhận/máy in trong cùng Room; chọn đích rồi bấm **Gửi lệnh in**.
8. Bấm **Để sau** trong hộp chọn để giữ tài liệu. Bấm **Làm mới** ở cửa sổ quản lý để chọn lại. Nếu đã thoát hoàn toàn agent, service vẫn giữ tài liệu; mở lại agent để chọn nơi nhận.

Service chạy dưới LocalSystem, dữ liệu nằm ở `%PROGRAMDATA%\RemotePrint\Service`. Khóa riêng được DPAPI bảo vệ theo máy; thư mục chỉ SYSTEM và Administrators được đọc. Giao diện dùng named pipe chỉ cho tài khoản cài đặt, không được đọc khóa riêng/bí mật thiết bị. Bộ cài chuyển cấu hình/khóa cũ từ `%APPDATA%\RemotePrint\Client` để giữ nguyên ID và Room đã duyệt. Cài lại không ghi đè cấu hình service đã có.

Bộ cài chép ứng dụng vào thư mục riêng theo phiên bản dưới `%ProgramFiles%\RemotePrint\versions`, vì vậy có thể đóng hoặc xóa bản giải nén sau khi cài. Task `RemotePrint Desktop Agent` chạy giao diện nền lúc tài khoản cài đặt đăng nhập. Windows không cho service trực tiếp mở hộp thoại ở Session 0, nên desktop agent đảm nhiệm bước chọn máy nhận.

Service chỉ chia sẻ máy in được cài cho toàn bộ máy tính. Máy in kết nối riêng cho một tài khoản hoặc UNC yêu cầu thông tin đăng nhập riêng cần cài lại phù hợp cho service. Máy in yêu cầu hộp thoại Save As/Fax, như Microsoft Print to PDF ở PORTPROMPT, được loại khỏi danh sách chia sẻ.

Kiểm tra dịch vụ:

```powershell
Get-Service RemotePrint
Get-ScheduledTask -TaskName 'RemotePrint Desktop Agent'
```

Gỡ dịch vụ bằng PowerShell Administrator:

```powershell
& '.\dist\client-win-x64\Scripts\Uninstall-Service.ps1'
```

Script gỡ giữ nguyên cấu hình, tài liệu đang chờ và máy in ảo. Nhật ký: `%PROGRAMDATA%\RemotePrint\Service\service.log`; lỗi cài đặt: `%PROGRAMDATA%\RemotePrint\install.log`.

## Kiểm thử

```powershell
cd server
npm test

cd ..\client\RemotePrint.Client
dotnet build -c Release
```

Kiểm tra kiến trúc service (sau khi publish; cần Microsoft Print to PDF; chỉ ghi PDF kiểm thử, không in giấy):

```powershell
dotnet run --project client/RemotePrint.Checks/RemotePrint.Checks.csproj -c Release -- "$PWD"
```

Bài kiểm tra xác nhận IPC, in PDF qua GDI, đóng cửa sổ vẫn polling, giữ tài liệu qua lần khởi động lại, mã hóa/gửi tài liệu, chặn đường dẫn giả và render giao diện vào `artifacts/service-checks`. Bài kiểm tra chạy tiến trình service dưới dạng console; kiểm tra SCM/khởi động Windows cần cài service thật.

Kiểm thử server bao phủ đăng ký, duyệt client, phân tách Room, công bố máy in, tạo/tải lệnh in và hoàn tất lệnh. Việc in vật lý cần kiểm tra trên máy Windows có máy in hoặc hàng đợi PDF thực tế.

## Giới hạn hiện tại

- Chỉ nhận tệp PDF, tối đa 25 MB theo cấu hình mặc định.
- Service bắt và giữ dữ liệu từ máy in ảo độc lập. Desktop agent cần chạy trong phiên đăng nhập để hiển thị hộp chọn đích.
- Máy in ảo dùng một cổng PDF tuần tự; không nên gửi nhiều lệnh đồng thời trên cùng một client.
- Dùng thiết lập giấy/driver mặc định của máy in, co trang PDF vào vùng in và render tối đa 300 DPI.
- Một lệnh in có thể bị in lặp nếu client mất điện đúng lúc đã gửi sang Windows spooler nhưng chưa kịp báo hoàn tất cho server.
- Dữ liệu trạng thái dùng tệp JSON, phù hợp nhóm nhỏ. Khi cần hàng trăm client hoặc nhiều server chạy song song, nên thay bằng PostgreSQL/Redis.
- Việc thay Room hoặc từ chối client chỉ có hiệu lực từ lần polling tiếp theo, mặc định trong khoảng 4 giây.

