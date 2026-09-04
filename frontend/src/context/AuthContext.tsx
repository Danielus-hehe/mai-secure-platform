import { createContext, useContext, useState, useCallback } from 'react';
import type { AuthSession, User, Role } from '../types';
import { ROLE_HIERARCHY } from '../utils/constants';
import { mockLogin } from '../api/mockAuth';

interface AuthContextValue {
    user: User | null;
    token: string | null;
    isAuthenticated: boolean;
    login: (username: string, password: string) => Promise<void>;
    logout: () => void;
    hasRole: (min: Role) => boolean;
}

const AuthContext = createContext<AuthContextValue | null>(null);

export function AuthProvider({ children }: { children: React.ReactNode }) {
    const [session, setSession] = useState<AuthSession | null>(() => {
        const raw = localStorage.getItem('mai_session');
        if (!raw) return null;
        const s: AuthSession = JSON.parse(raw);
        return s.expiresAt > Date.now() ? s : null;
    });

    const login = useCallback(async (username: string, password: string) => {
        // TODO producție: const { data } = await api.post<AuthSession>('/auth/login', {...});
        const data = await mockLogin(username, password);
        localStorage.setItem('mai_session', JSON.stringify(data));
        localStorage.setItem('mai_access_token', data.accessToken);
        setSession(data);
    }, []);

    const logout = useCallback(() => {
        localStorage.removeItem('mai_session');
        localStorage.removeItem('mai_access_token');
        setSession(null);
    }, []);

    const hasRole = useCallback(
        (min: Role) => !!session && ROLE_HIERARCHY[session.user.role] >= ROLE_HIERARCHY[min],
        [session]
    );

    return (
        <AuthContext.Provider
            value={{
                user: session?.user ?? null,
                token: session?.accessToken ?? null,
                isAuthenticated: !!session,
                login,
                logout,
                hasRole,
            }}
        >
            {children}
        </AuthContext.Provider>
    );
}

export function useAuth() {
    const ctx = useContext(AuthContext);
    if (!ctx) throw new Error('useAuth trebuie folosit în interiorul AuthProvider');
    return ctx;
}