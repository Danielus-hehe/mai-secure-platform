import { useEffect, useState } from 'react';
import { Loader2, ShieldAlert } from 'lucide-react';
import Modal from '../ui/Modal';
import Button from '../ui/Button';
import RecipientCombobox from './RecipientCombobox';
import { useKeys } from '../../context/KeysContext';
import { useToast } from '../../context/ToastContext';
import { apiErrorMessage } from '../../api/errors';
import { formatFileSize } from '../../utils/format';
import { daysFromNowLocal, dateTimeLocalToIso, toDateTimeLocalValue } from '../../utils/datetime';
import {
    listRecipients, uploadTransfer,
    TransferCategory, CATEGORY_LABELS,
    type Recipient, type TransferPolicy,
} from '../../api/transfers';
import { encryptFileForRecipients } from '../../crypto/E2ee';

interface Props {
    policy: TransferPolicy;
    onClose: () => void;
    onSent: () => void;
}

const CATEGORY_ORDER: TransferCategory[] = [
    TransferCategory.Critical,
    TransferCategory.Important,
    TransferCategory.General,
    TransferCategory.Normal,
];

export const fieldClass =
    'w-full rounded-lg border border-mai-200 dark:border-mai-600 bg-white dark:bg-mai-800 ' +
    'dark:text-mai-200 px-3 py-2 text-sm ' +
    'focus:border-mai-500 focus:outline-none focus:ring-2 focus:ring-mai-500/20 dark:focus:ring-mai-400/20';

/**
 * Trimiterea unui fișier criptat către unul sau mai mulți destinatari.
 *
 * Fișierul se criptează o singură dată; DEK-ul se împachetează pentru fiecare
 * destinatar. Serverul primește un singur cifrotext și N blocuri RSA-OAEP.
 */
/**
 * Pagina montează componenta doar cât fereastra e deschisă, deci fiecare
 * deschidere pornește cu un formular gol - fără resetări manuale ale stării.
 */
