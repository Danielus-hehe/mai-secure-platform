/**
 * Stratul de acces la API pentru transferuri.
 *
 * Totul trece prin clientul `api` din client.ts, care atașează tokenul și îl
 * reîmprospătează automat. Singura excepție este descărcarea de la un URL
 * presemnat: acolo autentificarea este în semnătura din query string, iar un
 * antet Authorization în plus ar face depozitul să respingă cererea.
 */

import api from './client';
import type { TransferCryptoEnvelope } from '../crypto/E2ee';

// ── Tipuri ───────────────────────────────────────────────────────────────────

export interface PagedResult<T> {
    items: T[];
    totalCount: number;
    page: number;
    pageSize: number;
    totalPages: number;
    hasPrevious: boolean;
    hasNext: boolean;
}

export type TransferStatus = 'Pending' | 'Downloaded' | 'Expired' | 'Revoked';

export const TransferCategory = {
    Critical:  0,
    Important: 1,
    General:   2,
    Normal:    3,
} as const;

export type TransferCategory = typeof TransferCategory[keyof typeof TransferCategory];

export const CATEGORY_LABELS: Record<TransferCategory, string> = {
    [TransferCategory.Critical]:  'Critic',
    [TransferCategory.Important]: 'Important',
    [TransferCategory.General]:   'General',
    [TransferCategory.Normal]:    'Obișnuit',
};

export interface TransferListItem {
    id: string;
    fileName: string;
    fileSize: number;
    ciphertextSize: number;
    sha256: string;
    senderId: string;
    senderName: string;
    senderDepartment: string;
    recipientId: string;
    recipientName: string;
    recipientDepartment: string;
    status: TransferStatus;
    createdAt: string;
    downloadedAt: string | null;
    signatureValid: boolean | null;
    revokedAt: string | null;
    revokedReason: string | null;
    canRevoke: boolean;
    expiresAt: string | null;
    category: TransferCategory;
    isMine: boolean;
    isEncrypted: boolean;
    cryptoSuite: string | null;
}

export interface Recipient {
    id: string;
    username: string;
    fullName: string;
    department: string;
    publicKeyEncryption: string;
    publicKeySigning: string;
}

/** Rezultat al căutării pentru dialogul de forward (autocomplete). */
export interface UserSearchResult {
    id: string;
    username: string;
    fullName: string;
    department: string;
    /** Cheia publică RSA-OAEP (SPKI, base64) — necesară pentru împachetarea DEK în browser. */
    publicKeyEncryption: string;
}

export interface TransferEnvelopeResponse {
    id: string;
    fileName: string;
    fileSize: number;
    ciphertextSize: number;
    isEncrypted: boolean;
    iv: string;
    wrappedKeyForMe: string;
    signature: string;
    ciphertextSha256: string;
    suite: string;
    senderId: string;
    senderName: string;
    senderPublicKeySigning: string | null;
    downloadUrl: string | null;
    expiresAt: string | null;
}

export interface ListTransfersParams {
    search?: string;
    status?: string;
    direction?: 'sent' | 'received' | '';
    sortBy?: string;
    sortDir?: 'asc' | 'desc';
    page?: number;
    pageSize?: number;
}

export interface UploadInput {
    recipientId: string;
    fileName: string;
    plaintextSize: number;
    ciphertext: Blob;
    envelope: TransferCryptoEnvelope;
    category: TransferCategory;
    expiresAt?: string;
}

/** Un destinatar la care se redirecționează transferul. */
export interface ForwardRecipientInput {
    /** UUID al destinatarului. */
    userId: string;
    /** DEK-ul transferului împachetat cu cheia publică RSA-OAEP a lui userId. */
    encryptedKeyForUser: string;
}

// ── Operații ─────────────────────────────────────────────────────────────────

export async function listTransfers(
    params: ListTransfersParams,
    signal?: AbortSignal
): Promise<PagedResult<TransferListItem>> {
    const { data } = await api.get<PagedResult<TransferListItem>>('/Transfers', {
        params: {
            search:    params.search    || undefined,
            status:    params.status    || undefined,
            direction: params.direction || undefined,
            sortBy:    params.sortBy    ?? 'createdAt',
            sortDir:   params.sortDir   ?? 'desc',
            page:      params.page      ?? 1,
            pageSize:  params.pageSize  ?? 25,
        },
        signal,
    });
    return data;
}

