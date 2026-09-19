import { useMemo } from 'react';
import type { OrgUnit } from '../../api/orgUnits';
import { buildOrgTree } from '../../utils/orgTree';

interface Props {
    units: OrgUnit[];
    value: string;
    onChange: (orgUnitId: string) => void;
    /** Textul opțiunii goale (value ""). Fără el, opțiunea goală nu există. */
    emptyLabel?: string;
    /** Doar aceste subdiviziuni se pot alege (celelalte apar dezactivate, pentru context). */
    allowedIds?: Set<string>;
    /** Subdiviziuni ascunse complet (ex. subdiviziunea însăși, la alegerea părintelui). */
    hiddenIds?: Set<string>;
    id?: string;
    disabled?: boolean;
    className?: string;
}

const DEFAULT_CLASS =
    'w-full rounded-lg border border-mai-200 dark:border-mai-600 bg-white dark:bg-mai-800 ' +
    'dark:text-mai-200 px-3 py-2 text-sm focus:border-mai-500 focus:outline-none ' +
    'focus:ring-2 focus:ring-mai-500/20 dark:focus:ring-mai-400/20';

/**
 * <select> cu subdiviziunile în ordinea arborelui, indentate după nivel.
 * Un <select> nativ rămâne accesibil din tastatură și pe mobil fără efort;
 * indentarea se face cu spații neseparabile, singurul lucru pe care un
 * <option> îl afișează fiabil.
 */
export default function OrgUnitSelect({
    units, value, onChange, emptyLabel, allowedIds, hiddenIds, id, disabled, className,
}: Props) {
    const rows = useMemo(
        () => buildOrgTree(units).filter((r) => !hiddenIds?.has(r.unit.id)),
        [units, hiddenIds]
    );

    return (
        <select
            id={id}
            value={value}
            disabled={disabled}
            onChange={(e) => onChange(e.target.value)}
            className={className ?? DEFAULT_CLASS}
        >
            {emptyLabel !== undefined && <option value="">{emptyLabel}</option>}
            {rows.map(({ unit, depth }) => (
                <option
                    key={unit.id}
                    value={unit.id}
                    disabled={allowedIds !== undefined && !allowedIds.has(unit.id)}
                >
                    {'\u00A0\u00A0\u00A0'.repeat(depth)}{depth > 0 ? '└ ' : ''}{unit.name}
                    {unit.code ? ` (${unit.code})` : ''}
                    {!unit.isActive ? ' — desființată' : ''}
                </option>
            ))}
        </select>
    );
}
