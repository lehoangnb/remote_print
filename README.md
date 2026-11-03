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
2. Trên máy nhận, chọn **Cài đặt > Máy in > Máy in thử > Cài máy in ảo để thử…**. Service tạo máy in
   Windows **Remote Print Test**, dùng driver **Microsoft Print To PDF** có sẵn
   trong Windows và cổng LPR chỉ nối tới `127.0.0.1:515`.
3. Tích chọn **Remote Print Test** trong **Máy in chia sẻ**. Hai client cần
   online, được duyệt vào cùng Room và tunnel đã kết nối.
4. Trên máy gửi, chọn máy nhận và **Remote Print Test**, bấm **Kết nối máy in**,
   rồi **In thử**. Cũng có thể in tài liệu Word từ ứng dụng vào queue `TênClient_Remote Print Test`
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

Trong **Cài đặt > Máy in > Máy in thử**, **Gỡ máy in test…** gỡ queue/cổng test và bỏ lựa chọn chia sẻ
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

## Gán nhiều Room

Trong bảng client trên web, mở dropdown Room, tích các Room muốn cấp quyền rồi bấm **Duyệt** hoặc **Lưu**. Một client có thể thuộc nhiều Room. Hai máy chỉ thấy nhau và được phép kết nối khi có ít nhất một Room chung; quyền không lan truyền qua máy trung gian. Bỏ Room và lưu để thu hồi quyền; phải chọn ít nhất một Room khi duyệt. **Từ chối** thu hồi toàn bộ Room. Dữ liệu một Room cũ được chuyển tự động sang danh sách Room khi server khởi động. Cập nhật client để hiển thị đầy đủ các Room; client cũ vẫn nhận danh sách peer do server kiểm soát.

## Kết nối

Client ưu tiên UDP trực tiếp; khi không xuyên NAT được thì dùng relay WSS tại
`/api/client/mesh`. Chỉ tạo route `/32` tới peer cùng Room, không đổi default route
hoặc DNS. TCP 27444 trên địa chỉ mesh phục vụ giao thức driver/RAW; firewall giới
hạn theo adapter, địa chỉ peer và chương trình service. SMB/RPC không được mở bởi
quy tắc mesh mới. Không mở TCP 27444, 135 hoặc 445 ra Internet.

Lease điều khiển hết hạn tối đa 15 giây; quyền được kiểm tra lại trong khi truyền
và trước khi đưa vào spooler. Máy bị đổi Room/từ chối hoặc máy in bị bỏ chọn không
được nhận lệnh mới. Lệnh đã được Windows spooler chấp nhận có thể tiếp tục in.

Client tự thử lại DNS discovery khi lỗi và làm mới địa chỉ định kỳ; đổi
`MESH_PUBLIC_HOST`/cổng UDP trên server được áp dụng khi client poll cấu hình.
Với client ở cùng LAN với server, NAT loopback có thể làm discovery nhìn thấy
IP riêng của router. Server bổ sung candidate từ IP public của
`MESH_PUBLIC_HOST`. Client mới ưu tiên địa chỉ WAN lấy bằng STUN trên chính
socket mesh, vì WAN của client có thể khác WAN server (nhiều đường Internet/VPN).
Mặc định dùng `stun.cloudflare.com:3478`; đổi bằng `MESH_STUN_HOST` và
`MESH_STUN_PORT` trên server, hoặc đặt `MESH_STUN_HOST` rỗng để tắt STUN.
STUN chỉ trao đổi IP/cổng và transaction ID, không gửi khóa hay dữ liệu in.
Client chỉ dùng candidate khi nhận ACK xác thực, đồng thời thử lại
cổng nguồn thực của peer nếu NAT đổi cổng theo đích. Firewall Windows giới hạn
UDP trực tiếp theo chương trình service, cổng đang dùng và IP các peer được
cấp quyền; quy tắc bị gỡ khi ngắt mesh/thu hồi quyền. NAT chặn cả hai chiều vẫn
cần relay.

API admin `GET /api/admin/mesh` cho biết endpoint quan sát được, tuổi discovery
và candidate từng client; không trả cookie/token. Snapshot IPC `status` có
`meshDiagnostics` với DNS, bộ đếm gửi discovery/probe và ACK, giúp phân biệt
UDP server hoạt động với kết nối trực tiếp đã được xác thực. Các bộ đếm byte
WireGuard direct/relay cho phép kiểm tra dữ liệu thực đi trên đường nào.
Đọc chẩn đoán trên tài khoản Windows đã ghép nối bằng
`./scripts/Get-MeshDiagnostics.ps1` (chỉ đọc, không restart service).

