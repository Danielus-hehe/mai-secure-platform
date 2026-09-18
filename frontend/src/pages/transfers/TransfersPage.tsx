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
    ShieldCheck, ShieldAlert, Lock, Inbox, Send, FileWarning, AlertTriangle, Share2, X,
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
    forwardTransfer, searchUsers,
    TransferCategory, CATEGORY_LABELS,
    type TransferListItem, type Recipient, type PagedResult, type UserSearchResult,
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

// ── Badge-uri ────────────────────────────────────────────────────────────────

const statusBadge = (status: string) => {
    if (status === 'Downloaded') return <Badge tone="green">Confirmat</Badge>;
    if (status === 'Expired')    return <Badge tone="red">Expirat</Badge>;
    if (status === 'Revoked')    return <Badge tone="gray">Retras</Badge>;
    return <Badge tone="gold">În așteptare</Badge>;
};

/** Badge vizual pentru categoria transferului. */
const CATEGORY_STYLES: Record<TransferCategory, string> = {
    [TransferCategory.Critical]:  'bg-red-100   text-red-800   border-red-200   dark:bg-red-900/30   dark:text-red-300   dark:border-red-700',
    [TransferCategory.Important]: 'bg-amber-100 text-amber-800 border-amber-200 dark:bg-amber-900/30 dark:text-amber-300 dark:border-amber-700',
    [TransferCategory.General]:   'bg-teal-100  text-teal-800  border-teal-200  dark:bg-teal-900/30  dark:text-teal-300  dark:border-teal-700',
    [TransferCategory.Normal]:    'bg-gray-100  text-gray-600  border-gray-200  dark:bg-gray-700/40  dark:text-gray-400  dark:border-gray-600',
};

const categoryBadge = (category: TransferCategory) => (
    <span className={`inline-flex items-center rounded-full border px-2 py-0.5 text-[11px] font-medium ${CATEGORY_STYLES[category]}`}>
        {CATEGORY_LABELS[category]}
    </span>
);

/** Afișează când expiră transferul, sau "Expirat" dacă data e trecută. */
const expiryLine = (expiresAt: string | null) => {
    if (!expiresAt) return null;

    const expiry = new Date(expiresAt);
    const now    = new Date();

    if (expiry < now) {
        return (
            <p className="mt-0.5 text-[11px] font-medium text-red-600 dark:text-red-400">
                ⊘ Expirat
            </p>
        );
    }

    const diffMs    = expiry.getTime() - now.getTime();
    const diffHours = Math.ceil(diffMs / 3_600_000);
    const diffDays  = Math.ceil(diffMs / 86_400_000);

    if (diffHours <= 24) {
        return (
            <p className="mt-0.5 text-[11px] font-medium text-amber-600 dark:text-amber-400">
                Expiră în {diffHours}h
            </p>
        );
    }

    return (
        <p className="mt-0.5 text-[11px] text-mai-400 dark:text-mai-500">
            Expiră în {diffDays}z
        </p>
    );
};

/**
 * Dovada de primire, afisata expeditorului sub statusul transferului.
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

// ── Helpers pentru expirare implicită ────────────────────────────────────────

/** Returnează data peste 7 zile, formatată pentru <input type="datetime-local">. */
const defaultExpiryValue = (): string => {
    const d = new Date();
    d.setDate(d.getDate() + 7);
    // datetime-local nu acceptă secunde sau timezone — tăiem la minut
    return d.toISOString().slice(0, 16);
};

