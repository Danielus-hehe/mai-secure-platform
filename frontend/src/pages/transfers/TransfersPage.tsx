import { useMemo, useState } from 'react';
import { ArrowLeftRight, Upload, Download, FileCheck, Search, AlertTriangle, Clock } from 'lucide-react';
import PageHeader from '../../components/ui/PageHeader';
import Badge from '../../components/ui/Badge';
import Button from '../../components/ui/Button';
import Modal from '../../components/ui/Modal';
import { transferStore, auditStore } from '../../api/mockStore';
import { MOCK_USERS } from '../../utils/mockData';
import { useAuth } from '../../context/AuthContext';
import { useToast } from '../../context/ToastContext';
import { sha256 } from '../../utils/crypto';
import { formatDateTime, formatFileSize, truncateSha } from '../../utils/format';
import type { SecureFile } from '../../types';

type StatusFilter = 'TOATE' | SecureFile['status'];

const statusBadge = (s: SecureFile['status']) =>
    s === 'CONFIRMAT' ? <Badge tone="green">Confirmat</Badge>
    : s === 'EXPIRAT' ? <Badge tone="red">Expirat</Badge>
    :                   <Badge tone="gold">În așteptare</Badge>;

export default function TransfersPage() {
    const { user }  = useAuth();
    const toast     = useToast();

    const [items, setItems]           = useState(() => transferStore.getAll());
    const [filter, setFilter]         = useState<StatusFilter>('TOATE');
    const [search, setSearch]         = useState('');
    const [uploadOpen, setUploadOpen] = useState(false);
    const [selected, setSelected]     = useState<SecureFile | null>(null);

    const [file, setFile]             = useState<File | null>(null);
    const [hash, setHash]             = useState('');
    const [recipient, setRecipient]   = useState('');
    const [computing, setComputing]   = useState(false);

    const refresh = () => setItems(transferStore.getAll());

    const filtered = useMemo(() =>
        items
            .filter(t => filter === 'TOATE' || t.status === filter)
            .filter(t =>
                !search ||
                t.fileName.toLowerCase().includes(search.toLowerCase()) ||
                t.sender.fullName.toLowerCase().includes(search.toLowerCase()) ||
                t.recipient.fullName.toLowerCase().includes(search.toLowerCase())
            ),
        [items, filter, search],
    );

    const handleFilePick = async (f: File | null) => {
        setFile(f); setHash('');
        if (!f) return;
        setComputing(true);
        setHash(await sha256(f));
        setComputing(false);
    };

    const handleSend = () => {
        if (!file || !recipient || !user) return;
        const dest = MOCK_USERS.find(u => u.id === recipient)!;
        transferStore.add({
            fileName: file.name, sizeBytes: file.size, sha256: hash,
            sender:    { id: user.id,  fullName: user.fullName  },
            recipient: { id: dest.id,  fullName: dest.fullName  },
        });
        auditStore.log({ userId: user.id, userName: user.fullName, action: 'UPLOAD',
            target: file.name, ipAddress: '10.0.12.61', result: 'SUCCES' });
        auditStore.log({ userId: user.id, userName: user.fullName, action: 'TRANSFER',
            target: `→ ${dest.fullName}`, ipAddress: '10.0.12.61', result: 'SUCCES' });
        refresh();
        setUploadOpen(false); setFile(null); setHash(''); setRecipient('');
        toast.success(`Fișierul „${file.name}" a fost trimis cu succes către ${dest.fullName}.`);
    };

    const handleConfirm = (f: SecureFile) => {
        transferStore.confirm(f.id);
        auditStore.log({ userId: user?.id ?? '', userName: user?.fullName ?? '',
            action: 'DOWNLOAD', target: f.fileName,
            ipAddress: '10.0.12.61', result: 'SUCCES' });
        refresh();
        toast.success('Transfer confirmat — integritatea SHA-256 a fost verificată.');
    };

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
                                ${filter === s
                                    ? 'bg-mai-700 text-white'
                                    : 'text-mai-600 hover:bg-mai-100 hover:text-mai-900'}`}>
                            {s === 'TOATE'         ? 'Toate'
                            : s === 'IN_ASTEPTARE' ? 'În așteptare'
                            : s === 'CONFIRMAT'    ? 'Confirmate'
                            :                        'Expirate'}
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
                {filtered.length === 0 ? (
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
                                {filtered.map(f => {
                                    const expiringSoon =
                                        f.status === 'IN_ASTEPTARE' &&
                                        new Date(f.expiresAt).getTime() - Date.now() < 3 * 24 * 3600_000;
                                    return (
                                        <tr key={f.id} className="hover:bg-mai-100/60 transition-colors">
                                            <td className="px-5 py-3.5 font-medium text-mai-900 whitespace-nowrap">
                                                {f.fileName}
                                                <p className="text-xs text-mai-400 font-normal">{formatFileSize(f.sizeBytes)}</p>
                                            </td>
                                            <td className="px-5 py-3.5 text-mai-500 whitespace-nowrap">
                                                {f.sender.fullName} → {f.recipient.fullName}
                                            </td>
                                            <td className="px-5 py-3.5 font-mono text-xs text-mai-400 whitespace-nowrap">
                                                {truncateSha(f.sha256)}
                                            </td>
                                            <td className="px-5 py-3.5 whitespace-nowrap">
                                                <span className={`inline-flex items-center gap-1.5 text-xs
                                                    ${expiringSoon ? 'text-red-600 font-semibold' : 'text-mai-500'}`}>
                                                    {expiringSoon && <AlertTriangle size={12} />}
                                                    {formatDateTime(f.expiresAt)}
                                                </span>
                                            </td>
                                            <td className="px-5 py-3.5">{statusBadge(f.status)}</td>
                                            <td className="px-5 py-3.5 text-right whitespace-nowrap space-x-1">
                                                <Button variant="ghost" className="px-2 py-1.5" onClick={() => setSelected(f)}>
                                                    Detalii
                                                </Button>
                                                {f.status === 'IN_ASTEPTARE' && (
                                                    <Button variant="secondary" className="px-2.5 py-1.5"
                                                        onClick={() => handleConfirm(f)}>
                                                        <FileCheck size={13} /> Confirmă
                                                    </Button>
                                                )}
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
            <Modal open={uploadOpen} title="Trimite fișier securizat" onClose={() => setUploadOpen(false)}>
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

                    <label className="block">
                        <span className="block text-sm font-medium text-mai-800 mb-1.5">Destinatar</span>
                        <select value={recipient} onChange={e => setRecipient(e.target.value)}
                            className="w-full rounded-lg border border-mai-200 px-3.5 py-2.5 text-sm
                                focus:outline-none focus:ring-2 focus:ring-mai-500 hover:border-mai-300 transition-colors">
                            <option value="">— selectează utilizatorul —</option>
                            {MOCK_USERS.filter(u => u.id !== user?.id && u.isActive).map(u => (
                                <option key={u.id} value={u.id}>{u.fullName} ({u.department})</option>
                            ))}
                        </select>
                    </label>

                    <div className="flex items-center gap-2 rounded-lg bg-gold-500/10 border border-gold-500/30 p-3">
                        <AlertTriangle size={16} className="text-gold-600 shrink-0" />
                        <p className="text-xs text-mai-700">
                            Fișierul va fi criptat AES-256-GCM și va expira automat după 14 zile.
                        </p>
                    </div>

                    <Button onClick={handleSend} disabled={!file || !recipient || computing} className="w-full">
                        <ArrowLeftRight size={15} /> Trimite securizat
                    </Button>
                </div>
            </Modal>

            {/* Modal: detalii */}
            <Modal open={!!selected} title={selected?.fileName ?? ''} onClose={() => setSelected(null)}>
                {selected && (
                    <div className="space-y-3 text-sm">
                        {([
                            ['Expeditor',  selected.sender.fullName],
                            ['Destinatar', selected.recipient.fullName],
                            ['Dimensiune', formatFileSize(selected.sizeBytes)],
                            ['Creat la',   formatDateTime(selected.createdAt)],
                            ['Expiră la',  formatDateTime(selected.expiresAt)],
                            ['Status',     selected.status],
                        ] as [string, string][]).map(([k, v]) => (
                            <div key={k} className="flex justify-between border-b border-mai-50 pb-2">
                                <span className="text-mai-400">{k}</span>
                                <span className="font-medium text-mai-900">{v}</span>
                            </div>
                        ))}
                        <div>
                            <p className="text-mai-400 mb-1">Amprentă SHA-256</p>
                            <p className="font-mono text-[11px] bg-mai-50 rounded-lg p-2.5 break-all text-mai-700">
                                {selected.sha256}
                            </p>
                        </div>
                        <p className="text-xs text-mai-400 flex items-center gap-1.5">
                            <Download size={12} /> La descărcare integritatea este re-verificată automat.
                        </p>
                    </div>
                )}
            </Modal>
        </div>
    );
}
