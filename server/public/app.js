import { elements as el, render, renderClients, renderRooms, renderJobs, showDashboard, showNotice } from './ui.js';

const state = {
  token: sessionStorage.getItem('adminToken'),
  clients: [], jobs: [], rooms: [],
  filters: { search: '', status: 'all', room: '' }, jobFilter: 'all',
  roomDrafts: new Map(), busyClients: new Set(), busyRooms: new Set(), clientErrors: new Map(),
  revision: 0
};
let loadPromise = null;
let polling = null;
const errorMessages = {
  INVALID_CREDENTIALS: 'Tài khoản hoặc mật khẩu không đúng.',
  ADMIN_AUTH_FAILED: 'Phiên đăng nhập đã hết hạn. Hãy đăng nhập lại.',
  ROOM_REQUIRED: 'Hãy nhập tên Room.',
  ROOM_IN_USE: 'Room đang có máy. Chuyển máy sang Room khác trước khi xóa.',
  ROOM_NOT_FOUND: 'Không tìm thấy Room. Hãy làm mới danh sách.',
  CLIENT_NOT_FOUND: 'Không tìm thấy máy client. Hãy làm mới danh sách.',
  SERVER_ERROR: 'Server gặp lỗi. Hãy thử lại hoặc kiểm tra nhật ký server.'
};

class ApiError extends Error {
  constructor(message, status) { super(message); this.status = status; }
}

async function api(url, options = {}) {
  const requestToken = state.token;
  let response;
  try {
    response = await fetch(url, {
      ...options, signal: AbortSignal.timeout(15_000),
      headers: { 'content-type': 'application/json', ...(requestToken ? { authorization: 'Bearer ' + requestToken } : {}) }
    });
  } catch {
    throw new ApiError('Không kết nối được server. Kiểm tra kết nối rồi thử lại.', 0);
  }
  const body = await response.json().catch(() => ({}));
  if (!response.ok) {
    if (response.status === 401 && url !== '/api/admin/login' && requestToken === state.token)
      logout('Phiên đăng nhập đã hết hạn. Hãy đăng nhập lại.');
    throw new ApiError(errorMessages[body.error] || (response.status === 429
      ? 'Có quá nhiều yêu cầu. Hãy chờ một lúc rồi thử lại.' : 'Thao tác thất bại (HTTP ' + response.status + ').'), response.status);
  }
  return body;
}

function startPolling() {
  clearInterval(polling);
  polling = setInterval(() => {
    if (state.token && el['auto-refresh'].checked && document.visibilityState === 'visible')
      load().catch(reportLoadError);
  }, 15_000);
}

function logout(message = '') {
  state.token = null;
  state.revision++;
  loadPromise = null;
  sessionStorage.removeItem('adminToken');
  clearInterval(polling);
  polling = null;
  state.clients = []; state.jobs = []; state.rooms = [];
  state.filters = { search: '', status: 'all', room: '' };
  state.jobFilter = 'all';
  el['client-search'].value = '';
  el['client-status-filter'].value = 'all';
  el['client-room-filter'].value = '';
  el['job-status-filter'].value = 'all';
  el['room-error'].textContent = '';
  el['new-room'].value = '';
  el.refresh.disabled = false;
  el.dashboard.removeAttribute('aria-busy');
  state.roomDrafts.clear(); state.busyClients.clear(); state.busyRooms.clear(); state.clientErrors.clear();
  showDashboard(false);
  el.notice.hidden = true;
  el.password.value = '';
  el['login-error'].textContent = message;
}

function reportLoadError(error) {
  if (!state.token) return;
  showNotice(error.message, true);
  el['updated-at'].textContent = 'Không cập nhật được · dữ liệu đang hiển thị có thể đã cũ';
  el['updated-at'].parentElement.classList.add('is-stale');
}

