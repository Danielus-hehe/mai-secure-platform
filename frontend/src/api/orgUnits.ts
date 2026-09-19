/**
 * Structura organizatorică: Direcții → Secții → Servicii, fiecare cu șef.
 * Citirea e deschisă tuturor; modificările sunt doar ale administratorului.
 */

import api from './client';

/**
 * Rangul nivelului (Direcție = 100, Secție = 200, Serviciu = 300; administratorul
 * poate adăuga altele, ex. 150 sau 400). Denumirile vin din /OrgLevels.
 */
export type OrgUnitType = number;

export interface OrgLevel {
    rank: number;
    name: string;
    unitCount: number;
}

export interface OrgUnit {
    id: string;
    name: string;
    code: string | null;
    type: OrgUnitType;
    /** Denumirea nivelului (ex. „Secție”). */
    levelName: string;
    parentId: string | null;
    headUserId: string | null;
    headName: string | null;
    isActive: boolean;
    memberCount: number;
}

export interface OrgUnitMember {
    id: string;
    username: string;
    fullName: string;
    isActive: boolean;
    isHead: boolean;
}

export interface SaveOrgUnitInput {
    name: string;
    code?: string | null;
    type: OrgUnitType;
    parentId: string | null;
    /** Doar la actualizare: false = desființare. */
    isActive?: boolean;
}

export async function listOrgUnits(includeInactive = false): Promise<OrgUnit[]> {
    const { data } = await api.get<OrgUnit[]>('/OrgUnits', { params: { includeInactive } });
    return data;
}

export async function listOrgUnitMembers(id: string): Promise<OrgUnitMember[]> {
    const { data } = await api.get<OrgUnitMember[]>(`/OrgUnits/${id}/members`);
    return data;
}

export async function createOrgUnit(input: SaveOrgUnitInput): Promise<{ id: string; message: string }> {
    const { data } = await api.post('/OrgUnits', input);
    return data;
}

export async function updateOrgUnit(id: string, input: SaveOrgUnitInput): Promise<{ message: string }> {
    const { data } = await api.put(`/OrgUnits/${id}`, input);
    return data;
}

export async function setOrgUnitHead(id: string, userId: string | null): Promise<{ message: string }> {
    const { data } = await api.patch(`/OrgUnits/${id}/head`, { userId });
    return data;
}

export async function deleteOrgUnit(id: string): Promise<{ message: string }> {
    const { data } = await api.delete(`/OrgUnits/${id}`);
    return data;
}

/** Încadrează persoanele în subdiviziune (le mută, dacă erau în alta). */
export async function addOrgUnitMembers(
    id: string,
    userIds: string[]
): Promise<{ message: string; moved: number; headsReleased: string[] }> {
    const { data } = await api.post(`/OrgUnits/${id}/members`, { userIds });
    return data;
}

export async function removeOrgUnitMember(id: string, userId: string): Promise<{ message: string }> {
    const { data } = await api.delete(`/OrgUnits/${id}/members/${userId}`);
    return data;
}

// ── Niveluri ─────────────────────────────────────────────────────────────────

export async function listOrgLevels(): Promise<OrgLevel[]> {
    const { data } = await api.get<OrgLevel[]>('/OrgLevels');
    return data;
}

/** Adaugă un nivel imediat sub `afterRank` (null = deasupra tuturor). */
export async function createOrgLevel(name: string, afterRank: number | null): Promise<{ rank: number; message: string }> {
    const { data } = await api.post('/OrgLevels', { name, afterRank });
    return data;
}

export async function renameOrgLevel(rank: number, name: string): Promise<{ message: string }> {
    const { data } = await api.put(`/OrgLevels/${rank}`, { name });
    return data;
}

export async function deleteOrgLevel(rank: number): Promise<{ message: string }> {
    const { data } = await api.delete(`/OrgLevels/${rank}`);
    return data;
}
