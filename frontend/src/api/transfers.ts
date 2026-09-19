/**
 * Stratul de acces la API pentru transferuri.
 *
 * Totul trece prin clientul `api` din client.ts, care atașează tokenul și îl
 * reîmprospătează automat. Singura excepție este descărcarea de la un URL
 * presemnat: acolo autentificarea este în semnătura din query string, iar un
 * antet Authorization în plus ar face depozitul să respingă cererea.
 */

import api from './client';
import type { TransferCryptoEnvelope, WrappedKeyForUser } from '../crypto/E2ee';

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

/** Un destinatar al transferului, cu dovada lui de primire. */
export interface TransferRecipientItem {
    userId: string;
    name: string;
    department: string;
    sentAt: string;
    /** Null = destinatar direct, ales de expeditor la trimitere. */
    forwardedById: string | null;
    forwardedByName: string | null;
    /**
     * False când utilizatorul curent nu are dreptul să vadă confirmarea
     * acestui rând (e doar un alt destinatar). Atunci downloadedAt și
     * signatureValid vin null - „necunoscut”, nu „nedescărcat”.
     */
    receiptVisible: boolean;
    downloadedAt: string | null;
    signatureValid: boolean | null;
}

export interface TransferListItem {
    id: string;
    fileName: string;
    fileSize: number;
    ciphertextSize: number;
    sha256: string;
    senderId: string;
    senderName: string;
    senderDepartment: string;
    status: TransferStatus;
    createdAt: string;
    expiresAt: string | null;
    revokedAt: string | null;
    revokedReason: string | null;
    category: TransferCategory;
    allowForward: boolean;
    isMine: boolean;
    isEncrypted: boolean;
    cryptoSuite: string | null;

    recipients: TransferRecipientItem[];
    recipientCount: number;
    /** Doar pentru expeditor; null pentru destinatari. */
    downloadedCount: number | null;
    isRecipient: boolean;
    myDownloadedAt: string | null;
    mySignatureValid: boolean | null;

    // Acțiuni permise, calculate de server.
    canDownload: boolean;
    canForward: boolean;
    canRevoke: boolean;
    canDelete: boolean;
}

export interface Recipient {
    id: string;
    username: string;
    fullName: string;
    department: string;
    publicKeyEncryption: string;
    publicKeySigning: string;
}

export interface TransferPolicy {
    defaultExpiryDays: number;
    maxExpiryDays: number;
    maxRecipients: number;
}

/** Valorile folosite până răspunde serverul; identice cu cele implicite din API. */
export const DEFAULT_TRANSFER_POLICY: TransferPolicy = {
    defaultExpiryDays: 7,
    maxExpiryDays: 30,
    maxRecipients: 20,
};

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
    /** True dacă utilizatorul curent e destinatar (nu expeditor). */
    isRecipient: boolean;
    alreadyConfirmed: boolean;
}

export interface ListTransfersParams {
    search?: string;
    status?: string;
    direction?: 'sent' | 'received' | '';
    category?: TransferCategory | '';
    /** Cealaltă parte a transferului lucrează în subdiviziune (cu subunități). */
    orgUnitId?: string;
    sortBy?: string;
    sortDir?: 'asc' | 'desc';
    page?: number;
    pageSize?: number;
}

export interface UploadInput {
    recipientKeys: WrappedKeyForUser[];
    fileName: string;
    plaintextSize: number;
    ciphertext: Blob;
    envelope: TransferCryptoEnvelope;
    category: TransferCategory;
    allowForward: boolean;
    /** ISO 8601 în UTC (cu „Z”). */
    expiresAt?: string;
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
            category:  params.category === '' || params.category === undefined ? undefined : params.category,
            orgUnitId: params.orgUnitId || undefined,
            sortBy:    params.sortBy    ?? 'createdAt',
            sortDir:   params.sortDir   ?? 'desc',
            page:      params.page      ?? 1,
            pageSize:  params.pageSize  ?? 25,
        },
        signal,
    });
    return data;
}

/** Câte fișiere primite nu am descărcat încă. */
export async function getAwaitingCount(): Promise<number> {
    const { data } = await api.get<{ count: number }>('/Transfers/awaiting-count');
    return data.count;
}

export async function getTransferPolicy(): Promise<TransferPolicy> {
    const { data } = await api.get<TransferPolicy>('/Transfers/policy');
    return data;
}

/** Doar utilizatorii care și-au generat cheile pot primi fișiere criptate. */
export async function listRecipients(): Promise<Recipient[]> {
    const { data } = await api.get<Recipient[]>('/Keys/recipients');
    return data;
}

export async function uploadTransfer(
    input: UploadInput,
    onProgress?: (percent: number) => void
): Promise<{ id: string; sha256: string; expiresAt: string; message: string }> {
    const form = new FormData();
    form.append('File', input.ciphertext, 'ciphertext.enc');
    form.append('FileName', input.fileName);
    form.append('PlaintextSize', String(input.plaintextSize));
    form.append('Iv', input.envelope.iv);
    form.append('EncryptedKeyForSender', input.envelope.encryptedKeyForSender);
    form.append('Signature', input.envelope.signature);
    form.append('CiphertextSha256', input.envelope.ciphertextSha256);
    form.append('Suite', input.envelope.suite);
    form.append('Category', String(input.category));
    form.append('AllowForward', String(input.allowForward));
    if (input.expiresAt) form.append('ExpiresAt', input.expiresAt);

    // Lista de destinatari în formatul pe care îl leagă model binding-ul
    // ASP.NET: Recipients[0].UserId, Recipients[0].EncryptedKeyForUser, …
    input.recipientKeys.forEach((r, i) => {
        form.append(`Recipients[${i}].UserId`, r.userId);
        form.append(`Recipients[${i}].EncryptedKeyForUser`, r.encryptedKeyForUser);
    });

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
 * cu cheia lui publică RSA-OAEP (vezi rewrapFileKey în E2ee.ts).
 */
export async function forwardTransfer(
    id: string,
    recipients: WrappedKeyForUser[]
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

/** Ștergere logică: dovezile de primire rămân în bază și în jurnal. */
export async function deleteTransfer(id: string): Promise<{ message: string }> {
    const { data } = await api.delete<{ message: string }>(`/Transfers/${id}`);
    return data;
}
