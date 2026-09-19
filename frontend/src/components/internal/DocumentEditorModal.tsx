import { useEffect, useMemo, useState } from 'react';
import { Loader2, Save, Users, AlertTriangle } from 'lucide-react';
import Modal from '../ui/Modal';
import Button from '../ui/Button';
import Input from '../ui/Input';
import UnitChecklist from '../org/UnitChecklist';
import RecipientCombobox from '../transfers/RecipientCombobox';
import { useToast } from '../../context/ToastContext';
import { apiErrorMessage } from '../../api/errors';
import { formatFileSize } from '../../utils/format';
import { listOrgUnits, type OrgUnit } from '../../api/orgUnits';
import { listDirectory, type DirectoryUser } from '../../api/users';
import {
    getDistributionOptions, previewDistribution, createInternalDocument, updateInternalDocument,
    DistributionMode, DISTRIBUTION_LABELS,
    type DistributionOptions, type DistributionPreview, type InternalDocumentDetail,
} from '../../api/internalDocuments';

interface Props {
    /** Ciorna de editat; null = document nou. */
    existing: InternalDocumentDetail | null;
    onClose: () => void;
    onSaved: (id: string) => void;
}

const MODE_ORDER: DistributionMode[] = [
    DistributionMode.MyUnitTree,
    DistributionMode.DirectSubordinates,
    DistributionMode.SelectedUnits,
    DistributionMode.UnitHeads,
    DistributionMode.SpecificUsers,
    DistributionMode.WholeInstitution,
];

/**
 * Crearea și editarea unei ciorne.
 *
 * Opțiunile de distribuție vin de la server (ce poate alege autorul curent),
 * iar numărul de destinatari se previzualizează tot pe server, cu aceeași
 * regulă care se aplică la publicare. Interfața nu reface regulile.
 */
