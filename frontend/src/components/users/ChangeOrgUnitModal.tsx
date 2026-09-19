import { useState } from 'react';
import { Building2, Loader2 } from 'lucide-react';
import Modal from '../ui/Modal';
import Button from '../ui/Button';
import OrgUnitSelect from '../org/OrgUnitSelect';
import { useToast } from '../../context/ToastContext';
import { apiErrorMessage } from '../../api/errors';
import { changeUserOrgUnit } from '../../api/users';
import type { OrgUnit } from '../../api/orgUnits';

interface Props {
    user: { id: string; username: string; fullName: string; orgUnitId: string | null; ledOrgUnitName: string | null };
    units: OrgUnit[];
    onClose: () => void;
    onDone: () => void;
}

/** Încadrarea unui utilizator într-o subdiviziune. */
export default function ChangeOrgUnitModal({ user, units, onClose, onDone }: Props) {
    const toast = useToast();
    const [value, setValue] = useState(user.orgUnitId ?? '');
    const [saving, setSaving] = useState(false);

    const activeUnits = units.filter((u) => u.isActive);
    const movingHead = user.ledOrgUnitName !== null && value !== (user.orgUnitId ?? '');

    const handleSave = async () => {
        setSaving(true);
        try {
            const result = await changeUserOrgUnit(user.id, value || null);
            if (result.headReleased) toast.warning(result.message);
            else toast.success(result.message);
            onDone();
        } catch (e) {
            toast.error(apiErrorMessage(e, 'Încadrarea nu a putut fi schimbată.'));
        } finally {
            setSaving(false);
        }
    };

    return (
        <Modal open title={`Subdiviziune — ${user.fullName || '@' + user.username}`} onClose={() => { if (!saving) onClose(); }}>
            <div className="space-y-4">
                <div>
                    <label htmlFor="user-org-unit" className="mb-1.5 block text-sm font-medium text-mai-800 dark:text-mai-200">
                        Încadrat în
                    </label>
                    <OrgUnitSelect
                        id="user-org-unit"
                        units={activeUnits}
                        value={value}
                        onChange={setValue}
                        emptyLabel="— neîncadrat —"
                        disabled={saving}
                    />
                </div>

                {movingHead && (
                    <p className="rounded-lg border border-amber-200 bg-amber-50 px-3.5 py-2.5 text-xs text-amber-800
                        dark:border-amber-700/50 dark:bg-amber-900/20 dark:text-amber-300">
                        Persoana conduce „{user.ledOrgUnitName}”. Dacă o mutați în altă subdiviziune,
                        funcția de șef rămâne vacantă și trebuie numit altcineva.
                    </p>
                )}

                <div className="flex justify-end gap-2">
                    <Button variant="secondary" disabled={saving} onClick={onClose}>Anulează</Button>
                    <Button disabled={saving || value === (user.orgUnitId ?? '')} onClick={() => void handleSave()}>
                        {saving ? <Loader2 size={15} className="animate-spin" /> : <Building2 size={15} />}
                        Salvează
                    </Button>
                </div>
            </div>
        </Modal>
    );
}
