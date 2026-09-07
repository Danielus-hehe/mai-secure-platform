import { useState, useEffect, useCallback, useMemo } from 'react';
import {
    ArrowLeftRight, Upload, Download, FileCheck,
    Search, AlertTriangle, Clock, Loader2,
} from 'lucide-react';
import PageHeader from '../../components/ui/PageHeader';
import Badge      from '../../components/ui/Badge';
import Button     from '../../components/ui/Button';
import Modal      from '../../components/ui/Modal';
import { useAuth }  from '../../context/AuthContext';
import { useToast } from '../../context/ToastContext';
import { sha256 }   from '../../utils/crypto';
import { formatDateTime, formatFileSize, truncateSha } from '../../utils/format';

const API = 'http://localhost:5000';

// Backend TransferStatus.ToString(): "Pending" | "Downloaded" | "Expired"
const toFrontendStatus = (s: string) =>
    s === 'Pending' ? 'IN_ASTEPTARE' : s === 'Downloaded' ? 'CONFIRMAT' : 'EXPIRAT';

type StatusFilter = 'TOATE' | 'IN_ASTEPTARE' | 'CONFIRMAT' | 'EXPIRAT';

interface Transfer {
    id: string;
    fileName: string;
    fileSize: number;
    sha256: string;
    senderName: string;
    senderDepartment: string;
    recipientName: string;
    recipientDepartment: string;
    status: string;         // "Pending" | "Downloaded" | "Expired"
    frontendStatus: StatusFilter;
    createdAt: string;
    expiresAt: string;      // computed: createdAt + 14 days
    isMine: boolean;
}

interface UserOption { id: string; fullName: string; username: string; department: string; isActive: boolean; }

function addDays(iso: string, days: number): string {
    const d = new Date(iso);
    d.setDate(d.getDate() + days);
    return d.toISOString();
}

const statusBadge = (s: StatusFilter) =>
    s === 'CONFIRMAT'    ? <Badge tone="green">Confirmat</Badge>
        : s === 'EXPIRAT'    ? <Badge tone="red">Expirat</Badge>
            :                       <Badge tone="gold">În așteptare</Badge>;

