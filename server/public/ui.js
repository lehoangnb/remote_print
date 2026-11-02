import { t, locale, translateTree } from './i18n.js';
export const elements = Object.fromEntries([
  'login', 'dashboard', 'login-form', 'login-error', 'username', 'password', 'login-submit',
  'refresh', 'auto-refresh', 'logout', 'updated-at', 'notice', 'notice-text', 'notice-dismiss',
  'metric-clients', 'metric-online', 'metric-pending',
  'room-form', 'new-room', 'room-submit', 'room-list', 'room-error', 'room-count',
  'clients', 'client-count', 'clients-empty', 'client-result-count', 'client-search',
  'client-status-filter', 'client-room-filter', 'clear-filters'
].map(id => [id, document.getElementById(id)]));

const clientRows = new Map();
const clientLabels = { pending: 'Chờ duyệt', approved: 'Đã duyệt', rejected: 'Bị từ chối' };
const statusClasses = new Set(['pending', 'approved', 'rejected']);
let roomCardsSignature = '';

export function isOnline(client, now = Date.now()) {
  const lastSeen = Date.parse(client.lastSeenAt);
  return Number.isFinite(lastSeen) && now - lastSeen <= 45_000;
}

function text(node, value) {
  const next = String(value ?? '');
  if (node.textContent !== next) node.textContent = next;
}

function node(tag, className, value) {
  const result = document.createElement(tag);
  if (className) result.className = className;
  if (value !== undefined) result.textContent = value;
  return result;
}

function statusBadge(element, status, labels) {
  element.className = 'pill' + (statusClasses.has(status) ? ' ' + status : '');
  text(element, t(labels[status] || 'Không xác định'));
}

function formatDate(value) {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? '—' : date.toLocaleString(locale());
}

export function showNotice(message, isError = false) {
  elements.notice.className = 'notice' + (isError ? ' error' : '');
  elements.notice.hidden = false;
  text(elements['notice-text'], message);
}

export function showDashboard(show) {
  elements.login.hidden = show;
  elements.dashboard.hidden = !show;
  if (!show) {
    clientRows.clear();
    elements.clients.replaceChildren();
    elements['room-list'].replaceChildren();
    roomCardsSignature = '';
  }
}

function updateRoomOptions(select, rooms, placeholder, selected) {
  const signature = JSON.stringify([rooms, placeholder]);
  if (select.dataset.options !== signature) {
    const fragment = document.createDocumentFragment();
    fragment.append(new Option(placeholder, ''));
    for (const room of rooms) fragment.append(new Option(room, room));
    select.replaceChildren(fragment);
    select.dataset.options = signature;
  }
  const value = rooms.includes(selected) ? selected : '';
  if (select.value !== value) select.value = value;
}

export function renderOverview(state) {
  const { clients, rooms } = state;
  const pending = clients.filter(client => client.status === 'pending').length;
  text(elements['metric-clients'], clients.length);
  text(elements['metric-online'], clients.filter(client => isOnline(client)).length);
  text(elements['metric-pending'], pending);
  text(elements['room-count'], rooms.length);
  text(elements['client-count'], clients.length);
}

