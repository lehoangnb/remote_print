export const elements = Object.fromEntries([
  'login', 'dashboard', 'login-form', 'login-error', 'username', 'password', 'login-submit',
  'refresh', 'auto-refresh', 'logout', 'updated-at', 'summary', 'notice', 'notice-text', 'notice-dismiss',
  'metric-clients', 'metric-online', 'metric-pending', 'metric-failed', 'nav-pending',
  'room-form', 'new-room', 'room-submit', 'room-list', 'room-error', 'room-count',
  'clients', 'client-count', 'clients-empty', 'client-result-count', 'client-search',
  'client-status-filter', 'client-room-filter', 'clear-filters', 'failed-metric',
  'jobs', 'job-count', 'jobs-empty', 'job-status-filter', 'job-result-count'
].map(id => [id, document.getElementById(id)]));

const clientRows = new Map();
const clientLabels = { pending: 'Chờ duyệt', approved: 'Đã duyệt', rejected: 'Bị từ chối' };
const jobLabels = { pending: 'Chờ xử lý', completed: 'Hoàn tất', failed: 'Thất bại' };
const statusClasses = new Set(['pending', 'approved', 'rejected', 'completed', 'failed']);
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
  text(element, labels[status] || 'Không xác định');
}

function formatDate(value) {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? '—' : date.toLocaleString('vi-VN');
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
    elements.jobs.replaceChildren();
    elements['room-list'].replaceChildren();
    roomCardsSignature = '';
  }
}

function updateRoomOptions(select, rooms, placeholder, selected) {
  const signature = JSON.stringify(rooms);
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
  const { clients, jobs, rooms } = state;
  const pending = clients.filter(client => client.status === 'pending').length;
  text(elements['metric-clients'], clients.length);
  text(elements['metric-online'], clients.filter(client => isOnline(client)).length);
  text(elements['metric-pending'], pending);
  text(elements['metric-failed'], jobs.filter(job => job.status === 'failed').length);
  text(elements['nav-pending'], pending);
  elements['nav-pending'].hidden = pending === 0;
  text(elements.summary, rooms.length + ' Room · ' + clients.length + ' máy');
  text(elements['room-count'], rooms.length);
  text(elements['client-count'], clients.length);
  text(elements['job-count'], jobs.length);
}

export function renderRooms(state) {
  const stats = new Map(state.rooms.map(room => [room, { total: 0, approved: 0 }]));
  for (const client of state.clients) {
    const entry = stats.get(client.room);
    if (entry) {
      entry.total++;
      if (client.status === 'approved') entry.approved++;
    }
  }
  const signature = JSON.stringify([...stats].map(([room, entry]) => [room, entry.total, entry.approved]));
  if (signature !== roomCardsSignature) {
    const fragment = document.createDocumentFragment();
    for (const room of state.rooms) {
      const entry = stats.get(room);
      const card = node('button', 'room-card');
      card.type = 'button';
      card.dataset.filterRoom = room;
      const icon = node('span', 'room-card-icon');
      icon.innerHTML = '<svg class="icon" aria-hidden="true"><use href="/icons.svg#rooms"/></svg>';
      const description = node('span', 'room-card-copy');
      description.append(node('strong', '', room),
        node('small', '', entry.total + ' máy · ' + entry.approved + ' đã duyệt'));
      const count = node('span', 'room-card-count', String(entry.total));
      count.setAttribute('aria-hidden', 'true');
      card.append(icon, description, count);
      fragment.append(card);
    }
    if (state.rooms.length === 0) {
      const empty = node('div', 'room-empty');
      empty.append(node('strong', '', 'Chưa có Room nào'),
        node('p', '', 'Tạo Room đầu tiên để bắt đầu phân nhóm và duyệt máy client.'));
      fragment.append(empty);
    }
    elements['room-list'].replaceChildren(fragment);
    roomCardsSignature = signature;
  }
  for (const card of elements['room-list'].querySelectorAll('[data-filter-room]')) {
    const selected = card.dataset.filterRoom === state.filters.room;
    card.classList.toggle('selected', selected);
    card.setAttribute('aria-pressed', String(selected));
  }
  updateRoomOptions(elements['client-room-filter'], state.rooms, 'Tất cả Room', state.filters.room);
}

function createClientRow(client) {
  const row = document.createElement('tr');
  row.dataset.clientId = client.id;
  // Only static markup is interpolated. Server-provided text is assigned via textContent.
  row.innerHTML = '<td data-label="Máy tính"><div class="machine-name"><span class="machine-avatar"><svg class="icon" aria-hidden="true"><use href="/icons.svg#computer"/></svg></span><strong class="name"></strong></div><small class="machine-id"></small></td>' +
    '<td data-label="Trạng thái"><span class="pill"></span><span class="client-connection"></span><small class="last-seen"></small></td>' +
    '<td data-label="Máy in chia sẻ" class="printers-cell"></td>' +
    '<td data-label="Room &amp; quyền truy cập"><div class="client-access"><div class="access-summary"><span class="access-symbol"><svg class="icon" aria-hidden="true"><use href="/icons.svg#shield"/></svg></span><div><span class="access-eyebrow">Quyền truy cập</span><strong class="access-state"></strong></div></div><div class="assigned-room"><span>Room hiện tại</span><strong class="current-room"></strong></div><label class="room-picker"><span>Gán / chuyển Room</span><select></select></label><div class="action-buttons"><button type="button" class="button button-primary button-small approve"></button><button type="button" class="button button-danger button-small reject">Từ chối</button></div><p class="row-feedback" role="status"></p></div></td>';
  const fields = Object.fromEntries(['name', 'machine-id', 'pill', 'client-connection', 'last-seen', 'printers-cell', 'approve', 'reject', 'row-feedback', 'access-summary', 'access-state', 'current-room']
    .map(className => [className, row.querySelector('.' + className)]));
  fields.room = row.querySelector('.room-picker select');
  fields.room.dataset.room = client.id;
  fields.approve.dataset.approve = client.id;
  fields.reject.dataset.reject = client.id;
  elements.clients.append(row);
  return { row, fields, printerSignature: '' };
}

