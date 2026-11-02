import { t, locale, translateTree } from './i18n.js';
import { elements as el, render, renderClients, renderRooms, showDashboard, showNotice, clientRooms } from './ui.js';

const state = {
  token: sessionStorage.getItem('adminToken'),
  clients: [], rooms: [],
  filters: { search: '', status: 'all', room: '' },
  roomDrafts: new Map(), busyClients: new Set(), busyRooms: new Set(), clientErrors: new Map(),
  revision: 0
};
let loadPromise = null;
let polling = null;
let pairExpiry = null;
const pairCode = document.getElementById('pair-code');
const pairStatus = document.getElementById('pair-status');
const pairServer = document.getElementById('pair-server');
const pairDialog = document.getElementById('pair-dialog');
const pairCopy = document.getElementById('pair-copy');
const pairCreate = document.getElementById('pair-create');
let pairRevision = 0;
let lastUpdated = null;
const updatesDialog = document.getElementById('updates-dialog');
const updateStatus = document.getElementById('updates-status');
const updateCurrent = document.getElementById('updates-current');
const updatePublish = document.getElementById('updates-publish');
let updateUpload = null;
let releases = [];
function renderReleases() {
  updateCurrent.textContent = releases.length ? releases.map(r => `${r.runtime}: ${r.version} · ${(r.size / 1048576).toFixed(1)} MiB`).join('\n') : t('Chưa có bản cập nhật trên server.');
}
document.getElementById('updates-open').addEventListener('click', async () => {
  updateStatus.textContent = ''; updateCurrent.textContent = t('Đang tải…'); updatesDialog.showModal();
  const token = state.token;
  try { const result = await api('/api/admin/updates'); if (token === state.token) { releases = result; renderReleases(); } }
  catch (e) { if (token === state.token) updateStatus.textContent = e.message; }
});
document.getElementById('updates-close').addEventListener('click', () => updatesDialog.close());
updatesDialog.addEventListener('close', () => {
  updateUpload?.abort();
  document.getElementById('updates-file').value = '';
});
document.getElementById('updates-form').addEventListener('submit', async event => {
  event.preventDefault();
  if (updateUpload) return;
  const file = document.getElementById('updates-file').files[0];
  if (!file) return;
  if (file.size > 256 * 1048576) { updateStatus.textContent = t('Gói cập nhật vượt quá 256 MiB.'); return; }
  const token = state.token;
  const runtime = document.getElementById('updates-runtime').value;
  const query = new URLSearchParams({ notes: document.getElementById('updates-notes').value.trim() });
  updatePublish.disabled = true; updateStatus.textContent = t('Đang tải bản cập nhật…');
  const controller = new AbortController(); updateUpload = controller;
  const timeout = setTimeout(() => controller.abort(), 10 * 60 * 1000);
  try {
    const response = await fetch(`/api/admin/updates/${runtime}?${query}`, { method: 'PUT', body: file, signal: controller.signal,
      headers: { authorization: 'Bearer ' + token, 'content-type': 'application/zip' } });
    const body = await response.json().catch(() => ({}));
    if (token !== state.token) return;
    if (response.status === 401) { logout(t('Phiên đăng nhập đã hết hạn. Hãy đăng nhập lại.')); return; }
    if (!response.ok) throw new Error(errorMessages()[body.error] || t('Không tải được gói cập nhật. Hãy kiểm tra kết nối và giới hạn tải lên của proxy.'));
    releases = releases.filter(r => r.runtime !== runtime).concat(body); renderReleases();
    updateStatus.textContent = t('Đã phát hành. Client sẽ đề nghị cập nhật trong lần kiểm tra tiếp theo.');
    document.getElementById('updates-file').value = '';
  } catch (e) {
    if (token === state.token && updatesDialog.open) updateStatus.textContent = e.name === 'AbortError' ? t('Đã hủy hoặc hết thời gian cập nhật.') : e.message;
  } finally { clearTimeout(timeout); updateUpload = null; updatePublish.disabled = false; }
});
window.addEventListener('languagechange-local', renderReleases);
document.getElementById('pair-open').addEventListener('click', () => pairDialog.showModal());
document.getElementById('pair-close').addEventListener('click', () => pairDialog.close());
pairDialog.addEventListener('close', () => {
  pairRevision++; clearTimeout(pairExpiry); pairCode.value = ''; pairCopy.disabled = true;
  pairStatus.textContent = ''; pairStatus.classList.remove('is-error'); pairCreate.textContent = t("Tạo mã");
});
pairServer.value = location.origin;
document.getElementById('pair-create').addEventListener('click', async event => {
  event.target.disabled = true;
  pairCreate.textContent = t("Đang tạo…"); pairStatus.classList.remove('is-error');
  const revision = pairRevision;
  const token = state.token;
  try {
    const result = await api('/api/admin/enrollment', { method: 'POST', body: JSON.stringify({ serverUrl: pairServer.value.trim() }) });
    if (token !== state.token || revision !== pairRevision || !pairDialog.open) return;
    clearTimeout(pairExpiry);
    pairCode.value = result.code;
    pairCopy.disabled = false;
    pairStatus.textContent = t("Hết hạn lúc ") + new Date(result.expiresAt).toLocaleTimeString(locale());
    pairExpiry = setTimeout(() => { pairCode.value = ''; pairCopy.disabled = true; pairStatus.textContent = t("Mã đã hết hạn. Tạo mã mới để ghép nối."); }, Math.max(0, Date.parse(result.expiresAt) - Date.now()));
  } catch { if (revision === pairRevision && token === state.token) { pairStatus.textContent = t("Không tạo được mã. Kiểm tra địa chỉ HTTPS hoặc thử lại sau."); pairStatus.classList.add('is-error'); } }
  finally { event.target.disabled = false; pairCreate.textContent = pairCode.value ? t("Tạo mã khác") : t("Tạo mã"); }
});
document.getElementById('pair-copy').addEventListener('click', async () => {
  if (!pairCode.value) return;
  pairStatus.classList.remove('is-error');
  try { await navigator.clipboard.writeText(pairCode.value); pairStatus.textContent = t("Đã sao chép; mã chỉ có hiệu lực 5 phút từ lúc tạo."); }
  catch { pairCode.select(); pairStatus.textContent = t("Nhấn Ctrl+C để sao chép mã đã chọn."); }
});
const errorMessages = () => ({
  INVALID_CREDENTIALS: t("Tài khoản hoặc mật khẩu không đúng."),
  ADMIN_AUTH_FAILED: t("Phiên đăng nhập đã hết hạn. Hãy đăng nhập lại."),
  ROOM_REQUIRED: t("Hãy nhập tên Room."),
  ROOM_IN_USE: t("Room đang có máy. Chuyển máy sang Room khác trước khi xóa."),
  ROOM_NOT_FOUND: t("Không tìm thấy Room. Hãy làm mới danh sách."),
  CLIENT_NOT_FOUND: t("Không tìm thấy máy client. Hãy làm mới danh sách."),
  SERVER_ERROR: t("Server gặp lỗi. Hãy thử lại hoặc kiểm tra nhật ký server."),
  INVALID_UPDATE_METADATA: t("Thông tin cập nhật không hợp lệ."),
  INVALID_UPDATE_PACKAGE: t("Gói cập nhật không hợp lệ hoặc không khớp phiên bản/kiến trúc."),
  UPDATE_VERSION_MUST_INCREASE: t("Phiên bản phải lớn hơn bản đã phát hành."),
  UPDATE_UPLOAD_BUSY: t("Đang có gói khác được tải lên. Hãy thử lại."),
  UPDATE_TOO_LARGE: t("Gói cập nhật vượt quá 256 MiB.")
});

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
    throw new ApiError(t("Không kết nối được server. Kiểm tra kết nối rồi thử lại."), 0);
  }
  const body = await response.json().catch(() => ({}));
  if (!response.ok) {
    if (response.status === 401 && url !== '/api/admin/login' && requestToken === state.token)
      logout(t("Phiên đăng nhập đã hết hạn. Hãy đăng nhập lại."));
    throw new ApiError(errorMessages()[body.error] || (response.status === 429
      ? t("Có quá nhiều yêu cầu. Hãy chờ một lúc rồi thử lại.") : t("Thao tác thất bại (HTTP ") + response.status + ').'), response.status);
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
  updateUpload?.abort();
  if (updatesDialog.open) updatesDialog.close();
  if (pairDialog.open) pairDialog.close();
  clearTimeout(pairExpiry); pairCode.value = ''; pairStatus.textContent = '';
  state.token = null;
  state.revision++;
  loadPromise = null;
  sessionStorage.removeItem('adminToken');
  clearInterval(polling);
  polling = null;
  state.clients = []; state.rooms = [];
  state.filters = { search: '', status: 'all', room: '' };
  el['client-search'].value = '';
  el['client-status-filter'].value = 'all';
  el['client-room-filter'].value = '';
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
  el['updated-at'].textContent = t("Không cập nhật được · dữ liệu đang hiển thị có thể đã cũ");
  el['updated-at'].parentElement.classList.add('is-stale');
}