export default function DocumentEditorModal({ existing, onClose, onSaved }: Props) {
    const toast = useToast();

    const [options, setOptions] = useState<DistributionOptions | null>(null);
    const [units, setUnits] = useState<OrgUnit[]>([]);
    const [directory, setDirectory] = useState<DirectoryUser[]>([]);
    const [loading, setLoading] = useState(true);

    const [title, setTitle] = useState(existing?.title ?? '');
    const [number, setNumber] = useState(existing?.number ?? '');
    const [summary, setSummary] = useState(existing?.summary ?? '');
    const [file, setFile] = useState<File | null>(null);
    const [requiresAck, setRequiresAck] = useState(existing?.requiresAcknowledgement ?? true);

    const [mode, setMode] = useState<DistributionMode | null>(existing?.distributionMode ?? null);
    const [unitIds, setUnitIds] = useState<string[]>(existing?.targetUnits.map((u) => u.id) ?? []);
    const [userIds, setUserIds] = useState<string[]>(existing?.targetUsers.map((u) => u.id) ?? []);
    const [includeSubunits, setIncludeSubunits] = useState(existing?.includeSubunits ?? true);

    const [preview, setPreview] = useState<DistributionPreview | null>(null);
    const [previewError, setPreviewError] = useState<string | null>(null);
    const [previewing, setPreviewing] = useState(false);
    const [saving, setSaving] = useState(false);

    // Datele formularului: opțiunile autorului, structura, directorul.
    useEffect(() => {
        let cancelled = false;
        Promise.all([getDistributionOptions(), listOrgUnits(), listDirectory()])
            .then(([opts, orgUnits, users]) => {
                if (cancelled) return;
                setOptions(opts);
                setUnits(orgUnits);
                setDirectory(users);
                // Modul implicit: primul permis (subdiviziunea mea pentru un șef,
                // persoane anume pentru ceilalți).
                setMode((m) => m ?? opts.modes[0] ?? DistributionMode.SpecificUsers);
            })
            .catch((e) => { if (!cancelled) toast.error(apiErrorMessage(e, 'Formularul nu a putut fi pregătit.')); })
            .finally(() => { if (!cancelled) setLoading(false); });
        return () => { cancelled = true; };
    }, [toast]);

    const selectable = useMemo(() => new Set(options?.selectableUnitIds ?? []), [options]);

    const distribution = useMemo(() => ({
        mode: mode ?? DistributionMode.SpecificUsers,
        unitIds: mode === DistributionMode.SelectedUnits ? unitIds : [],
        userIds: mode === DistributionMode.SpecificUsers ? userIds : [],
        includeSubunits,
    }), [mode, unitIds, userIds, includeSubunits]);

    // Previzualizarea destinatarilor, cu o mică întârziere la schimbări rapide.
    useEffect(() => {
        if (mode === null || loading) return;
        const needsTargets =
            (mode === DistributionMode.SelectedUnits && unitIds.length === 0) ||
            (mode === DistributionMode.SpecificUsers && userIds.length === 0);

        let cancelled = false;
        const timer = window.setTimeout(() => {
            if (needsTargets) {
                setPreview(null);
                setPreviewError(null);
                return;
            }
            setPreviewing(true);
            previewDistribution(distribution)
                .then((p) => { if (!cancelled) { setPreview(p); setPreviewError(null); } })
                .catch((e) => { if (!cancelled) { setPreview(null); setPreviewError(apiErrorMessage(e, 'Distribuția nu este validă.')); } })
                .finally(() => { if (!cancelled) setPreviewing(false); });
        }, 300);

        return () => { cancelled = true; window.clearTimeout(timer); };
    }, [distribution, mode, unitIds.length, userIds.length, loading]);

    const canSave = !!title.trim() && (existing !== null || file !== null) && mode !== null && !saving;

    const handleSave = async () => {
        if (!canSave) return;
        setSaving(true);
        try {
            const input = {
                ...distribution,
                title: title.trim(),
                number: number.trim(),
                summary: summary.trim(),
                requiresAcknowledgement: requiresAck,
                file,
            };
            if (existing) {
                const result = await updateInternalDocument(existing.id, input);
                toast.success(result.message);
                onSaved(existing.id);
            } else {
                const result = await createInternalDocument(input);
                toast.success(result.message);
                onSaved(result.id);
            }
        } catch (e) {
            toast.error(apiErrorMessage(e, 'Documentul nu a putut fi salvat.'));
        } finally {
            setSaving(false);
        }
    };

    return (
        <Modal open wide title={existing ? 'Modifică ciorna' : 'Document intern nou'} onClose={() => { if (!saving) onClose(); }}>
            {loading ? (
                <div className="flex items-center justify-center gap-3 py-12 text-mai-400">
                    <Loader2 size={18} className="animate-spin" /> Se pregătește formularul…
                </div>
            ) : (
                <div className="space-y-5">
                    {/* ── Documentul ─────────────────────────────────────── */}
                    <div className="grid grid-cols-1 gap-4 sm:grid-cols-[minmax(0,1fr)_12rem]">
                        <Input id="doc-title" label="Titlu *" value={title} maxLength={300}
                               onChange={(e) => setTitle(e.target.value)} placeholder="ex: Dispoziție privind programul de lucru" />
                        <Input id="doc-number" label="Număr de înregistrare" value={number} maxLength={64}
                               onChange={(e) => setNumber(e.target.value)} placeholder="ex: D-2026/0142" />
                    </div>

                    <div>
                        <label htmlFor="doc-summary" className="mb-1.5 block text-sm font-medium text-mai-800 dark:text-mai-200">Rezumat</label>
                        <textarea
                            id="doc-summary"
                            value={summary}
                            maxLength={2000}
                            rows={3}
                            onChange={(e) => setSummary(e.target.value)}
                            placeholder="Pe scurt, despre ce este documentul - apare în lista destinatarilor."
                            className="w-full rounded-lg border border-mai-200 bg-white px-3.5 py-2.5 text-sm dark:border-mai-600
                                dark:bg-mai-800 dark:text-mai-100 focus:border-mai-500 focus:outline-none focus:ring-2 focus:ring-mai-500"
                        />
                    </div>

                    <div>
                        <label htmlFor="doc-file" className="mb-1.5 block text-sm font-medium text-mai-800 dark:text-mai-200">
                            Fișier {existing ? '(opțional - înlocuiește fișierul actual)' : '*'}
                        </label>
                        <input
                            id="doc-file"
                            type="file"
                            disabled={saving}
                            onChange={(e) => setFile(e.target.files?.[0] ?? null)}
                            className="w-full rounded-lg border border-mai-200 bg-white px-3 py-2 text-sm dark:border-mai-600
                                dark:bg-mai-800 dark:text-mai-200 file:mr-3 file:rounded-md file:border-0 file:bg-mai-100
                                file:px-3 file:py-1.5 file:text-sm file:font-medium file:text-mai-700 dark:file:bg-mai-700 dark:file:text-mai-200"
                        />
                        {existing && !file && (
                            <p className="mt-1.5 text-xs text-mai-400">
                                Fișier actual: {existing.fileName} - {formatFileSize(existing.fileSize)}
                            </p>
                        )}
                    </div>

                    {/* ── Distribuția ────────────────────────────────────── */}
                    <fieldset className="space-y-3">
                        <legend className="text-sm font-medium text-mai-800 dark:text-mai-200">Cui se distribuie</legend>

                        {options?.ledUnitName ? (
                            <p className="text-xs text-mai-500 dark:text-mai-400">
                                Conduceți: <strong className="text-mai-700 dark:text-mai-200">{options.ledUnitName}</strong>
                            </p>
                        ) : !options?.isAdmin && (
                            <p className="text-xs text-mai-500 dark:text-mai-400">
                                Nu conduceți nicio subdiviziune, așa că puteți adresa documentul unor persoane anume.
                            </p>
                        )}

                        <div className="grid grid-cols-1 gap-2 sm:grid-cols-2">
                            {MODE_ORDER.filter((m) => options?.modes.includes(m)).map((m) => (
                                <label
                                    key={m}
                                    className={`flex cursor-pointer items-start gap-3 rounded-lg border px-3.5 py-2.5 transition-colors
                                        ${mode === m
                                            ? 'border-mai-500 bg-mai-50 dark:border-mai-400 dark:bg-mai-700/50'
                                            : 'border-mai-100 hover:border-mai-300 dark:border-mai-700'}`}
                                >
                                    <input
                                        type="radio"
                                        name="distribution-mode"
                                        checked={mode === m}
                                        disabled={saving}
                                        onChange={() => setMode(m)}
                                        className="mt-0.5 h-4 w-4 border-mai-300 text-mai-600 focus:ring-mai-500"
                                    />
                                    <span className="text-sm">
                                        <span className="font-medium text-mai-800 dark:text-mai-100">{DISTRIBUTION_LABELS[m].title}</span>
                                        <span className="block text-xs text-mai-500 dark:text-mai-400">{DISTRIBUTION_LABELS[m].hint}</span>
                                    </span>
                                </label>
                            ))}
                        </div>

                        {mode === DistributionMode.SelectedUnits && (
                            <div className="space-y-2">
                                <UnitChecklist units={units} value={unitIds} onChange={setUnitIds} allowedIds={selectable} disabled={saving} />
                                <label className="inline-flex cursor-pointer items-center gap-2 text-sm text-mai-700 dark:text-mai-200">
                                    <input
                                        type="checkbox"
                                        checked={includeSubunits}
                                        onChange={(e) => setIncludeSubunits(e.target.checked)}
                                        className="h-4 w-4 rounded border-mai-300 text-mai-600 focus:ring-mai-500"
                                    />
                                    Include și subunitățile subdiviziunilor alese
                                </label>
                            </div>
                        )}

                        {mode === DistributionMode.SpecificUsers && (
                            <RecipientCombobox
                                inputId="doc-users"
                                recipients={directory}
                                value={userIds}
                                onChange={setUserIds}
                                max={500}
                                disabled={saving}
                                noResultsText="Niciun utilizator activ nu corespunde căutării."
                                availableNoun={['persoană disponibilă', 'persoane disponibile']}
                            />
                        )}

                        {/* Previzualizarea destinatarilor */}
                        <div className="rounded-lg border border-mai-100 bg-mai-50 px-4 py-3 dark:border-mai-700 dark:bg-mai-900">
                            {previewing ? (
                                <p className="flex items-center gap-2 text-sm text-mai-500"><Loader2 size={14} className="animate-spin" /> Se calculează destinatarii…</p>
                            ) : previewError ? (
                                <p className="flex items-start gap-2 text-sm text-red-600 dark:text-red-400">
                                    <AlertTriangle size={15} className="mt-0.5 shrink-0" /> {previewError}
                                </p>
                            ) : preview ? (
                                <div className="space-y-1.5">
                                    <p className="flex items-center gap-2 text-sm font-medium text-mai-800 dark:text-mai-100">
                                        <Users size={15} />
                                        {preview.count} {preview.count === 1 ? 'destinatar' : 'destinatari'}
                                        {preview.units.length > 1 && ` în ${preview.units.length} subdiviziuni`}
                                    </p>
                                    <ul className="text-xs text-mai-500 dark:text-mai-400">
                                        {preview.units.slice(0, 6).map((u) => (
                                            <li key={u.orgUnitId ?? 'none'}>{u.name}: {u.count}</li>
                                        ))}
                                        {preview.units.length > 6 && <li>… și încă {preview.units.length - 6} subdiviziuni</li>}
                                    </ul>
                                    <p className="text-[11px] text-mai-400">
                                        Lista se fixează la publicare, după structura din acel moment.
                                    </p>
                                </div>
                            ) : (
                                <p className="text-sm text-mai-400">Alegeți ținta distribuției pentru a vedea destinatarii.</p>
                            )}
                        </div>
                    </fieldset>

                    <label className="flex cursor-pointer items-start gap-3 rounded-lg border border-mai-100 px-4 py-3 dark:border-mai-700">
                        <input
                            type="checkbox"
                            checked={requiresAck}
                            onChange={(e) => setRequiresAck(e.target.checked)}
                            className="mt-0.5 h-4 w-4 rounded border-mai-300 text-mai-600 focus:ring-mai-500"
                        />
                        <span className="text-sm">
                            <span className="font-medium text-mai-800 dark:text-mai-100">Cere confirmarea „Luat la cunoștință”</span>
                            <span className="block text-xs text-mai-500 dark:text-mai-400">
                                Fiecare destinatar confirmă după ce deschide documentul; primiți un raport cu cine a confirmat.
                            </span>
                        </span>
                    </label>

                    <div className="flex justify-end gap-2 border-t border-mai-100 pt-4 dark:border-mai-700">
                        <Button variant="secondary" disabled={saving} onClick={onClose}>Anulează</Button>
                        <Button disabled={!canSave} onClick={() => void handleSave()}>
                            {saving ? <Loader2 size={15} className="animate-spin" /> : <Save size={15} />}
                            Salvează ciorna
                        </Button>
                    </div>
                </div>
            )}
        </Modal>
    );
}
