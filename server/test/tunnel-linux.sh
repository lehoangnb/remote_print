#!/usr/bin/env bash
set -euo pipefail
umask 077
# Run in an isolated network namespace: unshare -Urn bash server/test/tunnel-linux.sh
# Requires node, ip, nft and wg. It never modifies the host's network namespace.
if [[ $(id -u) != 0 ]]; then echo 'Run in a root-owned network namespace' >&2; exit 1; fi
for tool in node ip nft wg; do command -v "$tool" >/dev/null; done
work=$(mktemp -d)
pid=''
ns1=''
ns2=''
cleanup() {
  if [[ -n "$pid" ]]; then kill -CONT "$pid" 2>/dev/null || true; kill "$pid" 2>/dev/null || true; wait "$pid" 2>/dev/null || true; fi
  for child in "$ns1" "$ns2"; do
    if [[ -n "$child" ]]; then kill "$child" 2>/dev/null || true; wait "$child" 2>/dev/null || true; fi
  done
  rm -rf -- "$work"
}
trap cleanup EXIT
wg genkey > "$work/private"
pub=$(wg pubkey < "$work/private")
wg genkey > "$work/client1"
wg genkey > "$work/client2"
peer1=$(wg pubkey < "$work/client1")
peer2=$(wg pubkey < "$work/client2")
touch "$work/tunnel-heartbeat"
cat > "$work/state.json" <<EOF
{"clients":[{"status":"approved","lastSeenAt":"$(date -u +%Y-%m-%dT%H:%M:%SZ)","room":"one","tunnelAddress":"10.247.0.2","tunnelPublicKey":"$peer1"},{"status":"approved","lastSeenAt":"$(date -u +%Y-%m-%dT%H:%M:%SZ)","room":"one","tunnelAddress":"10.247.0.3","tunnelPublicKey":"$peer2"}]}
EOF
TUNNEL_STATE_DIR="$work" TUNNEL_PRIVATE_KEY_FILE="$work/private" TUNNEL_SERVER_PUBLIC_KEY="$pub" \
  node server/src/tunnel-controller.js > "$work/log" 2>&1 &
pid=$!
for _ in {1..50}; do
  if wg show rpwg0 peers 2>/dev/null | grep -Fxq "$peer2" &&
     nft list table inet remote_print 2>/dev/null | grep -q '10.247.0.2'; then break; fi
  if ! kill -0 "$pid" 2>/dev/null; then cat "$work/log" >&2; exit 1; fi
  sleep 0.1
done
wg show rpwg0 peers | grep -Fxq "$peer1"
wg show rpwg0 peers | grep -Fxq "$peer2"
nft list table inet remote_print | grep -q '10.247.0.2'
# Two more network namespaces act as independent Internet clients. WireGuard
# traffic traverses veth links and the relay forwards only within a Room.
unshare -n sleep 60 & ns1=$!
unshare -n sleep 60 & ns2=$!
sleep 0.2
ip link add veth1 type veth peer name client1
ip link set client1 netns "$ns1"
ip address add 192.0.2.1/24 dev veth1
ip link set veth1 up
ip link add veth2 type veth peer name client2
ip link set client2 netns "$ns2"
ip address add 192.0.3.1/24 dev veth2
ip link set veth2 up
echo 1 > /proc/sys/net/ipv4/ip_forward
for index in 1 2; do
  if [[ $index == 1 ]]; then child=$ns1; subnet=2; else child=$ns2; subnet=3; fi
  nsenter -t "$child" -n ip link set lo up
  nsenter -t "$child" -n ip address add "192.0.$subnet.2/24" dev "client$index"
  nsenter -t "$child" -n ip link set "client$index" up
  nsenter -t "$child" -n ip link add wgclient type wireguard
  nsenter -t "$child" -n wg set wgclient private-key "$work/client$index" peer "$pub" \
    allowed-ips 10.247.0.0/16 endpoint "192.0.$subnet.1:51820" persistent-keepalive 1
  nsenter -t "$child" -n ip address add "10.247.0.$((index + 1))/32" dev wgclient
  nsenter -t "$child" -n ip link set wgclient up
  nsenter -t "$child" -n ip route add 10.247.0.0/16 dev wgclient
done
nsenter -t "$ns2" -n python3 -c 'import socket; s=socket.socket(); s.setsockopt(socket.SOL_SOCKET,socket.SO_REUSEADDR,1); s.bind(("10.247.0.3",445)); s.listen(); exec("while True:\n c,_=s.accept(); c.sendall(b\"ok\"); c.close()")' &
listener=$!
nsenter -t "$ns2" -n python3 -c 'import socket; s=socket.socket(); s.bind(("10.247.0.3",8080)); s.listen(); s.accept()' &
other_listener=$!
sleep 0.3
nsenter -t "$ns1" -n python3 -c 'import socket; s=socket.create_connection(("10.247.0.3",445),5); assert s.recv(2)==b"ok"'
if nsenter -t "$ns1" -n python3 -c 'import socket; socket.create_connection(("10.247.0.3",8080),1)' 2>/dev/null; then
  echo 'Unexpected port was accepted' >&2; exit 1
fi
kill "$other_listener" 2>/dev/null || true
# Even a frozen controller must lose the ability to forward after the nft set
# expires. Resume it and confirm the same valid Room becomes usable again.
kill -STOP "$pid"
sleep 17
if nsenter -t "$ns1" -n python3 -c 'import socket; socket.create_connection(("10.247.0.3",445),1)' 2>/dev/null; then
  echo 'Frozen controller left forwarding enabled' >&2; exit 1
fi
touch "$work/tunnel-heartbeat"
kill -CONT "$pid"
sleep 2
nsenter -t "$ns1" -n python3 -c 'import socket; s=socket.create_connection(("10.247.0.3",445),5); assert s.recv(2)==b"ok"'
# A Room change removes the allowed pair without removing either WireGuard peer.
sed -i 's/"room":"one","tunnelAddress":"10.247.0.3"/"room":"two","tunnelAddress":"10.247.0.3"/' "$work/state.json"
sleep 2
if nft list table inet remote_print | grep -q '10.247.0.2'; then echo 'Room isolation failed' >&2; exit 1; fi
wg show rpwg0 peers | grep -Fxq "$peer2"
if nsenter -t "$ns1" -n python3 -c 'import socket; socket.create_connection(("10.247.0.3",445),1)' 2>/dev/null; then
  echo 'Cross-Room traffic was accepted' >&2; exit 1
fi
kill "$listener" 2>/dev/null || true
# An approved peer that stops contacting the API must lose its key even if
# the server itself is healthy and its heartbeat continues.
sed -i 's/"lastSeenAt":"[^"]*"/"lastSeenAt":"1970-01-01T00:00:00Z"/' "$work/state.json"
touch "$work/tunnel-heartbeat"
sleep 2
if wg show rpwg0 peers | grep -Fxq "$peer1"; then echo 'Expired client lease remained active' >&2; exit 1; fi
wg show rpwg0 peers | grep -Fxq "$peer2"
# A stopped control plane must revoke existing peers.
touch -d '30 seconds ago' "$work/tunnel-heartbeat"
sleep 2
if [[ -n $(wg show rpwg0 peers) ]]; then echo 'Stale peers remained active' >&2; exit 1; fi
echo 'Tunnel controller and firewall checks passed.'
