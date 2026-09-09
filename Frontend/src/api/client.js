// ตัวกลางเรียก API ทั้งหมด — แนบ access token, ต่ออายุ token อัตโนมัติเมื่อได้ 401
// และแปลง error จาก backend (ApiError) ให้เป็น Error ที่มีข้อความภาษาไทยพร้อมแสดง

const API_BASE = (import.meta.env.VITE_API_BASE_URL || 'http://localhost:5080').replace(/\/$/, '');
const STORAGE_KEY = 'mgt-ai-authen';

const listeners = new Set();

/** ให้ AuthContext รู้เมื่อ token/ผู้ใช้เปลี่ยน (รวมถึงตอนถูกเตะออกเพราะ token หมดอายุ) */
export function onAuthChange(listener) {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

function notify(auth) {
  listeners.forEach((l) => l(auth));
}

export function loadAuth() {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    return raw ? JSON.parse(raw) : null;
  } catch {
    return null;
  }
}

export function saveAuth(auth) {
  localStorage.setItem(STORAGE_KEY, JSON.stringify(auth));
  notify(auth);
}

export function clearAuth() {
  localStorage.removeItem(STORAGE_KEY);
  notify(null);
}

export class ApiError extends Error {
  constructor(message, status, code) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
    this.code = code;
  }
}

async function toApiError(response) {
  let message = `Request failed (HTTP ${response.status})`;
  let code = null;

  try {
    const data = await response.json();
    if (data?.message) {
      message = data.message;
      code = data.code ?? null;
    } else if (data?.errors) {
      // รูปแบบ ValidationProblemDetails ของ ASP.NET Core
      const first = Object.values(data.errors).flat()[0];
      if (first) message = first;
    } else if (data?.title) {
      message = data.title;
    }
  } catch {
    /* ไม่ใช่ JSON — ใช้ข้อความตั้งต้น */
  }

  return new ApiError(message, response.status, code);
}

// รวม request ที่ต่ออายุ token ให้เหลือครั้งเดียว แม้จะมีหลาย request เจอ 401 พร้อมกัน
let refreshInFlight = null;

async function refreshAccessToken() {
  const auth = loadAuth();
  if (!auth?.refreshToken) return null;

  refreshInFlight ??= (async () => {
    try {
      const response = await fetch(`${API_BASE}/api/auth/refresh`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ refreshToken: auth.refreshToken }),
      });

      if (!response.ok) {
        clearAuth();
        return null;
      }

      const data = await response.json();
      saveAuth(data);
      return data.accessToken;
    } catch {
      clearAuth();
      return null;
    } finally {
      refreshInFlight = null;
    }
  })();

  return refreshInFlight;
}

async function send(path, { method, body, signal, accept }) {
  const auth = loadAuth();
  const headers = {};

  if (auth?.accessToken) headers.Authorization = `Bearer ${auth.accessToken}`;
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  if (accept) headers.Accept = accept;

  return fetch(`${API_BASE}${path}`, {
    method,
    headers,
    signal,
    body: body === undefined ? undefined : JSON.stringify(body),
  });
}

/**
 * เรียก API แล้วคืน JSON — โยน ApiError เมื่อไม่สำเร็จ
 * ถ้าได้ 401 จะลองต่ออายุ token หนึ่งครั้งแล้วยิงซ้ำ
 */
export async function api(path, { method = 'GET', body, signal, skipRefresh = false } = {}) {
  let response = await send(path, { method, body, signal });

  if (response.status === 401 && !skipRefresh) {
    const token = await refreshAccessToken();
    if (token) {
      response = await send(path, { method, body, signal });
    }
  }

  if (!response.ok) throw await toApiError(response);
  if (response.status === 204) return null;

  const text = await response.text();
  return text ? JSON.parse(text) : null;
}

/**
 * ส่ง multipart/form-data (ใช้ตอนแนบไฟล์)
 * ห้ามตั้ง Content-Type เอง — ต้องให้ browser ใส่ boundary ให้
 */
export async function apiForm(path, formData, { method = 'POST' } = {}) {
  const send = () => {
    const auth = loadAuth();
    const headers = {};
    if (auth?.accessToken) headers.Authorization = `Bearer ${auth.accessToken}`;
    return fetch(`${API_BASE}${path}`, { method, headers, body: formData });
  };

  let response = await send();

  if (response.status === 401) {
    const token = await refreshAccessToken();
    if (token) response = await send();
  }

  if (!response.ok) throw await toApiError(response);
  if (response.status === 204) return null;

  const text = await response.text();
  return text ? JSON.parse(text) : null;
}

/** ดาวน์โหลดไฟล์ (ใช้กับ export CSV) — ต้องยิงผ่าน fetch เพราะต้องแนบ token */
export async function download(path, fallbackFileName) {
  let response = await send(path, { method: 'GET', accept: 'text/csv' });

  if (response.status === 401) {
    const token = await refreshAccessToken();
    if (token) response = await send(path, { method: 'GET', accept: 'text/csv' });
  }

  if (!response.ok) throw await toApiError(response);

  const disposition = response.headers.get('Content-Disposition') || '';
  const match = /filename\*?=(?:UTF-8'')?"?([^;"]+)"?/i.exec(disposition);
  const fileName = match ? decodeURIComponent(match[1]) : fallbackFileName;

  const blob = await response.blob();
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = url;
  link.download = fileName;
  document.body.appendChild(link);
  link.click();
  link.remove();
  URL.revokeObjectURL(url);
}

/** แปลง object เป็น query string โดยตัดค่าว่างออก */
export function toQuery(params) {
  const search = new URLSearchParams();

  Object.entries(params).forEach(([key, value]) => {
    if (value === undefined || value === null || value === '' || value === false) return;
    search.append(key, String(value));
  });

  const query = search.toString();
  return query ? `?${query}` : '';
}

export { API_BASE };
