import { CheckCheck, Clock, ShieldAlert, Share2, EyeOff } from 'lucide-react';
import type { TransferListItem, TransferRecipientItem } from '../../api/transfers';
import { formatDateTime } from '../../utils/format';

/**
 * Dovada de primire, per destinatar.
 *
 * Înainte existau o singură dată de descărcare și un singur rezultat de
 * semnătură pe tot transferul - primul destinatar care deschidea fișierul
 * „confirma” pentru toți. Acum fiecare rând are confirmarea lui, iar expeditorul
 * vede exact cine a primit, când și dacă semnătura s-a verificat.
 */

/** Rezumatul din coloana de stare: „2 / 3 confirmări” sau confirmarea proprie. */
export function ReceiptSummary({ transfer }: { transfer: TransferListItem }) {
    if (transfer.isMine && transfer.downloadedCount !== null) {
        const total = transfer.recipientCount;
        const done = transfer.downloadedCount;
        const invalid = transfer.recipients.some((r) => r.signatureValid === false);

        if (total === 0) return null;

        return (
            <p
                className={`mt-1 flex items-center gap-1 text-[11px] ${
                    invalid
                        ? 'font-medium text-red-600 dark:text-red-400'
                        : done === total
                            ? 'text-green-600 dark:text-green-400'
                            : 'text-mai-500 dark:text-mai-400'
                }`}
            >
                {invalid ? <ShieldAlert size={11} className="shrink-0" /> : <CheckCheck size={11} className="shrink-0" />}
                {done} / {total} {total === 1 ? 'confirmare' : 'confirmări'}
                {invalid && ' · semnătură INVALIDĂ'}
            </p>
        );
    }

    if (transfer.isRecipient && transfer.myDownloadedAt) {
        return (
            <p className="mt-1 flex items-center gap-1 text-[11px] text-green-600 dark:text-green-400">
                <CheckCheck size={11} className="shrink-0" />
                Primit de dvs. {formatDateTime(transfer.myDownloadedAt)}
            </p>
        );
    }

    return null;
}

function RecipientRow({ r }: { r: TransferRecipientItem }) {
    let status;
    if (!r.receiptVisible) {
        status = (
            <span className="inline-flex items-center gap-1 text-mai-400" title="Doar expeditorul vede confirmările celorlalți destinatari">
                <EyeOff size={12} /> -
            </span>
        );
    } else if (r.downloadedAt) {
        status = r.signatureValid === false ? (
            <span className="inline-flex items-center gap-1 font-medium text-red-600 dark:text-red-400">
                <ShieldAlert size={12} /> {formatDateTime(r.downloadedAt)} · semnătură INVALIDĂ
            </span>
        ) : (
            <span className="inline-flex items-center gap-1 text-green-600 dark:text-green-400">
                <CheckCheck size={12} /> {formatDateTime(r.downloadedAt)}
                {r.signatureValid === true && ' · semnătură validă'}
            </span>
        );
    } else {
        status = (
            <span className="inline-flex items-center gap-1 text-amber-600 dark:text-amber-400">
                <Clock size={12} /> Nedescărcat
            </span>
        );
    }

    return (
        <li className="grid grid-cols-1 gap-1 py-2 sm:grid-cols-[minmax(0,1fr)_auto] sm:items-center sm:gap-4">
            <div className="min-w-0">
                <p className="truncate text-sm font-medium text-mai-800 dark:text-mai-100">{r.name}</p>
                <p className="truncate text-xs text-mai-400">
                    {r.department || '-'}
                    {r.forwardedById && (
                        <span className="ml-2 inline-flex items-center gap-1">
                            <Share2 size={10} /> redirecționat de {r.forwardedByName ?? '-'}, {formatDateTime(r.sentAt)}
                        </span>
                    )}
                </p>
            </div>
            <div className="text-xs">{status}</div>
        </li>
    );
}

/** Lista completă a destinatarilor, afișată la extinderea rândului. */
export default function TransferReceipts({ transfer }: { transfer: TransferListItem }) {
    const direct = transfer.recipients.filter((r) => !r.forwardedById);
    const forwarded = transfer.recipients.filter((r) => r.forwardedById);

    return (
        <div className="space-y-3">
            <div>
                <p className="text-[11px] font-semibold uppercase tracking-wide text-mai-400">
                    Destinatari direcți ({direct.length})
                </p>
                <ul className="divide-y divide-mai-100 dark:divide-mai-700">
                    {direct.map((r) => <RecipientRow key={r.userId} r={r} />)}
                </ul>
            </div>

            {forwarded.length > 0 && (
                <div>
                    <p className="text-[11px] font-semibold uppercase tracking-wide text-mai-400">
                        Adăugați prin redirecționare ({forwarded.length})
                    </p>
                    <ul className="divide-y divide-mai-100 dark:divide-mai-700">
                        {forwarded.map((r) => <RecipientRow key={r.userId} r={r} />)}
                    </ul>
                </div>
            )}

            <p className="text-[11px] text-mai-400">
                {transfer.allowForward
                    ? 'Expeditorul a permis destinatarilor să redirecționeze fișierul.'
                    : 'Doar expeditorul poate adăuga destinatari.'}
                {' '}Confirmarea înseamnă că browserul destinatarului a decriptat fișierul și a
                raportat rezultatul verificării semnăturii.
            </p>
        </div>
    );
}
