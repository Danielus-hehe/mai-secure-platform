import type { SecureFile, NormativeDocument, AuditEntry, User } from '../types';
import { MOCK_TRANSFERS, MOCK_DOCUMENTS, MOCK_AUDIT, MOCK_USERS } from '../utils/mockData';

/**
 * „Magazin" mock în memorie — simulează backend-ul.
 * Când conectăm API-ul real, doar înlocuim funcțiile de aici
 * cu apeluri axios — restul paginilor nu se schimbă.
 */

let transfers = [...MOCK_TRANSFERS];
let documents = [...MOCK_DOCUMENTS];
let auditLog = [...MOCK_AUDIT];
let users = [...MOCK_USERS.map(({ password, ...u }) => u)];

// ---------- Transferuri ----------
export const transferStore = {
    getAll: () => [...transfers],

    add(file: Omit<SecureFile, 'id' | 'createdAt' | 'expiresAt' | 'status'>): SecureFile {
        const now = new Date();
        const expires = new Date(now.getTime() + 14 * 24 * 3600_000); // retentție 14 zile
        const entry: SecureFile = {
            ...file, id: `f-${Date.now()}`,
            createdAt: now.toISOString(), expiresAt: expires.toISOString(),
            status: 'IN_ASTEPTARE',
        };
        transfers = [entry, ...transfers];
        return entry;
    },

    confirm(id: string) {
        transfers = transfers.map((t) => (t.id === id ? { ...t, status: 'CONFIRMAT' } : t));
    },
};

// ---------- Documente normative ----------
export const documentStore = {
    getAll: () => [...documents],

    search(query: string): NormativeDocument[] {
        const q = query.trim().toLowerCase();
        if (!q) return documents;
        return documents.filter(
            (d) =>
                d.title.toLowerCase().includes(q) ||
                d.number.toLowerCase().includes(q) ||
                d.keywords.some((k) => k.toLowerCase().includes(q))
        );
    },

    addNewVersion(id: string, fileName: string, uploadedBy: string) {
        documents = documents.map((d) => {
            if (d.id !== id) return d;
            const nextVersion = d.currentVersion + 1;
            return {
                ...d,
                currentVersion: nextVersion,
                publishedAt: new Date().toISOString(),
                publishedBy: uploadedBy,
                versions: [
                    { version: nextVersion, fileName, sha256: `nou-${Date.now()}`,
                        uploadedAt: new Date().toISOString(), uploadedBy, isArchived: false },
                    // versiunea curentă anterioară devine arhivată
                    ...d.versions.map((v) => ({ ...v, isArchived: true })),
                ],
            };
        });
    },

    publish(doc: Omit<NormativeDocument, 'id' | 'currentVersion' | 'publishedAt' | 'publishedBy' | 'versions'>,
            fileName: string, publishedBy: string) {
        const now = new Date().toISOString();
        documents = [
            {
                ...doc, id: `d-${Date.now()}`, currentVersion: 1,
                publishedAt: now, publishedBy,
                versions: [{ version: 1, fileName, sha256: `nou-${Date.now()}`,
                    uploadedAt: now, uploadedBy: publishedBy, isArchived: false }],
            },
            ...documents,
        ];
    },
};

// ---------- Audit ----------
export const auditStore = {
    getAll: () => [...auditLog],

    log(entry: Omit<AuditEntry, 'id' | 'timestamp'>) {
        auditLog = [
            { ...entry, id: `a-${Date.now()}`, timestamp: new Date().toISOString() },
            ...auditLog,
        ];
    },
};

// ---------- Utilizatori ----------
export const userStore = {
    getAll: () => [...users],

    create(u: Omit<User, 'id' | 'createdAt' | 'isActive'>): User {
        const user: User = { ...u, id: `u-${Date.now()}`, isActive: true,
            createdAt: new Date().toISOString() };
        users = [...users, user];
        auditStore.log({ userId: 'u-001', userName: 'Administrator', action: 'ADMIN',
            target: `creare cont: ${u.username}`, ipAddress: '10.0.12.45', result: 'SUCCES' });
        return user;
    },

    setActive(id: string, isActive: boolean) {
        users = users.map((u) => (u.id === id ? { ...u, isActive } : u));
        auditStore.log({ userId: 'u-001', userName: 'Administrator', action: 'ADMIN',
            target: `${isActive ? 'activare' : 'dezactivare'} cont: ${id}`,
            ipAddress: '10.0.12.45', result: 'SUCCES' });
    },

    changeRole(id: string, role: User['role']) {
        users = users.map((u) => (u.id === id ? { ...u, role } : u));
        auditStore.log({ userId: 'u-001', userName: 'Administrator', action: 'ADMIN',
            target: `schimbare rol: ${id} → ${role}`, ipAddress: '10.0.12.45', result: 'SUCCES' });
    },
};