## Cài đặt và nâng cấp

Máy mới có thể dùng **RemotePrint.Setup.exe** (bộ cài nhỏ, không kèm client/runtime).
Tải bằng nút **Tải bộ cài client** trên web hoặc lấy `dist/RemotePrint.Setup.exe`,
chạy trên Windows x64/ARM64 có .NET Framework 4.8. Bản tải từ web tự điền URL của
server đã tải, kể cả khi chép/đổi tên file EXE; bản `dist` chung có thể nhập URL
thủ công. Bấm **Tải và cài đặt**. Setup tự chọn kiến trúc Windows, lấy bản mới nhất đã
phát hành trên server, kiểm tra SHA-256, manifest và phiên bản các assembly,
tải/cài .NET 8 Desktop Runtime chính thức từ Microsoft nếu thiếu (kiểm tra SHA-512),
cài service qua UAC rồi mở client. Máy mới vẫn cần mã mời và duyệt Room như dưới đây.
Sau khi cài, Windows Start Menu có thư mục **Remote Print** với shortcut mở client
và **Gỡ cài đặt Remote Print**. Ứng dụng cũng xuất hiện trong **Settings > Apps >
Installed apps** để gỡ bằng cơ chế chuẩn của Windows. Khi cập nhật, cả hai mục
được trỏ sang phiên bản mới nhất.
Có thể gỡ toàn bộ client tại **Cài đặt > Ứng dụng > Gỡ Remote Print khỏi máy…** trong ứng dụng,
Start Menu hoặc Windows Installed apps. Quy trình hỏi xác nhận/UAC, dừng và gỡ
Windows Service, bỏ mục tự khởi động, shortcut, đăng ký Installed apps và các
thư mục phiên bản cài trong `Program Files\RemotePrint`. File đang được Windows
sử dụng sẽ được xóa sau lần khởi động lại. Cấu hình ghép nối và file in đã nhận
trong `ProgramData\RemotePrint` được giữ lại để hỗ trợ cài lại.
Chạy lại setup dùng bản server đang phát hành kể cả cùng phiên bản, giữ cấu hình
service đã có. File setup không chứa mã mời hay khóa thiết bị.
Khi cài lại, setup đóng giao diện client đã cài trong phiên Windows hiện tại
để mở đúng bản vừa cài; service chạy độc lập với giao diện.

Build riêng: `./scripts/publish-setup.ps1`; `publish-client.ps1` và `publish.ps1`
cũng tạo setup và chép vào `server/public/downloads/` để đóng gói vào image server.
Docker build yêu cầu file setup đã được tạo; khi build bằng Compose trực tiếp,
chạy `./scripts/publish-setup.ps1` trước `docker compose build`.
Nút **Tải bộ cài client** có ngay trên trang đăng nhập, không cần tài khoản admin.
Link chia sẻ trực tiếp là `https://print.example.com/downloads/RemotePrint.Setup.exe`.
Server gắn URL vào bản EXE trả về, không cần build riêng cho mỗi tên miền.
Nếu dùng reverse proxy, đặt `PUBLIC_SERVER_URL=https://print.example.com` trong
`server/.env` để luôn điền URL công khai chính xác. Khi không đặt biến này,
server lấy scheme/host/cổng của request; `X-Forwarded-Proto` và `X-Forwarded-Host`
chỉ được dùng khi đã cấu hình `TRUST_PROXY` đúng với proxy tin cậy. URL phải là
HTTPS origin (HTTP chỉ dùng loopback), không gồm đường dẫn, query hay tài khoản.
Sau khi triển khai server, phát hành ZIP x64/ARM64 trong **Cập nhật client** trước
khi đưa setup cho người dùng. Chưa có ZIP đúng kiến trúc thì setup báo rõ và dừng.
Để điền sẵn địa chỉ khi triển khai: `RemotePrint.Setup.exe --server https://print.example.com`.
Kiểm tra tải/giải nén mà không cài: `RemotePrint.Setup.exe --download-only --server https://print.example.com --output C:\Temp\RemotePrint-staging`
(thư mục output phải chưa tồn tại; mã thoát 0 khi thành công, lỗi ghi `error.txt`).
Setup hỗ trợ VI/EN, chỉ dùng HTTP với loopback để kiểm thử cục bộ.
Kiểm thử bootstrapper trên Windows sau khi có gói client:
`node --test server/test/setup-bootstrapper.test.js`.

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
ứng, mở client và chọn **Cài đặt > Dịch vụ > Cài / khởi động dịch vụ**. Chấp nhận UAC một lần
để cài service. Sau đó nhập HTTPS/tên máy, chọn máy in muốn chia sẻ; admin duyệt
client vào Room. Chọn/bỏ chọn máy in tự lưu qua service mà không restart service.

