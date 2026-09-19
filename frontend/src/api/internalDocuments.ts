/**
 * Documente interne: distribuție pe structura organizatorică, cu confirmare
 * „Luat la cunoștință” și raport pentru autor.
 */

import api from './client';
import type { PagedResult } from './transfers';

export const InternalDocumentStatus = {
    Draft:     0,
    Published: 1,
    Repealed:  2,
} as const;

export type InternalDocumentStatus = typeof InternalDocumentStatus[keyof typeof InternalDocumentStatus];

export const DistributionMode = {
    MyUnitTree:         0,
    DirectSubordinates: 1,
    SelectedUnits:      2,
    UnitHeads:          3,
    SpecificUsers:      4,
    WholeInstitution:   5,
} as const;

export type DistributionMode = typeof DistributionMode[keyof typeof DistributionMode];

export const DISTRIBUTION_LABELS: Record<DistributionMode, { title: string; hint: string }> = {
    [DistributionMode.MyUnitTree]: {
        title: 'Subdiviziunea mea',
        hint:  'Toți membrii subdiviziunii pe care o conduceți, inclusiv ai subunităților.',
    },
    [DistributionMode.DirectSubordinates]: {
        title: 'Doar subordonații direcți',
        hint:  'Membrii subdiviziunii conduse (fără subunități) și șefii subunităților imediate.',
    },
    [DistributionMode.SelectedUnits]: {
        title: 'Subdiviziuni selectate',
        hint:  'Alegeți subdiviziunile; opțional cu subunitățile lor.',
    },
    [DistributionMode.UnitHeads]: {
        title: 'Doar șefii subunităților',
        hint:  'Conducătorii tuturor subunităților din subordine.',
    },
    [DistributionMode.SpecificUsers]: {
        title: 'Persoane anume',
        hint:  'Alegeți nominal destinatarii.',
    },
    [DistributionMode.WholeInstitution]: {
        title: 'Toată instituția',
        hint:  'Toți utilizatorii activi. Disponibil doar administratorului.',
    },
};

export const STATUS_LABELS: Record<InternalDocumentStatus, string> = {
    [InternalDocumentStatus.Draft]:     'Ciornă',
    [InternalDocumentStatus.Published]: 'Publicat',
    [InternalDocumentStatus.Repealed]:  'Abrogat',
};

export interface InternalDocumentListItem {
    id: string;
    title: string;
    number: string | null;
    summary: string | null;
    status: InternalDocumentStatus;
    distributionMode: DistributionMode;
    requiresAcknowledgement: boolean;
    authorId: string;
    authorName: string;
    authorUnitName: string | null;
    fileName: string;
    fileSize: number;
    createdAt: string;
    publishedAt: string | null;
    repealedAt: string | null;
    isAuthor: boolean;
    myOpenedAt: string | null;
    myAcknowledgedAt: string | null;
    recipientCount: number | null;
    openedCount: number | null;
    acknowledgedCount: number | null;
}

export interface InternalDocumentDetail {
    id: string;
    title: string;
    number: string | null;
    summary: string | null;
    status: InternalDocumentStatus;
    authorId: string;
    authorName: string;
    authorUnitName: string | null;
    distributionMode: DistributionMode;
    includeSubunits: boolean;
    requiresAcknowledgement: boolean;
    targetUnits: { id: string; name: string }[];
    targetUsers: { id: string; name: string }[];
    fileName: string;
    fileSize: number;
    sha256: string;
    createdAt: string;
    updatedAt: string | null;
    publishedAt: string | null;
    repealedAt: string | null;
    repealedReason: string | null;
    isAuthor: boolean;
    isRecipient: boolean;
    myOpenedAt: string | null;
    myAcknowledgedAt: string | null;
    counts: { total: number; opened: number; acknowledged: number } | null;
    canEdit: boolean;
    canPublish: boolean;
    canDelete: boolean;
    canRepeal: boolean;
    canAcknowledge: boolean;
    canViewReport: boolean;
}

export interface DistributionOptions {
    ledUnitId: string | null;
    ledUnitName: string | null;
    isAdmin: boolean;
    modes: DistributionMode[];
    selectableUnitIds: string[];
}

export interface DistributionInput {
    mode: DistributionMode;
    unitIds: string[];
    userIds: string[];
    includeSubunits: boolean;
}

export interface DistributionPreview {
    count: number;
    units: { orgUnitId: string | null; name: string; count: number }[];
}