export function renderRooms(state) {
  const stats = new Map(state.rooms.map(room => [room, 0]));
  for (const client of state.clients) {
    if (stats.has(client.room)) stats.set(client.room, stats.get(client.room) + 1);
  }
  const signature = JSON.stringify([[...stats], locale()]);
  if (signature !== roomCardsSignature) {
    const fragment = document.createDocumentFragment();
    for (const room of state.rooms) {
      const card = node('div', 'room-card');
      const select = node('button', 'room-card-select');
      select.type = 'button';
      select.dataset.filterRoom = room;
      const count = node('span', 'room-card-count', stats.get(room) + t(" máy"));
      select.append(node('strong', 'room-name', room), count);
      const remove = node('button', 'room-delete');
      remove.type = 'button';
      remove.dataset.deleteRoom = room;
      remove.setAttribute('aria-label', t("Xóa Room ") + room);
      remove.title = t("Xóa Room ") + room;
      remove.innerHTML = '<svg class="icon" aria-hidden="true"><use href="/icons.svg#trash"/></svg>';
      card.append(select, remove);
      fragment.append(card);
    }
    if (state.rooms.length === 0) {
      const empty = node('div', 'room-empty');
      empty.append(node('p', '', t("Chưa có Room. Tạo Room đầu tiên để bắt đầu.")));
      fragment.append(empty);
    }
    elements['room-list'].replaceChildren(fragment);
    roomCardsSignature = signature;
  }
  for (const card of elements['room-list'].querySelectorAll('[data-filter-room]')) {
    const selected = card.dataset.filterRoom === state.filters.room;
    card.closest('.room-card').classList.toggle('selected', selected);
    card.setAttribute('aria-pressed', String(selected));
  }
  for (const button of elements['room-list'].querySelectorAll('[data-delete-room]')) {
    button.disabled = state.busyRooms.has(button.dataset.deleteRoom);
  }
  updateRoomOptions(elements['client-room-filter'], state.rooms, t("Tất cả Room"), state.filters.room);
}

function createClientRow(client) {
  const row = document.createElement('tr');
  row.dataset.clientId = client.id;
  // Only static markup is interpolated. Server-provided text is assigned via textContent.
  row.innerHTML = '<td data-label="Máy tính"><div class="machine-name"><span class="machine-avatar"><svg class="icon" aria-hidden="true"><use href="/icons.svg#computer"/></svg></span><strong class="name"></strong></div><small class="machine-id"></small></td>' +
    '<td data-label="Trạng thái"><span class="pill"></span><span class="client-connection"></span><small class="last-seen"></small><small class="machine-tunnel"></small></td>' +
    '<td data-label="Máy in chia sẻ" class="printers-cell"></td>' +
    '<td data-label="Room &amp; quyền truy cập"><div class="client-access"><div class="access-meta"><span class="access-room">Room: <strong class="current-room"></strong></span><span class="access-draft" hidden>Chưa lưu</span></div><div class="access-edit"><select class="room-select"></select><button type="button" class="button button-primary button-small approve"></button><button type="button" class="button button-danger button-small reject">Từ chối</button></div><p class="row-feedback error" role="alert" hidden></p></div></td>';
  const fields = Object.fromEntries(['name', 'machine-id', 'pill', 'client-connection', 'last-seen', 'machine-tunnel', 'printers-cell', 'approve', 'reject', 'row-feedback', 'access-draft', 'current-room']
    .map(className => [className, row.querySelector('.' + className)]));
  fields.room = row.querySelector('.room-select');
  fields.room.dataset.room = client.id;
  fields.approve.dataset.approve = client.id;
  fields.reject.dataset.reject = client.id;
  translateTree(row);
  elements.clients.append(row);
  return { row, fields, printerSignature: '' };
}

function renderPrinterList(view, printers) {
  const signature = JSON.stringify([printers, locale()]);
  if (view.printerSignature === signature) return;
  view.printerSignature = signature;
  const cell = view.fields['printers-cell'];
  const expanded = Boolean(cell.querySelector('details')?.open);
  cell.replaceChildren();
  if (printers.length === 0) {
    cell.append(node('span', 'muted', t("Không chia sẻ")));
    return;
  }
  const list = node('ul', 'printer-list');
  for (const printer of printers.slice(0, 2)) list.append(node('li', '', printer));
  cell.append(list);
  if (printers.length > 2) {
    const details = node('details', 'printer-details');
    details.open = expanded;
    const remaining = node('ul', 'printer-list');
    for (const printer of printers.slice(2)) remaining.append(node('li', '', printer));
    details.append(node('summary', '', t("Xem thêm ") + (printers.length - 2) + t(" máy in")), remaining);
    cell.append(details);
  }
}

