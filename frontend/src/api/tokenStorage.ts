/**
 * Sursa unica de adevar pentru tokenuri si utilizatorul curent.
 *
 * Inainte, `client.ts` citea `mai_access_token` iar `AuthContext` scria in `user` -
 * doua chei diferite, deci cererile nu purtau header-ul Authorization. Aici totul
 * trece printr-un singur modul.
 *
 * Nota de securitate: localStorage este vulnerabil la XSS. Varianta corecta pentru
 * productie e refresh token in cookie httpOnly + SameSite=Strict, setat de backend.
 * Pentru intranet-ul de practica ramanem pe localStorage, dar merita mentionat in
 * documentatie ca limitare cunoscuta.
 */

const ACCESS_TOKEN_KEY = 'mai_access_token';
const REFRESH_TOKEN_KEY = 'mai_refresh_token';
const ACCESS_EXPIRES_KEY = 'mai_access_expires_at';
const USER_KEY = 'user';

export interface StoredSession {
    accessToken: string;
    refreshToken: string;
    /** Timestamp (ms) al expirarii access token-ului. */
    accessTokenExpiresAt: number;
}

export const tokenStorage = {
    getAccessToken(): string | null {
        return localStorage.getItem(ACCESS_TOKEN_KEY);
    },

    getRefreshToken(): string | null {
        return localStorage.getItem(REFRESH_TOKEN_KEY);
    },

    getAccessTokenExpiresAt(): number | null {
        const raw = localStorage.getItem(ACCESS_EXPIRES_KEY);
        return raw ? Number(raw) : null;
    },

    /** True daca access token-ul expira in mai putin de `marginSeconds`. */
    isAccessTokenExpiring(marginSeconds = 60): boolean {
        const expiresAt = this.getAccessTokenExpiresAt();
        if (!expiresAt) return false;
        return Date.now() >= expiresAt - marginSeconds * 1000;
    },

    save(session: StoredSession): void {
        localStorage.setItem(ACCESS_TOKEN_KEY, session.accessToken);
        localStorage.setItem(REFRESH_TOKEN_KEY, session.refreshToken);
        localStorage.setItem(ACCESS_EXPIRES_KEY, String(session.accessTokenExpiresAt));
    },

    clear(): void {
        localStorage.removeItem(ACCESS_TOKEN_KEY);
        localStorage.removeItem(REFRESH_TOKEN_KEY);
        localStorage.removeItem(ACCESS_EXPIRES_KEY);
        localStorage.removeItem(USER_KEY);
    },

    getUserRaw(): string | null {
        return localStorage.getItem(USER_KEY);
    },

    saveUserRaw(value: unknown): void {
        localStorage.setItem(USER_KEY, JSON.stringify(value));
    },
};

/**
 * Punte de compatibilitate pentru paginile care inca folosesc fetch() direct
 * si citeau tokenul din obiectul user (`user?.token`). Acel camp nu mai exista.
 *
 * Foloseste-o asa:
 *   const res = await fetch(url, { headers: getAuthHeader() });
 *
 * Atentie: fetch() direct NU beneficiaza de refresh automat pe 401. Solutia
 * corecta pe termen lung e migrarea paginilor pe clientul `api` din client.ts,
 * care are interceptoarele. Helperul asta le tine functionale pana atunci.
 */
export function getAuthHeader(): Record<string, string> {
    const token = tokenStorage.getAccessToken();
    return token ? { Authorization: `Bearer ${token}` } : {};
}

export { ACCESS_TOKEN_KEY, REFRESH_TOKEN_KEY, ACCESS_EXPIRES_KEY, USER_KEY };