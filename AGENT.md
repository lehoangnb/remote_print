# AGENT.md — Quy ước làm việc với agent

## Quy tắc bắt buộc

1. **KHÔNG tự commit/push.** Agent tuyệt đối không chạy `git commit`, `git push`
   (hay lệnh tương đương) khi chưa được user yêu cầu rõ ràng trong lượt hiện tại.
   Các lệnh chỉ đọc (`status`, `diff`, `log`) được dùng tự do.
2. **Mọi thay đổi đều phải cập nhật AGENT.md.** Bất kỳ thay đổi nào (code, config,
   gitignore, docs, tokensave, ...) đều phải ghi một dòng vào "Nhật ký thay đổi"
   bên dưới: ngày, nội dung, file liên quan, trạng thái commit (hash nếu đã commit,
   "chưa commit" nếu chưa). Commit chỉ để cập nhật log AGENT.md không cần dòng log riêng.

## Tổng quan dự án

Remote Print: chia sẻ máy in giữa các máy Windows qua server trung tâm.
Chi tiết cách chạy xem `README.md`.

## Cấu trúc repo

- `client/RemotePrint.Client`: giao diện WPF + desktop agent (khay hệ thống).
- `client/RemotePrint.Service`: Windows Service nhận/giải mã/in tài liệu.
- `client/RemotePrint.Core`: mã hóa, API, named pipe.
- `client/RemotePrint.Checks`: kiểm tra kiến trúc service.
- `server/`: Node.js 20+, API + trang quản trị (`src/`, `public/`, `test/`).
- `scripts/`, `docker-compose.yml`.
- `dist/`, `artifacts/`: output build, không commit.

## Quy ước git

- Branch chính: `main`. Remote: `origin` → `https://github.com/lehoangnb/remote_print.git`.
- Không commit: `server/.env`, `server/data/`, `server/certs/`, `*.log`,
  `bin/`/`obj/`, `dist/`, `artifacts/`, `.tokensave/*-shm`/`*-wal`.
- `.tokensave` được track (`tokensave.db` + `config.json` + `branch-meta.json`)
  để dùng chung nhiều máy. Chỉ commit lại khi re-index lớn;
  KHÔNG commit mỗi lần bump timestamp (`last_synced_at`).
- Máy khác: pull rồi chạy `tokensave sync` local, không push DB ngược lại trừ khi cần.

## Trạng thái hiện tại (2026-10-13)

- Commit mới nhất: `6b8a1fc` Add AGENT.md conventions and sync tokensave index.
- Cây sạch sau commit, chờ push `origin main`.

## Nhật ký thay đổi

| Ngày       | Nội dung                                              | File liên quan                        | Commit     |
|------------|-------------------------------------------------------|---------------------------------------|------------|
| 2026-10-13 | Commit initial + gitignore mở rộng (Node/.NET/Win)    | `.gitignore`, toàn repo               | `cd190b1`  |
| 2026-10-13 | Init tokensave (37 files, 718 nodes)                  | `.tokensave/`                         | chưa commit|
| 2026-10-13 | Track tokensave index để dùng chung nhiều máy         | `.gitignore`, `.tokensave/`           | `9113093`  |
| 2026-10-13 | Tạo AGENT.md: cấm tự commit, mọi đổi phải ghi log đây | `AGENT.md`                            | `6b8a1fc`  |
| 2026-10-13 | Commit AGENT.md + tokensave sync, push origin main    | `AGENT.md`, `.tokensave/`             | `6b8a1fc`  |