export function renderClients(state) {
  const ids = new Set(state.clients.map(client => client.id));
  for (const [id, view] of clientRows) {
    if (!ids.has(id)) { view.row.remove(); clientRows.delete(id); }
  }
  let visible = 0;
  const query = state.filters.search.trim().toLocaleLowerCase('vi');
  for (const client of state.clients) {
    let view = clientRows.get(client.id);
    if (!view) { view = createClientRow(client); clientRows.set(client.id, view); }
    const f = view.fields;
    translateTree(view.row);
    const online = isOnline(client);
    const statusMatch = state.filters.status === 'all' ||
      (state.filters.status === 'online' ? online : state.filters.status === 'offline' ? !online : client.status === state.filters.status);
    const roomMatch = !state.filters.room || client.room === state.filters.room;
    const searchMatch = !query || [client.name, client.id, ...(client.printers || [])].join(' ').toLocaleLowerCase('vi').includes(query);
    view.row.hidden = !(statusMatch && roomMatch && searchMatch);
    if (!view.row.hidden) visible++;
    text(f.name, client.name);
    text(f['machine-id'], 'ID · ' + client.id.slice(0, 8));
    f['machine-id'].title = client.id;
    statusBadge(f.pill, client.status, clientLabels);
    text(f['current-room'], client.room || t("Chưa gán"));
    f['current-room'].title = client.room || t("Chưa gán Room");
    f['current-room'].classList.toggle('is-empty', !client.room);
    f['client-connection'].classList.toggle('online', online);
    text(f['client-connection'], online ? 'Online' : 'Offline');
    f['client-connection'].title = t("Kết nối gần nhất: ") + formatDate(client.lastSeenAt);
    f['last-seen'].hidden = true;
    text(f['machine-tunnel'], !client.tunnel?.enrolled ? t("Tunnel: chưa ghi danh") :
      client.status !== 'approved' ? t("Tunnel: đã thu hồi") : t("Tunnel: đã ghi danh · ") + client.tunnel.address);
    f['machine-tunnel'].title = t("Ghi danh không xác nhận tunnel đang kết nối.");
    renderPrinterList(view, client.printers || []);
    const room = state.roomDrafts.has(client.id) ? state.roomDrafts.get(client.id) : client.room || '';
    updateRoomOptions(f.room, state.rooms, t("Chọn Room…"), room);
    f.room.setAttribute('aria-label', t("Gán hoặc chuyển Room cho ") + client.name);
    f['access-draft'].hidden = !(client.status === 'approved' && f.room.value !== (client.room || ''));
    f.approve.setAttribute('aria-label', (client.status === 'approved' ? t("Lưu Room cho ") : t("Duyệt ")) + client.name);
    f.reject.setAttribute('aria-label', t("Từ chối ") + client.name);
    const busy = state.busyClients.has(client.id);
    f.room.disabled = busy;
    f.approve.disabled = busy || !f.room.value || (client.status === 'approved' && f.room.value === client.room);
    f.reject.disabled = busy || client.status === 'rejected';
    text(f.approve, busy ? t("Đang lưu…") : client.status === 'approved' ? t("Lưu") : t("Duyệt"));
    const feedback = state.clientErrors.get(client.id);
    f['row-feedback'].hidden = !feedback;
    text(f['row-feedback'], feedback || '');
  }
  const empty = elements['clients-empty'];
  empty.hidden = visible > 0;
  text(empty.querySelector('strong'), state.clients.length ? t("Không tìm thấy máy phù hợp") : t("Chưa có máy client"));
  text(empty.querySelector('p'), state.clients.length ? t("Thử bỏ bộ lọc.") : t("Kết nối Remote Print Client để bắt đầu."));
  text(elements['client-result-count'], visible + ' / ' + state.clients.length + t(" máy"));
}

export function render(state) {
  renderOverview(state);
  renderRooms(state);
  renderClients(state);
}
