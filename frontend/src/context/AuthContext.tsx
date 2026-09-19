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
import { isTwoFactorChallenge, type TwoFactorChallenge } from '../api/twoFactor';

export interface User {
    /**
     * Guid din backend, mereu string. Era `string | number`, ceea ce obliga
     * fiecare comparatie de identitate la o conversie defensiva - si lasa loc
     * ca `user.id === transfer.senderId` sa fie fals doar pentru ca unul era
     * numar si celalalt sir.
     */
    id: string;
    username: string;
    fullName: string;
    role: Role;
    department?: string;
    /**
     * Parola a fost stabilită de un administrator (cont nou sau resetare).
     * PasswordChangeGate cere schimbarea ei înaintea oricărei alte acțiuni;
     * serverul refuză oricum înregistrarea cheilor E2EE până atunci.
     */
    mustChangePassword?: boolean;
    /**
     * Rolul cere 2FA (TwoFactor:RequiredForPrivilegedRoles), dar sesiunea nu a
     * fost deschisă cu al doilea factor. Paginile privilegiate primesc 403.
     */
    mfaEnrollmentRequired?: boolean;
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
    mustChangePassword?: boolean;
    mfaEnrollmentRequired?: boolean;
    /** Prezente doar cand autentificarea s-a incheiat prin pasul 2FA. */
    usedRecoveryCode?: boolean;
    remainingRecoveryCodes?: number;
}

/**
 * Rezultatul unui login.
 *
 * `login` nu mai returneaza intotdeauna un utilizator: daca acel cont si-a
 * activat 2FA, returneaza provocarea, iar sesiunea se deschide abia dupa
 * `verifyTwoFactor`. Tipul uniune obliga apelantul sa trateze ambele cazuri -
 * un `User` returnat direct ar fi ascuns pasul lipsa in tipuri si l-ar fi
 * transformat intr-un bug la runtime.
 */
export type LoginResult =
    | { kind: 'session'; user: User }
    | { kind: 'twoFactor'; challenge: TwoFactorChallenge };

export interface TwoFactorSuccess {
    user: User;
    usedRecoveryCode: boolean;
    remainingRecoveryCodes: number;
}

interface AuthContextType {
    user: User | null;
    isAuthenticated: boolean;
    /** True cat timp se verifica sesiunea salvata la pornirea aplicatiei. */
    isInitializing: boolean;
    login: (username: string, password: string) => Promise<LoginResult>;
    /** Pasul doi. Se apeleaza doar daca `login` a returnat kind: 'twoFactor'. */
    verifyTwoFactor: (challengeToken: string, code: string) => Promise<TwoFactorSuccess>;
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
        id: String(raw.id),
        username: raw.username,
        fullName: raw.fullName || raw.username,
        role: ROLE_NUM_TO_STRING[Number(raw.role)] ?? 'UTILIZATOR',
        department: raw.department || '',
        mustChangePassword: raw.mustChangePassword === true,
        mfaEnrollmentRequired: raw.mfaEnrollmentRequired === true,
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

    // Sesiunea a expirat definitiv (refresh respins) - semnalat de interceptor.
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

            // Access token inca valid - nu deranjam serverul.
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

    /** Comun intre login direct si login incheiat prin 2FA. */
    const openSession = useCallback(
        (data: LoginResponse): User => {
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

    const login = useCallback(
        async (username: string, password: string): Promise<LoginResult> => {
            const { data } = await api.post<LoginResponse | TwoFactorChallenge>(
                '/Auth/login', { username, password }
            );

            // Contul are 2FA activ: parola a fost corecta, dar nu s-a emis niciun
            // token. Nu salvam absolut nimic in localStorage in acest punct -
            // pana la codul corect, nu exista sesiune.
            if (isTwoFactorChallenge(data)) {
                return { kind: 'twoFactor', challenge: data };
            }

            return { kind: 'session', user: openSession(data as LoginResponse) };
        },
        [openSession]
    );

    const verifyTwoFactor = useCallback(
        async (challengeToken: string, code: string): Promise<TwoFactorSuccess> => {
            const { data } = await api.post<LoginResponse>(
                '/Auth/2fa/verify', { challengeToken, code }
            );

            return {
                user: openSession(data),
                usedRecoveryCode: data.usedRecoveryCode ?? false,
                remainingRecoveryCodes: data.remainingRecoveryCodes ?? 0,
            };
        },
        [openSession]
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
            verifyTwoFactor,
            logout,
            hasRole,
        }),
        [user, isInitializing, login, verifyTwoFactor, logout, hasRole]
    );

    return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth() {
    const context = useContext(AuthContext);
    if (!context) throw new Error('useAuth trebuie folosit în interiorul AuthProvider');
    return context;
}