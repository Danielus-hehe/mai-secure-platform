import api from './client';

/**
 * Integrarea cu Active Directory, partea de administrare.
 *
 * Toate apelurile de aici cer rol de Administrator, cu excepția lui
 * `getLoginInfo`, care e public: pagina de autentificare trebuie să știe dacă
 * se acceptă conturi de domeniu înainte ca cineva să fie autentificat.
 */

export interface DirectoryLoginInfo {
    enabled: boolean;
    domain: string;
    realm: string;
}

export interface DirectoryStatus {
    enabled: boolean;
    configuration: Record<string, string>;
    roleMappings: { group: string; role: string }[];
    defaultRole: string;
    linkedAccounts: number;
    importedOrgUnits: number;
}

export interface DirectoryProbe {
    reachable: boolean;
    serviceAccountBound: boolean;
    userCount: number;
    orgUnitCount: number;
    serverCertificateSubject: string | null;
    serverCertificateThumbprint: string | null;
    error: string | null;
}

export type OrgUnitImportAction = 'Create' | 'Update' | 'Unchanged';

export interface OrgUnitImportItem {
    dn: string;
    name: string;
    parentDn: string | null;
    depth: number;
    rank: number;
    action: OrgUnitImportAction;
    note: string | null;
}

export interface OrgUnitImportPreview {
    searchBase: string;
    found: number;
    toCreate: number;
    toUpdate: number;
    unchanged: number;
    warnings: string[];
    items: OrgUnitImportItem[];
}

export async function getLoginInfo(): Promise<DirectoryLoginInfo> {
    const { data } = await api.get<DirectoryLoginInfo>('/Directory/login-info');
    return data;
}

export async function getStatus(): Promise<DirectoryStatus> {
    const { data } = await api.get<DirectoryStatus>('/Directory/status');
    return data;
}

export async function testConnection(): Promise<DirectoryProbe> {
    const { data } = await api.post<DirectoryProbe>('/Directory/test-connection');
    return data;
}

export async function previewOrgUnits(): Promise<OrgUnitImportPreview> {
    const { data } = await api.get<OrgUnitImportPreview>('/Directory/org-units/preview');
    return data;
}

/**
 * Aplică importul. `distinguishedNames` gol înseamnă „tot ce e în
 * previzualizare”; confirmarea e obligatorie și se trimite explicit, ca un
 * apel accidental să nu rescrie structura instituției.
 */
export async function importOrgUnits(
    distinguishedNames: string[]
): Promise<{ message: string; created: number; updated: number }> {
    const { data } = await api.post('/Directory/org-units/import', {
        confirm: true,
        distinguishedNames,
    });
    return data as { message: string; created: number; updated: number };
}

export async function linkAccount(
    userId: string,
    samAccountName?: string
): Promise<{ message: string; distinguishedName: string; keysNeedRewrap: boolean }> {
    const { data } = await api.post(`/Directory/users/${userId}/link`, { samAccountName });
    return data as { message: string; distinguishedName: string; keysNeedRewrap: boolean };
}

export async function unlinkAccount(userId: string): Promise<{ message: string }> {
    const { data } = await api.post(`/Directory/users/${userId}/unlink`);
    return data as { message: string };
}

export async function syncAccount(
    userId: string
): Promise<{ message: string; changes: string[]; blocked: string | null }> {
    const { data } = await api.post(`/Directory/users/${userId}/sync`);
    return data as { message: string; changes: string[]; blocked: string | null };
}
