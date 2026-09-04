export type Role = 'UTILIZATOR' | 'SEF_DIRECTIE' | 'ADMINISTRATOR';

export interface User {
    id: string;
    username: string;
    fullName: string;
    email: string;
    role: Role;
    department: string;
    isActive: boolean;
    createdAt: string;
}

export interface AuthSession {
    accessToken: string;
    expiresAt: number;
    user: User;
}

export interface SecureFile {
    id: string;
    fileName: string;
    sizeBytes: number;
    sha256: string;
    sender: Pick<User, 'id' | 'fullName'>;
    recipient: Pick<User, 'id' | 'fullName'>;
    createdAt: string;
    expiresAt: string;
    status: 'IN_ASTEPTARE' | 'CONFIRMAT' | 'EXPIRAT';
}

export type DocCategory = 'ORDIN' | 'REGULAMENT' | 'PROCEDURA' | 'ALTA';

export interface NormativeDocument {
    id: string;
    title: string;
    category: DocCategory;
    number: string;
    keywords: string[];
    currentVersion: number;
    publishedBy: string;
    publishedAt: string;
    versions: DocumentVersion[];
}

export interface DocumentVersion {
    version: number;
    fileName: string;
    sha256: string;
    uploadedAt: string;
    uploadedBy: string;
    isArchived: boolean;
}

export interface AuditEntry {
    id: string;
    timestamp: string;
    userId: string;
    userName: string;
    action: 'LOGIN' | 'LOGOUT' | 'UPLOAD' | 'DOWNLOAD' | 'TRANSFER' | 'MODIFICARE_DOC' | 'ADMIN';
    target: string;
    ipAddress: string;
    result: 'SUCCES' | 'ESEC';
}

export interface DashboardStats {
    totalTransfers: number;
    activeTransfers: number;
    pendingConfirmations: number;
    normativeDocs: number;
    activeUsers: number;
    failedLogins24h: number;
}