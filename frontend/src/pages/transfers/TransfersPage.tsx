/**
 * Transferuri securizate de fișiere.
 *
 * Fluxul complet, cu locul unde se face fiecare operație:
 *
 *   TRIMITERE (browserul expeditorului)
 *     1. se cere cheia publică a destinatarului
 *     2. se generează o cheie AES-256-GCM aleatorie, unică pentru transferul ăsta
 *     3. fișierul se criptează cu ea
 *     4. cheia se împachetează RSA-OAEP de două ori: pentru destinatar și pentru
 *        expeditor (altfel nu ți-ai mai putea deschide propriile fișiere trimise)
 *     5. SHA-256 al conținutului în clar se semnează RSA-PSS
 *     6. spre server pleacă doar cifrotextul și plicul
 *
 *   PRIMIRE (browserul destinatarului)
 *     1. se cere plicul de la API (autorizare + audit se fac acolo)
 *     2. cifrotextul se ia direct din depozit, prin URL presemnat
 *     3. cheia de fișier se despachetează cu cheia privată proprie
 *     4. se decriptează — tagul GCM garantează că niciun bit nu s-a schimbat
 *     5. se verifică semnătura expeditorului
 *     6. abia atunci se confirmă preluarea către server
 *
 * Serverul nu participă la niciun pas criptografic. Dacă ar participa, garanția
 * end-to-end ar dispărea.
 */

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
    ArrowLeftRight, Upload, Download, Search, Trash2, Loader2, Undo2, CheckCheck,
    ShieldCheck, ShieldAlert, Lock, Inbox, Send, FileWarning, AlertTriangle,
} from 'lucide-react';
import PageHeader from '../../components/ui/PageHeader';
import { apiErrorMessage } from '../../api/errors';
import KeyFingerprint from '../../components/security/KeyFingerprint';
import Badge from '../../components/ui/Badge';
import Button from '../../components/ui/Button';
import Modal from '../../components/ui/Modal';
import EmptyState from '../../components/ui/EmptyState';
import Pagination from '../../components/ui/Pagination';
import { useAuth } from '../../context/AuthContext';
import { useToast } from '../../context/ToastContext';
import { useKeys } from '../../context/KeysContext';
import { formatDateTime, formatFileSize, truncateSha } from '../../utils/format';
import {
    listTransfers, listRecipients, uploadTransfer, getEnvelope,
    fetchCiphertext, confirmTransfer, deleteTransfer, revokeTransfer,
    type TransferListItem, type Recipient, type PagedResult,
} from '../../api/transfers';
import {
    encryptFileForRecipient, decryptTransfer, saveDecryptedFile,
    importEncryptionPublicKey, importSigningPublicKey,
} from '../../crypto/E2ee';

type DirectionFilter = '' | 'received' | 'sent';
type StatusFilter = '' | 'Pending' | 'Downloaded' | 'Expired';

const EMPTY_PAGE: PagedResult<TransferListItem> = {
    items: [], totalCount: 0, page: 1, pageSize: 25,
    totalPages: 0, hasPrevious: false, hasNext: false,
};

const statusBadge = (status: string) => {
    if (status === 'Downloaded') return <Badge tone="green">Confirmat</Badge>;
    if (status === 'Expired') return <Badge tone="red">Expirat</Badge>;
    if (status === 'Revoked') return <Badge tone="gray">Retras</Badge>;
    return <Badge tone="gold">În așteptare</Badge>;
};

/**
 * Dovada de primire, afisata expeditorului sub statusul transferului.
 *
 * Distinge trei situatii pe care un singur badge „Confirmat” le amesteca:
 * nedescarcat, descarcat cu semnatura verificata, si descarcat cu semnatura
 * INVALIDA. Ultima nu e o eroare de sistem — fisierul a ajuns — dar inseamna ca
 * nu se poate dovedi cine l-a trimis, iar expeditorul are dreptul sa stie.
 */
const receiptLine = (t: TransferListItem) => {
    if (!t.isMine || !t.downloadedAt) return null;

    const when = formatDateTime(t.downloadedAt);

    if (t.signatureValid === false) {
        return (
            <p className="mt-1 flex items-center gap-1 text-[11px] font-medium text-red-600
                dark:text-red-400">
                <ShieldAlert size={11} className="shrink-0" />
                Primit {when} · semnătură INVALIDĂ
            </p>
        );
    }

    return (
        <p className="mt-1 flex items-center gap-1 text-[11px] text-green-600 dark:text-green-400">
            <CheckCheck size={11} className="shrink-0" />
            Primit {when}
            {t.signatureValid === true && ' · semnătură validă'}
        </p>
    );
};

