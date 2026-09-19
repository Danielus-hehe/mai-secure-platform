/**
 * Transferuri securizate de fișiere.
 *
 * Fluxul complet, cu locul unde se face fiecare operație:
 *
 *   TRIMITERE (browserul expeditorului) — SendTransferModal
 *     1. se iau cheile publice ale destinatarilor aleși (unul sau mai mulți)
 *     2. se generează o cheie AES-256-GCM aleatorie, unică pentru transferul ăsta
 *     3. fișierul se criptează O DATĂ cu ea
 *     4. cheia se împachetează RSA-OAEP pentru fiecare destinatar și pentru
 *        expeditor (altfel nu ți-ai mai putea deschide propriile fișiere trimise)
 *     5. SHA-256 al conținutului în clar se semnează RSA-PSS
 *     6. spre server pleacă doar cifrotextul și plicul
 *
 *   PRIMIRE (browserul destinatarului) — handleDownload
 *     1. se cere plicul de la API (autorizare + audit se fac acolo)
 *     2. cifrotextul se ia din depozit (URL presemnat sau prin API)
 *     3. cheia de fișier se despachetează cu cheia privată proprie
 *     4. se decriptează — tagul GCM garantează că niciun bit nu s-a schimbat
 *     5. se verifică semnătura expeditorului
 *     6. abia atunci se confirmă primirea — pe rândul PROPRIU de destinatar
 *
 *   REDIRECȚIONARE — ForwardTransferModal (doar dacă serverul o permite:
 *   expeditorul întotdeauna, destinatarii doar cu „Permite redistribuirea”).
 *
 * Serverul nu participă la niciun pas criptografic. Dacă ar participa, garanția
 * end-to-end ar dispărea.
 */

import { Fragment, useCallback, useEffect, useRef, useState } from 'react';
import {
    ArrowLeftRight, Upload, Download, Search, Trash2, Loader2, Undo2,
    ShieldCheck, Lock, Inbox, Send, FileWarning, AlertTriangle, Share2,
    ChevronDown, ChevronRight, Users,
} from 'lucide-react';
import PageHeader from '../../components/ui/PageHeader';
import KeyFingerprint from '../../components/security/KeyFingerprint';
import Badge from '../../components/ui/Badge';
import Button from '../../components/ui/Button';
import EmptyState from '../../components/ui/EmptyState';
import Pagination from '../../components/ui/Pagination';
import { CategoryBadge } from '../../components/transfers/CategoryBadge';
import { ExpiryBadge } from '../../components/transfers/ExpiryBadge';
import TransferReceipts, { ReceiptSummary } from '../../components/transfers/TransferReceipts';
import SendTransferModal from '../../components/transfers/SendTransferModal';
import ForwardTransferModal from '../../components/transfers/ForwardTransferModal';
import { apiErrorMessage } from '../../api/errors';
import { useToast } from '../../context/ToastContext';
import { useKeys } from '../../context/KeysContext';
import { formatDateTime, formatFileSize, truncateSha } from '../../utils/format';
import {
    listTransfers, getTransferPolicy, getEnvelope, fetchCiphertext,
    confirmTransfer, deleteTransfer, revokeTransfer,
    TransferCategory, CATEGORY_LABELS, DEFAULT_TRANSFER_POLICY,
    type TransferListItem, type PagedResult, type TransferPolicy,
} from '../../api/transfers';
import { decryptTransfer, saveDecryptedFile, importSigningPublicKey } from '../../crypto/E2ee';

type DirectionFilter = '' | 'received' | 'sent';
type StatusFilter = '' | 'Pending' | 'Downloaded' | 'Expired' | 'Revoked';

const EMPTY_PAGE: PagedResult<TransferListItem> = {
    items: [], totalCount: 0, page: 1, pageSize: 25,
    totalPages: 0, hasPrevious: false, hasNext: false,
};

const CATEGORY_ORDER: TransferCategory[] = [
    TransferCategory.Critical,
    TransferCategory.Important,
    TransferCategory.General,
    TransferCategory.Normal,
];