Khi bấm **Kết nối máy in**, service SYSTEM thực hiện cài driver và tạo queue cho
máy tính; không hiện hộp nhập tài khoản máy kia. Windows vẫn áp dụng chính sách
chữ ký và cài driver. Driver phải cùng kiến trúc máy gửi; app không tải driver
khác kiến trúc từ Internet. Nếu nhà sản xuất có thành phần ngoài gói INF hoặc
phụ thuộc giao tiếp hai chiều, có thể cần bộ cài OEM và kiểm thử tương thích.

Queue do app tạo có tên `TênClient_Model máy in`, ví dụ `MayKeToan_Canon LBP2900`,
port `RP_...`; nếu tên trùng máy in khác thì thêm `_2`, `_3`. Bấm **Kết nối máy in**
lại để đổi queue tên cũ sang tên gọn; cần xử lý job Windows đang chờ trước khi đổi.
Queue relay không được chọn chia sẻ lại để tránh vòng lặp.

Chọn **Cài đặt > Máy in > Máy in đã cài…**, chọn máy in rồi bấm **Xóa máy in** để gỡ queue
đã kết nối qua app trên máy này, kể cả khi client bên kia offline. Service gỡ queue,
cổng relay không còn dùng và mapping; giữ driver dùng chung và file đã nhận.
Nếu còn job Windows, RAW đang chờ chuyển hoặc kết quả cần kiểm tra thì xử lý trước
khi xóa. Cập nhật cả giao diện và service để có mục này.

Windows share do app cũ tạo được gỡ; share sẵn có
của người dùng được giữ nguyên. Kết nối UNC đã cài trước đây không tự được chuyển
đổi: kết nối máy in lại từ app để tạo queue driver/RAW mới. Nếu máy đã chạy một LPD server trên loopback 515,
cần giải quyết xung đột cổng trước khi khởi động service mới.

## Tự động cập nhật client

Trang quản trị có nút **Cập nhật client** để tải và phát hành gói ZIP cho
Windows x64 hoặc ARM64. Chọn đúng kiến trúc, gói ZIP,
ghi chú rồi bấm **Tải lên và phát hành**. Server tự đọc phiên bản từ
`build-info.json` trong ZIP, không cần nhập phiên bản. Chỉ quản trị viên đã đăng nhập được
phát hành; có thể tải đè gói của cùng phiên bản, nhưng không thể hạ phiên bản.
Tích **Buộc cập nhật dù trùng phiên bản** trước khi phát hành để client/service
**1.1.9 trở lên** cài lại dù đang chạy cùng số phiên bản. Mỗi lượt phát hành có
mã riêng; client chỉ cài thành công một lần cho mã đó, kể cả khi server khởi
động lại. Phát hành lại cùng ZIP với tùy chọn này tạo lệnh cài lại mới.
Client cũ cần nâng lên 1.1.9 bằng luồng nâng phiên bản thông thường hoặc cài
thủ công trước khi nhận được lệnh cài lại cùng phiên bản.

Mọi request API của client đều gửi `x-client-version` và `x-client-runtime`.
Khi phiên bản đã phát hành cho đúng kiến trúc mới hơn client, hoặc trùng phiên
bản và bật tùy chọn buộc cập nhật, server gửi lệnh
`x-client-update` ngay trong phản hồi API thông thường (đăng ký, trạng thái,
heartbeat, danh sách máy hoặc tunnel). Service chuyển lệnh cho ứng dụng qua
IPC; client quyết định cập nhật theo quyền thực tế của tiến trình:

- **Có quyền admin:** tự tải, xác minh và cài đặt, kể cả đang chạy trong khay.
  Bộ cài kế thừa quyền admin hiện có, không yêu cầu UAC lần nữa.
- **User thường:** không tải hay hiện thông báo/UAC khi chạy trong khay.
  Khi mở GUI, client báo bản mới và cho chọn cài ngay; chỉ lúc đồng ý mới tải
  rồi yêu cầu UAC. Chọn không để tiếp tục dùng; lần mở GUI tiếp theo có thể báo lại.