// ── Componenta principală ────────────────────────────────────────────────────

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
    const [category, setCategory] = useState<TransferCategory>(TransferCategory.General);
    const [expiresAt, setExpiresAt] = useState<string>(defaultExpiryValue);
    const [sending, setSending] = useState(false);
    const [sendStage, setSendStage] = useState('');
    const [uploadPercent, setUploadPercent] = useState(0);

    // ── Descărcare ───────────────────────────────────────────────────────────
    const [busyId, setBusyId] = useState<string | null>(null);
    const [deletingId, setDeletingId] = useState<string | null>(null);

    // ── Stare forward ────────────────────────────────────────────────────────
    const [forwardTarget, setForwardTarget]       = useState<TransferListItem | null>(null);
    const [forwardQuery, setForwardQuery]         = useState('');
    const [forwardResults, setForwardResults]     = useState<UserSearchResult[]>([]);
    const [forwardSelected, setForwardSelected]   = useState<UserSearchResult[]>([]);
    const [forwardSearching, setForwardSearching] = useState(false);
    const [forwardSubmitting, setForwardSubmitting] = useState(false);
    const [forwardDropOpen, setForwardDropOpen]   = useState(false);

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
        setCategory(TransferCategory.General);
        setExpiresAt(defaultExpiryValue());
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
                    category,
                    expiresAt: expiresAt ? new Date(expiresAt).toISOString() : undefined,
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

        const reason = window.prompt('Motivul retragerii (opțional):') ?? undefined;

        setDeletingId(transfer.id);
        try {
            const result = await revokeTransfer(transfer.id, reason || undefined);
            toast.success(result.message);
            await load();
        } catch (e) {
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

    // ── Forward: căutare utilizatori (debounced) ─────────────────────────────

    useEffect(() => {
        if (!forwardQuery.trim()) {
            setForwardResults([]);
            setForwardDropOpen(false);
            return;
        }
        const timer = window.setTimeout(async () => {
            setForwardSearching(true);
            try {
                const results = await searchUsers(forwardQuery);
                // Exclude utilizatorii deja selectați
                const selectedIds = new Set(forwardSelected.map((u) => u.id));
                setForwardResults(results.filter((u) => !selectedIds.has(u.id)));
                setForwardDropOpen(true);
            } catch {
                // Eroare silențioasă — utilizatorul poate reîncerca
            } finally {
                setForwardSearching(false);
            }
        }, 300);
        return () => window.clearTimeout(timer);
    }, [forwardQuery, forwardSelected]);

    const openForward = (transfer: TransferListItem) => {
        setForwardTarget(transfer);
        setForwardQuery('');
        setForwardResults([]);
        setForwardSelected([]);
        setForwardDropOpen(false);
    };

    const addForwardRecipient = (user: UserSearchResult) => {
        setForwardSelected((prev) => [...prev, user]);
        setForwardQuery('');
        setForwardResults([]);
        setForwardDropOpen(false);
    };

    const removeForwardRecipient = (userId: string) => {
        setForwardSelected((prev) => prev.filter((u) => u.id !== userId));
    };

    // ── Forward: trimitere ────────────────────────────────────────────────────

    const handleForwardSubmit = async () => {
        if (!forwardTarget || forwardSelected.length === 0 || !keys) return;

        setForwardSubmitting(true);
        try {
            // 1. Plicul conține wrappedKeyForMe — DEK-ul împachetat pentru userul curent.
            const envelope = await getEnvelope(forwardTarget.id);

            // 2. Despachetăm DEK-ul cu cheia privată proprie.
            const rawDek = await crypto.subtle.decrypt(
                { name: 'RSA-OAEP' },
                keys.decryptionKey,
                Uint8Array.from(atob(envelope.wrappedKeyForMe), (c) => c.charCodeAt(0))
            );

            // 3. Re-împachetăm DEK-ul cu cheia publică a fiecărui destinatar.
            //    Operațiile sunt independente — le rulăm în paralel.
            const recipientInputs = await Promise.all(
                forwardSelected.map(async (user) => {
                    const pubKey = await crypto.subtle.importKey(
                        'spki',
                        Uint8Array.from(atob(user.publicKeyEncryption), (c) => c.charCodeAt(0)),
                        { name: 'RSA-OAEP', hash: 'SHA-256' },
                        false,
                        ['encrypt']
                    );
                    const wrapped = await crypto.subtle.encrypt(
                        { name: 'RSA-OAEP' },
                        pubKey,
                        rawDek
                    );
                    // base64 fără bucle pentru ArrayBuffer → string
                    const bytes = new Uint8Array(wrapped);
                    let binary = '';
                    for (let i = 0; i < bytes.length; i++) binary += String.fromCharCode(bytes[i]);
                    return { userId: user.id, encryptedKeyForUser: btoa(binary) };
                })
            );

            // 4. Trimitem la server.
            const result = await forwardTransfer(forwardTarget.id, recipientInputs);
            toast.success(result.message);
            setForwardTarget(null);
            await load();
        } catch (err) {
            toast.error(apiErrorMessage(err, 'Redirecționarea a eșuat.'));
        } finally {
            setForwardSubmitting(false);
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
                                    <th className="px-5 py-3 font-semibold">Categorie</th>
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
                                                    {expiryLine(t.expiresAt)}
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

                                        {/* ── Categorie ── */}
                                        <td className="px-5 py-3">
                                            {categoryBadge(t.category)}
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

                                                {(t.status === 'Pending' || t.status === 'Downloaded') && (
                                                    <Button
                                                        variant="ghost"
                                                        className="!px-2.5 !py-1.5 text-mai-600 hover:bg-mai-50
                                                            dark:text-mai-400 dark:hover:bg-mai-900/30"
                                                        disabled={!keys}
                                                        onClick={() => openForward(t)}
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
                    {/* Destinatar */}
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
                                {file.name} — {formatFileSize(file.size)}
                            </p>
                        )}
                    </div>

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
                            className={`${selectClass} w-full`}
                        >
                            <option value={TransferCategory.Critical}>Critic</option>
                            <option value={TransferCategory.Important}>Important</option>
                            <option value={TransferCategory.General}>General</option>
                            <option value={TransferCategory.Normal}>Obișnuit</option>
                        </select>
                    </div>

                    {/* Data de expirare */}
                    <div>
                        <label htmlFor="transfer-expiry" className="mb-1.5 block text-sm font-medium text-mai-700 dark:text-mai-200">
                            Expiră la
                        </label>
                        <input
                            id="transfer-expiry"
                            type="datetime-local"
                            value={expiresAt}
                            min={new Date().toISOString().slice(0, 16)}
                            disabled={sending}
                            onChange={(e) => setExpiresAt(e.target.value)}
                            className={`${selectClass} w-full`}
                        />
                        <p className="mt-1 text-xs text-mai-400">
                            Implicit: 7 zile de la creare dacă nu modificați.
                        </p>
                    </div>

                    {/* Notă E2EE */}
                    <div className="rounded-lg border border-mai-100 dark:border-mai-700
                        bg-mai-50 dark:bg-mai-900 px-4 py-3">
                        <p className="text-xs leading-relaxed text-mai-500 dark:text-mai-400">
                            Fișierul se criptează pe acest calculator cu o cheie AES-256-GCM unică,
                            împachetată apoi cu cheia publică a destinatarului. Serverul primește doar
                            cifrotextul și nu îl poate deschide.
                        </p>
                    </div>

                    {/* Avertisment nume fișier */}
                    <div className="rounded-lg border border-amber-200 dark:border-amber-700/50
                        bg-amber-50 dark:bg-amber-900/20 px-4 py-2.5">
                        <p className="text-xs text-amber-700 dark:text-amber-400 leading-relaxed">
                            <strong>Atenție:</strong> numele fișierului nu este criptat și va fi
                            vizibil pe server. Nu includeți informații sensibile în numele fișierului.
                        </p>
                    </div>

                    {/* Progress */}
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

            {/* ── Modal forward ─────────────────────────────────────────── */}
            <Modal
                open={forwardTarget !== null}
                title={`Redirecționează „${forwardTarget?.fileName ?? ''}"`}
                onClose={() => { if (!forwardSubmitting) setForwardTarget(null); }}
            >
                <div className="space-y-4">
                    <p className="text-sm text-mai-500 dark:text-mai-400">
                        Fișierul rămâne criptat. DEK-ul va fi re-împachetat în browser
                        cu cheia publică a fiecărui destinatar ales.
                    </p>

                    {/* Căutare utilizatori */}
                    <div className="relative">
                        <label className="mb-1.5 block text-sm font-medium text-mai-700 dark:text-mai-200">
                            Caută destinatar
                        </label>
                        <div className="relative">
                            <input
                                type="text"
                                placeholder="Nume, username sau departament…"
                                value={forwardQuery}
                                disabled={forwardSubmitting}
                                onChange={(e) => setForwardQuery(e.target.value)}
                                onFocus={() => { if (forwardResults.length > 0) setForwardDropOpen(true); }}
                                className={`${selectClass} w-full pr-8`}
                            />
                            {forwardSearching && (
                                <Loader2 size={14} className="absolute right-2.5 top-1/2 -translate-y-1/2
                                    animate-spin text-mai-400" />
                            )}
                        </div>

                        {/* Dropdown rezultate */}
                        {forwardDropOpen && forwardResults.length > 0 && (
                            <ul className="absolute z-10 mt-1 w-full overflow-hidden rounded-lg border
                                border-mai-200 dark:border-mai-600 bg-white dark:bg-mai-800 shadow-lg">
                                {forwardResults.map((u) => (
                                    <li key={u.id}>
                                        <button
                                            type="button"
                                            className="flex w-full items-center gap-2.5 px-3 py-2.5 text-left
                                                text-sm hover:bg-mai-50 dark:hover:bg-mai-700
                                                focus:bg-mai-50 dark:focus:bg-mai-700 outline-none"
                                            onClick={() => addForwardRecipient(u)}
                                        >
                                            <span className="font-medium text-mai-800 dark:text-mai-200">
                                                {u.fullName}
                                            </span>
                                            {u.department && (
                                                <span className="text-xs text-mai-400 dark:text-mai-500">
                                                    — {u.department}
                                                </span>
                                            )}
                                        </button>
                                    </li>
                                ))}
                            </ul>
                        )}

                        {forwardDropOpen && !forwardSearching && forwardResults.length === 0 && forwardQuery.trim() && (
                            <div className="absolute z-10 mt-1 w-full rounded-lg border border-mai-200
                                dark:border-mai-600 bg-white dark:bg-mai-800 px-3 py-2.5 text-sm
                                text-mai-400 dark:text-mai-500 shadow-lg">
                                Niciun utilizator cu chei generate găsit.
                            </div>
                        )}
                    </div>

                    {/* Destinatari selectați */}
                    {forwardSelected.length > 0 && (
                        <div className="space-y-1.5">
                            <p className="text-xs font-medium uppercase tracking-wide text-mai-400 dark:text-mai-500">
                                Destinatari ({forwardSelected.length})
                            </p>
                            <ul className="space-y-1">
                                {forwardSelected.map((u) => (
                                    <li key={u.id}
                                        className="flex items-center justify-between rounded-lg
                                            bg-mai-50 dark:bg-mai-700/40 px-3 py-2">
                                        <span className="text-sm text-mai-800 dark:text-mai-200">
                                            {u.fullName}
                                            {u.department && (
                                                <span className="ml-2 text-xs text-mai-400">
                                                    {u.department}
                                                </span>
                                            )}
                                        </span>
                                        <button
                                            type="button"
                                            disabled={forwardSubmitting}
                                            onClick={() => removeForwardRecipient(u.id)}
                                            className="ml-2 rounded p-0.5 text-mai-400 hover:text-red-500
                                                dark:hover:text-red-400 transition-colors"
                                            title="Elimină destinatarul"
                                        >
                                            <X size={14} />
                                        </button>
                                    </li>
                                ))}
                            </ul>
                        </div>
                    )}

                    {/* Notă securitate */}
                    <div className="rounded-lg border border-mai-100 dark:border-mai-700
                        bg-mai-50 dark:bg-mai-900 px-4 py-3">
                        <p className="text-xs leading-relaxed text-mai-500 dark:text-mai-400">
                            DEK-ul se despachetează și re-împachetează pe acest calculator.
                            Serverul vede doar blocuri RSA-OAEP opace — nu accesează conținutul.
                        </p>
                    </div>

                    {forwardSubmitting && (
                        <div className="flex items-center gap-2 text-sm text-mai-600 dark:text-mai-300">
                            <Loader2 size={15} className="animate-spin" />
                            Se re-împachetează cheile și se trimite…
                        </div>
                    )}

                    <div className="flex justify-end gap-2 pt-2">
                        <Button
                            variant="secondary"
                            disabled={forwardSubmitting}
                            onClick={() => setForwardTarget(null)}
                        >
                            Anulează
                        </Button>
                        <Button
                            disabled={forwardSubmitting || forwardSelected.length === 0 || !keys}
                            onClick={() => void handleForwardSubmit()}
                        >
                            {forwardSubmitting
                                ? <Loader2 size={16} className="animate-spin" />
                                : <Share2 size={16} />}
                            Redirecționează
                        </Button>
                    </div>
                </div>
            </Modal>
        </div>
    );
}