export interface DocumentReport {
    id: string;
    title: string;
    requiresAcknowledgement: boolean;
    publishedAt: string | null;
    total: number;
    opened: number;
    acknowledged: number;
    byUnit: { orgUnitId: string | null; name: string; total: number; opened: number; acknowledged: number }[];
    recipients: {
        userId: string;
        name: string;
        username: string;
        isActive: boolean;
        orgUnitId: string | null;
        unitName: string;
        openedAt: string | null;
        acknowledgedAt: string | null;
    }[];
}

export interface SaveDocumentInput extends DistributionInput {
    title: string;
    number: string;
    summary: string;
    requiresAcknowledgement: boolean;
    /** Obligatoriu la creare, opțional la editarea ciornei. */
    file: File | null;
}

// ── Operații ─────────────────────────────────────────────────────────────────

export async function listInternalDocuments(
    params: { box: 'inbox' | 'authored'; search?: string; status?: InternalDocumentStatus | ''; pending?: boolean; page?: number; pageSize?: number },
    signal?: AbortSignal
): Promise<PagedResult<InternalDocumentListItem>> {
    const { data } = await api.get<PagedResult<InternalDocumentListItem>>('/InternalDocuments', {
        params: {
            box:      params.box,
            search:   params.search || undefined,
            status:   params.status === '' || params.status === undefined ? undefined : params.status,
            pending:  params.pending || undefined,
            page:     params.page ?? 1,
            pageSize: params.pageSize ?? 25,
        },
        signal,
    });
    return data;
}

export async function getPendingAcknowledgements(): Promise<number> {
    const { data } = await api.get<{ count: number }>('/InternalDocuments/pending-count');
    return data.count;
}

export async function getInternalDocument(id: string): Promise<InternalDocumentDetail> {
    const { data } = await api.get<InternalDocumentDetail>(`/InternalDocuments/${id}`);
    return data;
}

export async function getDistributionOptions(): Promise<DistributionOptions> {
    const { data } = await api.get<DistributionOptions>('/InternalDocuments/distribution-options');
    return data;
}

export async function previewDistribution(input: DistributionInput): Promise<DistributionPreview> {
    const { data } = await api.post<DistributionPreview>('/InternalDocuments/preview-distribution', input);
    return data;
}

function toForm(input: SaveDocumentInput): FormData {
    const form = new FormData();
    if (input.file) form.append('File', input.file);
    form.append('Title', input.title);
    form.append('Number', input.number);
    form.append('Summary', input.summary);
    form.append('DistributionMode', String(input.mode));
    form.append('IncludeSubunits', String(input.includeSubunits));
    form.append('RequiresAcknowledgement', String(input.requiresAcknowledgement));
    input.unitIds.forEach((id, i) => form.append(`UnitIds[${i}]`, id));
    input.userIds.forEach((id, i) => form.append(`UserIds[${i}]`, id));
    return form;
}

export async function createInternalDocument(input: SaveDocumentInput): Promise<{ id: string; message: string }> {
    const { data } = await api.post('/InternalDocuments', toForm(input), { timeout: 300_000 });
    return data;
}

export async function updateInternalDocument(id: string, input: SaveDocumentInput): Promise<{ message: string }> {
    const { data } = await api.put(`/InternalDocuments/${id}`, toForm(input), { timeout: 300_000 });
    return data;
}

export async function deleteInternalDocument(id: string): Promise<{ message: string }> {
    const { data } = await api.delete(`/InternalDocuments/${id}`);
    return data;
}

export async function publishInternalDocument(id: string): Promise<{ message: string; recipients: number }> {
    const { data } = await api.post(`/InternalDocuments/${id}/publish`);
    return data;
}

export async function repealInternalDocument(id: string, reason?: string): Promise<{ message: string }> {
    const { data } = await api.post(`/InternalDocuments/${id}/repeal`, { reason: reason ?? null });
    return data;
}

export async function acknowledgeInternalDocument(id: string): Promise<{ message: string }> {
    const { data } = await api.post(`/InternalDocuments/${id}/acknowledge`);
    return data;
}

export async function getDocumentReport(id: string): Promise<DocumentReport> {
    const { data } = await api.get<DocumentReport>(`/InternalDocuments/${id}/report`);
    return data;
}

/**
 * Descarcă documentul. Prima descărcare a unui destinatar e înregistrată de
 * server ca „deschis” - condiția pentru „Luat la cunoștință”.
 */
export async function downloadInternalDocument(id: string, fileName: string): Promise<void> {
    const { data } = await api.get<Blob>(`/InternalDocuments/${id}/download`, {
        responseType: 'blob',
        timeout: 300_000,
    });
    const url = URL.createObjectURL(data);
    const a = document.createElement('a');
    a.href = url;
    a.download = fileName;
    document.body.appendChild(a);
    a.click();
    a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
}
