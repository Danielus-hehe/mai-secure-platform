import type { Role } from '../types';

export const APP_NAME = 'SGDM — Sistem de Gestiune Documente MAI';

export const ROLE_LABELS: Record<Role, string> = {
    UTILIZATOR: 'Utilizator',
    SEF_DIRECTIE: 'Șef de Direcție',
    ADMINISTRATOR: 'Administrator',
};

export const ROLE_BADGE_CLASSES: Record<Role, string> = {
    UTILIZATOR: 'bg-mai-100 text-mai-700',
    SEF_DIRECTIE: 'bg-gold-500/15 text-gold-600',
    ADMINISTRATOR: 'bg-mai-700 text-white',
};

/** Ierarhie RBAC — rolul trebuie să fie >= minimul cerut */
export const ROLE_HIERARCHY: Record<Role, number> = {
    UTILIZATOR: 1,
    SEF_DIRECTIE: 2,
    ADMINISTRATOR: 3,
};