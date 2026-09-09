import { createContext, useCallback, useContext, useEffect, useMemo, useState } from 'react';
import { api, clearAuth, loadAuth, onAuthChange, saveAuth } from '../api/client';

const AuthContext = createContext(null);

export function AuthProvider({ children }) {
  const [auth, setAuth] = useState(() => loadAuth());
  const [checking, setChecking] = useState(true);

  // ให้ state ตามการเปลี่ยนแปลงใน localStorage (เช่นตอน refresh token หรือถูกเตะออก)
  useEffect(() => onAuthChange(setAuth), []);

  // ตรวจว่า token ที่เก็บไว้ยังใช้ได้จริงตอนเปิดหน้าเว็บ
  useEffect(() => {
    let cancelled = false;

    (async () => {
      if (!loadAuth()?.accessToken) {
        setChecking(false);
        return;
      }

      try {
        const profile = await api('/api/auth/me');
        if (!cancelled) {
          const current = loadAuth();
          if (current) saveAuth({ ...current, user: profile });
        }
      } catch {
        if (!cancelled) clearAuth();
      } finally {
        if (!cancelled) setChecking(false);
      }
    })();

    return () => {
      cancelled = true;
    };
  }, []);

  const login = useCallback(async (username, password) => {
    const data = await api('/api/auth/login', {
      method: 'POST',
      body: { username, password },
      skipRefresh: true,
    });
    saveAuth(data);
    return data.user;
  }, []);

  const logout = useCallback(async () => {
    const current = loadAuth();
    try {
      if (current?.accessToken) {
        await api('/api/auth/logout', {
          method: 'POST',
          body: { refreshToken: current.refreshToken ?? '' },
        });
      }
    } catch {
      /* ออกจากระบบฝั่ง client ให้ได้เสมอ แม้เรียก API ไม่สำเร็จ */
    } finally {
      clearAuth();
    }
  }, []);

  const value = useMemo(() => {
    const user = auth?.user ?? null;
    const role = user?.userRole ?? null;

    return {
      user,
      role,
      checking,
      isAuthenticated: Boolean(auth?.accessToken && user),
      isAdmin: role === 'Admin',
      canReadLogs: role === 'Admin' || role === 'Auditor',
      login,
      logout,
    };
  }, [auth, checking, login, logout]);

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth() {
  const context = useContext(AuthContext);
  if (!context) throw new Error('useAuth must be used inside <AuthProvider>');
  return context;
}
