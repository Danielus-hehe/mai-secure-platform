/**
 * Structura organizatorică (doar Administrator).
 *
 * Arborele Direcție → Secție → Serviciu, cu șeful fiecărei subdiviziuni.
 * Șeful e definit aici — de unitatea condusă — nu de rolul contului: un șef
 * de serviciu poate avea rolul Utilizator și totuși distribuie documente
 * interne subordonaților lui.
 */

import { useCallback, useEffect, useMemo, useState } from 'react';
import {
    Network, Plus, Pencil, Trash2, Crown, Loader2, Users, ChevronRight, Building2, Power,
} from 'lucide-react';
import PageHeader from '../../components/ui/PageHeader';
import Button from '../../components/ui/Button';
import Badge from '../../components/ui/Badge';
import Modal from '../../components/ui/Modal';
import Input from '../../components/ui/Input';
import EmptyState from '../../components/ui/EmptyState';
import OrgUnitSelect from '../../components/org/OrgUnitSelect';
import RecipientCombobox from '../../components/transfers/RecipientCombobox';
import { useToast } from '../../context/ToastContext';
import { apiErrorMessage } from '../../api/errors';
import {
    listOrgUnits, listOrgUnitMembers, createOrgUnit, updateOrgUnit, setOrgUnitHead, deleteOrgUnit,
    OrgUnitType, ORG_UNIT_TYPE_LABELS,
    type OrgUnit, type OrgUnitMember,
} from '../../api/orgUnits';
import { listDirectory, type DirectoryUser } from '../../api/users';
import { buildOrgTree, subtreeIds } from '../../utils/orgTree';

const TYPE_TONE: Record<OrgUnitType, 'blue' | 'gold' | 'gray'> = {
    [OrgUnitType.Directie]: 'blue',
    [OrgUnitType.Sectie]:   'gold',
    [OrgUnitType.Serviciu]: 'gray',
};

interface EditState {
    id: string | null;          // null = subdiviziune nouă
    name: string;
    code: string;
    type: OrgUnitType;
    parentId: string;
    isActive: boolean;
}

/** Nivelul implicit al unei subunități: imediat sub părinte. */
const childType = (parent?: OrgUnit): OrgUnitType =>
    !parent ? OrgUnitType.Directie
        : parent.type === OrgUnitType.Directie ? OrgUnitType.Sectie
            : OrgUnitType.Serviciu;

