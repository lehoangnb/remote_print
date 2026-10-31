# Remote Print

Chia sẻ máy in Windows qua Internet bằng danh tính thiết bị do app quản lý.
Không tạo user Windows, không yêu cầu tài khoản Windows của máy nhận.

## Luồng in

1. Service máy sở hữu công bố những máy in người dùng chọn vào Room.
2. Máy gửi chọn máy và máy in, bấm **Kết nối máy in**. Service tải gói driver
   từ Windows Driver Store của máy sở hữu qua mesh đã xác thực.
3. Windows kiểm tra chữ ký và cài driver. App tạo hàng đợi Windows cục bộ dùng
   driver đó, nối tới cổng LPR chuẩn chỉ lắng nghe trên `127.0.0.1:515`.
4. Khi in, driver trên máy gửi tạo dữ liệu máy in gốc (RAW). Service lưu dữ liệu
   vào spool được bảo vệ rồi chuyển tới service máy nhận qua WireGuard.
5. Máy nhận kiểm tra quyền Room, máy in đang được chọn, độ dài và SHA-256 rồi
   đưa RAW vào Windows spooler bằng `StartDocPrinter`/`WritePrinter`.

App không chuyển tài liệu sang XPS/PDF và không dùng SMB/RPC để cài hoặc gửi in.
Không tắt password-protected sharing, không bật Guest, không lưu mật khẩu Windows.
Room và khóa WireGuard là căn cứ xác thực. Server điều phối quyền/khóa; relay chỉ
chuyển gói WireGuard mã hóa, không có khóa riêng hoặc bản tài liệu được giải mã.

## Kết nối

Client ưu tiên UDP trực tiếp; khi không xuyên NAT được thì dùng relay WSS tại
`/api/client/mesh`. Chỉ tạo route `/32` tới peer cùng Room, không đổi default route
hoặc DNS. TCP 27444 trên địa chỉ mesh phục vụ giao thức driver/RAW; firewall giới
hạn theo adapter, địa chỉ peer và chương trình service. SMB/RPC không được mở bởi
quy tắc mesh mới. Không mở TCP 27444, 135 hoặc 445 ra Internet.

Lease điều khiển hết hạn tối đa 15 giây; quyền được kiểm tra lại trong khi truyền
và trước khi đưa vào spooler. Máy bị đổi Room/từ chối hoặc máy in bị bỏ chọn không
được nhận lệnh mới. Lệnh đã được Windows spooler chấp nhận có thể tiếp tục in.

## Cài đặt và nâng cấp

Chỉ duyệt các máy tin cậy vào Room vì driver có thể được cài bằng service SYSTEM.

Cập nhật **server và toàn bộ client** để nhận biết tính năng `nativePrinting`.
Client cũ chưa có relay RAW sẽ không hiện máy in để kết nối từ client mới.
Server cần `TUNNEL_MODE=mesh`; hub SMB/RPC cũ không được client mới sử dụng.

Máy Windows cần .NET 8 Desktop Runtime. Giải nén toàn bộ gói x64 hoặc ARM64 tương
ứng, mở client và chọn **Cài đặt > Cài / khởi động dịch vụ**. Chấp nhận UAC một lần
để cài service. Sau đó nhập HTTPS/tên máy, chọn máy in muốn chia sẻ; admin duyệt
client vào Room. Chọn/bỏ chọn máy in tự lưu qua service mà không restart service.

Khi bấm **Kết nối máy in**, service SYSTEM thực hiện cài driver và tạo queue cho
máy tính; không hiện hộp nhập tài khoản máy kia. Windows vẫn áp dụng chính sách
chữ ký và cài driver. Driver phải cùng kiến trúc máy gửi; app không tải driver
khác kiến trúc từ Internet. Nếu nhà sản xuất có thành phần ngoài gói INF hoặc
phụ thuộc giao tiếp hai chiều, có thể cần bộ cài OEM và kiểm thử tương thích.

Queue do app tạo có tên `RemotePrint ... [id]`, port `RP_...`; không được chọn
chia sẻ lại để tránh vòng lặp. Windows share do app cũ tạo được gỡ; share sẵn có
của người dùng được giữ nguyên. Kết nối UNC đã cài trước đây không tự được chuyển
đổi: kết nối máy in lại từ app để tạo queue driver/RAW mới. Nếu máy đã chạy một LPD server trên loopback 515,
cần giải quyết xung đột cổng trước khi khởi động service mới.

