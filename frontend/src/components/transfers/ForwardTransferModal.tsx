import { useEffect, useMemo, useState } from 'react';
import { Loader2, Share2 } from 'lucide-react';
import Modal from '../ui/Modal';
import Button from '../ui/Button';
import RecipientCombobox from './RecipientCombobox';
import { useKeys } from '../../context/KeysContext';
import { useToast } from '../../context/ToastContext';
import { apiErrorMessage } from '../../api/errors';
import {
    listRecipients, getEnvelope, forwardTransfer,
    type Recipient, type TransferListItem, type TransferPolicy,
} from '../../api/transfers';
import { rewrapFileKey } from '../../crypto/E2ee';

interface Props {
    transfer: TransferListItem;
    policy: TransferPolicy;
    onClose: () => void;
    onDone: () => void;
}

/**
 * Redirecționarea unui transfer.
 *
 * DEK-ul se despachetează cu cheia privată proprie și se re-împachetează în
 * browser pentru fiecare destinatar nou. Serverul primește doar blocurile
 * RSA-OAEP; cifrotextul și semnătura expeditorului original rămân neatinse.
 */
/** Montată doar cât fereastra e deschisă - fiecare deschidere pornește curat. */
export default function ForwardTransferModal({ transfer, policy, onClose, onDone }: Props) {
    const toast = useToast();
    const { keys } = useKeys();

    const [recipients, setRecipients] = useState<Recipient[]>([]);
    const [loading, setLoading] = useState(true);
    const [selectedIds, setSelectedIds] = useState<string[]>([]);
    const [submitting, setSubmitting] = useState(false);

    useEffect(() => {
        let cancelled = false;
        listRecipients()
            .then((list) => { if (!cancelled) setRecipients(list); })
            .catch(() => { if (!cancelled) toast.error('Lista destinatarilor nu a putut fi încărcată.'); })
            .finally(() => { if (!cancelled) setLoading(false); });

        return () => { cancelled = true; };
    }, [toast]);

    // Cine are deja acces nu mai apare în căutare: destinatarii existenți și expeditorul.
    const exclude = useMemo(
        () => [transfer.senderId, ...transfer.recipients.map((r) => r.userId)],
        [transfer]
    );

    const remaining = Math.max(policy.maxRecipients - transfer.recipientCount, 0);

    const handleSubmit = async () => {
        if (selectedIds.length === 0 || !keys) return;

        const chosen = selectedIds
            .map((id) => recipients.find((r) => r.id === id))
            .filter((r): r is Recipient => r !== undefined);

        setSubmitting(true);
        try {
            const envelope = await getEnvelope(transfer.id);
            const wrapped = await rewrapFileKey(
                envelope.wrappedKeyForMe,
                keys.decryptionKey,
                chosen.map((r) => ({ userId: r.id, publicKeyEncryption: r.publicKeyEncryption }))
            );

            const result = await forwardTransfer(transfer.id, wrapped);
            toast.success(result.message);
            onDone();
        } catch (err) {
            toast.error(apiErrorMessage(err, err instanceof Error ? err.message : 'Redirecționarea a eșuat.'));
        } finally {
            setSubmitting(false);
        }
    };

    return (
        <Modal
            open
            title={`Redirecționează „${transfer.fileName}"`}
            onClose={() => { if (!submitting) onClose(); }}
        >
            <div className="space-y-4">
                <p className="text-sm text-mai-500 dark:text-mai-400">
                    Destinatarii noi primesc același fișier, semnat de{' '}
                    <strong className="text-mai-700 dark:text-mai-200">{transfer.senderName}</strong>.
                    Redirecționarea apare în lista destinatarilor și în jurnalul de audit.
                </p>

                <div>
                    <label htmlFor="forward-recipient" className="mb-1.5 block text-sm font-medium text-mai-700 dark:text-mai-200">
                        Destinatari noi
                    </label>
                    {loading ? (
                        <div className="flex items-center gap-2 py-2 text-sm text-mai-400">
                            <Loader2 size={14} className="animate-spin" /> Se încarcă lista colegilor…
                        </div>
                    ) : remaining === 0 ? (
                        <p className="text-sm text-amber-700 dark:text-amber-400">
                            Transferul are deja numărul maxim de destinatari ({policy.maxRecipients}).
                        </p>
                    ) : (
                        <RecipientCombobox
                            inputId="forward-recipient"
                            recipients={recipients}
                            value={selectedIds}
                            onChange={setSelectedIds}
                            exclude={exclude}
                            max={remaining}
                            disabled={submitting}
                        />
                    )}
                </div>

                <div className="rounded-lg border border-mai-100 dark:border-mai-700 bg-mai-50 dark:bg-mai-900 px-4 py-3">
                    <p className="text-xs leading-relaxed text-mai-500 dark:text-mai-400">
                        Cheia de fișier se despachetează și se re-împachetează pe acest calculator.
                        Serverul vede doar blocuri RSA-OAEP opace - nu accesează conținutul.
                    </p>
                </div>

                {submitting && (
                    <div className="flex items-center gap-2 text-sm text-mai-600 dark:text-mai-300">
                        <Loader2 size={15} className="animate-spin" />
                        Se re-împachetează cheile și se trimite…
                    </div>
                )}

                <div className="flex justify-end gap-2 pt-2">
                    <Button variant="secondary" disabled={submitting} onClick={onClose}>
                        Anulează
                    </Button>
                    <Button
                        disabled={submitting || selectedIds.length === 0 || !keys}
                        onClick={() => void handleSubmit()}
                    >
                        {submitting ? <Loader2 size={16} className="animate-spin" /> : <Share2 size={16} />}
                        Redirecționează
                    </Button>
                </div>
            </div>
        </Modal>
    );
}