export default function OrgUnitsPage() {
    const toast = useToast();

    const [units, setUnits] = useState<OrgUnit[]>([]);
    const [loading, setLoading] = useState(true);
    const [showInactive, setShowInactive] = useState(false);

    const [selectedId, setSelectedId] = useState<string | null>(null);
    const [members, setMembers] = useState<OrgUnitMember[]>([]);
    const [membersLoading, setMembersLoading] = useState(false);

    const [edit, setEdit] = useState<EditState | null>(null);
    const [saving, setSaving] = useState(false);

    const [headUnit, setHeadUnit] = useState<OrgUnit | null>(null);
    const [directory, setDirectory] = useState<DirectoryUser[]>([]);
    const [headPick, setHeadPick] = useState<string[]>([]);

    const load = useCallback(async () => {
        setLoading(true);
        try {
            setUnits(await listOrgUnits(true));
        } catch (e) {
            toast.error(apiErrorMessage(e, 'Structura nu a putut fi încărcată.'));
        } finally {
            setLoading(false);
        }
    }, [toast]);

    useEffect(() => { void load(); }, [load]);

    const visible = useMemo(() => units.filter((u) => showInactive || u.isActive), [units, showInactive]);
    const rows = useMemo(() => buildOrgTree(visible), [visible]);
    const selected = units.find((u) => u.id === selectedId) ?? null;

    // Membrii subdiviziunii selectate.
    useEffect(() => {
        if (!selectedId) return;
        let cancelled = false;
        listOrgUnitMembers(selectedId)
            .then((m) => { if (!cancelled) setMembers(m); })
            .catch(() => { if (!cancelled) toast.error('Membrii nu au putut fi încărcați.'); })
            .finally(() => { if (!cancelled) setMembersLoading(false); });
        return () => { cancelled = true; };
    }, [selectedId, toast]);

    const selectUnit = (id: string) => {
        if (id === selectedId) return;
        setMembers([]);
        setMembersLoading(true);
        setSelectedId(id);
    };

    // ── Creare / editare ─────────────────────────────────────────────────────

    const openCreate = (parent?: OrgUnit) =>
        setEdit({ id: null, name: '', code: '', type: childType(parent), parentId: parent?.id ?? '', isActive: true });

    const openEdit = (u: OrgUnit) =>
        setEdit({ id: u.id, name: u.name, code: u.code ?? '', type: u.type, parentId: u.parentId ?? '', isActive: u.isActive });

    const handleSave = async () => {
        if (!edit || !edit.name.trim()) return;
        setSaving(true);
        try {
            const input = {
                name: edit.name.trim(),
                code: edit.code.trim() || null,
                type: edit.type,
                parentId: edit.parentId || null,
            };
            const result = edit.id
                ? await updateOrgUnit(edit.id, { ...input, isActive: edit.isActive })
                : await createOrgUnit(input);
            toast.success(result.message);
            setEdit(null);
            await load();
        } catch (e) {
            toast.error(apiErrorMessage(e, 'Subdiviziunea nu a putut fi salvată.'));
        } finally {
            setSaving(false);
        }
    };

    const handleDelete = async (u: OrgUnit) => {
        if (!window.confirm(`Ștergeți subdiviziunea „${u.name}”?\n\nSe poate șterge doar o subdiviziune goală, fără subunități, membri sau documente distribuite. Altfel, desființați-o.`)) return;
        try {
            const result = await deleteOrgUnit(u.id);
            toast.success(result.message);
            if (selectedId === u.id) setSelectedId(null);
            await load();
        } catch (e) {
            toast.error(apiErrorMessage(e, 'Subdiviziunea nu a putut fi ștearsă.'));
        }
    };

    // ── Numirea șefului ──────────────────────────────────────────────────────

    const openHead = async (u: OrgUnit) => {
        setHeadUnit(u);
        setHeadPick(u.headUserId ? [u.headUserId] : []);
        if (directory.length === 0) {
            try {
                setDirectory(await listDirectory());
            } catch (e) {
                toast.error(apiErrorMessage(e, 'Lista utilizatorilor nu a putut fi încărcată.'));
            }
        }
    };

    const handleSetHead = async (userId: string | null) => {
        if (!headUnit) return;
        setSaving(true);
        try {
            const result = await setOrgUnitHead(headUnit.id, userId);
            toast.success(result.message);
            setHeadUnit(null);
            await load();
            if (selectedId === headUnit.id) {
                setMembersLoading(true);
                setMembers(await listOrgUnitMembers(headUnit.id));
                setMembersLoading(false);
            }
        } catch (e) {
            toast.error(apiErrorMessage(e, 'Șeful nu a putut fi numit.'));
        } finally {
            setSaving(false);
        }
    };

    // La editare, părintele nu poate fi subdiviziunea însăși sau un descendent.
    const editId = edit?.id ?? null;
    const forbiddenParents = useMemo(
        () => (editId ? subtreeIds(units, editId) : new Set<string>()),
        [editId, units]
    );

    const selectCls = 'w-full rounded-lg border border-mai-200 dark:border-mai-600 bg-white dark:bg-mai-800 ' +
        'dark:text-mai-200 px-3 py-2 text-sm focus:border-mai-500 focus:outline-none focus:ring-2 focus:ring-mai-500/20';

    return (
        <div className="space-y-5">
            <PageHeader
                title="Structura organizatorică"
                subtitle="Direcții, secții și servicii — cu șefii lor. Distribuția documentelor interne urmează această structură."
                actions={
                    <Button onClick={() => openCreate()}>
                        <Plus size={16} /> Direcție nouă
                    </Button>
                }
            />

            <label className="inline-flex cursor-pointer items-center gap-2 text-sm text-mai-600 dark:text-mai-300">
                <input
                    type="checkbox"
                    checked={showInactive}
                    onChange={(e) => setShowInactive(e.target.checked)}
                    className="h-4 w-4 rounded border-mai-300 text-mai-600 focus:ring-mai-500"
                />
                Arată și subdiviziunile desființate
            </label>

            <div className="grid grid-cols-1 gap-5 lg:grid-cols-[minmax(0,3fr)_minmax(0,2fr)]">
                {/* ── Arborele ─────────────────────────────────────────── */}
                <div className="overflow-hidden rounded-xl border border-mai-100 bg-white dark:border-mai-700 dark:bg-mai-800">
                    {loading ? (
                        <div className="flex items-center justify-center gap-3 py-16 text-mai-400">
                            <Loader2 size={18} className="animate-spin" /> Se încarcă…
                        </div>
                    ) : rows.length === 0 ? (
                        <div className="p-6">
                            <EmptyState
                                icon={Network}
                                title="Nicio subdiviziune"
                                description="Începeți cu o direcție, apoi adăugați secțiile și serviciile ei."
                            />
                        </div>
                    ) : (
                        <ul className="divide-y divide-mai-50 dark:divide-mai-700">
                            {rows.map(({ unit, depth }) => (
                                <li
                                    key={unit.id}
                                    className={`flex items-center gap-2 py-2.5 pr-3 transition-colors
                                        ${selectedId === unit.id ? 'bg-mai-50 dark:bg-mai-700/50' : 'hover:bg-mai-50/60 dark:hover:bg-mai-700/30'}
                                        ${unit.isActive ? '' : 'opacity-60'}`}
                                    style={{ paddingLeft: `${1 + depth * 1.5}rem` }}
                                >
                                    <button
                                        type="button"
                                        onClick={() => selectUnit(unit.id)}
                                        className="flex min-w-0 flex-1 items-center gap-2 text-left"
                                    >
                                        {depth > 0 && <ChevronRight size={13} className="shrink-0 text-mai-300" />}
                                        <div className="min-w-0">
                                            <p className="flex items-center gap-2 truncate text-sm font-medium text-mai-900 dark:text-white">
                                                {unit.name}
                                                {unit.code && <span className="text-xs font-normal text-mai-400">{unit.code}</span>}
                                            </p>
                                            <p className="flex flex-wrap items-center gap-2 text-xs text-mai-400">
                                                <Badge tone={TYPE_TONE[unit.type]}>{ORG_UNIT_TYPE_LABELS[unit.type]}</Badge>
                                                {!unit.isActive && <Badge tone="gray">Desființată</Badge>}
                                                <span className="inline-flex items-center gap-1">
                                                    <Crown size={11} className={unit.headName ? 'text-gold-600' : ''} />
                                                    {unit.headName ?? 'fără șef'}
                                                </span>
                                                <span className="inline-flex items-center gap-1">
                                                    <Users size={11} /> {unit.memberCount}
                                                </span>
                                            </p>
                                        </div>
                                    </button>

                                    <div className="flex shrink-0 items-center gap-1">
                                        {unit.isActive && unit.type !== OrgUnitType.Serviciu && (
                                            <Button variant="ghost" className="!px-2 !py-1.5" title="Adaugă subunitate" onClick={() => openCreate(unit)}>
                                                <Plus size={14} />
                                            </Button>
                                        )}
                                        {unit.isActive && (
                                            <Button variant="ghost" className="!px-2 !py-1.5" title="Numește șeful" onClick={() => void openHead(unit)}>
                                                <Crown size={14} />
                                            </Button>
                                        )}
                                        <Button variant="ghost" className="!px-2 !py-1.5" title="Modifică" onClick={() => openEdit(unit)}>
                                            <Pencil size={14} />
                                        </Button>
                                        <Button
                                            variant="ghost"
                                            className="!px-2 !py-1.5 text-red-600 hover:bg-red-50 dark:text-red-400 dark:hover:bg-red-900/30"
                                            title="Șterge (doar dacă e goală)"
                                            onClick={() => void handleDelete(unit)}
                                        >
                                            <Trash2 size={14} />
                                        </Button>
                                    </div>
                                </li>
                            ))}
                        </ul>
                    )}
                </div>

                {/* ── Membrii subdiviziunii selectate ──────────────────── */}
                <div className="rounded-xl border border-mai-100 bg-white p-5 dark:border-mai-700 dark:bg-mai-800">
                    {!selected ? (
                        <EmptyState
                            icon={Building2}
                            title="Alegeți o subdiviziune"
                            description="Membrii ei apar aici. Încadrarea se schimbă din „Gestiune utilizatori”."
                        />
                    ) : (
                        <div className="space-y-3">
                            <div>
                                <h2 className="font-bold text-mai-900 dark:text-white">{selected.name}</h2>
                                <p className="text-xs text-mai-400">
                                    {ORG_UNIT_TYPE_LABELS[selected.type]} · {selected.memberCount} membri activi
                                </p>
                            </div>
                            {membersLoading ? (
                                <div className="flex items-center gap-2 py-6 text-sm text-mai-400">
                                    <Loader2 size={15} className="animate-spin" /> Se încarcă membrii…
                                </div>
                            ) : members.length === 0 ? (
                                <p className="py-4 text-sm text-mai-400">Nimeni nu este încadrat direct aici.</p>
                            ) : (
                                <ul className="divide-y divide-mai-50 dark:divide-mai-700">
                                    {members.map((m) => (
                                        <li key={m.id} className="flex items-center justify-between gap-3 py-2">
                                            <div className="min-w-0">
                                                <p className="truncate text-sm font-medium text-mai-800 dark:text-mai-100">{m.fullName}</p>
                                                <p className="text-xs text-mai-400">@{m.username}</p>
                                            </div>
                                            <div className="flex shrink-0 gap-1">
                                                {m.isHead && <Badge tone="gold"><Crown size={11} className="mr-1" />Șef</Badge>}
                                                {!m.isActive && <Badge tone="gray">Dezactivat</Badge>}
                                            </div>
                                        </li>
                                    ))}
                                </ul>
                            )}
                            <p className="text-[11px] text-mai-400">
                                Membrii subunităților nu apar aici — sunt încadrați în subunitatea lor.
                            </p>
                        </div>
                    )}
                </div>
            </div>

            {/* ── Modal: creare / editare ──────────────────────────────── */}
            {edit && (
                <Modal
                    open
                    title={edit.id ? 'Modifică subdiviziunea' : 'Subdiviziune nouă'}
                    onClose={() => { if (!saving) setEdit(null); }}
                >
                    <div className="space-y-4">
                        <Input
                            id="unit-name"
                            label="Denumire *"
                            value={edit.name}
                            onChange={(e) => setEdit({ ...edit, name: e.target.value })}
                            placeholder="ex: Direcția Tehnologii Informaționale"
                        />
                        <Input
                            id="unit-code"
                            label="Prescurtare"
                            value={edit.code}
                            maxLength={20}
                            onChange={(e) => setEdit({ ...edit, code: e.target.value })}
                            placeholder="ex: DTI"
                        />
                        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
                            <div>
                                <label htmlFor="unit-type" className="mb-1.5 block text-sm font-medium text-mai-800 dark:text-mai-200">Nivel</label>
                                <select
                                    id="unit-type"
                                    value={edit.type}
                                    onChange={(e) => setEdit({ ...edit, type: Number(e.target.value) as OrgUnitType })}
                                    className={selectCls}
                                >
                                    {Object.values(OrgUnitType).map((t) => (
                                        <option key={t} value={t}>{ORG_UNIT_TYPE_LABELS[t]}</option>
                                    ))}
                                </select>
                            </div>
                            <div>
                                <label htmlFor="unit-parent" className="mb-1.5 block text-sm font-medium text-mai-800 dark:text-mai-200">În subordinea</label>
                                <OrgUnitSelect
                                    id="unit-parent"
                                    units={units.filter((u) => u.isActive)}
                                    value={edit.parentId}
                                    onChange={(v) => setEdit({ ...edit, parentId: v })}
                                    emptyLabel="— nivel de vârf —"
                                    hiddenIds={forbiddenParents}
                                />
                            </div>
                        </div>
                        <p className="text-xs text-mai-400">
                            Ordinea nivelurilor este Direcție → Secție → Serviciu; serverul refuză o plasare inversă.
                        </p>

                        {edit.id && (
                            <label className="flex cursor-pointer items-start gap-3 rounded-lg border border-mai-100 px-4 py-3 dark:border-mai-700">
                                <input
                                    type="checkbox"
                                    checked={!edit.isActive}
                                    onChange={(e) => setEdit({ ...edit, isActive: !e.target.checked })}
                                    className="mt-0.5 h-4 w-4 rounded border-mai-300 text-mai-600 focus:ring-mai-500"
                                />
                                <span className="text-sm">
                                    <span className="inline-flex items-center gap-1 font-medium text-mai-800 dark:text-mai-100">
                                        <Power size={13} /> Subdiviziune desființată
                                    </span>
                                    <span className="block text-xs text-mai-500 dark:text-mai-400">
                                        Nu mai apare la distribuție și încadrare; istoricul documentelor rămâne corect.
                                    </span>
                                </span>
                            </label>
                        )}

                        <div className="flex justify-end gap-2">
                            <Button variant="secondary" disabled={saving} onClick={() => setEdit(null)}>Anulează</Button>
                            <Button disabled={saving || !edit.name.trim()} onClick={() => void handleSave()}>
                                {saving && <Loader2 size={15} className="animate-spin" />}
                                Salvează
                            </Button>
                        </div>
                    </div>
                </Modal>
            )}

            {/* ── Modal: numirea șefului ───────────────────────────────── */}
            {headUnit && (
                <Modal open title={`Șeful subdiviziunii „${headUnit.name}”`} onClose={() => { if (!saving) setHeadUnit(null); }}>
                    <div className="space-y-4">
                        <p className="text-sm text-mai-500 dark:text-mai-400">
                            Persoana aleasă e încadrată automat în această subdiviziune. Dacă mai conducea
                            alta, acea funcție se eliberează — un cont conduce o singură subdiviziune.
                        </p>
                        <RecipientCombobox
                            inputId="head-user"
                            recipients={directory}
                            value={headPick}
                            onChange={(ids) => setHeadPick(ids.slice(-1))}
                            max={1}
                            disabled={saving}
                            noResultsText="Niciun utilizator activ nu corespunde căutării."
                            availableNoun={['persoană disponibilă', 'persoane disponibile']}
                        />
                        <div className="flex flex-wrap justify-between gap-2">
                            <Button
                                variant="ghost"
                                disabled={saving || !headUnit.headUserId}
                                onClick={() => void handleSetHead(null)}
                            >
                                Eliberează funcția
                            </Button>
                            <div className="flex gap-2">
                                <Button variant="secondary" disabled={saving} onClick={() => setHeadUnit(null)}>Anulează</Button>
                                <Button
                                    disabled={saving || headPick.length === 0 || headPick[0] === headUnit.headUserId}
                                    onClick={() => void handleSetHead(headPick[0])}
                                >
                                    {saving ? <Loader2 size={15} className="animate-spin" /> : <Crown size={15} />}
                                    Numește
                                </Button>
                            </div>
                        </div>
                    </div>
                </Modal>
            )}
        </div>
    );
}