Chọn **Cài đặt > Ứng dụng > Cập nhật client…** trong MenuStrip để chủ động kiểm tra bản mới
trên server. Nếu có bản mới, client hỏi xác nhận trước khi tải/cài và mở lại;
nếu chưa có, client thông báo rõ. Không có lịch kiểm tra bản mới riêng. Không cập
nhật khi client mới hơn bản trên server; client cùng phiên bản chỉ cập nhật
khi có lệnh buộc chưa được cài. Client kiểm tra kích thước,
SHA-256, kiến trúc, phiên bản và manifest của toàn bộ file trước khi cài.
Lượt tự cập nhật admin thất bại/hết thời gian/bị hủy chờ ít nhất 15 phút để
thử lại cùng lượt phát hành từ cùng server. Lựa chọn cài ngay trong GUI cho phép thử lại
sớm hơn. Cần giữ ứng dụng chạy trong khay để tự thực thi lệnh; nếu đã thoát
client, lệnh được xử lý khi mở lại và kết nối service.

Trong **Cài đặt > Luôn chạy với quyền quản trị**, bật tùy chọn để nâng quyền
phiên hiện tại và dùng Task Scheduler cho startup khi đăng nhập Windows.
Cần xác nhận UAC một lần khi bật từ phiên thường. Task dùng đúng tài khoản
Windows hiện tại, `InteractiveToken` và `HighestAvailable`; không lưu mật khẩu.
Tài khoản này phải thuộc nhóm Administrators; không dùng tài khoản admin khác
để cấp quyền chạy admin tự động cho một tài khoản thường. Cần cài service trước.
Khi tắt tùy chọn, startup trở về Run key thông thường; phiên đang chạy admin
vẫn giữ quyền đến khi thoát client. Lựa chọn và đường dẫn task được giữ/cập
nhật qua các lần nâng cấp, khôi phục về bản trước khi cài lỗi, và task được gỡ
khi gỡ service. Giao diện chỉ khởi động sau khi đăng nhập, service vẫn tự chạy
khi Windows boot dù chưa đăng nhập.

Ứng dụng tự mở lại từ thư mục đã cài. Service khởi động lại nên kết nối in
tạm ngắt; cấu hình, danh tính thiết bị và dữ liệu spool vẫn được giữ nguyên.
Nếu không khởi động được service mới, bộ cài thử khôi phục service/bản cũ và
báo lỗi; lỗi khôi phục được ghi trong nhật ký cài đặt.

Build gói phát hành với phiên bản tăng dần, ví dụ:

```powershell
.\scripts\publish-client.ps1 -Runtime win-x64 -Version 1.1.9
.\scripts\publish-client.ps1 -Runtime win-arm64 -Version 1.1.9
```

Tải file `dist/RemotePrint.Client-win-x64.zip` hoặc
`dist/RemotePrint.Client-win-arm64.zip` lên web; không chọn ZIP bọc script
`RemotePrint-Update-win-x64.zip`. Phiên bản mặc định nằm trong
`client/Directory.Build.props` (bản đầu hỗ trợ tự cập nhật: **1.1.0**).
Phiên bản có 3 hoặc 4 phần số, mỗi phần từ 0 tới 65534.

Gói/metadata được lưu bền vững tại `DATA_DIR/client-updates` trong volume dữ
liệu của server. Server chỉ giữ một gói đang phát hành cho mỗi kiến trúc (x64 và
ARM64), tự xóa ZIP cũ sau khi phát hành bản thay thế và dọn ZIP tồn từ phiên
bản server cũ ngay lúc khởi động. Lượt tải đang diễn ra được hoàn tất trước khi
xóa file; đường dẫn gói cũ không nhận lượt tải mới. Tải đè cùng phiên bản chỉ
thay thế gói trên server nếu không bật tùy chọn buộc cập nhật. Sau khi cài,
client/service giữ biên nhận `client-update-receipt.json` trong thư mục bản
đã cài và gửi `x-client-update-id` để server ngừng gửi lại cùng lệnh. Biên
nhận được sao chép cùng gói vào thư mục cài mới, nên cài lỗi và khôi phục về
bản cũ vẫn cho phép thử lại. API tải gói là công khai, chỉ chứa file phát
hành, không đóng gói `.env`, khóa hay cấu hình máy vào gói client.
Reverse proxy cần cho phép upload ZIP tới 256 MiB và thời gian tải phù hợp.
Client dùng HTTPS của server đã cấu hình (HTTP chỉ được phép với loopback để
thử cục bộ), không chuyển hướng sang máy chủ tải khác.

