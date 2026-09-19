/**
 * Operații pe conturi folosite în mai multe pagini (gestiunea utilizatorilor,
 * structura organizatorică, documentele interne).
 */

import api from './client';
import type { PickableUser } from '../components/transfers/RecipientCombobox';

/** Un cont din directorul intern (GET /Users/all) — fără email, rol sau stare. */
export interface DirectoryUser extends PickableUser {
    orgUnitId: string | null;
}

export async function listDirectory(): Promise<DirectoryUser[]> {
    const { data } = await api.get<DirectoryUser[]>('/Users/all');
    return data;
}

/** Trimite pe email un link de resetare a parolei. Parola actuală rămâne valabilă până atunci. */
export async function sendPasswordResetLink(userId: string): Promise<{ message: string; expiresAt: string }> {
    const { data } = await api.post(`/Users/${userId}/send-password-reset`);
    return data;
}

/** Varianta fără email: administratorul stabilește o parolă temporară. */
export async function resetPasswordTemporary(
    userId: string,
    newPassword: string
): Promise<{ message: string; keysInvalidated: boolean }> {
    const { data } = await api.post(`/Users/${userId}/reset-password`, { newPassword });
    return data;
}

export async function changeUserOrgUnit(
    userId: string,
    orgUnitId: string | null
): Promise<{ message: string; headReleased: boolean }> {
    const { data } = await api.patch(`/Users/${userId}/org-unit`, { orgUnitId });
    return data;
}

// ── Link de resetare (pagina publică /reset-password) ───────────────────────

export interface PasswordResetTokenInfo {
    username: string;
    fullName: string;
    hasKeys: boolean;
    expiresAt: string;
}

export async function checkPasswordResetToken(token: string): Promise<PasswordResetTokenInfo> {
    const { data } = await api.get<PasswordResetTokenInfo>('/Auth/check-password-reset', { params: { token } });
    return data;
}

export async function completePasswordReset(
    token: string,
    newPassword: string,
    confirmPassword: string
): Promise<{ message: string; keysCleared: boolean }> {
    const { data } = await api.post('/Auth/reset-password', { token, newPassword, confirmPassword });
    return data;
}