function load() {
  if (loadPromise) return loadPromise;
  const token = state.token;
  const revision = state.revision;
  el.refresh.disabled = true;
  el.dashboard.setAttribute('aria-busy', 'true');
  const operation = (async () => {
    const [clients, jobs, rooms] = await Promise.all([
      api('/api/admin/clients'), api('/api/admin/jobs'), api('/api/admin/rooms')
    ]);
    // A response started before a mutation or logout must not overwrite newer state.
    if (token !== state.token || revision !== state.revision) return;
    state.clients = clients; state.jobs = jobs; state.rooms = rooms;
    render(state);
    el['updated-at'].textContent = 'Cập nhật ' + new Date().toLocaleTimeString('vi-VN');
    el['updated-at'].parentElement.classList.remove('is-stale');
  })();
  const wrapped = operation.finally(() => {
    if (loadPromise === wrapped) {
      loadPromise = null;
      el.refresh.disabled = false;
      el.dashboard.removeAttribute('aria-busy');
    }
  });
  loadPromise = wrapped;
  return wrapped;
}

el['login-form'].addEventListener('submit', async event => {
  event.preventDefault();
  el['login-error'].textContent = '';
  el['login-submit'].disabled = true;
  try {
    const result = await api('/api/admin/login', {
      method: 'POST', body: JSON.stringify({ username: el.username.value.trim(), password: el.password.value })
    });
    state.token = result.token;
    sessionStorage.setItem('adminToken', result.token);
    el.password.value = '';
    showDashboard(true);
    startPolling();
    await load().catch(reportLoadError);
  } catch (error) {
    el['login-error'].textContent = error.message;
  } finally { el['login-submit'].disabled = false; }
});

el.clients.addEventListener('change', event => {
  const select = event.target.closest('select[data-room]');
  if (!select) return;
  state.roomDrafts.set(select.dataset.room, select.value);
  state.clientErrors.delete(select.dataset.room);
  renderClients(state);
});

el.clients.addEventListener('click', async event => {
  const button = event.target.closest('button[data-approve], button[data-reject]');
  if (!button || !el.clients.contains(button)) return;
  const id = button.dataset.approve || button.dataset.reject;
  if (state.busyClients.has(id)) return;
  const client = state.clients.find(item => item.id === id);
  if (!client) return;
  const approving = Boolean(button.dataset.approve);
  const actionToken = state.token;
  const room = state.roomDrafts.has(id) ? state.roomDrafts.get(id) : client.room || '';
  if (approving && !room) {
    state.clientErrors.set(id, 'Chọn Room trước khi duyệt máy.');
    renderClients(state);
    return;
  }
  state.busyClients.add(id);
  state.clientErrors.delete(id);
  renderClients(state);
  try {
    const updated = await api('/api/admin/clients/' + encodeURIComponent(id) + (approving ? '/approve' : '/reject'), {
      method: 'POST', body: JSON.stringify(approving ? { room } : {})
    });
    if (state.token !== actionToken) return;
    state.revision++;
    state.clients = state.clients.map(item => item.id === id ? updated : item);
    state.roomDrafts.delete(id);
    if (approving && !state.rooms.includes(room)) state.rooms.push(room);
    showNotice(approving ? 'Đã duyệt ' + client.name + ' vào Room ' + room + '.' : 'Đã từ chối quyền truy cập của ' + client.name + '.');
    render(state);
  } catch (error) {
    if (state.token === actionToken) state.clientErrors.set(id, error.message);
  } finally {
    state.busyClients.delete(id);
    if (state.token) renderClients(state);
  }
});

el['room-form'].addEventListener('submit', async event => {
  event.preventDefault();
  const name = el['new-room'].value.trim();
  const actionToken = state.token;
  if (!name || el['room-submit'].disabled) return;
  el['room-error'].textContent = '';
  el['room-submit'].disabled = true;
  try {
    const result = await api('/api/admin/rooms', { method: 'POST', body: JSON.stringify({ name }) });
    if (state.token !== actionToken) return;
    state.revision++;
    if (!state.rooms.includes(result.name)) state.rooms.push(result.name);
    state.rooms.sort((a, b) => a.localeCompare(b, 'vi'));
    if (el['new-room'].value.trim() === name) el['new-room'].value = '';
    render(state);
    showNotice('Đã tạo Room ' + result.name + '.');
    if (document.activeElement === el['room-submit'] || document.activeElement === el['new-room'])
      el['new-room'].focus();
  } catch (error) {
    el['room-error'].textContent = error.message;
  } finally { el['room-submit'].disabled = false; }
});