export default function TransfersPage() {
    const { user }  = useAuth();
    const toast     = useToast();

    const [items,      setItems]      = useState<Transfer[]>([]);
    const [users,      setUsers]      = useState<UserOption[]>([]);
    const [loading,    setLoading]    = useState(true);
    const [filter,     setFilter]     = useState<StatusFilter>('TOATE');
    const [search,     setSearch]     = useState('');
    const [uploadOpen, setUploadOpen] = useState(false);
    const [selected,   setSelected]   = useState<Transfer | null>(null);
    const [sending,    setSending]    = useState(false);

    const [file,        setFile]        = useState<File | null>(null);
    const [hash,        setHash]        = useState('');
    const [recipientId, setRecipientId] = useState('');
    const [computing,   setComputing]   = useState(false);

    const hdrs = useCallback(() => ({
        Authorization: `Bearer ${user?.token ?? ''}`,
    }), [user?.token]);

    // ── Citire transferuri ───────────────────────────────────────────────
    const fetchTransfers = useCallback(async () => {
        setLoading(true);
        try {
            const res = await fetch(`${API}/api/Transfers`, { headers: hdrs() });
            if (!res.ok) throw new Error(`HTTP ${res.status}`);
            const data: Omit<Transfer, 'frontendStatus' | 'expiresAt'>[] = await res.json();
            setItems(data.map(t => ({
                ...t,
                frontendStatus: toFrontendStatus(t.status) as StatusFilter,
                expiresAt:      addDays(t.createdAt, 14),
            })));
        } catch {
            toast.error('Nu s-au putut încărca transferurile.');
        } finally {
            setLoading(false);
        }
    }, [hdrs, toast]);

    // ── Citire utilizatori pentru dropdown ───────────────────────────────
    useEffect(() => {
        fetch(`${API}/api/Users`, { headers: hdrs() })
            .then(r => r.ok ? r.json() : [])
            .then((data: UserOption[]) => setUsers(data.filter(u => u.isActive)))
            .catch(() => {});
    }, [hdrs]);

    useEffect(() => { fetchTransfers(); }, [fetchTransfers]);

    // ── Filtrare client-side ─────────────────────────────────────────────
    const filtered = useMemo(() =>
            items
                .filter(t => filter === 'TOATE' || t.frontendStatus === filter)
                .filter(t =>
                    !search ||
                    t.fileName.toLowerCase().includes(search.toLowerCase()) ||
                    t.senderName.toLowerCase().includes(search.toLowerCase()) ||
                    t.recipientName.toLowerCase().includes(search.toLowerCase())
                ),
        [items, filter, search],
    );

    // ── SHA-256 în browser ───────────────────────────────────────────────
    const handleFilePick = async (f: File | null) => {
        setFile(f); setHash('');
        if (!f) return;
        setComputing(true);
        setHash(await sha256(f));
        setComputing(false);
    };

    // ── Upload fișier ────────────────────────────────────────────────────
    const handleSend = async () => {
        if (!file || !recipientId || !user) return;
        setSending(true);
        try {
            const fd = new FormData();
            fd.append('file', file);
            fd.append('recipientId', recipientId);
            if (hash) fd.append('sha256', hash);

            const res = await fetch(`${API}/api/Transfers`, {
                method: 'POST',
                headers: hdrs(),   // NU setăm Content-Type! Browser-ul pune boundary automat
                body: fd,
            });
            if (!res.ok) {
                const err = await res.json().catch(() => ({ message: `HTTP ${res.status}` }));
                throw new Error(err.message);
            }
            const dest = users.find(u => u.id === recipientId);
            toast.success(`Fișierul „${file.name}" a fost trimis către ${dest?.fullName ?? 'destinatar'}.`);
            setUploadOpen(false);
            setFile(null); setHash(''); setRecipientId('');
            fetchTransfers();
        } catch (e: unknown) {
            toast.error(`Eroare: ${e instanceof Error ? e.message : 'Eroare necunoscută'}`);
        } finally {
            setSending(false);
        }
    };

    // ── Confirmare primire ───────────────────────────────────────────────
    const handleConfirm = async (t: Transfer) => {
        try {
            const res = await fetch(`${API}/api/Transfers/${t.id}/confirm`, {
                method: 'PATCH',
                headers: hdrs(),
            });
            if (!res.ok) throw new Error(`HTTP ${res.status}`);
            toast.success('Transfer confirmat — integritatea SHA-256 a fost verificată.');
            fetchTransfers();
        } catch {
            toast.error('Eroare la confirmarea transferului.');
        }
    };

    // ── Descărcare fișier ────────────────────────────────────────────────
    const handleDownload = async (t: Transfer) => {
        try {
            const res = await fetch(`${API}/api/Transfers/${t.id}/download`, { headers: hdrs() });
            if (!res.ok) throw new Error(`HTTP ${res.status}`);
            const blob = await res.blob();
            const url  = URL.createObjectURL(blob);
            const a    = document.createElement('a');
            a.href = url; a.download = t.fileName;
            a.click();
            URL.revokeObjectURL(url);
        } catch {
            toast.error('Fișierul nu a putut fi descărcat.');
        }
    };

    // ── UI ───────────────────────────────────────────────────────────────
    const recipientOptions = users.filter(u => String(u.id) !== String(user?.id));

    return (
        <div className="space-y-6">
            <PageHeader
                title="Transferuri securizate"
                subtitle="Fișiere criptate AES-256-GCM · integritate verificată SHA-256 · retenție 14 zile"
                actions={
                    <Button onClick={() => setUploadOpen(true)}>
                        <Upload size={16} /> Trimite fișier
                    </Button>
                }
            />

            {/* Filtre + căutare */}
            <div className="flex flex-wrap items-center gap-3">
                <div className="flex rounded-lg border border-mai-200 overflow-hidden bg-white">
                    {(['TOATE', 'IN_ASTEPTARE', 'CONFIRMAT', 'EXPIRAT'] as StatusFilter[]).map(s => (
                        <button key={s} onClick={() => setFilter(s)}
                                className={`px-3.5 py-2 text-sm font-medium transition
                                ${filter === s ? 'bg-mai-700 text-white' : 'text-mai-600 hover:bg-mai-100 hover:text-mai-900'}`}>
                            {s === 'TOATE' ? 'Toate' : s === 'IN_ASTEPTARE' ? 'În așteptare' : s === 'CONFIRMAT' ? 'Confirmate' : 'Expirate'}
                        </button>
                    ))}
                </div>
                <div className="relative ml-auto w-72">
                    <Search size={15} className="absolute left-3 top-1/2 -translate-y-1/2 text-mai-300" />
                    <input value={search} onChange={e => setSearch(e.target.value)}
                           placeholder="Caută fișier sau utilizator…"
                           className="w-full rounded-lg border border-mai-200 bg-white pl-9 pr-3 py-2 text-sm
                            focus:outline-none focus:ring-2 focus:ring-mai-500 hover:border-mai-300 transition-colors" />
                </div>
            </div>

            {/* Tabel */}
            <div className="bg-white rounded-xl shadow-card border border-mai-100/50 overflow-hidden">
                {loading ? (
                    <div className="flex items-center justify-center gap-3 py-14 text-mai-400">
                        <Loader2 size={20} className="animate-spin" />
                        <span className="text-sm">Se încarcă transferurile…</span>
                    </div>
                ) : filtered.length === 0 ? (
                    <div className="py-14 text-center">
                        <ArrowLeftRight size={36} className="mx-auto text-mai-200 mb-3" />
                        <p className="text-sm font-medium text-mai-400">
                            {search ? 'Niciun transfer pentru această căutare.' : 'Niciun transfer disponibil.'}
                        </p>
                    </div>
                ) : (
                    <div className="overflow-x-auto">
                        <table className="w-full text-sm">
                            <thead>
                            <tr className="bg-mai-50 text-left text-xs uppercase tracking-wide text-mai-500">
                                <th className="px-5 py-3 font-semibold">Fișier</th>
                                <th className="px-5 py-3 font-semibold">Expeditor → Destinatar</th>
                                <th className="px-5 py-3 font-semibold">SHA-256</th>
                                <th className="px-5 py-3 font-semibold">Expiră</th>
                                <th className="px-5 py-3 font-semibold">Status</th>
                                <th className="px-5 py-3 font-semibold text-right">Acțiuni</th>
                            </tr>
                            </thead>
                            <tbody className="divide-y divide-mai-50">
                            {filtered.map(t => {
                                const expiringSoon =
                                    t.frontendStatus === 'IN_ASTEPTARE' &&
                                    new Date(t.expiresAt).getTime() - Date.now() < 3 * 24 * 3600_000;
                                return (
                                    <tr key={t.id} className="hover:bg-mai-100/60 transition-colors">
                                        <td className="px-5 py-3.5 font-medium text-mai-900 whitespace-nowrap">
                                            {t.fileName}
                                            <p className="text-xs text-mai-400 font-normal">{formatFileSize(t.fileSize)}</p>
                                        </td>
                                        <td className="px-5 py-3.5 text-mai-500 whitespace-nowrap">
                                            {t.senderName} → {t.recipientName}
                                        </td>
                                        <td className="px-5 py-3.5 font-mono text-xs text-mai-400 whitespace-nowrap">
                                            {t.sha256 ? truncateSha(t.sha256) : '—'}
                                        </td>
                                        <td className="px-5 py-3.5 whitespace-nowrap">
                                                <span className={`inline-flex items-center gap-1.5 text-xs
                                                    ${expiringSoon ? 'text-red-600 font-semibold' : 'text-mai-500'}`}>
                                                    {expiringSoon && <AlertTriangle size={12} />}
                                                    {formatDateTime(t.expiresAt)}
                                                </span>
                                        </td>
                                        <td className="px-5 py-3.5">{statusBadge(t.frontendStatus)}</td>
                                        <td className="px-5 py-3.5 text-right whitespace-nowrap space-x-1">
                                            <Button variant="ghost" className="px-2 py-1.5"
                                                    onClick={() => setSelected(t)}>Detalii</Button>
                                            {t.frontendStatus === 'IN_ASTEPTARE' && !t.isMine && (
                                                <Button variant="secondary" className="px-2.5 py-1.5"
                                                        onClick={() => handleConfirm(t)}>
                                                    <FileCheck size={13} /> Confirmă
                                                </Button>
                                            )}
                                            <Button variant="ghost" className="px-2 py-1.5"
                                                    onClick={() => handleDownload(t)} title="Descarcă fișierul">
                                                <Download size={14} />
                                            </Button>
                                        </td>
                                    </tr>
                                );
                            })}
                            </tbody>
                        </table>
                    </div>
                )}
            </div>

            {/* Modal: trimite fișier */}
            <Modal open={uploadOpen} title="Trimite fișier securizat"
                   onClose={() => { setUploadOpen(false); setFile(null); setHash(''); setRecipientId(''); }}>
                <div className="space-y-5">
                    <label className="block rounded-xl border-2 border-dashed border-mai-200
                        hover:border-mai-500 transition cursor-pointer p-8 text-center bg-mai-50/50">
                        <input type="file" className="hidden"
                               accept=".pdf,.doc,.docx,.xls,.xlsx,.zip,.rar"
                               onChange={e => handleFilePick(e.target.files?.[0] ?? null)} />
                        <Upload size={28} className="mx-auto text-mai-400 mb-3" />
                        {file ? (
                            <div>
                                <p className="font-semibold text-mai-900">{file.name}</p>
                                <p className="text-xs text-mai-400">{formatFileSize(file.size)}</p>
                            </div>
                        ) : (
                            <p className="text-sm text-mai-500">
                                Trage fișierul aici sau click pentru selectare<br />
                                <span className="text-xs text-mai-300">PDF, DOC(X), XLS(X), ZIP, RAR — max 50 MB</span>
                            </p>
                        )}
                    </label>

                    {computing && (
                        <p className="text-sm text-mai-500 flex items-center gap-2">
                            <Clock size={14} className="animate-spin" />
                            Se calculează amprenta SHA-256…
                        </p>
                    )}
                    {hash && (
                        <div className="rounded-lg bg-mai-50 border border-mai-100 p-3">
                            <p className="text-xs font-semibold text-mai-700 mb-1">Amprentă SHA-256</p>
                            <p className="font-mono text-[11px] text-mai-500 break-all">{hash}</p>
                        </div>
                    )}

                    <div>
                        <label className="block text-sm font-medium text-mai-800 mb-1.5">Destinatar</label>
                        <select value={recipientId} onChange={e => setRecipientId(e.target.value)}
                                className="w-full rounded-lg border border-mai-200 px-3.5 py-2.5 text-sm
                                focus:outline-none focus:ring-2 focus:ring-mai-500 hover:border-mai-300 transition-colors">
                            <option value="">— selectează utilizatorul —</option>
                            {recipientOptions.map(u => (
                                <option key={u.id} value={u.id}>
                                    {u.fullName || u.username}{u.department ? ` (${u.department})` : ''}
                                </option>
                            ))}
                        </select>
                    </div>

                    <div className="flex items-center gap-2 rounded-lg bg-gold-500/10 border border-gold-500/30 p-3">
                        <AlertTriangle size={16} className="text-gold-600 shrink-0" />
                        <p className="text-xs text-mai-700">
                            Fișierul va fi stocat securizat și va expira automat după 14 zile.
                        </p>
                    </div>

                    <Button onClick={handleSend}
                            disabled={!file || !recipientId || computing || sending}
                            className="w-full flex items-center justify-center gap-2">
                        <ArrowLeftRight size={15} />
                        {sending ? 'Se trimite…' : 'Trimite securizat'}
                    </Button>
                </div>
            </Modal>

            {/* Modal: detalii */}
            <Modal open={!!selected} title={selected?.fileName ?? ''} onClose={() => setSelected(null)}>
                {selected && (
                    <div className="space-y-3 text-sm">
                        {([
                            ['Expeditor',  selected.senderName    + (selected.senderDepartment    ? ` (${selected.senderDepartment})`    : '')],
                            ['Destinatar', selected.recipientName + (selected.recipientDepartment ? ` (${selected.recipientDepartment})` : '')],
                            ['Dimensiune', formatFileSize(selected.fileSize)],
                            ['Creat la',   formatDateTime(selected.createdAt)],
                            ['Expiră la',  formatDateTime(selected.expiresAt)],
                            ['Status',     selected.frontendStatus],
                        ] as [string, string][]).map(([k, v]) => (
                            <div key={k} className="flex justify-between border-b border-mai-50 pb-2">
                                <span className="text-mai-400">{k}</span>
                                <span className="font-medium text-mai-900">{v}</span>
                            </div>
                        ))}
                        {selected.sha256 && (
                            <div>
                                <p className="text-mai-400 mb-1">Amprentă SHA-256</p>
                                <p className="font-mono text-[11px] bg-mai-50 rounded-lg p-2.5 break-all text-mai-700">
                                    {selected.sha256}
                                </p>
                            </div>
                        )}
                        <Button variant="secondary" className="w-full flex items-center justify-center gap-2"
                                onClick={() => handleDownload(selected)}>
                            <Download size={14} /> Descarcă fișierul
                        </Button>
                        <p className="text-xs text-mai-400 flex items-center gap-1.5">
                            <Download size={12} /> La descărcare integritatea este re-verificată automat.
                        </p>
                    </div>
                )}
            </Modal>
        </div>
    );
}