**Chuyển sang cơ chế mới:** cập nhật server và cài client/service **1.1.4**.
Client 1.1.0–1.1.2 vẫn có thể nhận 1.1.4 bằng cơ chế cập nhật cũ; sau khi
cài xong mới gửi phiên bản và tự thực thi lệnh từ server. Client trước 1.1.0
cần cài thủ công một lần. Client 1.1.3 nhận 1.1.4 qua lệnh cập nhật từ server.

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
`x-device-id`, `x-device-secret`, `x-enrollment-key`, `x-client-version`,
`x-client-runtime` và header phản hồi `x-client-update`; không ghi header bí mật vào access log.
Nếu proxy ở máy khác thì điều chỉnh binding TCP 7443 cho mạng nội bộ phù hợp.
Mở HTTPS và UDP 51821 tới server. Với Cloudflare proxy, UDP discovery phải dùng
hostname DNS-only trỏ tới origin qua `MESH_PUBLIC_HOST`; WSS vẫn dùng domain API.
Nếu UDP không đi được, WSS relay vẫn hoạt động.

```sh
docker compose pull
docker compose up -d
docker compose logs -f remote-print-server
```

## Sửa giao diện C# bằng Visual Studio

Mở `RemotePrint.sln` tại thư mục gốc bằng Visual Studio có workload **.NET desktop
development** và bộ công cụ .NET 8. Solution chứa toàn bộ sáu project C#:
Client, Service, Core, Transport, Checks và Transport.Checks. Chọn
`RemotePrint.Client` làm **Startup Project** nếu muốn chạy giao diện.

Trong `RemotePrint.Client`, nhấp phải `MainForm.cs` → **View Designer** (Shift+F7)
để kéo thả control và sửa thuộc tính trong **Properties**. Các cửa sổ
`DeviceConfigForm`, `SetupNoticeForm`, `InstalledPrintersForm`, `TestPrintFilesForm`
và `UpdateProgressForm` cũng mở Designer tương tự. Bố cục dùng `TableLayoutPanel`
và `SplitContainer`: chọn panel trong **Document Outline** để chỉnh hàng/cột,
Dock, Anchor, Margin hoặc Padding.

Mỗi form có `.Designer.cs` chứa `InitializeComponent()` và `.resx` đi kèm;
logic sự kiện/service nằm trong `.cs`. Designer hiển thị caption tiếng Việt.
Nếu thay đổi/thêm caption cần dịch, cập nhật `server/public/translations.json`
(được copy khi build). Giữ **Localizable = False** để tiếp tục dùng cơ chế VI/EN
hiện có. Form chỉ kết nối service khi chạy; mở Designer không khởi động polling.

```powershell
dotnet build RemotePrint.sln -c Release
dotnet run --project client/RemotePrint.Checks -c Release -- --designer-check "$PWD"
dotnet run --project client/RemotePrint.Checks -c Release -- --ui-fit-check "$PWD"
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
dotnet artifacts/checks/RemotePrint.Checks.dll --installed-printer-check "$PWD"
dotnet artifacts/checks/RemotePrint.Checks.dll --test-printer-check "$PWD"
dotnet artifacts/checks/RemotePrint.Checks.dll --autosave-check
dotnet artifacts/checks/RemotePrint.Checks.dll --update-check "$PWD"
dotnet artifacts/checks/RemotePrint.Checks.dll --admin-startup-check "$PWD"
dotnet artifacts/checks/RemotePrint.Checks.dll --installer-check "$PWD"
dotnet artifacts/checks/RemotePrint.Checks.dll --mesh-check "$PWD"
```

Kiểm thử native relay dùng TCP thật và spool sink mô phỏng: kiểm tra byte RAW,
hash, ACK/retry, kết quả spool không xác định, từ chối quyền, thu hồi trong khi
truyền và ZIP traversal. Có xuất và kiểm tra gói driver từ máy in thật nhưng
không gửi lệnh ra máy in vật lý trong bài kiểm thử này.

**Chưa xác nhận cài driver và in đầu cuối giữa hai máy Windows thật** với cơ chế
RAW mới. Cần kiểm tra chữ ký/cài gói OEM, port monitor Windows, firewall và giấy
in thực tế trên từng dòng máy in. Build và kiểm thử relay không thay thế bước đó.