/** Doar utilizatorii care și-au generat cheile pot primi fișiere criptate. */
export async function listRecipients(): Promise<Recipient[]> {
    const { data } = await api.get<Recipient[]>('/Keys/recipients');
    return data;
}

/**
 * Caută utilizatori activi cu chei generate, pentru autocomplete-ul de forward.
 * Returnează maxim 10 rezultate; include cheia publică de criptare.
 */
export async function searchUsers(
    q: string,
    signal?: AbortSignal
): Promise<UserSearchResult[]> {
    if (!q.trim()) return [];
    const { data } = await api.get<UserSearchResult[]>('/Users/search', {
        params: { q: q.trim() },
        signal,
    });
    return data;
}

export async function uploadTransfer(
    input: UploadInput,
    onProgress?: (percent: number) => void
): Promise<{ id: string; sha256: string; expiresAt: string }> {
    const form = new FormData();
    form.append('File', input.ciphertext, 'ciphertext.enc');
    form.append('RecipientId', input.recipientId);
    form.append('FileName', input.fileName);
    form.append('PlaintextSize', String(input.plaintextSize));
    form.append('Iv', input.envelope.iv);
    form.append('EncryptedKeyForRecipient', input.envelope.encryptedKeyForRecipient);
    form.append('EncryptedKeyForSender', input.envelope.encryptedKeyForSender);
    form.append('Signature', input.envelope.signature);
    form.append('CiphertextSha256', input.envelope.ciphertextSha256);
    form.append('Suite', input.envelope.suite);
    form.append('Category', String(input.category));
    if (input.expiresAt) form.append('ExpiresAt', input.expiresAt);

    const { data } = await api.post('/Transfers', form, {
        timeout: 300_000,
        onUploadProgress: (event) => {
            if (!onProgress || !event.total) return;
            onProgress(Math.round((event.loaded / event.total) * 100));
        },
    });
    return data;
}

/**
 * Redirecționează un transfer către destinatari suplimentari.
 *
 * `recipients` conține, pentru fiecare destinatar nou, DEK-ul deja împachetat
 * cu cheia lui publică RSA-OAEP — browserul face împachetarea, serverul nu
 * participă la niciun pas criptografic.
 */
export async function forwardTransfer(
    id: string,
    recipients: ForwardRecipientInput[]
): Promise<{ message: string; recipients: { id: string; name: string }[] }> {
    const { data } = await api.post(`/Transfers/${id}/forward`, { recipients });
    return data;
}

export async function getEnvelope(id: string): Promise<TransferEnvelopeResponse> {
    const { data } = await api.get<TransferEnvelopeResponse>(`/Transfers/${id}/envelope`);
    return data;
}

export async function fetchCiphertext(
    envelope: TransferEnvelopeResponse,
    onProgress?: (percent: number) => void
): Promise<ArrayBuffer> {
    if (envelope.downloadUrl) {
        const response = await fetch(envelope.downloadUrl);
        if (!response.ok) throw new Error(`Depozitul a răspuns cu ${response.status}.`);
        onProgress?.(100);
        return response.arrayBuffer();
    }
    const { data } = await api.get<ArrayBuffer>(`/Transfers/${envelope.id}/content`, {
        responseType: 'arraybuffer',
        timeout: 300_000,
        onDownloadProgress: (event) => {
            if (!onProgress || !event.total) return;
            onProgress(Math.round((event.loaded / event.total) * 100));
        },
    });
    return data;
}

export async function confirmTransfer(id: string, signatureValid: boolean): Promise<void> {
    await api.patch(`/Transfers/${id}/confirm`, { signatureValid });
}

export async function revokeTransfer(id: string, reason?: string): Promise<{ message: string }> {
    const { data } = await api.post<{ message: string }>(
        `/Transfers/${id}/revoke`,
        { reason: reason ?? null },
    );
    return data;
}

export async function deleteTransfer(id: string): Promise<void> {
    await api.delete(`/Transfers/${id}`);
}
