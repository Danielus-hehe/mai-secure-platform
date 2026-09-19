/**
 * Structura organizatorică: Direcții → Secții → Servicii, fiecare cu șef.
 * Citirea e deschisă tuturor; modificările sunt doar ale administratorului.
 */

import api from './client';

export const OrgUnitType = {
    Directie: 1,
    Sectie:   2,
    Serviciu: 3,
} as const;

export type OrgUnitType = typeof OrgUnitType[keyof typeof OrgUnitType];

export const ORG_UNIT_TYPE_LABELS: Record<OrgUnitType, string> = {
    [OrgUnitType.Directie]: 'Direcție',
    [OrgUnitType.Sectie]:   'Secție',
    [OrgUnitType.Serviciu]: 'Serviciu',
};

export interface OrgUnit {
    id: string;
    name: string;
    code: string | null;
    type: OrgUnitType;
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
