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

/** Statusurile returnate de backend (TransferStatus.ToString()). */
export type TransferStatus = 'Pending' | 'Downloaded' | 'Expired' | 'Revoked';

/**
 * Categoria unui transfer — oglindește enum-ul TransferCategory din backend.
 * Folosim const object în loc de enum: erasableSyntaxOnly interzice enum-urile
 * care emit cod JavaScript la runtime.
 */
export const TransferCategory = {
    Critical:  0,
    Important: 1,
    General:   2,
    Normal:    3,
} as const;

/** Tipul numeric al categoriei: 0 | 1 | 2 | 3. */
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

    // ── Dovada de primire ────────────────────────────────────────────────────

    /** Cand a descarcat destinatarul. null = inca nu. */
    downloadedAt: string | null;

    /**
     * Ce a raportat browserul destinatarului la verificarea semnaturii.
     * null pentru transferurile necriptate sau nedescarcate inca — stare
     * diferita de false, care inseamna „descarcat, dar semnatura nu s-a verificat".
     */
    signatureValid: boolean | null;

    // ── Retragere ────────────────────────────────────────────────────────────

    revokedAt: string | null;
    revokedReason: string | null;

    /**
     * Daca utilizatorul curent poate retrage acest transfer chiar acum.
     * Calculat server-side: butonul din interfata si verificarea din endpoint nu
     * trebuie sa poata diverge.
     */
    canRevoke: boolean;

    // ── Expirare și categorie ────────────────────────────────────────────────

    expiresAt: string | null;

    /** Categoria transferului (0=Critical … 3=Normal). */
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

export interface TransferEnvelopeResponse {
    id: string;
    fileName: string;
    fileSize: number;
    ciphertextSize: number;
    isEncrypted: boolean;
    iv: string;
    /** Cheia de fișier împachetată pentru contul curent — nu pentru celălalt. */
    wrappedKeyForMe: string;
    signature: string;
    ciphertextSha256: string;
    suite: string;
    senderId: string;
    senderName: string;
    senderPublicKeySigning: string | null;
    /** URL temporar către depozit. Null → se folosește ruta /content prin API. */
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
    /** Categoria selectată de expeditor. Implicit: General. */
    category: TransferCategory;
    /** Data de expirare aleasă (ISO 8601). Dacă lipsește, backend aplică 7 zile. */
    expiresAt?: string;
}

// ── Operații ─────────────────────────────────────────────────────────────────

export async function listTransfers(
    params: ListTransfersParams,
    signal?: AbortSignal
): Promise<PagedResult<TransferListItem>> {
    const { data } = await api.get<PagedResult<TransferListItem>>('/Transfers', {
        params: {
            search: params.search || undefined,
            status: params.status || undefined,
            direction: params.direction || undefined,
            sortBy: params.sortBy ?? 'createdAt',
            sortDir: params.sortDir ?? 'desc',
            page: params.page ?? 1,
            pageSize: params.pageSize ?? 25,
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

export async function uploadTransfer(
    input: UploadInput,
    onProgress?: (percent: number) => void
): Promise<{ id: string; sha256: string; expiresAt: string }> {
    const form = new FormData();

    // Numele blobului nu contează: numele real merge separat, în FileName.
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

    // Câmpuri noi: categorie și expirare
    form.append('Category', String(input.category));
    if (input.expiresAt) {
        form.append('ExpiresAt', input.expiresAt);
    }

    const { data } = await api.post('/Transfers', form, {
        timeout: 300_000,
        onUploadProgress: (event) => {
            if (!onProgress || !event.total) return;
            onProgress(Math.round((event.loaded / event.total) * 100));
        },
    });

    return data;
}

export async function getEnvelope(id: string): Promise<TransferEnvelopeResponse> {
    const { data } = await api.get<TransferEnvelopeResponse>(`/Transfers/${id}/envelope`);
    return data;
}

/**
 * Aduce cifrotextul. Preferă URL-ul presemnat: octeții vin direct din depozit,
 * fără să treacă prin API. Dacă providerul nu suportă (filesystem local) sau
 * funcția e dezactivată din configurare, cade pe ruta /content.
 */
export async function fetchCiphertext(
    envelope: TransferEnvelopeResponse,
    onProgress?: (percent: number) => void
): Promise<ArrayBuffer> {
    if (envelope.downloadUrl) {
        const response = await fetch(envelope.downloadUrl);
        if (!response.ok) {
            throw new Error(`Depozitul a răspuns cu ${response.status}.`);
        }
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

/** Se apelează DUPĂ decriptare reușită, cu rezultatul verificării semnăturii. */
export async function confirmTransfer(id: string, signatureValid: boolean): Promise<void> {
    await api.patch(`/Transfers/${id}/confirm`, { signatureValid });
}

/**
 * Retrage un transfer inainte ca destinatarul sa il descarce.
 */
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