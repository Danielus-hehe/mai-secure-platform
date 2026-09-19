import { useMemo } from 'react';
import type { OrgUnit } from '../../api/orgUnits';
import { buildOrgTree } from '../../utils/orgTree';

interface Props {
    units: OrgUnit[];
    value: string[];
    onChange: (ids: string[]) => void;
    /** Doar acestea se pot bifa (subordinea autorului; toate, pentru administrator). */
    allowedIds: Set<string>;
    disabled?: boolean;
}

/** Listă cu bife, în ordinea arborelui - pentru distribuția „subdiviziuni selectate”. */
export default function UnitChecklist({ units, value, onChange, allowedIds, disabled }: Props) {
    const rows = useMemo(
        () => buildOrgTree(units.filter((u) => u.isActive)).filter((r) => allowedIds.has(r.unit.id)),
        [units, allowedIds]
    );
    const selected = new Set(value);

    const toggle = (id: string) =>
        onChange(selected.has(id) ? value.filter((v) => v !== id) : [...value, id]);

    if (rows.length === 0) {
        return <p className="text-sm text-mai-400">Nu există subdiviziuni pe care să le puteți alege.</p>;
    }

    // Adâncimea relativă: subordinea unui șef de secție începe de la secția lui.
    const minDepth = Math.min(...rows.map((r) => r.depth));

    return (
        <ul className="max-h-56 overflow-y-auto rounded-lg border border-mai-200 py-1 dark:border-mai-600">
            {rows.map(({ unit, depth }) => (
                <li key={unit.id}>
                    <label
                        className="flex cursor-pointer items-center gap-2 px-3 py-1.5 text-sm hover:bg-mai-50 dark:hover:bg-mai-700/50"
                        style={{ paddingLeft: `${0.75 + (depth - minDepth) * 1.25}rem` }}
                    >
                        <input
                            type="checkbox"
                            checked={selected.has(unit.id)}
                            disabled={disabled}
                            onChange={() => toggle(unit.id)}
                            className="h-4 w-4 rounded border-mai-300 text-mai-600 focus:ring-mai-500"
                        />
                        <span className="text-mai-800 dark:text-mai-100">{unit.name}</span>
                        <span className="text-xs text-mai-400">{unit.memberCount} pers.</span>
                    </label>
                </li>
            ))}
        </ul>
    );
}