const selectClass =
    'rounded-lg border border-mai-200 dark:border-mai-600 bg-white dark:bg-mai-800 ' +
    'dark:text-mai-200 px-3 py-2 text-sm ' +
    'focus:border-mai-500 focus:outline-none focus:ring-2 focus:ring-mai-500/20 dark:focus:ring-mai-400/20';

/**
 * Starea agregată. Pentru un destinatar care a descărcat deja, „În așteptare”
 * ar fi înșelător (transferul așteaptă de fapt pe alți colegi), deci vede
 * „Primit”.
 */
const statusBadge = (t: TransferListItem) => {
    if (t.status === 'Revoked')    return <Badge tone="gray">Retras</Badge>;
    if (t.status === 'Expired')    return <Badge tone="red">Expirat</Badge>;
    if (t.status === 'Downloaded') return <Badge tone="green">Primit de toți</Badge>;
    if (!t.isMine && t.myDownloadedAt) return <Badge tone="green">Primit</Badge>;
    return <Badge tone="gold">În așteptare</Badge>;
};

/** Contrapartea din listă: expeditorul (la primite) sau destinatarii (la trimise). */
const counterpart = (t: TransferListItem) => {
    if (!t.isMine) {
        return { name: t.senderName, detail: t.senderDepartment };
    }
    const [first, ...rest] = t.recipients;
    if (!first) return { name: '—', detail: '' };
    return rest.length === 0
        ? { name: first.name, detail: first.department }
        : { name: `${first.name} +${rest.length}`, detail: `${t.recipientCount} destinatari` };
};

