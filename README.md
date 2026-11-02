# Remote Print

Chia sẻ máy in Windows qua Internet bằng danh tính thiết bị do app quản lý.
Không tạo user Windows, không yêu cầu tài khoản Windows của máy nhận.

## Ngôn ngữ

Chọn Tiếng Việt hoặc English trên trang quản trị hoặc trong menu Language / Ngôn ngữ của client. Lựa chọn được lưu riêng cho từng trình duyệt và tài khoản Windows; giao diện cập nhật ngay khi đổi ngôn ngữ. Các bản dịch nằm chung trong `server/public/translations.json`; file này được đóng gói cùng client và server. Tên máy, máy in và nội dung chi tiết do hệ điều hành hoặc dịch vụ bên ngoài trả về giữ nguyên nội dung gốc.

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

App không chuyển đổi tài liệu của máy in vật lý sang XPS/PDF và không dùng SMB/RPC để cài hoặc gửi in.
Riêng máy in ảo test dùng driver Windows tạo PDF để kiểm tra nội dung và bố cục.
Không tắt password-protected sharing, không bật Guest, không lưu mật khẩu Windows.
Room và khóa WireGuard là căn cứ xác thực. Server điều phối quyền/khóa; relay chỉ
chuyển gói WireGuard mã hóa, không có khóa riêng hoặc bản tài liệu được giải mã.

## Máy in ảo để thử giữa các client

1. Cập nhật cả giao diện và service trên các client bằng gói mới.
2. Trên máy nhận, chọn **Cài đặt > Cài máy in ảo để thử…**. Service tạo máy in
   Windows **Remote Print Test**, dùng driver **Microsoft Print To PDF** có sẵn
   trong Windows và cổng LPR chỉ nối tới `127.0.0.1:515`.
3. Tích chọn **Remote Print Test** trong **Máy in chia sẻ**. Hai client cần
   online, được duyệt vào cùng Room và tunnel đã kết nối.
4. Trên máy gửi, chọn máy nhận và **Remote Print Test**, bấm **Kết nối máy in**,
   rồi **In thử**. Cũng có thể in tài liệu Word từ ứng dụng vào queue `RemotePrint ...`
   vừa tạo. Để thử chiều ngược lại, cài và chia sẻ máy in ảo trên máy còn lại.
5. Kiểm tra **Hoạt động gần đây** trên máy nhận: dòng **Máy in ảo đã nhận:**
   xác nhận đã lưu dữ liệu. Thông báo gửi thành công trên máy gửi mới xác nhận
   vào queue Windows; cần chờ dòng nhận này để xác nhận đi hết luồng.
6. Trên máy nhận, bấm **Mở file in thử…** dưới **Hoạt động gần đây**. Chọn
   file rồi bấm **Mở file** hoặc nhấp đúp để xem bằng trình đọc PDF mặc định. **Làm mới** để
   tải lại danh sách tối đa 200 file mới nhất; không cần máy gửi đang online.

Mở file qua giao diện không cần quyền quản trị: service đọc file đã nhận qua
IPC và client kiểm tra SHA-256 trước khi mở bản sao `.pdf` tại
`%LocalAppData%\RemotePrint\TestPrints`. File gốc và receipt trong service được
giữ nguyên. File văn bản từ phiên bản cũ vẫn mở bằng Notepad; file chỉ chứa ký tự
chuyển trang được ghi rõ **Trống**. Nút **Mở file in thử** chỉ hiện khi máy này đã
cài máy in ảo. Cần cập nhật cả client và service để có các tùy chọn mới.

Trong **Cài đặt**, **Gỡ máy in test…** gỡ queue/cổng test và bỏ lựa chọn chia sẻ
máy in đó; không xóa file đã nhận hoặc driver PDF dùng chung của Windows. Nếu còn
job đang chờ thì cần hoàn tất/hủy job trước khi gỡ. **Dọn file in thử…** xóa các
file test/receipt đã nhận và bản sao đã mở của người dùng sau khi xác nhận. Nút
**Dọn tất cả** trong danh sách file có cùng tác dụng. Dọn dẹp vẫn dùng được sau
khi gỡ máy in; không xóa file capture đang được nhận hoặc dữ liệu relay in thật.

Service tự nâng cấp queue test cũ từ Generic / Text Only khi khởi động nếu queue
đúng cấu hình và không còn job chờ. Cập nhật cả hai client, bấm **Kết nối máy in**
lại trên máy gửi để nâng cấp queue gửi, rồi đóng/mở lại Word để nạp driver mới.
Nếu queue còn job chờ, xử lý job rồi chọn cài máy in ảo lại từ menu.

Mỗi lệnh được lưu riêng dưới
`%ProgramData%\RemotePrint\Service\NativeRelay\TestPrints` thành `.raw` và receipt
`.json` có thời gian UTC, số byte, SHA-256. Thư mục kế thừa quyền service, cần
quyền quản trị để truy cập/xóa trực tiếp bằng Explorer. Đuôi `.raw` là tên lưu nội
bộ: với driver mới, nội dung là PDF. Máy gửi dựng PDF bằng driver Windows qua GDI,
máy nhận lưu PDF sau khi xác thực Room và SHA-256; không đưa PDF qua driver lần
nữa. Nút **In thử** cũng đi qua bước dựng trang, có tiếng Việt và hình chữ nhật.
Kết quả này kiểm tra tài liệu test, không thay thế kiểm thử máy in vật lý.

Không hiện Save As, không cần Windows share/SMB, không tự bật chia sẻ khi cài.
Tổng file thử giới hạn 2 GiB; khi đầy, xóa các cặp `.raw`/`.json` cũ bằng quyền
quản trị hoặc dùng **Dọn file in thử…** rồi cho Windows thử lại. File không tự bị xóa. Gửi lại cùng job Windows
không tạo bản trùng. Gỡ máy in ảo trong Windows Settings nếu không còn dùng.
Nếu Windows thiếu driver PDF, bật **Microsoft Print to PDF** trong Windows
Features rồi thử lại; client không tải driver ngoài từ Internet.

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

Máy mới phải ghép nối bằng mã mời: trong trang admin chọn **Tạo mã 5 phút**,
kiểm tra địa chỉ HTTPS công khai rồi sao chép mã `RPPAIR1.…`. Người dùng dán mã
vào ô **Địa chỉ server** của client và lưu. Mã chứa URL server, endpoint ngẫu nhiên
và khóa ngẫu nhiên 256 bit, chỉ gắn với một danh tính máy, hết hạn sau 5 phút.
Mã chưa dùng mất hiệu lực khi server khởi động lại. Không lưu mã vào nhật ký.

Ghép nối xong vẫn cần admin duyệt Room. Client đã đăng ký tiếp tục dùng khóa
thiết bị hiện có; không cần mã mới sau mỗi 5 phút. Mã mời không xuất hiện trong
snapshot gửi cho giao diện và được xóa khỏi cấu hình service sau đăng ký thành
công. Đổi địa chỉ server sẽ tạo danh tính thiết bị mới để không gửi khóa cũ sang
server khác. Server mới không nhận đăng ký tự do từ client cũ chưa ghép nối.

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
`x-device-id`, `x-device-secret`, `x-enrollment-key`; không ghi header bí mật vào access log.
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
dotnet artifacts/checks/RemotePrint.Checks.dll --test-printer-check "$PWD"
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
