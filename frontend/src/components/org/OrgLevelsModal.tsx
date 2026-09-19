import { useState } from 'react';
import { Plus, Pencil, Trash2, Loader2, Check, X } from 'lucide-react';
import Modal from '../ui/Modal';
import Button from '../ui/Button';
import Input from '../ui/Input';
import { useToast } from '../../context/ToastContext';
import { apiErrorMessage } from '../../api/errors';
import { createOrgLevel, renameOrgLevel, deleteOrgLevel, type OrgLevel } from '../../api/orgUnits';

interface Props {
    levels: OrgLevel[];
    onClose: () => void;
    onChanged: () => void;
}

/**
 * Nivelurile structurii: redenumire, adăugare într-o poziție anume, ștergere.
 *
 * Ordinea contează: o subdiviziune poate sta doar sub una de nivel superior.
 * Un nivel nou se inserează „sub” un nivel existent (sau deasupra tuturor),
 * iar serverul îi alege rangul fără să atingă subdiviziunile existente.
 */
export default function OrgLevelsModal({ levels, onClose, onChanged }: Props) {
    const toast = useToast();

    const [editing, setEditing] = useState<number | null>(null);
    const [editName, setEditName] = useState('');
    const [newName, setNewName] = useState('');
    const [position, setPosition] = useState<string>(levels.length > 0 ? String(levels[levels.length - 1].rank) : '');
    const [busy, setBusy] = useState(false);

    const run = async (action: () => Promise<{ message: string }>) => {
        setBusy(true);
        try {
            toast.success((await action()).message);
            onChanged();
            return true;
        } catch (e) {
            toast.error(apiErrorMessage(e, 'Operația nu a reușit.'));
            return false;
        } finally {
            setBusy(false);
        }
    };

    const handleAdd = async () => {
        if (!newName.trim()) return;
        const ok = await run(() => createOrgLevel(newName.trim(), position === '' ? null : Number(position)));
        if (ok) setNewName('');
    };

    const handleRename = async (rank: number) => {
        if (!editName.trim()) return;
        const ok = await run(() => renameOrgLevel(rank, editName.trim()));
        if (ok) setEditing(null);
    };

    const handleDelete = async (level: OrgLevel) => {
        if (!window.confirm(`Ștergeți nivelul „${level.name}”?`)) return;
        await run(() => deleteOrgLevel(level.rank));
    };

    const selectCls = 'w-full rounded-lg border border-mai-200 dark:border-mai-600 bg-white dark:bg-mai-800 ' +
        'dark:text-mai-200 px-3 py-2.5 text-sm focus:border-mai-500 focus:outline-none focus:ring-2 focus:ring-mai-500/20';

    return (
        <Modal open title="Nivelurile structurii" onClose={() => { if (!busy) onClose(); }}>
            <div className="space-y-5">
                <p className="text-sm text-mai-500 dark:text-mai-400">
                    De sus în jos, în ordinea ierarhiei. O subdiviziune poate sta doar sub una de
                    nivel superior; nivelurile se pot sări (un serviciu direct sub o direcție e permis).
                </p>

                <ol className="divide-y divide-mai-50 rounded-lg border border-mai-100 dark:divide-mai-700 dark:border-mai-700">
                    {levels.map((level, i) => (
                        <li key={level.rank} className="flex items-center gap-3 px-3 py-2.5">
                            <span className="w-6 text-center text-xs font-bold text-mai-400">{i + 1}.</span>
                            {editing === level.rank ? (
                                <>
                                    <input
                                        autoFocus
                                        value={editName}
                                        maxLength={60}
                                        onChange={(e) => setEditName(e.target.value)}
                                        onKeyDown={(e) => { if (e.key === 'Enter') void handleRename(level.rank); if (e.key === 'Escape') setEditing(null); }}
                                        className="min-w-0 flex-1 rounded-md border border-mai-200 px-2 py-1 text-sm dark:border-mai-600 dark:bg-mai-800 dark:text-mai-100"
                                    />
                                    <Button variant="ghost" className="!px-2 !py-1" disabled={busy} onClick={() => void handleRename(level.rank)} title="Salvează">
                                        <Check size={14} />
                                    </Button>
                                    <Button variant="ghost" className="!px-2 !py-1" disabled={busy} onClick={() => setEditing(null)} title="Renunță">
                                        <X size={14} />
                                    </Button>
                                </>
                            ) : (
                                <>
                                    <span className="flex-1 text-sm font-medium text-mai-800 dark:text-mai-100">{level.name}</span>
                                    <span className="text-xs text-mai-400">{level.unitCount} subdiviziuni</span>
                                    <Button variant="ghost" className="!px-2 !py-1" disabled={busy}
                                            onClick={() => { setEditing(level.rank); setEditName(level.name); }} title="Redenumește">
                                        <Pencil size={14} />
                                    </Button>
                                    <Button variant="ghost"
                                            className="!px-2 !py-1 text-red-600 hover:bg-red-50 dark:text-red-400 dark:hover:bg-red-900/30"
                                            disabled={busy || level.unitCount > 0}
                                            title={level.unitCount > 0 ? 'Nivelul are subdiviziuni' : 'Șterge'}
                                            onClick={() => void handleDelete(level)}>
                                        <Trash2 size={14} />
                                    </Button>
                                </>
                            )}
                        </li>
                    ))}
                </ol>

                <div className="space-y-3 border-t border-mai-100 pt-4 dark:border-mai-700">
                    <p className="text-sm font-medium text-mai-800 dark:text-mai-100">Nivel nou</p>
                    <Input
                        id="level-name"
                        label="Denumire"
                        value={newName}
                        maxLength={60}
                        onChange={(e) => setNewName(e.target.value)}
                        placeholder="ex: Departament, Inspectorat, Birou"
                    />
                    <div>
                        <label htmlFor="level-position" className="mb-1.5 block text-sm font-medium text-mai-800 dark:text-mai-200">Poziție</label>
                        <select id="level-position" value={position} onChange={(e) => setPosition(e.target.value)} className={selectCls}>
                            <option value="">Deasupra tuturor (nivelul cel mai înalt)</option>
                            {levels.map((l) => (
                                <option key={l.rank} value={l.rank}>Sub „{l.name}”</option>
                            ))}
                        </select>
                    </div>
                    <Button className="w-full justify-center" disabled={busy || !newName.trim()} onClick={() => void handleAdd()}>
                        {busy ? <Loader2 size={15} className="animate-spin" /> : <Plus size={15} />}
                        Adaugă nivelul
                    </Button>
                </div>
            </div>
        </Modal>
    );
}
