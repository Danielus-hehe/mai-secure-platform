import type { OrgUnit } from '../api/orgUnits';

/**
 * Arborele subdiviziunilor pentru afișare: ordine de parcurgere (părinte,
 * apoi copiii lui), adâncime pentru indentare și calea completă.
 *
 * Serverul trimite o listă plată; aici se construiește ordinea de afișare o
 * singură dată, nu la fiecare randare a unui <select>.
 */

export interface OrgTreeRow {
    unit: OrgUnit;
    depth: number;
    path: string;
}

export function buildOrgTree(units: OrgUnit[]): OrgTreeRow[] {
    const byParent = new Map<string | null, OrgUnit[]>();
    const ids = new Set(units.map((u) => u.id));

    for (const u of units) {
        // O subdiviziune al cărei părinte lipsește din listă (ex. părinte
        // desființat, filtrat) se afișează la nivelul de vârf, nu dispare.
        const key = u.parentId && ids.has(u.parentId) ? u.parentId : null;
        const list = byParent.get(key) ?? [];
        list.push(u);
        byParent.set(key, list);
    }

    for (const list of byParent.values()) {
        list.sort((a, b) => a.type - b.type || a.name.localeCompare(b.name, 'ro'));
    }

    const rows: OrgTreeRow[] = [];
    const seen = new Set<string>();

    const walk = (parentId: string | null, depth: number, prefix: string) => {
        for (const unit of byParent.get(parentId) ?? []) {
            if (seen.has(unit.id)) continue;   // protecție la cicluri
            seen.add(unit.id);
            const path = prefix ? `${prefix} / ${unit.name}` : unit.name;
            rows.push({ unit, depth, path });
            walk(unit.id, depth + 1, path);
        }
    };

    walk(null, 0, '');
    return rows;
}

/** Id-urile subdiviziunii și ale tuturor descendenților. */
export function subtreeIds(units: OrgUnit[], rootId: string): Set<string> {
    const result = new Set<string>([rootId]);
    let added = true;
    while (added) {
        added = false;
        for (const u of units) {
            if (u.parentId && result.has(u.parentId) && !result.has(u.id)) {
                result.add(u.id);
                added = true;
            }
        }
    }
    return result;
}
