import axios, {
    AxiosError,
    type AxiosInstance,
    type InternalAxiosRequestConfig,
} from 'axios';
import { tokenStorage } from './tokenStorage';

const BASE_URL = import.meta.env.VITE_API_URL ?? 'http://localhost:5000/api';

/** Rutele care nu trebuie sa declanseze niciodata logica de refresh. */
const AUTH_FREE_PATHS = ['/Auth/login', '/Auth/refresh', '/Auth/logout'];

export const api: AxiosInstance = axios.create({
    baseURL: BASE_URL,
    timeout: 30_000,
});

/** Client separat, fara interceptoare - altfel refresh-ul s-ar apela recursiv pe el insusi. */
const rawClient: AxiosInstance = axios.create({ baseURL: BASE_URL, timeout: 30_000 });

interface RetriableConfig extends InternalAxiosRequestConfig {
    _retried?: boolean;
}

export interface RefreshResponse {
    accessToken: string;
    token: string;
    refreshToken: string;
    expiresIn: number;
    accessTokenExpiresAt: string;
    refreshTokenExpiresAt: string;
}

// ── Refresh single-flight ────────────────────────────────────────────────────
// Daca 5 cereri primesc 401 in acelasi timp, se face UN singur apel /refresh
// si toate cele 5 asteapta acelasi rezultat. Fara asta, fiecare ar consuma
// refresh token-ul, iar rotatia din backend le-ar invalida pe celelalte patru.

let refreshPromise: Promise<string> | null = null;

/** Apelat cand sesiunea nu mai poate fi salvata. Setat de AuthContext. */
let onSessionExpired: (() => void) | null = null;

export function setOnSessionExpired(handler: () => void): void {
    onSessionExpired = handler;
}

function handleSessionExpired(): void {
    tokenStorage.clear();
    if (onSessionExpired) {
        onSessionExpired();
    } else if (window.location.pathname !== '/login') {
        window.location.href = '/login';
    }
}

export async function refreshAccessToken(): Promise<string> {
    if (refreshPromise) return refreshPromise;

    refreshPromise = (async () => {
        const refreshToken = tokenStorage.getRefreshToken();
        if (!refreshToken) throw new Error('Nu exista refresh token.');

        const { data } = await rawClient.post<RefreshResponse>('/Auth/refresh', { refreshToken });

        tokenStorage.save({
            accessToken: data.accessToken ?? data.token,
            refreshToken: data.refreshToken,
            accessTokenExpiresAt: new Date(data.accessTokenExpiresAt).getTime(),
        });

        return data.accessToken ?? data.token;
    })();

    try {
        return await refreshPromise;
    } finally {
        refreshPromise = null;
    }
}

// ── Request: ataseaza tokenul, il reimprospateaza preventiv ─────────────────

api.interceptors.request.use(async (config) => {
    const path = config.url ?? '';
    const isAuthFree = AUTH_FREE_PATHS.some((p) => path.includes(p));

    if (!isAuthFree && tokenStorage.getRefreshToken() && tokenStorage.isAccessTokenExpiring(60)) {
        // Reimprospatare preventiva: evitam un 401 inutil daca stim ca tokenul
        // expira in urmatoarele 60 de secunde.
        try {
            await refreshAccessToken();
        } catch {
            // Lasam cererea sa plece; daca da 401, se trateaza mai jos.
        }
    }

    const token = tokenStorage.getAccessToken();
    if (token && !isAuthFree) {
        config.headers.Authorization = `Bearer ${token}`;
    }
    return config;
});

// ── Response: pe 401, incearca refresh o singura data, apoi reia cererea ────

api.interceptors.response.use(
    (res) => res,
    async (error: AxiosError) => {
        const config = error.config as RetriableConfig | undefined;
        const status = error.response?.status;

        if (status !== 401 || !config) return Promise.reject(error);

        const path = config.url ?? '';
        if (AUTH_FREE_PATHS.some((p) => path.includes(p))) return Promise.reject(error);

        // O singura reincercare per cerere - altfel intram in bucla infinita.
        if (config._retried) {
            handleSessionExpired();
            return Promise.reject(error);
        }

        if (!tokenStorage.getRefreshToken()) {
            handleSessionExpired();
            return Promise.reject(error);
        }

        config._retried = true;

        try {
            const newToken = await refreshAccessToken();
            config.headers.Authorization = `Bearer ${newToken}`;
            return api.request(config);
        } catch {
            handleSessionExpired();
            return Promise.reject(error);
        }
    }
);

export default api;