// An explicit empty array revokes membership; room is only a legacy fallback.
export function roomsFor(client) {
  return Array.isArray(client?.rooms) ? client.rooms : typeof client?.room === 'string' && client.room ? [client.room] : [];
}
export function shareRoom(a, b) {
  const rooms = new Set(roomsFor(a));
  return roomsFor(b).some(room => rooms.has(room));
}
