import api from './client';

/**
 * Client pentru /api/TwoFactor si pentru pasul doi de la /api/Auth.
 *
 * 2FA este optional. Nimic din modulul asta nu se apeleaza automat - utilizatorul
 * il porneste singur din pagina de profil, iar conturile care nu l-au activat nu
 * ating niciodata codul de aici.
 */

export interface TwoFactorStatus {
    enabled: boolean;
    enrolledAt: string | null;
    remainingRecoveryCodes: number;
    /** Exista un secret generat dar neconfirmat. */
    setupInProgress: boolean;
    issuer: string;
    digits: number;
    periodSeconds: number;
    /** Rolurile privilegiate primesc o recomandare in interfata, nu o obligatie. */
    recommended: boolean;
}

export interface TwoFactorSetup {
    /** Secretul Base32. Iese din server o singura data, aici. */
    secret: string;
    /** Acelasi secret, in grupuri de 4, pentru introducere manuala. */
    secretFormatted: string;
    /** URI-ul otpauth:// din care se deseneaza codul QR. */
    otpauthUri: string;
    digits: number;
    periodSeconds: number;
    issuer: string;
    account: string;
}

export interface RecoveryCodesResult {
    message: string;
    recoveryCodes: string[];
    warning?: string;
}

/** Raspunsul de la /Auth/login cand contul are 2FA activ. */
export interface TwoFactorChallenge {
    twoFactorRequired: true;
    challengeToken: string;
    expiresAt: string;
    recoveryAvailable: boolean;
}

/** Discriminare la runtime intre sesiune completa si provocare 2FA. */
export function isTwoFactorChallenge(value: unknown): value is TwoFactorChallenge {
    return !!value
        && typeof value === 'object'
        && (value as { twoFactorRequired?: unknown }).twoFactorRequired === true;
}

// ── Gestiune din profil ─────────────────────────────────────────────────────

export async function getTwoFactorStatus(signal?: AbortSignal): Promise<TwoFactorStatus> {
    const { data } = await api.get<TwoFactorStatus>('/TwoFactor/status', { signal });
    return data;
}

/** Pasul 1: genereaza secretul in asteptare. Cere parola. */
export async function startTwoFactorSetup(password: string): Promise<TwoFactorSetup> {
    const { data } = await api.post<TwoFactorSetup>('/TwoFactor/setup', { password });
    return data;
}

/** Pasul 2: confirma cu un cod din aplicatie si activeaza. Returneaza codurile de recuperare. */
export async function enableTwoFactor(code: string): Promise<RecoveryCodesResult> {
    const { data } = await api.post<RecoveryCodesResult>('/TwoFactor/enable', { code });
    return data;
}

export async function cancelTwoFactorSetup(): Promise<void> {
    await api.post('/TwoFactor/cancel-setup');
}

/** Dezactivare. Cere parola SI un cod valid - nu doar parola. */
export async function disableTwoFactor(password: string, code: string): Promise<{ message: string }> {
    const { data } = await api.post<{ message: string }>('/TwoFactor/disable', { password, code });
    return data;
}

export async function regenerateRecoveryCodes(
    password: string, code: string,
): Promise<RecoveryCodesResult> {
    const { data } = await api.post<RecoveryCodesResult>('/TwoFactor/recovery-codes', { password, code });
    return data;
}

/** Deblocare administrativa pentru un coleg care si-a pierdut telefonul si codurile. */
export async function adminResetTwoFactor(userId: string): Promise<{ message: string }> {
    const { data } = await api.post<{ message: string }>(`/TwoFactor/admin/reset/${userId}`);
    return data;
}