## Dữ liệu và trạng thái

Dữ liệu service nằm trong `%ProgramData%\RemotePrint\Service`, chỉ SYSTEM và
Administrators đọc được. Khóa WireGuard được bảo vệ DPAPI. `NativeRelay` chứa
mapping queue, RAW đang chờ chuyển và receipt; không được xóa khi còn lệnh chờ.
Mỗi lệnh tối đa 512 MiB; spool cục bộ kiểm tra ngưỡng 2 GiB trước khi nhận
lệnh mới, ngoài tối đa hai luồng capture đang nhận. Các lệnh đang chờ được thử lại khi máy nhận online/cùng Room.

Service chỉ ACK cho LPR sau khi flush dữ liệu và ghi nhận bền vững. Nếu không
lưu được thì trả ACK lỗi để Windows giữ lệnh trong queue. Job ID và thời điểm
submit của Windows được dùng chống nhận trùng khi mất ACK cục bộ.

Windows queue trên máy gửi hoàn tất bước gửi khi chuyển cho service cục bộ;
điều đó chưa xác nhận giấy đã ra máy. Nhật ký **Hoạt động gần đây** ghi nhận lúc
RAW được lưu và lúc Windows spooler máy nhận chấp nhận, kèm job ID Windows.
Không xác nhận được kết quả spool sau lỗi/crash thì lệnh được giữ để kiểm tra,
không tự in lại. Receipt ngăn in trùng khi mất ACK rồi gửi lại cùng job ID.

## Server Docker

Image: `registry.xn--lh-eja.vn/remote-print-server:latest`.
Có thể cấu hình trực tiếp trong Compose, không cần file `.env`:

```yaml
services:
  remote-print-server:
    image: registry.xn--lh-eja.vn/remote-print-server:latest
    restart: unless-stopped
    environment:
      HOST: "0.0.0.0"
      PORT: "7443"
      DATA_DIR: "/app/data"
      ADMIN_USERNAME: "admin"
      ADMIN_PASSWORD: "replace-with-a-private-password-at-least-12-characters"
      JWT_SECRET: "replace-with-a-private-random-secret-at-least-32-characters"
      TUNNEL_MODE: "mesh"
      MESH_UDP_PORT: "51821"
      # TRUST_PROXY: "1" # Chỉ khi đứng sau đúng một proxy tin cậy.
    ports:
      - "127.0.0.1:7443:7443"
      - "51821:51821/udp"
    volumes:
      - remote-print-data:/app/data
volumes:
  remote-print-data:
```

Dùng reverse proxy HTTPS chuyển tới 7443 và hỗ trợ WebSocket, giữ header
`x-device-id`, `x-device-secret`; không ghi header bí mật vào access log.
Nếu proxy ở máy khác thì điều chỉnh binding TCP 7443 cho mạng nội bộ phù hợp.
Mở HTTPS và UDP 51821 tới server. Với Cloudflare proxy, UDP discovery phải dùng
hostname DNS-only trỏ tới origin qua `MESH_PUBLIC_HOST`; WSS vẫn dùng domain API.
Nếu UDP không đi được, WSS relay vẫn hoạt động.

```sh
docker compose pull
docker compose up -d
docker compose logs -f remote-print-server
```

## Build và kiểm thử

```powershell
.\scripts\publish-client.ps1
.\scripts\publish.ps1 -SkipClient
cd server
npm test
cd ..
dotnet build client/RemotePrint.Checks/RemotePrint.Checks.csproj -c Release -o artifacts/checks
dotnet artifacts/checks/RemotePrint.Checks.dll --native-relay-check "$PWD"
dotnet artifacts/checks/RemotePrint.Checks.dll --autosave-check
dotnet artifacts/checks/RemotePrint.Checks.dll --mesh-check "$PWD"
```

Kiểm thử native relay dùng TCP thật và spool sink mô phỏng: kiểm tra byte RAW,
hash, ACK/retry, kết quả spool không xác định, từ chối quyền, thu hồi trong khi
truyền và ZIP traversal. Có xuất và kiểm tra gói driver từ máy in thật nhưng
không gửi lệnh ra máy in vật lý trong bài kiểm thử này.

**Chưa xác nhận cài driver và in đầu cuối giữa hai máy Windows thật** với cơ chế
RAW mới. Cần kiểm tra chữ ký/cài gói OEM, port monitor Windows, firewall và giấy
in thực tế trên từng dòng máy in. Build và kiểm thử relay không thay thế bước đó.
