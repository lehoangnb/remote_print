#!/usr/bin/env bash
set -euo pipefail
umask 077
# Run only against a disposable Docker daemon with both :test images built.
# All resources get unique names; no production data or ports are used.
: "${DOCKER_HOST:?Set DOCKER_HOST to a disposable daemon}"
work=$(mktemp -d)
suffix=$(basename "$work" | tr -cd 'a-zA-Z0-9')
api="rp-smoke-api-$suffix"
relay="rp-smoke-relay-$suffix"
state="rp-smoke-state-$suffix"
data="rp-smoke-data-$suffix"
api_created=0
relay_created=0
state_created=0
data_created=0
cleanup() {
  if [[ $api_created == 1 ]]; then docker rm -f "$api" >/dev/null 2>&1 || true; fi
  if [[ $relay_created == 1 ]]; then docker rm -f "$relay" >/dev/null 2>&1 || true; fi
  if [[ $state_created == 1 ]]; then docker volume rm "$state" >/dev/null 2>&1 || true; fi
  if [[ $data_created == 1 ]]; then docker volume rm "$data" >/dev/null 2>&1 || true; fi
  rm -rf -- "$work"
}
trap cleanup EXIT
for name in "$api" "$relay"; do
  if docker container inspect "$name" >/dev/null 2>&1; then echo 'Test container name already exists' >&2; exit 1; fi
done
for name in "$state" "$data"; do
  if docker volume inspect "$name" >/dev/null 2>&1; then echo 'Test volume name already exists' >&2; exit 1; fi
done
docker run --rm --network none --entrypoint wg remote-print-tunnel:test genkey > "$work/private"
pub=$(docker run --rm -i --network none --entrypoint wg remote-print-tunnel:test pubkey < "$work/private")
docker volume create "$state" >/dev/null
state_created=1
docker volume create "$data" >/dev/null
data_created=1
docker run -d --name "$api" --network none \
  -e HOST=127.0.0.1 -e PORT=7443 -e TUNNEL_MODE=legacy -e TUNNEL_STATE_DIR=/app/tunnel-state \
  -e TUNNEL_ENDPOINT=vpn.example.test:51820 -e TUNNEL_SERVER_PUBLIC_KEY="$pub" \
  -e ADMIN_USERNAME=admin -e ADMIN_PASSWORD=integration-password-123 \
  -e JWT_SECRET=integration-secret-longer-than-32-bytes \
  -v "$state:/app/tunnel-state" -v "$data:/app/data" remote-print-server:test >/dev/null
api_created=1
docker run -d --name "$relay" --network none --read-only --tmpfs /tmp:rw,noexec,nosuid,nodev,size=16m \
  --cap-drop ALL --cap-add NET_ADMIN --security-opt no-new-privileges \
  --sysctl net.ipv4.ip_forward=1 -e TUNNEL_SERVER_PUBLIC_KEY="$pub" \
  -v "$state:/app/tunnel-state:ro" -v "$work/private:/run/secrets/tunnel_private_key:ro" \
  remote-print-tunnel:test >/dev/null
relay_created=1
for _ in {1..50}; do
  if docker exec "$relay" wg show rpwg0 >/dev/null 2>&1 && \
     docker exec "$api" node -e 'fetch("http://127.0.0.1:7443/health").then(r=>process.exit(r.ok?0:1)).catch(()=>process.exit(1))' >/dev/null 2>&1; then break; fi
  sleep 0.2
done
docker exec "$relay" wg show rpwg0 >/dev/null
docker exec "$relay" sh -c 'test ! -e /app/data/db.json'
docker exec -i "$api" node --input-type=module - <<'NODE'
import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
const base = 'http://127.0.0.1:7443';
async function request(path, method = 'GET', body, headers = {}) {
  const response = await fetch(base + path, { method, headers: { 'content-type': 'application/json', ...headers },
    ...(body === undefined ? {} : { body: JSON.stringify(body) }) });
  assert(response.ok, `${method} ${path}: ${response.status}`);
  return response.json();
}
const login = await request('/api/admin/login', 'POST', { username: 'admin', password: 'integration-password-123' });
const admin = { authorization: `Bearer ${login.token}` };
for (const byte of [1, 2]) {
  const deviceId = randomUUID(), secret = Buffer.alloc(48, byte).toString('base64');
  await request('/api/client/register', 'POST', { deviceId, secret, name: `Client ${byte}`, printers: [] });
  await request(`/api/admin/clients/${deviceId}/approve`, 'POST', { room: 'one' }, admin);
  await request('/api/client/tunnel', 'PUT', { publicKey: Buffer.alloc(32, byte).toString('base64') },
    { 'x-device-id': deviceId, 'x-device-secret': secret });
}
NODE
for _ in {1..50}; do
  count=$(docker exec "$relay" wg show rpwg0 peers | wc -l)
  if [[ "$count" -eq 2 ]] && docker exec "$relay" nft list table inet remote_print | grep -q '10.247.0.2'; then break; fi
  sleep 0.2
done
[[ $(docker exec "$relay" wg show rpwg0 peers | wc -l) -eq 2 ]]
docker exec "$relay" nft list table inet remote_print | grep -q '10.247.0.2'
docker stop -t 3 "$api" >/dev/null
for _ in {1..100}; do
  [[ $(docker exec "$relay" wg show rpwg0 peers | wc -l) -eq 0 ]] && break
  sleep 0.2
done
[[ $(docker exec "$relay" wg show rpwg0 peers | wc -l) -eq 0 ]]
echo 'Docker API and isolated relay smoke checks passed.'