export default function TransfersPage() {
    const { user } = useAuth();
    const toast = useToast();
    const { keys, myEncryptionPublicKey, fingerprint } = useKeys();

    // ── Listă și filtre ──────────────────────────────────────────────────────
    const [data, setData] = useState<PagedResult<TransferListItem>>(EMPTY_PAGE);
    const [loading, setLoading] = useState(true);

    const [searchInput, setSearchInput] = useState('');
    const [search, setSearch] = useState('');
    const [direction, setDirection] = useState<DirectionFilter>('');
    const [status, setStatus] = useState<StatusFilter>('');
    const [page, setPage] = useState(1);
    const [pageSize, setPageSize] = useState(25);

    // ── Trimitere ────────────────────────────────────────────────────────────
    const [uploadOpen, setUploadOpen] = useState(false);
    const [recipients, setRecipients] = useState<Recipient[]>([]);
    const [recipientId, setRecipientId] = useState('');
    const [file, setFile] = useState<File | null>(null);
    const [sending, setSending] = useState(false);
    const [sendStage, setSendStage] = useState('');
    const [uploadPercent, setUploadPercent] = useState(0);

    // ── Descărcare ───────────────────────────────────────────────────────────
    const [busyId, setBusyId] = useState<string | null>(null);
    const [deletingId, setDeletingId] = useState<string | null>(null);

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
                { search, direction, status, page, pageSize },
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
    }, [search, direction, status, page, pageSize, toast]);

    useEffect(() => {
        void load();
        return () => abortRef.current?.abort();
    }, [load]);

    // ── Deschiderea ferestrei de trimitere ───────────────────────────────────

    const openUpload = async () => {
        setUploadOpen(true);
        setFile(null);
        setRecipientId('');
        setUploadPercent(0);
        setSendStage('');

        try {
            setRecipients(await listRecipients());
        } catch {
            toast.error('Lista destinatarilor nu a putut fi încărcată.');
        }
    };

    // ── Trimiterea ───────────────────────────────────────────────────────────

    const handleSend = async () => {
        if (!file || !recipientId) return;

        if (!keys || !myEncryptionPublicKey) {
            toast.error('Cheile nu sunt descuiate. Reîncărcați pagina și introduceți parola.');
            return;
        }

        const recipient = recipients.find((r) => r.id === recipientId);
        if (!recipient) {
            toast.error('Destinatarul selectat nu mai este disponibil.');
            return;
        }

        setSending(true);
        setUploadPercent(0);

        try {
            setSendStage('Se importă cheia publică a destinatarului…');
            const recipientPublicKey = await importEncryptionPublicKey(recipient.publicKeyEncryption);

            setSendStage('Se criptează fișierul…');
            const encrypted = await encryptFileForRecipient(
                file,
                recipientPublicKey,
                myEncryptionPublicKey,
                keys.signingKey
            );

            setSendStage('Se încarcă cifrotextul…');
            await uploadTransfer(
                {
                    recipientId,
                    fileName: file.name,
                    plaintextSize: file.size,
                    ciphertext: encrypted.ciphertext,
                    envelope: encrypted.envelope,
                },
                setUploadPercent
            );

            toast.success(`Fișierul „${file.name}" a fost trimis criptat.`);
            setUploadOpen(false);
            setPage(1);
            await load();
        } catch (err) {
            console.error(err);
            const message = err instanceof Error ? err.message : 'Trimiterea a eșuat.';
            toast.error(message);
        } finally {
            setSending(false);
            setSendStage('');
        }
    };

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
                {
                    iv: envelope.iv,
                    encryptedKeyForRecipient: envelope.wrappedKeyForMe,
                    encryptedKeyForSender: envelope.wrappedKeyForMe,
                    signature: envelope.signature,
                    ciphertextSha256: envelope.ciphertextSha256,
                    suite: envelope.suite,
                },
                envelope.wrappedKeyForMe,
                keys.decryptionKey,
                senderSigningKey
            );

            saveDecryptedFile(result.plaintext, envelope.fileName);

            // Confirmarea are propriul try: fișierul e deja decriptat și salvat.
            // Un eșec aici (transfer retras sau expirat între timp, rețea căzută)
            // nu trebuie raportat ca eșec al descărcării.
            if (transfer.recipientId === String(user?.id)) {
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
            toast.error(err instanceof Error ? err.message : 'Descărcarea a eșuat.');
        } finally {
            setBusyId(null);
        }
    };

    // ── Retragerea ───────────────────────────────────────────────────────────

    const handleRevoke = async (transfer: TransferListItem) => {
        const ok = window.confirm(
            `Retrageți transferul „${transfer.fileName}"?\n\n` +
            'Fișierul va fi șters din depozit și destinatarul nu îl va mai putea ' +
            'descărca. Va vedea în schimb că transferul a fost retras.',
        );
        if (!ok) return;

        // Motivul e optional: prompt-ul anulat sau lasat gol trimite null, iar
        // destinatarul vede doar ca transferul a fost retras.
        const reason = window.prompt('Motivul retragerii (opțional):') ?? undefined;

        setDeletingId(transfer.id);
        try {
            const result = await revokeTransfer(transfer.id, reason || undefined);
            toast.success(result.message);
            await load();
        } catch (e) {
            // 409 inseamna ca destinatarul a apucat sa descarce intre incarcarea
            // listei si apasarea butonului. Mesajul serverului spune exact asta si
            // e mai util decat un generic „a esuat”.
            toast.error(apiErrorMessage(e, 'Retragerea a eșuat.'));
            await load();
        } finally {
            setDeletingId(null);
        }
    };

    // ── Ștergerea ────────────────────────────────────────────────────────────

    const handleDelete = async (transfer: TransferListItem) => {
        if (!window.confirm(`Ștergeți definitiv transferul „${transfer.fileName}"?`)) return;

        setDeletingId(transfer.id);
        try {
            await deleteTransfer(transfer.id);
            toast.success('Transferul a fost șters.');
            await load();
        } catch {
            toast.error('Ștergerea a eșuat.');
        } finally {
            setDeletingId(null);
        }
    };

    // ── Render ───────────────────────────────────────────────────────────────

    const selectClass =
        'rounded-lg border border-mai-200 dark:border-mai-600 bg-white dark:bg-mai-800 ' +
        'dark:text-mai-200 px-3 py-2 text-sm ' +
        'focus:border-mai-500 focus:outline-none focus:ring-2 focus:ring-mai-500/20 dark:focus:ring-mai-400/20';

    const recipientHasKeys = useMemo(
        () => recipients.length > 0,
        [recipients]
    );

    return (
        <div className="space-y-5">
            <PageHeader
                title="Transferuri securizate"
                subtitle="Fișierele se criptează în browser. Serverul stochează doar cifrotext."
                actions={
                    <Button onClick={() => void openUpload()}>
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

            {/* ── Banner: limitare nume fișiere necriptate ──────────────── */}
            <div className="flex items-start gap-2.5 rounded-lg border border-amber-200 dark:border-amber-700/50
                bg-amber-50 dark:bg-amber-900/20 px-4 py-3">
                <AlertTriangle size={15} className="shrink-0 text-amber-600 dark:text-amber-400 mt-0.5" />
                <p className="text-xs text-amber-800 dark:text-amber-300 leading-relaxed">
                    <span className="font-semibold">Limitare cunoscută:</span> conținutul fișierelor
                    este criptat end-to-end, dar <strong>numele fișierelor nu sunt criptate</strong> — serverul
                    le vede în clar, pentru a permite căutarea pe partea de server.
                    Evitați includerea informațiilor sensibile în numele fișierelor.
                </p>
            </div>

            {/* ── Filtre ────────────────────────────────────────────────── */}
            <div className="flex flex-col sm:flex-row sm:flex-wrap items-stretch sm:items-center gap-3">
                <div className="relative w-full sm:min-w-56 sm:flex-1">
                    <Search size={16} className="absolute left-3 top-1/2 -translate-y-1/2 text-mai-300 dark:text-mai-500" />
                    <input
                        value={searchInput}
                        onChange={(e) => setSearchInput(e.target.value)}
                        placeholder="Caută după fișier, expeditor sau destinatar…"
                        className="w-full rounded-lg border border-mai-200 dark:border-mai-600
                                   bg-white dark:bg-mai-800 dark:text-mai-100
                                   py-2 pl-9 pr-3 text-sm
                                   placeholder:text-mai-300 dark:placeholder:text-mai-500
                                   focus:border-mai-500 focus:outline-none focus:ring-2 focus:ring-mai-500/20
                                   dark:focus:ring-mai-400/20"
                    />
                </div>

                <select
                    value={direction}
                    onChange={(e) => { setDirection(e.target.value as DirectionFilter); setPage(1); }}
                    className={selectClass}
                >
                    <option value="">Toate</option>
                    <option value="received">Primite</option>
                    <option value="sent">Trimise</option>
                </select>

                <select
                    value={status}
                    onChange={(e) => { setStatus(e.target.value as StatusFilter); setPage(1); }}
                    className={selectClass}
                >
                    <option value="">Orice status</option>
                    <option value="Pending">În așteptare</option>
                    <option value="Downloaded">Confirmate</option>
                    <option value="Expired">Expirate</option>
                </select>
            </div>

            {/* ── Tabel ─────────────────────────────────────────────────── */}
            <div className="overflow-hidden rounded-xl border border-mai-100 dark:border-mai-700
                bg-white dark:bg-mai-800">
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
                                search || direction || status
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
                                <tr className="border-b border-mai-100 dark:border-mai-700 text-left text-xs
                                    uppercase tracking-wide text-mai-400">
                                    <th className="px-5 py-3 font-semibold">Fișier</th>
                                    <th className="px-5 py-3 font-semibold">Direcție</th>
                                    <th className="px-5 py-3 font-semibold">Contraparte</th>
                                    <th className="px-5 py-3 font-semibold">Data</th>
                                    <th className="px-5 py-3 font-semibold">Status</th>
                                    <th className="px-5 py-3 text-right font-semibold">Acțiuni</th>
                                </tr>
                                </thead>
                                <tbody>
                                {data.items.map((t) => (
                                    <tr key={t.id} className="border-b border-mai-50 dark:border-mai-700
                                        last:border-0 hover:bg-mai-50/40 dark:hover:bg-mai-700/40">
                                        <td className="px-5 py-3">
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
                                                </div>
                                            </div>
                                        </td>

                                        <td className="px-5 py-3">
                                            {t.isMine ? (
                                                <span className="inline-flex items-center gap-1.5 text-xs text-mai-500 dark:text-mai-300">
                                                        <Send size={13} /> Trimis
                                                    </span>
                                            ) : (
                                                <span className="inline-flex items-center gap-1.5 text-xs text-mai-500 dark:text-mai-300">
                                                        <Inbox size={13} /> Primit
                                                    </span>
                                            )}
                                        </td>

                                        <td className="px-5 py-3">
                                            <p className="text-mai-800 dark:text-mai-200">
                                                {t.isMine ? t.recipientName : t.senderName}
                                            </p>
                                            <p className="text-xs text-mai-400">
                                                {t.isMine ? t.recipientDepartment : t.senderDepartment}
                                            </p>
                                        </td>

                                        <td className="px-5 py-3 whitespace-nowrap text-mai-500 dark:text-mai-300">
                                            {formatDateTime(t.createdAt)}
                                        </td>

                                        <td className="px-5 py-3">
                                            {statusBadge(t.status)}
                                            {receiptLine(t)}
                                            {t.revokedAt && t.revokedReason && (
                                                <p className="mt-1 text-[11px] text-mai-400">
                                                    {t.revokedReason}
                                                </p>
                                            )}
                                        </td>

                                        <td className="px-5 py-3">
                                            <div className="flex items-center justify-end gap-1.5">
                                                <Button
                                                    variant="secondary"
                                                    className="!px-2.5 !py-1.5"
                                                    disabled={busyId === t.id
                                                        || t.status === 'Expired'
                                                        || t.status === 'Revoked'}
                                                    onClick={() => void handleDownload(t)}
                                                    title="Descarcă și decriptează"
                                                >
                                                    {busyId === t.id
                                                        ? <Loader2 size={15} className="animate-spin" />
                                                        : <Download size={15} />}
                                                </Button>

                                                {/*
                                                  Retragerea apare doar cat timp
                                                  serverul spune ca e posibila.
                                                  Conditia e calculata acolo
                                                  (canRevoke), nu aici: butonul si
                                                  verificarea din endpoint nu
                                                  trebuie sa poata diverge.
                                                */}
                                                {t.canRevoke && (
                                                    <Button
                                                        variant="ghost"
                                                        className="!px-2.5 !py-1.5 text-amber-600 hover:bg-amber-50
                                                            dark:text-amber-400 dark:hover:bg-amber-900/30"
                                                        disabled={deletingId === t.id}
                                                        onClick={() => void handleRevoke(t)}
                                                        title="Retrage transferul înainte de descărcare"
                                                    >
                                                        {deletingId === t.id
                                                            ? <Loader2 size={15} className="animate-spin" />
                                                            : <Undo2 size={15} />}
                                                    </Button>
                                                )}

                                                {t.isMine && (
                                                    <Button
                                                        variant="ghost"
                                                        className="!px-2.5 !py-1.5 text-red-600 hover:bg-red-50
                                                            dark:text-red-400 dark:hover:bg-red-900/30"
                                                        disabled={deletingId === t.id}
                                                        onClick={() => void handleDelete(t)}
                                                        title="Șterge transferul"
                                                    >
                                                        {deletingId === t.id
                                                            ? <Loader2 size={15} className="animate-spin" />
                                                            : <Trash2 size={15} />}
                                                    </Button>
                                                )}
                                            </div>
                                        </td>
                                    </tr>
                                ))}
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

            {/* ── Fereastra de trimitere ────────────────────────────────── */}
            <Modal
                open={uploadOpen}
                title="Trimite un fișier criptat"
                onClose={() => { if (!sending) setUploadOpen(false); }}
            >
                <div className="space-y-4">
                    <div>
                        <label htmlFor="recipient" className="mb-1.5 block text-sm font-medium text-mai-700 dark:text-mai-200">
                            Destinatar
                        </label>
                        <select
                            id="recipient"
                            value={recipientId}
                            onChange={(e) => setRecipientId(e.target.value)}
                            disabled={sending}
                            className={`${selectClass} w-full`}
                        >
                            <option value="">Selectați un destinatar…</option>
                            {recipients.map((r) => (
                                <option key={r.id} value={r.id}>
                                    {r.fullName} {r.department ? `— ${r.department}` : ''}
                                </option>
                            ))}
                        </select>

                        {!recipientHasKeys && (
                            <p className="mt-2 text-xs text-amber-700 dark:text-amber-400">
                                Niciun coleg nu și-a generat încă cheile. Fișierele criptate se pot
                                trimite doar către utilizatori care s-au autentificat cel puțin o dată
                                după activarea criptării.
                            </p>
                        )}
                    </div>

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
                                {file.name} — {formatFileSize(file.size)}
                            </p>
                        )}
                    </div>

                    <div className="rounded-lg border border-mai-100 dark:border-mai-700
                        bg-mai-50 dark:bg-mai-900 px-4 py-3">
                        <p className="text-xs leading-relaxed text-mai-500 dark:text-mai-400">
                            Fișierul se criptează pe acest calculator cu o cheie AES-256-GCM unică,
                            împachetată apoi cu cheia publică a destinatarului. Serverul primește doar
                            cifrotextul și nu îl poate deschide.
                        </p>
                    </div>

                    {/* Avertisment: numele fișierului este vizibil serverului */}
                    <div className="rounded-lg border border-amber-200 dark:border-amber-700/50
                        bg-amber-50 dark:bg-amber-900/20 px-4 py-2.5">
                        <p className="text-xs text-amber-700 dark:text-amber-400 leading-relaxed">
                            <strong>Atenție:</strong> numele fișierului nu este criptat și va fi
                            vizibil pe server. Nu includeți informații sensibile în numele fișierului.
                        </p>
                    </div>

                    {sending && (
                        <div className="space-y-2">
                            <div className="flex items-center gap-2 text-sm text-mai-600 dark:text-mai-300">
                                <Loader2 size={15} className="animate-spin" />
                                {sendStage}
                            </div>
                            {uploadPercent > 0 && (
                                <div className="h-1.5 w-full overflow-hidden rounded-full bg-mai-100 dark:bg-mai-700">
                                    <div
                                        className="h-full bg-mai-600 dark:bg-mai-400 transition-all"
                                        style={{ width: `${uploadPercent}%` }}
                                    />
                                </div>
                            )}
                        </div>
                    )}

                    <div className="flex justify-end gap-2 pt-2">
                        <Button
                            variant="secondary"
                            disabled={sending}
                            onClick={() => setUploadOpen(false)}
                        >
                            Anulează
                        </Button>
                        <Button
                            disabled={sending || !file || !recipientId}
                            onClick={() => void handleSend()}
                        >
                            {sending ? <Loader2 size={16} className="animate-spin" /> : <ShieldAlert size={16} />}
                            Criptează și trimite
                        </Button>
                    </div>
                </div>
            </Modal>
        </div>
    );
}