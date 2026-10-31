# Hub WireGuard tương thích

Đây chỉ là lựa chọn đường mạng thay cho mesh; client vẫn dùng hàng đợi máy in
Windows và driver gốc. Server trong mô hình này giải mã rồi mã hóa lại gói IP,
vì vậy nên dùng mesh để mã hóa giữa hai client.

Tạo khóa riêng ở máy Linux và giữ tệp chỉ cho root đọc:

```sh
mkdir -p server/certs
umask 077
wg genkey > server/certs/tunnel-private.key
wg pubkey < server/certs/tunnel-private.key
```

Đặt `TUNNEL_ENDPOINT=print.example.com:51820` và khóa công khai vào
`TUNNEL_SERVER_PUBLIC_KEY` trong `server/.env`. Không tạo lại khóa khi đang chạy.

```sh
export TUNNEL_SERVER_PUBLIC_KEY="$(wg pubkey < server/certs/tunnel-private.key)"
docker compose -f docker-compose.yml -f docker-compose.tunnel.yml up -d --build
```

Override chọn `TUNNEL_MODE=legacy`, tạo container NET_ADMIN với network namespace
riêng và chỉ đọc snapshot định tuyến. Mở UDP 51820; không mở SMB/RPC ra Internet.
Firewall nftables cho TCP 135, 445 và RPC động giữa các máy được duyệt cùng Room,
thu hồi khi thiết bị hết heartbeat hoặc controller/API mất lease. Dải IP cố định
`10.247.0.0/16`; quyền SMB và printer ACL vẫn do Windows thực thi.

```sh
unshare -Urn bash server/test/tunnel-linux.sh
unshare -Urn node server/test/relay-api-integration.mjs
```
