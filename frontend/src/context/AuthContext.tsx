import {
    createContext,
    useCallback,
    useContext,
    useEffect,
    useMemo,
    useRef,
    useState,
    type ReactNode,
} from 'react';
import type { Role } from '../types';
import { ROLE_HIERARCHY } from '../utils/constants';
import { api, refreshAccessToken, setOnSessionExpired } from '../api/client';
import { tokenStorage } from '../api/tokenStorage';

export interface User {
    id: string | number;
    username: string;
    fullName: string;
    role: Role;
    department?: string;
}

interface LoginResponse {
    id: string;
    username: string;
    fullName: string;
    department: string;
    role: number;
    accessToken: string;
    token: string;
    refreshToken: string;
    expiresIn: number;
    accessTokenExpiresAt: string;
    refreshTokenExpiresAt: string;
}

interface AuthContextType {
    user: User | null;
    isAuthenticated: boolean;
    /** True cat timp se verifica sesiunea salvata la pornirea aplicatiei. */
    isInitializing: boolean;
    login: (username: string, password: string) => Promise<User>;
    logout: () => Promise<void>;
    /** Returneaza true daca utilizatorul curent are cel putin rolul cerut (ierarhic) */
    hasRole: (minRole: Role) => boolean;
}

// UserRole enum din backend: Utilizator=1, SefDirectie=2, Administrator=3
const ROLE_NUM_TO_STRING: Record<number, Role> = {
    1: 'UTILIZATOR',
    2: 'SEF_DIRECTIE',
    3: 'ADMINISTRATOR',
};

function normalizeUser(raw: LoginResponse): User {
    return {
        id: raw.id,
        username: raw.username,
        fullName: raw.fullName || raw.username,
        role: ROLE_NUM_TO_STRING[Number(raw.role)] ?? 'UTILIZATOR',
        department: raw.department || '',
    };
}

const AuthContext = createContext<AuthContextType | undefined>(undefined);

export function AuthProvider({ children }: { children: ReactNode }) {
    const [user, setUser] = useState<User | null>(() => {
        const saved = tokenStorage.getUserRaw();
        if (!saved) return null;
        try {
            return JSON.parse(saved) as User;
        } catch {
            return null;
        }
    });
    const [isInitializing, setIsInitializing] = useState(true);

    // Timer pentru reimprospatarea proactiva, inainte de expirare.
    const refreshTimer = useRef<number | null>(null);

    const clearSession = useCallback(() => {
        if (refreshTimer.current !== null) {
            window.clearTimeout(refreshTimer.current);
            refreshTimer.current = null;
        }
        tokenStorage.clear();
        setUser(null);
    }, []);

    /**
     * Programeaza un refresh cu 60s inainte de expirarea access token-ului.
     * Astfel utilizatorul nu vede niciodata un 401 in timpul lucrului.
     */
    const scheduleRefresh = useCallback(() => {
        if (refreshTimer.current !== null) {
            window.clearTimeout(refreshTimer.current);
            refreshTimer.current = null;
        }

        const expiresAt = tokenStorage.getAccessTokenExpiresAt();
        if (!expiresAt) return;

        const delay = Math.max(expiresAt - Date.now() - 60_000, 5_000);

        refreshTimer.current = window.setTimeout(async () => {
            try {
                await refreshAccessToken();
                scheduleRefresh();
            } catch {
                clearSession();
            }
        }, delay);
    }, [clearSession]);

    // Sesiunea a expirat definitiv (refresh respins) — semnalat de interceptor.
    useEffect(() => {
        setOnSessionExpired(() => {
            clearSession();
            if (window.location.pathname !== '/login') {
                window.location.href = '/login';
            }
        });
    }, [clearSession]);

    // La pornire: daca exista refresh token, incercam sa reluam sesiunea.
    useEffect(() => {
        let cancelled = false;

        (async () => {
            const refreshToken = tokenStorage.getRefreshToken();

            if (!refreshToken) {
                if (!cancelled) {
                    clearSession();
                    setIsInitializing(false);
                }
                return;
            }

            // Access token inca valid — nu deranjam serverul.
            if (!tokenStorage.isAccessTokenExpiring(60)) {
                if (!cancelled) {
                    scheduleRefresh();
                    setIsInitializing(false);
                }
                return;
            }

            try {
                await refreshAccessToken();
                if (!cancelled) scheduleRefresh();
            } catch {
                if (!cancelled) clearSession();
            } finally {
                if (!cancelled) setIsInitializing(false);
            }
        })();

        return () => {
            cancelled = true;
        };
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, []);

    // Curatare timer la unmount.
    useEffect(() => {
        return () => {
            if (refreshTimer.current !== null) window.clearTimeout(refreshTimer.current);
        };
    }, []);

    const login = useCallback(
        async (username: string, password: string): Promise<User> => {
            const { data } = await api.post<LoginResponse>('/Auth/login', { username, password });

            tokenStorage.save({
                accessToken: data.accessToken ?? data.token,
                refreshToken: data.refreshToken,
                accessTokenExpiresAt: new Date(data.accessTokenExpiresAt).getTime(),
            });

            const loggedUser = normalizeUser(data);
            tokenStorage.saveUserRaw(loggedUser);
            setUser(loggedUser);
            scheduleRefresh();

            return loggedUser;
        },
        [scheduleRefresh]
    );

    const logout = useCallback(async (): Promise<void> => {
        const refreshToken = tokenStorage.getRefreshToken();
        try {
            // Revocam refresh token-ul pe server; fara asta ar ramane valid 7 zile.
            await api.post('/Auth/logout', { refreshToken: refreshToken ?? '' });
        } catch {
            // Delogarea locala se face oricum.
        } finally {
            clearSession();
        }
    }, [clearSession]);

    const hasRole = useCallback(
        (minRole: Role): boolean => {
            if (!user) return false;
            const userLevel = ROLE_HIERARCHY[user.role as Role] ?? 0;
            const minLevel = ROLE_HIERARCHY[minRole] ?? 0;
            return userLevel >= minLevel;
        },
        [user]
    );

    const value = useMemo<AuthContextType>(
        () => ({
            user,
            isAuthenticated: !!user && !!tokenStorage.getAccessToken(),
            isInitializing,
            login,
            logout,
            hasRole,
        }),
        [user, isInitializing, login, logout, hasRole]
    );

    return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth() {
    const context = useContext(AuthContext);
    if (!context) throw new Error('useAuth trebuie folosit în interiorul AuthProvider');
    return context;
}