export default function SendTransferModal({ policy, onClose, onSent }: Props) {
    const toast = useToast();
    const { keys, myEncryptionPublicKey } = useKeys();

    const [recipients, setRecipients] = useState<Recipient[]>([]);
    const [loadingRecipients, setLoadingRecipients] = useState(true);
    const [selectedIds, setSelectedIds] = useState<string[]>([]);
    const [file, setFile] = useState<File | null>(null);
    const [category, setCategory] = useState<TransferCategory>(TransferCategory.General);
    const [allowForward, setAllowForward] = useState(false);
    const [expiresAt, setExpiresAt] = useState(() => daysFromNowLocal(policy.defaultExpiryDays));
    const [sending, setSending] = useState(false);
    const [stage, setStage] = useState('');
    const [percent, setPercent] = useState(0);

    // Lista colegilor, reîncărcată la fiecare deschidere (cheile noi apar imediat).
    useEffect(() => {
        let cancelled = false;
        listRecipients()
            .then((list) => { if (!cancelled) setRecipients(list); })
            .catch(() => { if (!cancelled) toast.error('Lista destinatarilor nu a putut fi încărcată.'); })
            .finally(() => { if (!cancelled) setLoadingRecipients(false); });

        return () => { cancelled = true; };
    }, [toast]);

    // Limitele câmpului datetime-local, în ora locală a browserului.
    const minExpiry = toDateTimeLocalValue(new Date());
    const maxExpiry = daysFromNowLocal(policy.maxExpiryDays);

    const handleSend = async () => {
        if (!file || selectedIds.length === 0) return;

        if (!keys || !myEncryptionPublicKey) {
            toast.error('Cheile nu sunt descuiate. Reîncărcați pagina și introduceți parola.');
            return;
        }

        const chosen = selectedIds
            .map((id) => recipients.find((r) => r.id === id))
            .filter((r): r is Recipient => r !== undefined);

        if (chosen.length !== selectedIds.length) {
            toast.error('Unul dintre destinatarii aleși nu mai este disponibil.');
            return;
        }

        const expiresIso = dateTimeLocalToIso(expiresAt);
        if (expiresAt && !expiresIso) {
            toast.error('Data de expirare este invalidă.');
            return;
        }

        setSending(true);
        setPercent(0);

        try {
            setStage(
                chosen.length === 1
                    ? 'Se criptează fișierul…'
                    : `Se criptează fișierul pentru ${chosen.length} destinatari…`
            );
            const encrypted = await encryptFileForRecipients(
                file,
                chosen.map((r) => ({ userId: r.id, publicKeyEncryption: r.publicKeyEncryption })),
                myEncryptionPublicKey,
                keys.signingKey
            );

            setStage('Se încarcă cifrotextul…');
            const result = await uploadTransfer(
                {
                    recipientKeys: encrypted.recipientKeys,
                    fileName: file.name,
                    plaintextSize: file.size,
                    ciphertext: encrypted.ciphertext,
                    envelope: encrypted.envelope,
                    category,
                    allowForward,
                    expiresAt: expiresIso,
                },
                setPercent
            );

            toast.success(result.message);
            onSent();
        } catch (err) {
            console.error(err);
            toast.error(apiErrorMessage(err, err instanceof Error ? err.message : 'Trimiterea a eșuat.'));
        } finally {
            setSending(false);
            setStage('');
        }
    };

    return (
        <Modal open title="Trimite un fișier criptat" onClose={() => { if (!sending) onClose(); }}>
            <div className="space-y-4">
                {/* Destinatari */}
                <div>
                    <label htmlFor="recipient" className="mb-1.5 block text-sm font-medium text-mai-700 dark:text-mai-200">
                        Destinatari
                    </label>
                    {loadingRecipients ? (
                        <div className="flex items-center gap-2 py-2 text-sm text-mai-400">
                            <Loader2 size={14} className="animate-spin" /> Se încarcă lista colegilor…
                        </div>
                    ) : (
                        <RecipientCombobox
                            recipients={recipients}
                            value={selectedIds}
                            onChange={setSelectedIds}
                            max={policy.maxRecipients}
                            disabled={sending}
                        />
                    )}
                    {!loadingRecipients && recipients.length === 0 && (
                        <p className="mt-2 text-xs text-amber-700 dark:text-amber-400">
                            Niciun coleg nu și-a generat încă cheile. Fișierele criptate se pot
                            trimite doar către utilizatori care s-au autentificat cel puțin o dată
                            după activarea criptării.
                        </p>
                    )}
                </div>

                {/* Fișier */}
                <div>
                    <label htmlFor="file" className="mb-1.5 block text-sm font-medium text-mai-700 dark:text-mai-200">
                        Fișier
                    </label>
                    <input
                        id="file"
                        type="file"
                        disabled={sending}
                        onChange={(e) => setFile(e.target.files?.[0] ?? null)}
                        className="w-full rounded-lg border border-mai-200 dark:border-mai-600
                                   bg-white dark:bg-mai-800 dark:text-mai-200 px-3 py-2 text-sm
                                   file:mr-3 file:rounded-md file:border-0 file:bg-mai-100 dark:file:bg-mai-700
                                   file:px-3 file:py-1.5 file:text-sm file:font-medium
                                   file:text-mai-700 dark:file:text-mai-200"
                    />
                    {file && (
                        <p className="mt-1.5 text-xs text-mai-400">
                            {file.name} - {formatFileSize(file.size)}
                        </p>
                    )}
                </div>

                <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
                    {/* Categorie */}
                    <div>
                        <label htmlFor="transfer-category" className="mb-1.5 block text-sm font-medium text-mai-700 dark:text-mai-200">
                            Categorie
                        </label>
                        <select
                            id="transfer-category"
                            value={category}
                            disabled={sending}
                            onChange={(e) => setCategory(Number(e.target.value) as TransferCategory)}
                            className={fieldClass}
                        >
                            {CATEGORY_ORDER.map((c) => (
                                <option key={c} value={c}>{CATEGORY_LABELS[c]}</option>
                            ))}
                        </select>
                    </div>

                    {/* Expirare */}
                    <div>
                        <label htmlFor="transfer-expiry" className="mb-1.5 block text-sm font-medium text-mai-700 dark:text-mai-200">
                            Expiră la
                        </label>
                        <input
                            id="transfer-expiry"
                            type="datetime-local"
                            value={expiresAt}
                            min={minExpiry}
                            max={maxExpiry}
                            disabled={sending}
                            onChange={(e) => setExpiresAt(e.target.value)}
                            className={fieldClass}
                        />
                    </div>
                </div>
                <p className="-mt-2 text-xs text-mai-400">
                    Implicit {policy.defaultExpiryDays} zile, maxim {policy.maxExpiryDays} zile (ora locală).
                </p>

                {/* Politica de forward */}
                <label className="flex cursor-pointer items-start gap-3 rounded-lg border border-mai-100
                    px-4 py-3 dark:border-mai-700">
                    <input
                        type="checkbox"
                        checked={allowForward}
                        disabled={sending}
                        onChange={(e) => setAllowForward(e.target.checked)}
                        className="mt-0.5 h-4 w-4 rounded border-mai-300 text-mai-600 focus:ring-mai-500"
                    />
                    <span className="text-sm">
                        <span className="font-medium text-mai-800 dark:text-mai-100">Permite redistribuirea</span>
                        <span className="block text-xs text-mai-500 dark:text-mai-400">
                            Destinatarii vor putea redirecționa fișierul către alți colegi. Fiecare
                            redirecționare apare în lista destinatarilor și în jurnalul de audit.
                            Fără bifă, doar dumneavoastră puteți adăuga destinatari.
                        </span>
                    </span>
                </label>

                {/* Notă E2EE */}
                <div className="rounded-lg border border-mai-100 dark:border-mai-700 bg-mai-50 dark:bg-mai-900 px-4 py-3">
                    <p className="text-xs leading-relaxed text-mai-500 dark:text-mai-400">
                        Fișierul se criptează pe acest calculator cu o cheie AES-256-GCM unică,
                        împachetată apoi cu cheia publică a fiecărui destinatar. Serverul primește doar
                        cifrotextul și nu îl poate deschide.
                    </p>
                </div>

                {/* Avertisment nume fișier */}
                <div className="rounded-lg border border-amber-200 dark:border-amber-700/50 bg-amber-50 dark:bg-amber-900/20 px-4 py-2.5">
                    <p className="text-xs leading-relaxed text-amber-700 dark:text-amber-400">
                        <strong>Atenție:</strong> numele fișierului nu este criptat și va fi
                        vizibil pe server. Nu includeți informații sensibile în numele fișierului.
                    </p>
                </div>

                {sending && (
                    <div className="space-y-2">
                        <div className="flex items-center gap-2 text-sm text-mai-600 dark:text-mai-300">
                            <Loader2 size={15} className="animate-spin" />
                            {stage}
                        </div>
                        {percent > 0 && (
                            <div className="h-1.5 w-full overflow-hidden rounded-full bg-mai-100 dark:bg-mai-700">
                                <div
                                    className="h-full bg-mai-600 transition-all dark:bg-mai-400"
                                    style={{ width: `${percent}%` }}
                                />
                            </div>
                        )}
                    </div>
                )}

                <div className="flex justify-end gap-2 pt-2">
                    <Button variant="secondary" disabled={sending} onClick={onClose}>
                        Anulează
                    </Button>
                    <Button
                        disabled={sending || !file || selectedIds.length === 0 || !keys}
                        onClick={() => void handleSend()}
                    >
                        {sending ? <Loader2 size={16} className="animate-spin" /> : <ShieldAlert size={16} />}
                        {selectedIds.length > 1
                            ? `Criptează și trimite (${selectedIds.length})`
                            : 'Criptează și trimite'}
                    </Button>
                </div>
            </div>
        </Modal>
    );
}