function load() {
  if (loadPromise) return loadPromise;
  const token = state.token;
  const revision = state.revision;
  el.refresh.disabled = true;
  el.dashboard.setAttribute('aria-busy', 'true');
  const operation = (async () => {
    const [clients, rooms] = await Promise.all([
      api('/api/admin/clients'), api('/api/admin/rooms')
    ]);
    // A response started before a mutation or logout must not overwrite newer state.
    if (token !== state.token || revision !== state.revision) return;
    state.clients = clients; state.rooms = rooms;
    render(state);
    lastUpdated = new Date();
    el['updated-at'].textContent = t("Cập nhật ") + lastUpdated.toLocaleTimeString(locale());
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
  const select = event.target.closest('[data-room]');
  if (!select) return;
  state.roomDrafts.set(select.dataset.room, [...select.querySelectorAll("input:checked")].map(input => input.value));
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
  const rooms = state.roomDrafts.get(id) ?? clientRooms(client);
  if (approving && !rooms.length) {
    state.clientErrors.set(id, t("Chọn Room trước khi duyệt máy."));
    renderClients(state);
    return;
  }
  state.busyClients.add(id);
  state.clientErrors.delete(id);
  renderClients(state);
  try {
    const updated = await api('/api/admin/clients/' + encodeURIComponent(id) + (approving ? '/approve' : '/reject'), {
      method: 'POST', body: JSON.stringify(approving ? { rooms } : {})
    });
    if (state.token !== actionToken) return;
    state.revision++;
    state.clients = state.clients.map(item => item.id === id ? updated : item);
    state.roomDrafts.delete(id);
    if (approving) for (const room of updated.rooms) if (!state.rooms.includes(room)) state.rooms.push(room);
    showNotice(approving ? t("Đã duyệt ") + client.name + t(" vào Room ") + rooms.join(", ") + '.' : t("Đã từ chối quyền truy cập của ") + client.name + '.');
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
    showNotice(t("Đã tạo Room ") + result.name + '.');
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
    const assigned = state.clients.filter(client => clientRooms(client).includes(room)).length;
    if (assigned > 0) {
      showNotice('Room ' + room + t(" đang có ") + assigned + t(" máy. Chuyển máy sang Room khác trước khi xóa."), true);
      return;
    }
    if (!window.confirm(t("Xóa Room \"") + room + '"?')) return;
    const actionToken = state.token;
    state.busyRooms.add(room);
    renderRooms(state);
    try {
      await api('/api/admin/rooms/' + encodeURIComponent(room), { method: 'DELETE' });
      if (state.token !== actionToken) return;
      state.revision++;
      state.rooms = state.rooms.filter(item => item !== room);
      for (const [id, draft] of state.roomDrafts) {
        if (draft.includes(room)) state.roomDrafts.set(id, draft.filter(value => value !== room));
      }
      if (state.filters.room === room) {
        state.filters.room = '';
        el['client-room-filter'].value = '';
      }
      render(state);
      showNotice(t("Đã xóa Room ") + room + '.');
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

window.addEventListener('languagechange-local', () => {
  render(state);
  if (lastUpdated && !el['updated-at'].parentElement.classList.contains('is-stale'))
    el['updated-at'].textContent = t("Cập nhật ") + lastUpdated.toLocaleTimeString(locale());
});
