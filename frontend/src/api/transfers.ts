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
export type TransferStatus = 'Pending' | 'Downloaded' | 'Expired';

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
    expiresAt: string | null;
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

    const { data } = await api.post('/Transfers', form, {
        // Uploadul unui fișier de 50 MB pe o rețea lentă depășește ușor timeoutul
        // implicit de 30 s al clientului.
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
        // fetch simplu, FĂRĂ antetul Authorization: autorizarea e deja în
        // semnătura URL-ului, iar un antet în plus ar invalida cererea.
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

export async function deleteTransfer(id: string): Promise<void> {
    await api.delete(`/Transfers/${id}`);
}