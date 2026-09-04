import type { User, SecureFile, NormativeDocument, AuditEntry } from '../types';

/** Conturi demo — în producție vin din API/PostgreSQL */
export const MOCK_USERS: (User & { password: string })[] = [
    {
        id: 'u-001', username: 'admin', password: 'Admin2025!',
        fullName: 'Calancea Dan-Alexandru', email: 'dan.calancea@mai.gov.md',
        role: 'ADMINISTRATOR', department: 'Direcția IT', isActive: true,
        createdAt: '2025-06-02T08:00:00Z',
    },
    {
        id: 'u-002', username: 'sef.directie', password: 'Sef2025!',
        fullName: 'Ion Bivol', email: 'ion.bivol@mai.gov.md',
        role: 'SEF_DIRECTIE', department: 'Direcția Analiză și Documentare',
        isActive: true, createdAt: '2025-06-03T08:00:00Z',
    },
    {
        id: 'u-003', username: 'utilizator', password: 'User2025!',
        fullName: 'Maria Ceban', email: 'maria.ceban@mai.gov.md',
        role: 'UTILIZATOR', department: 'Direcția Analiză și Documentare',
        isActive: true, createdAt: '2025-06-04T08:00:00Z',
    },
];

export const MOCK_TRANSFERS: SecureFile[] = [
    {
        id: 'f-001', fileName: 'Raport_serviciu_iunie.pdf', sizeBytes: 2_412_544,
        sha256: 'a3f5e8b2c1d4f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0c1d2e3f4a5b6c7d8e9f0a1',
        sender: { id: 'u-003', fullName: 'Maria Ceban' },
        recipient: { id: 'u-002', fullName: 'Ion Bivol' },
        createdAt: '2025-06-10T09:15:00Z', expiresAt: '2025-06-24T09:15:00Z',
        status: 'IN_ASTEPTARE',
    },
    {
        id: 'f-002', fileName: 'Ordin_intern_145_proceduri.pdf', sizeBytes: 845_312,
        sha256: 'b7c9d0e1f2a3b4c5d6e7f8a9b0c1d2e3f4a5b6c7d8e9f0a1a3f5e8b2c1d4f6',
        sender: { id: 'u-002', fullName: 'Ion Bivol' },
        recipient: { id: 'u-003', fullName: 'Maria Ceban' },
        createdAt: '2025-06-08T14:30:00Z', expiresAt: '2025-06-22T14:30:00Z',
        status: 'CONFIRMAT',
    },
    {
        id: 'f-003', fileName: 'Buget_directie_Q2.xlsx', sizeBytes: 128_768,
        sha256: 'c1d4f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0c1d2e3f4a5b6c7d8e9f0a1a3f5e8b2',
        sender: { id: 'u-001', fullName: 'Calancea Dan-Alexandru' },
        recipient: { id: 'u-002', fullName: 'Ion Bivol' },
        createdAt: '2025-06-05T11:00:00Z', expiresAt: '2025-06-19T11:00:00Z',
        status: 'CONFIRMAT',
    },
];

export const MOCK_DOCUMENTS: NormativeDocument[] = [
    {
        id: 'd-001', title: 'Regulament intern de ordine și disciplină',
        category: 'REGULAMENT', number: 'OI-145/2025',
        keywords: ['disciplină', 'ordine internă', 'conduită'],
        currentVersion: 3, publishedBy: 'Ion Bivol', publishedAt: '2025-06-06T10:00:00Z',
        versions: [
            { version: 3, fileName: 'regulament_v3.pdf', sha256: 'a1b2c3…', uploadedAt: '2025-06-06T10:00:00Z', uploadedBy: 'Ion Bivol', isArchived: false },
            { version: 2, fileName: 'regulament_v2.pdf', sha256: 'd4e5f6…', uploadedAt: '2025-04-12T10:00:00Z', uploadedBy: 'Ion Bivol', isArchived: true },
            { version: 1, fileName: 'regulament_v1.pdf', sha256: 'g7h8i9…', uploadedAt: '2025-02-01T10:00:00Z', uploadedBy: 'Ion Bivol', isArchived: true },
        ],
    },
    {
        id: 'd-002', title: 'Procedura de transfer securizat al documentelor',
        category: 'PROCEDURA', number: 'PO-078/2025',
        keywords: ['transfer', 'criptare', 'securitate'],
        currentVersion: 1, publishedBy: 'Calancea Dan-Alexandru', publishedAt: '2025-06-09T09:00:00Z',
        versions: [
            { version: 1, fileName: 'procedura_transfer_v1.pdf', sha256: 'j0k1l2…', uploadedAt: '2025-06-09T09:00:00Z', uploadedBy: 'Calancea Dan-Alexandru', isArchived: false },
        ],
    },
    {
        id: 'd-003', title: 'Ordin privind politica de retenție a fișierelor',
        category: 'ORDIN', number: 'OI-151/2025',
        keywords: ['retenție', 'fișiere', 'expirare'],
        currentVersion: 2, publishedBy: 'Ion Bivol', publishedAt: '2025-06-11T08:30:00Z',
        versions: [
            { version: 2, fileName: 'ordin_retentie_v2.pdf', sha256: 'm3n4o5…', uploadedAt: '2025-06-11T08:30:00Z', uploadedBy: 'Ion Bivol', isArchived: false },
            { version: 1, fileName: 'ordin_retentie_v1.pdf', sha256: 'p6q7r8…', uploadedAt: '2025-05-20T08:30:00Z', uploadedBy: 'Ion Bivol', isArchived: true },
        ],
    },
];

export const MOCK_AUDIT: AuditEntry[] = [
    { id: 'a-001', timestamp: '2025-06-11T15:42:10Z', userId: 'u-001', userName: 'Calancea Dan-Alexandru', action: 'LOGIN', target: 'sesiune web', ipAddress: '10.0.12.45', result: 'SUCCES' },
    { id: 'a-002', timestamp: '2025-06-11T15:40:03Z', userId: 'u-003', userName: 'Maria Ceban', action: 'UPLOAD', target: 'Raport_serviciu_iunie.pdf', ipAddress: '10.0.12.61', result: 'SUCCES' },
    { id: 'a-003', timestamp: '2025-06-11T14:58:44Z', userId: 'u-000', userName: 'necunoscut', action: 'LOGIN', target: 'sesiune web', ipAddress: '10.0.99.7', result: 'ESEC' },
    { id: 'a-004', timestamp: '2025-06-11T14:30:12Z', userId: 'u-002', userName: 'Ion Bivol', action: 'DOWNLOAD', target: 'Buget_directie_Q2.xlsx', ipAddress: '10.0.12.30', result: 'SUCCES' },
    { id: 'a-005', timestamp: '2025-06-11T13:05:29Z', userId: 'u-003', userName: 'Maria Ceban', action: 'TRANSFER', target: '→ Ion Bivol', ipAddress: '10.0.12.61', result: 'SUCCES' },
];