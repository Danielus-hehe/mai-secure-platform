import api from './client';
import { tokenStorage } from './tokenStorage';

/**
 * Client pentru /api/Sessions - sesiunile proprii și revocarea lor de la distanță.
 *
 * Toate apelurile trimit refresh token-ul curent, ca serverul să poată marca
 * „acest dispozitiv” și ca revocarea în masă să nu deconecteze chiar sesiunea
 * din care utilizatorul încearcă să-și securizeze contul.
 *
 * Tokenul pleacă spre server, dar nu iese din perimetrul aplicației: e același
 * token pe care clientul îl trimite oricum la fiecare /refresh.
 */

export interface SessionInfo {
    id: string;
    createdAt: string;
    lastSeenAt: string;
    expiresAt: string;
    revokedAt: string | null;
    revokedReason: string | null;
    ipAddress: string | null;

    /** Etichetă lizibilă, ex. „Chrome pe Windows”. */
    device: string;

    /** Șirul complet, pentru cine vrea să verifice în detaliu. */
    userAgent: string | null;

    isActive: boolean;

    /** Sesiunea din care se face cererea. Nu se poate revoca din listă. */
    isCurrent: boolean;
}

export const sessionsApi = {
    /** Sesiunile proprii: cele active întâi, apoi ultimele închise. */
    async list(): Promise<SessionInfo[]> {
        const refreshToken = tokenStorage.getRefreshToken();

        const { data } = await api.get<SessionInfo[]>('/Sessions', {
            params: refreshToken ? { currentRefreshToken: refreshToken } : undefined,
        });

        return data;
    },

    /** Închide o sesiune anume. */
    async revoke(id: string): Promise<{ message: string }> {
        const { data } = await api.delete<{ message: string }>(`/Sessions/${id}`);
        return data;
    },

    /**
     * Închide toate sesiunile în afară de cea curentă.
     *
     * Aruncă dacă nu există refresh token local: fără el serverul nu poate ști
     * ce sesiune să păstreze, iar utilizatorul s-ar deconecta singur.
     */
    async revokeOthers(): Promise<{ closed: number; message: string }> {
        const refreshToken = tokenStorage.getRefreshToken();

        if (!refreshToken) {
            throw new Error('Sesiunea curentă nu a putut fi identificată. Reautentificați-vă.');
        }

        const { data } = await api.delete<{ closed: number; message: string }>(
            '/Sessions/others',
            { data: { currentRefreshToken: refreshToken } },
        );

        return data;
    },
};

export default sessionsApi;
