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

// ── Refresh: o singura rotatie odata, in toata aplicatia ────────────────────
//
// Doua niveluri de serializare, pentru doua probleme diferite:
//
//   1. In aceeasi fila (refreshPromise): daca 5 cereri primesc 401 simultan,
//      se face UN singur apel /refresh si toate asteapta acelasi rezultat.
//
//   2. Intre file (navigator.locks): tokenurile stau in localStorage, comun
//      tuturor filelor aceleiasi origini. Inainte, doua file care expirau in
//      acelasi minut trimiteau ACELASI refresh token. Serverul il roteste la
//      prima cerere, deci a doua primea 401, iar handleSessionExpired() golea
//      localStorage - stergand si tokenurile proaspete ale primei file. Rezultat:
//      ambele file deconectate, exact cand utilizatorul avea mai multe deschise
//      (de exemplu, in timpul unei demonstratii).
//
//      Cu lacatul, filele se rotesc pe rand, iar fiecare citeste refresh
//      token-ul DUPA ce a obtinut lacatul. Daca intre timp alta fila a rotit
//      deja sesiunea, fila curenta foloseste direct tokenul ei, fara un apel nou.
//
// Browserele fara Web Locks (foarte vechi) raman pe comportamentul de dinainte,
// serializat doar in fila curenta.

const REFRESH_LOCK = 'sgdm-token-refresh';

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

/**
 * Rotatia propriu-zisa. Ruleaza sub lacat, deci citirea tokenului si
 * inlocuirea lui nu se intrepatrund cu ale altei file.
 *
 * @param seenRefreshToken refresh token-ul vazut INAINTE de asteptarea
 *        lacatului. Daca in storage e acum altul, alta fila a rotit sesiunea.
 */
async function rotateTokens(seenRefreshToken: string | null): Promise<string> {
    const refreshToken = tokenStorage.getRefreshToken();
    if (!refreshToken) throw new Error('Nu exista refresh token.');

    if (seenRefreshToken && refreshToken !== seenRefreshToken) {
        const accessToken = tokenStorage.getAccessToken();
        // Tokenul proaspat al celeilalte file e bun daca nu expira chiar acum.
        if (accessToken && !tokenStorage.isAccessTokenExpiring(10)) {
            return accessToken;
        }
    }

    const { data } = await rawClient.post<RefreshResponse>('/Auth/refresh', { refreshToken });

    tokenStorage.save({
        accessToken: data.accessToken ?? data.token,
        refreshToken: data.refreshToken,
        accessTokenExpiresAt: new Date(data.accessTokenExpiresAt).getTime(),
    });

    return data.accessToken ?? data.token;
}

export async function refreshAccessToken(): Promise<string> {
    if (refreshPromise) return refreshPromise;

    const seen = tokenStorage.getRefreshToken();
    const locks = typeof navigator !== 'undefined' ? navigator.locks : undefined;

    refreshPromise = locks
        ? locks.request(REFRESH_LOCK, () => rotateTokens(seen))
        : rotateTokens(seen);

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