function syncClientFilters() {
  state.filters = {
    search: el['client-search'].value,
    status: el['client-status-filter'].value,
    room: el['client-room-filter'].value
  };
  renderRooms(state);
  renderClients(state);
}
el['client-search'].addEventListener('input', syncClientFilters);
el['client-status-filter'].addEventListener('change', syncClientFilters);
el['client-room-filter'].addEventListener('change', syncClientFilters);
el['clear-filters'].addEventListener('click', () => {
  el['client-search'].value = ''; el['client-status-filter'].value = 'all'; el['client-room-filter'].value = '';
  syncClientFilters();
});
el['room-list'].addEventListener('click', async event => {
  const remove = event.target.closest('button[data-delete-room]');
  if (remove && el['room-list'].contains(remove)) {
    const room = remove.dataset.deleteRoom;
    if (!state.rooms.includes(room) || state.busyRooms.has(room)) return;
    const assigned = state.clients.filter(client => client.room === room).length;
    if (assigned > 0) {
      showNotice('Room ' + room + ' đang có ' + assigned + ' máy. Chuyển máy sang Room khác trước khi xóa.', true);
      return;
    }
    if (!window.confirm('Xóa Room "' + room + '"?')) return;
    const actionToken = state.token;
    state.busyRooms.add(room);
    renderRooms(state);
    try {
      await api('/api/admin/rooms/' + encodeURIComponent(room), { method: 'DELETE' });
      if (state.token !== actionToken) return;
      state.revision++;
      state.rooms = state.rooms.filter(item => item !== room);
      for (const [id, draft] of state.roomDrafts) {
        if (draft === room) state.roomDrafts.delete(id);
      }
      if (state.filters.room === room) {
        state.filters.room = '';
        el['client-room-filter'].value = '';
      }
      render(state);
      showNotice('Đã xóa Room ' + room + '.');
    } catch (error) {
      if (state.token === actionToken) showNotice(error.message, true);
    } finally {
      state.busyRooms.delete(room);
      if (state.token) renderRooms(state);
    }
    return;
  }
  const card = event.target.closest('button[data-filter-room]');
  if (!card || !el['room-list'].contains(card)) return;
  el['client-room-filter'].value = card.dataset.filterRoom;
  syncClientFilters();
  document.getElementById('clients-panel').scrollIntoView({ block: 'start' });
});
document.querySelector('.metrics').addEventListener('click', event => {
  const card = event.target.closest('[data-client-filter]');
  if (!card) return;
  el['client-search'].value = ''; el['client-room-filter'].value = '';
  el['client-status-filter'].value = card.dataset.clientFilter;
  syncClientFilters();
  document.getElementById('clients-panel').scrollIntoView({ block: 'start' });
});
el['job-status-filter'].addEventListener('change', () => {
  state.jobFilter = el['job-status-filter'].value;
  renderJobs(state);
});
el['failed-metric'].addEventListener('click', () => {
  state.jobFilter = 'failed';
  el['job-status-filter'].value = 'failed';
  renderJobs(state);
  document.getElementById('jobs-panel').scrollIntoView({ block: 'start' });
});
el.refresh.addEventListener('click', () => load().catch(reportLoadError));
el['auto-refresh'].addEventListener('change', () => load().catch(reportLoadError));
el.logout.addEventListener('click', () => logout());
el['notice-dismiss'].addEventListener('click', () => { el.notice.hidden = true; });
document.addEventListener('visibilitychange', () => {
  if (document.visibilityState === 'visible' && state.token && el['auto-refresh'].checked)
    load().catch(reportLoadError);
});
window.addEventListener('pagehide', () => clearInterval(polling));
window.addEventListener('pageshow', () => { if (state.token) startPolling(); });

if (state.token) {
  showDashboard(true);
  startPolling();
  load().catch(reportLoadError);
}