export default function TransfersPage() {
    const toast = useToast();
    const { keys, fingerprint } = useKeys();

    // ── Listă și filtre ──────────────────────────────────────────────────────
    const [data, setData] = useState<PagedResult<TransferListItem>>(EMPTY_PAGE);
    const [loading, setLoading] = useState(true);

    const [searchInput, setSearchInput] = useState('');
    const [search, setSearch] = useState('');
    const [direction, setDirection] = useState<DirectionFilter>('');
    const [status, setStatus] = useState<StatusFilter>('');
    const [category, setCategory] = useState<TransferCategory | ''>('');
    const [page, setPage] = useState(1);
    const [pageSize, setPageSize] = useState(25);
    const [expanded, setExpanded] = useState<Set<string>>(new Set());

    // ── Politica serverului (expirare, număr de destinatari) ─────────────────
    const [policy, setPolicy] = useState<TransferPolicy>(DEFAULT_TRANSFER_POLICY);

    // ── Ferestre și operații în curs ─────────────────────────────────────────
    const [uploadOpen, setUploadOpen] = useState(false);
    const [forwardTarget, setForwardTarget] = useState<TransferListItem | null>(null);
    const [busyId, setBusyId] = useState<string | null>(null);

    useEffect(() => {
        getTransferPolicy()
            .then(setPolicy)
            .catch(() => { /* rămân valorile implicite; serverul validează oricum */ });
    }, []);

    useEffect(() => {
        const timer = window.setTimeout(() => {
            setSearch(searchInput.trim());
            setPage(1);
        }, 350);
        return () => window.clearTimeout(timer);
    }, [searchInput]);

    const abortRef = useRef<AbortController | null>(null);

    const load = useCallback(async () => {
        abortRef.current?.abort();
        const controller = new AbortController();
        abortRef.current = controller;

        setLoading(true);
        try {
            const result = await listTransfers(
                { search, direction, status, category, page, pageSize },
                controller.signal
            );
            setData(result);
        } catch (err) {
            if (controller.signal.aborted) return;
            console.error(err);
            toast.error('Nu s-au putut încărca transferurile.');
        } finally {
            if (!controller.signal.aborted) setLoading(false);
        }
    }, [search, direction, status, category, page, pageSize, toast]);

    useEffect(() => {
        void load();
        return () => abortRef.current?.abort();
    }, [load]);

    const toggleExpanded = (id: string) =>
        setExpanded((prev) => {
            const next = new Set(prev);
            if (next.has(id)) next.delete(id); else next.add(id);
            return next;
        });

    // ── Descărcarea ──────────────────────────────────────────────────────────

    const handleDownload = async (transfer: TransferListItem) => {
        if (!keys) {
            toast.error('Cheile nu sunt descuiate.');
            return;
        }

        setBusyId(transfer.id);
        try {
            const envelope = await getEnvelope(transfer.id);

            if (!envelope.isEncrypted) {
                toast.warning('Transfer necriptat, dinaintea migrării. Nu poate fi verificat criptografic.');
                return;
            }

            if (!envelope.senderPublicKeySigning) {
                toast.error('Cheia publică de semnătură a expeditorului lipsește. Autenticitatea nu poate fi verificată.');
                return;
            }

            const ciphertext = await fetchCiphertext(envelope);
            const senderSigningKey = await importSigningPublicKey(envelope.senderPublicKeySigning);

            const result = await decryptTransfer(
                ciphertext,
                { iv: envelope.iv, signature: envelope.signature },
                envelope.wrappedKeyForMe,
                keys.decryptionKey,
                senderSigningKey
            );

            saveDecryptedFile(result.plaintext, envelope.fileName);

            // Confirmarea o face doar un destinatar, și doar prima dată: o a doua
            // descărcare nu mută momentul primei confirmări.
            if (envelope.isRecipient && !envelope.alreadyConfirmed) {
                try {
                    await confirmTransfer(transfer.id, result.signatureValid);
                } catch (confirmError) {
                    toast.warning(apiErrorMessage(
                        confirmError,
                        'Fișierul s-a decriptat, dar confirmarea de primire nu s-a putut înregistra.'
                    ));
                }
            }

            if (result.signatureValid) {
                toast.success('Fișier decriptat. Semnătura expeditorului este validă.');
            } else {
                toast.warning(
                    'Fișierul s-a decriptat, dar SEMNĂTURA NU SE VERIFICĂ. ' +
                    'Nu îl considerați autentic și anunțați administratorul.'
                );
            }

            await load();
        } catch (err) {
            console.error(err);
            toast.error(apiErrorMessage(err, 'Descărcarea a eșuat.'));
        } finally {
            setBusyId(null);
        }
    };

    // ── Retragerea ───────────────────────────────────────────────────────────

    const handleRevoke = async (transfer: TransferListItem) => {
        const downloaded = transfer.downloadedCount ?? 0;
        const ok = window.confirm(
            `Retrageți transferul „${transfer.fileName}"?\n\n` +
            'Fișierul va fi șters din depozit, iar destinatarii care nu l-au descărcat ' +
            'nu îl vor mai putea deschide. Vor vedea în schimb că a fost retras.' +
            (downloaded > 0
                ? `\n\n${downloaded} destinatar(i) l-au descărcat deja și îl păstrează.`
                : ''),
        );
        if (!ok) return;

        const reason = window.prompt('Motivul retragerii (opțional):') ?? undefined;

        setBusyId(transfer.id);
        try {
            const result = await revokeTransfer(transfer.id, reason || undefined);
            toast.success(result.message);
        } catch (e) {
            toast.error(apiErrorMessage(e, 'Retragerea a eșuat.'));
        } finally {
            setBusyId(null);
            await load();
        }
    };

    // ── Ștergerea (logică) ───────────────────────────────────────────────────

    const handleDelete = async (transfer: TransferListItem) => {
        const ok = window.confirm(
            `Ștergeți transferul „${transfer.fileName}"?\n\n` +
            'Dispare din liste și fișierul criptat se șterge din depozit. ' +
            'Confirmările de primire rămân în jurnalul de audit.'
        );
        if (!ok) return;

        setBusyId(transfer.id);
        try {
            const result = await deleteTransfer(transfer.id);
            toast.success(result.message);
        } catch (e) {
            toast.error(apiErrorMessage(e, 'Ștergerea a eșuat.'));
        } finally {
            setBusyId(null);
            await load();
        }
    };

    // ── Render ───────────────────────────────────────────────────────────────

    const hasFilters = Boolean(search || direction || status || category !== '');

    return (
        <div className="space-y-5">
            <PageHeader
                title="Transferuri securizate"
                subtitle="Fișierele se criptează în browser. Serverul stochează doar cifrotext."
                actions={
                    <Button onClick={() => setUploadOpen(true)}>
                        <Upload size={16} />
                        Trimite fișier
                    </Button>
                }
            />

            {fingerprint && (
                <div className="flex items-center gap-2 rounded-lg border border-mai-100 dark:border-mai-700
                    bg-mai-50 dark:bg-mai-800 px-4 py-2.5">
                    <ShieldCheck size={15} className="shrink-0 text-mai-600 dark:text-mai-400" />
                    <p className="text-xs text-mai-500 dark:text-mai-400">
                        Amprenta cheii dumneavoastră publice:{' '}
                        <KeyFingerprint value={fingerprint} />
                        {' '}— comparați-o cu colegii pe alt canal pentru a exclude substituirea cheilor.
                    </p>
                </div>
            )}

            <div className="flex items-start gap-2.5 rounded-lg border border-amber-200 dark:border-amber-700/50
                bg-amber-50 dark:bg-amber-900/20 px-4 py-3">
                <AlertTriangle size={15} className="mt-0.5 shrink-0 text-amber-600 dark:text-amber-400" />
                <p className="text-xs leading-relaxed text-amber-800 dark:text-amber-300">
                    <span className="font-semibold">Limitare cunoscută:</span> conținutul fișierelor
                    este criptat end-to-end, dar <strong>numele fișierelor nu sunt criptate</strong> — serverul
                    le vede în clar, pentru a permite căutarea pe partea de server.
                    Evitați includerea informațiilor sensibile în numele fișierelor.
                </p>
            </div>

            {/* ── Filtre ────────────────────────────────────────────────── */}
            <div className="flex flex-col items-stretch gap-3 sm:flex-row sm:flex-wrap sm:items-center">
                <div className="relative w-full sm:min-w-56 sm:flex-1">
                    <Search size={16} className="absolute left-3 top-1/2 -translate-y-1/2 text-mai-300 dark:text-mai-500" />
                    <input
                        value={searchInput}
                        onChange={(e) => setSearchInput(e.target.value)}
                        placeholder="Caută după fișier, expeditor sau destinatar…"
                        className="w-full rounded-lg border border-mai-200 bg-white py-2 pl-9 pr-3 text-sm
                                   placeholder:text-mai-300 focus:border-mai-500 focus:outline-none
                                   focus:ring-2 focus:ring-mai-500/20 dark:border-mai-600 dark:bg-mai-800
                                   dark:text-mai-100 dark:placeholder:text-mai-500 dark:focus:ring-mai-400/20"
                    />
                </div>

                <select
                    value={direction}
                    onChange={(e) => { setDirection(e.target.value as DirectionFilter); setPage(1); }}
                    className={selectClass}
                    aria-label="Direcție"
                >
                    <option value="">Toate</option>
                    <option value="received">Primite</option>
                    <option value="sent">Trimise</option>
                </select>

                <select
                    value={status}
                    onChange={(e) => { setStatus(e.target.value as StatusFilter); setPage(1); }}
                    className={selectClass}
                    aria-label="Stare"
                >
                    <option value="">Orice stare</option>
                    <option value="Pending">În așteptare</option>
                    <option value="Downloaded">Primite de toți</option>
                    <option value="Expired">Expirate</option>
                    <option value="Revoked">Retrase</option>
                </select>

                <select
                    value={category === '' ? '' : String(category)}
                    onChange={(e) => {
                        setCategory(e.target.value === '' ? '' : Number(e.target.value) as TransferCategory);
                        setPage(1);
                    }}
                    className={selectClass}
                    aria-label="Categorie"
                >
                    <option value="">Orice categorie</option>
                    {CATEGORY_ORDER.map((c) => (
                        <option key={c} value={c}>{CATEGORY_LABELS[c]}</option>
                    ))}
                </select>
            </div>

            {/* ── Tabel ─────────────────────────────────────────────────── */}
            <div className="overflow-hidden rounded-xl border border-mai-100 bg-white dark:border-mai-700 dark:bg-mai-800">
                {loading ? (
                    <div className="flex items-center justify-center gap-3 py-16 text-mai-400">
                        <Loader2 size={18} className="animate-spin" />
                        <span className="text-sm">Se încarcă…</span>
                    </div>
                ) : data.items.length === 0 ? (
                    <div className="p-6">
                        <EmptyState
                            icon={ArrowLeftRight}
                            title="Niciun transfer"
                            description={
                                hasFilters
                                    ? 'Niciun rezultat pentru filtrele curente.'
                                    : 'Trimiteți primul fișier folosind butonul din dreapta sus.'
                            }
                        />
                    </div>
                ) : (
                    <>
                        <div className="overflow-x-auto">
                            <table className="w-full text-sm">
                                <thead>
                                <tr className="border-b border-mai-100 text-left text-xs uppercase tracking-wide
                                    text-mai-400 dark:border-mai-700">
                                    <th className="w-8 px-3 py-3" aria-label="Detalii" />
                                    <th className="px-4 py-3 font-semibold">Fișier</th>
                                    <th className="px-4 py-3 font-semibold">Direcție</th>
                                    <th className="px-4 py-3 font-semibold">Contraparte</th>
                                    <th className="px-4 py-3 font-semibold">Data</th>
                                    <th className="px-4 py-3 font-semibold">Categorie</th>
                                    <th className="px-4 py-3 font-semibold">Stare</th>
                                    <th className="px-4 py-3 text-right font-semibold">Acțiuni</th>
                                </tr>
                                </thead>
                                <tbody>
                                {data.items.map((t) => {
                                    const isOpen = expanded.has(t.id);
                                    const other = counterpart(t);
                                    const busy = busyId === t.id;

                                    return (
                                        <Fragment key={t.id}>
                                            <tr className={`border-b border-mai-50 hover:bg-mai-50/40 dark:border-mai-700
                                                dark:hover:bg-mai-700/40 ${isOpen ? 'bg-mai-50/40 dark:bg-mai-700/30' : ''}`}>
                                                <td className="px-3 py-3 align-top">
                                                    <button
                                                        type="button"
                                                        onClick={() => toggleExpanded(t.id)}
                                                        className="rounded p-1 text-mai-400 hover:bg-mai-100 hover:text-mai-700
                                                            dark:hover:bg-mai-700 dark:hover:text-mai-200"
                                                        aria-expanded={isOpen}
                                                        aria-label={isOpen ? 'Ascunde destinatarii' : 'Arată destinatarii și confirmările'}
                                                        title="Destinatari și confirmări de primire"
                                                    >
                                                        {isOpen ? <ChevronDown size={15} /> : <ChevronRight size={15} />}
                                                    </button>
                                                </td>

                                                <td className="px-4 py-3">
                                                    <div className="flex items-start gap-2">
                                                        {t.isEncrypted
                                                            ? <Lock size={14} className="mt-0.5 shrink-0 text-green-600 dark:text-green-400" />
                                                            : <FileWarning size={14} className="mt-0.5 shrink-0 text-amber-500 dark:text-amber-400" />}
                                                        <div className="min-w-0">
                                                            <p className="truncate font-medium text-mai-900 dark:text-mai-100">{t.fileName}</p>
                                                            <p className="text-xs text-mai-400">
                                                                {formatFileSize(t.fileSize)}
                                                                {t.sha256 && (
                                                                    <span className="ml-2 font-mono" title={t.sha256}>
                                                                        {truncateSha(t.sha256)}
                                                                    </span>
                                                                )}
                                                            </p>
                                                            {(t.status === 'Pending' || t.status === 'Downloaded') && (
                                                                <ExpiryBadge expiresAt={t.expiresAt} />
                                                            )}
                                                        </div>
                                                    </div>
                                                </td>

                                                <td className="px-4 py-3">
                                                    <span className="inline-flex items-center gap-1.5 text-xs text-mai-500 dark:text-mai-300">
                                                        {t.isMine ? <><Send size={13} /> Trimis</> : <><Inbox size={13} /> Primit</>}
                                                    </span>
                                                </td>

                                                <td className="px-4 py-3">
                                                    <p className="flex items-center gap-1 text-mai-800 dark:text-mai-200">
                                                        {t.isMine && t.recipientCount > 1 && <Users size={13} className="shrink-0 text-mai-400" />}
                                                        {other.name}
                                                    </p>
                                                    <p className="text-xs text-mai-400">{other.detail}</p>
                                                </td>

                                                <td className="whitespace-nowrap px-4 py-3 text-mai-500 dark:text-mai-300">
                                                    {formatDateTime(t.createdAt)}
                                                </td>

                                                <td className="px-4 py-3">
                                                    <CategoryBadge category={t.category} />
                                                </td>

                                                <td className="px-4 py-3">
                                                    {statusBadge(t)}
                                                    <ReceiptSummary transfer={t} />
                                                    {t.revokedAt && t.revokedReason && (
                                                        <p className="mt-1 text-[11px] text-mai-400">{t.revokedReason}</p>
                                                    )}
                                                </td>

                                                <td className="px-4 py-3">
                                                    <div className="flex items-center justify-end gap-1.5">
                                                        <Button
                                                            variant="secondary"
                                                            className="!px-2.5 !py-1.5"
                                                            disabled={busy || !t.canDownload || !keys}
                                                            onClick={() => void handleDownload(t)}
                                                            title="Descarcă și decriptează"
                                                        >
                                                            {busy ? <Loader2 size={15} className="animate-spin" /> : <Download size={15} />}
                                                        </Button>

                                                        {t.canForward && (
                                                            <Button
                                                                variant="ghost"
                                                                className="!px-2.5 !py-1.5 text-mai-600 hover:bg-mai-50
                                                                    dark:text-mai-400 dark:hover:bg-mai-900/30"
                                                                disabled={busy || !keys}
                                                                onClick={() => setForwardTarget(t)}
                                                                title={keys ? 'Redirecționează' : 'Cheile nu sunt descuiate'}
                                                            >
                                                                <Share2 size={15} />
                                                            </Button>
                                                        )}

                                                        {t.canRevoke && (
                                                            <Button
                                                                variant="ghost"
                                                                className="!px-2.5 !py-1.5 text-amber-600 hover:bg-amber-50
                                                                    dark:text-amber-400 dark:hover:bg-amber-900/30"
                                                                disabled={busy}
                                                                onClick={() => void handleRevoke(t)}
                                                                title="Retrage transferul pentru cei care nu l-au descărcat"
                                                            >
                                                                <Undo2 size={15} />
                                                            </Button>
                                                        )}

                                                        {t.canDelete && (
                                                            <Button
                                                                variant="ghost"
                                                                className="!px-2.5 !py-1.5 text-red-600 hover:bg-red-50
                                                                    dark:text-red-400 dark:hover:bg-red-900/30"
                                                                disabled={busy}
                                                                onClick={() => void handleDelete(t)}
                                                                title="Șterge din listă (confirmările rămân în jurnal)"
                                                            >
                                                                <Trash2 size={15} />
                                                            </Button>
                                                        )}
                                                    </div>
                                                </td>
                                            </tr>

                                            {isOpen && (
                                                <tr className="border-b border-mai-100 bg-mai-50/30 dark:border-mai-700 dark:bg-mai-900/20">
                                                    <td />
                                                    <td colSpan={7} className="px-4 py-3">
                                                        <TransferReceipts transfer={t} />
                                                    </td>
                                                </tr>
                                            )}
                                        </Fragment>
                                    );
                                })}
                                </tbody>
                            </table>
                        </div>

                        <Pagination
                            page={data.page}
                            pageSize={data.pageSize}
                            totalCount={data.totalCount}
                            totalPages={data.totalPages}
                            onPageChange={setPage}
                            onPageSizeChange={(size) => { setPageSize(size); setPage(1); }}
                            itemLabel="transferuri"
                        />
                    </>
                )}
            </div>

            {uploadOpen && (
                <SendTransferModal
                    policy={policy}
                    onClose={() => setUploadOpen(false)}
                    onSent={() => {
                        setUploadOpen(false);
                        setPage(1);
                        void load();
                    }}
                />
            )}

            {forwardTarget && (
                <ForwardTransferModal
                    transfer={forwardTarget}
                    policy={policy}
                    onClose={() => setForwardTarget(null)}
                    onDone={() => {
                        setForwardTarget(null);
                        void load();
                    }}
                />
            )}
        </div>
    );
}
