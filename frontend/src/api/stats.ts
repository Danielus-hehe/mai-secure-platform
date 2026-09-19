import api from './client';
// ── GET /api/Stats (pagina principală) ──────────────────────────────────────
//
// Transferurile sunt doar ale utilizatorului curent (trimise sau primite).
// Serverul nu mai întoarce transferurile altor conturi: numele fișierelor și
// perechile expeditor–destinatar sunt metadate sensibile.

export interface RecentTransfer {
    id: string;
    fileName: string;
    fileSize: number;
    /** Din perspectiva utilizatorului curent. */
    direction: 'sent' | 'received';
    senderName: string;
    recipientName: string;
    /** "Pending" | "Downloaded" | "Expired" | "Revoked" */
    status: string;
    createdAt: string;
}

export interface DashboardStats {
    activeUsers: number;
    totalDocuments: number;
    /** Transferurile trimise sau primite de utilizatorul curent. */
    myTransfersTotal: number;
    /** Fișierele primite, încă nedescărcate și în termen. */
    awaitingMyDownload: number;
    /** null pentru rolurile care nu au acces la metricile de securitate. */
    failedLoginsLast24h: number | null;
    recentTransfers: RecentTransfer[];
}

// ── GET /api/Stats/admin (panou administrare) ───────────────────────────────

export interface RoleCount {
    role: string;
    label: string;
    count: number;
}

export interface AdminUserStats {
    total: number;
    active: number;
    inactive: number;
    locked: number;
    /** Conturi active fara chei generate - nu pot primi fisiere criptate. */
    withoutKeys: number;
    byRole: RoleCount[];
}

export interface AdminTransferStats {
    total: number;
    pending: number;
    downloaded: number;
    expired: number;
    encrypted: number;
    /** Procentul de transferuri care trec prin plicul E2E. */
    encryptedPercent: number;
    /** Expirate dar inca nepurjate - coada jobului de fundal. */
    awaitingPurge: number;
    expiringNext24h: number;
}

export interface AdminStorageStats {
    provider: string;
    bucket: string;
    presignedDownload: boolean;
    maxFileSizeMb: number;
    /** Octeti de cifrotext care ocupa efectiv spatiu acum. */
    storedCiphertextBytes: number;
    /** Total istoric, inclusiv transferurile deja purjate. */
    totalCiphertextBytes: number;
}

export interface AdminDocumentStats {
    total: number;
    versions: number;
}

export interface AdminSecurityStats {
    failedLoginsLast24h: number;
    successfulLoginsLast24h: number;
    /** Descarcari in care clientul a raportat semnatura invalida. Asteptat: 0. */
    invalidSignatures: number;
}

export interface SeriesPoint {
    /** ISO, "2026-09-01" */
    date: string;
    /** Eticheta scurta pentru axa, "01.09" */
    label: string;
    uploads: number;
    downloads: number;
    expirations: number;
    transfers: number;
    bytes: number;
    failedLogins: number;
}

export interface TopSender {
    userId: string;
    name: string;
    count: number;
    bytes: number;
}

export interface ExpirationJobStatus {
    enabled: boolean;
    intervalMinutes: number;
    purgeObjects: boolean;
    /** "Nu a rulat inca" | "In curs" | "OK" | "Eroare" | "Dezactivat" */
    status: string;
    lastRunAt: string | null;
    lastSuccessAt: string | null;
    lastError: string | null;
    runCount: number;
    lastRunExpired: number;
    lastRunPurged: number;
    expiredTotal: number;
    purgedTotal: number;
    failedPurgeTotal: number;
}

export interface ModuleHealth {
    name: string;
    online: boolean;
    latencyMs: number;
}

export interface AdminStats {
    generatedAt: string;
    windowDays: number;
    users: AdminUserStats;
    transfers: AdminTransferStats;
    storage: AdminStorageStats;
    documents: AdminDocumentStats;
    security: AdminSecurityStats;
    series: SeriesPoint[];
    topSenders: TopSender[];
    expirationJob: ExpirationJobStatus;
    modules: ModuleHealth[];
}

export interface RunExpirationResult {
    message: string;
    expired: number;
    purged: number;
    failed: number;
}

// ── Apeluri ─────────────────────────────────────────────────────────────────

export async function fetchDashboardStats(signal?: AbortSignal): Promise<DashboardStats> {
    const { data } = await api.get<DashboardStats>('/Stats', { signal });
    return data;
}

export async function fetchAdminStats(days = 14, signal?: AbortSignal): Promise<AdminStats> {
    const { data } = await api.get<AdminStats>('/Stats/admin', { params: { days }, signal });
    return data;
}

/** Declanseaza manual o trecere a jobului de expirare. Doar Administrator. */
export async function runExpirationJob(): Promise<RunExpirationResult> {
    const { data } = await api.post<RunExpirationResult>('/Stats/expiration/run');
    return data;
}