function renderPrinterList(view, printers) {
  const signature = JSON.stringify(printers);
  if (view.printerSignature === signature) return;
  view.printerSignature = signature;
  const cell = view.fields['printers-cell'];
  const expanded = Boolean(cell.querySelector('details')?.open);
  cell.replaceChildren();
  if (printers.length === 0) {
    cell.append(node('span', 'muted', 'Chỉ gửi lệnh in'));
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
    details.append(node('summary', '', 'Xem thêm ' + (printers.length - 2) + ' máy in'), remaining);
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
    f['access-summary'].dataset.status = client.status;
    text(f['access-state'], client.status === 'approved' ? 'Đã cấp quyền' :
      client.status === 'rejected' ? 'Đã từ chối' : 'Chờ cấp quyền');
    text(f['current-room'], client.room || 'Chưa gán Room');
    f['current-room'].classList.toggle('is-empty', !client.room);
    f['client-connection'].classList.toggle('online', online);
    text(f['client-connection'], online ? 'Đang online' : 'Đang offline');
    text(f['last-seen'], formatDate(client.lastSeenAt));
    renderPrinterList(view, client.printers || []);
    const room = state.roomDrafts.has(client.id) ? state.roomDrafts.get(client.id) : client.room || '';
    updateRoomOptions(f.room, state.rooms, 'Chọn Room…', room);
    f.room.setAttribute('aria-label', 'Room cho ' + client.name);
    f.approve.setAttribute('aria-label', (client.status === 'approved' ? 'Áp dụng Room cho ' : 'Duyệt ') + client.name);
    f.reject.setAttribute('aria-label', 'Từ chối ' + client.name);
    const busy = state.busyClients.has(client.id);
    f.room.disabled = busy;
    f.approve.disabled = busy || !f.room.value || (client.status === 'approved' && f.room.value === client.room);
    f.reject.disabled = busy || client.status === 'rejected';
    text(f.approve, busy ? 'Đang lưu…' : client.status === 'approved' ? 'Áp dụng' : 'Duyệt');
    const feedback = state.clientErrors.get(client.id);
    f['row-feedback'].classList.toggle('error', Boolean(feedback));
    text(f['row-feedback'], feedback || (!f.room.value ? 'Chọn Room để duyệt máy.' :
      client.status === 'approved' && f.room.value !== client.room ? 'Thay đổi Room chưa được lưu.' : ''));
  }
  const empty = elements['clients-empty'];
  empty.hidden = visible > 0;
  text(empty.querySelector('strong'), state.clients.length ? 'Không tìm thấy máy phù hợp' : 'Chưa có máy client');
  text(empty.querySelector('p'), state.clients.length ? 'Thử đổi từ khóa hoặc bỏ bộ lọc để xem các máy khác.' : 'Mở Remote Print Client và kết nối tới server để đăng ký máy.');
  text(elements['client-result-count'], 'Hiển thị ' + visible + ' / ' + state.clients.length + ' máy');
}

export function renderJobs(state) {
  const jobs = state.jobs.filter(job => state.jobFilter === 'all' || job.status === state.jobFilter);
  const fragment = document.createDocumentFragment();
  for (const job of jobs) {
    const row = document.createElement('tr');
    const fileCell = document.createElement('td');
    fileCell.append(node('div', 'job-document', job.fileName), node('small', 'last-seen', formatDate(job.createdAt)));
    const routeCell = document.createElement('td');
    const route = node('div', 'job-route');
    route.append(node('span', '', job.senderName), node('span', 'route-arrow', '↓ ' + (job.targetName || '—')));
    routeCell.append(route);
    const printerCell = node('td', 'job-printer', job.printerName);
    const resultCell = document.createElement('td');
    const badge = node('span');
    statusBadge(badge, job.status, jobLabels);
    resultCell.append(badge);
    if (job.error) resultCell.append(node('p', 'job-error', job.error));
    row.append(fileCell, routeCell, printerCell, resultCell);
    fragment.append(row);
  }
  elements.jobs.replaceChildren(fragment);
  elements['jobs-empty'].hidden = jobs.length > 0;
  text(elements['jobs-empty'].querySelector('strong'), state.jobs.length ? 'Không có lệnh in ở trạng thái này' : 'Chưa có lệnh in');
  text(elements['jobs-empty'].querySelector('p'), state.jobs.length ? 'Chọn trạng thái khác để xem thêm lệnh in.' : 'Lệnh in từ các client sẽ xuất hiện tại đây.');
  text(elements['job-result-count'], 'Hiển thị ' + jobs.length + ' / ' + state.jobs.length + ' lệnh in');
}

export function render(state) {
  renderOverview(state);
  renderRooms(state);
  renderClients(state);
  renderJobs(state);
}
