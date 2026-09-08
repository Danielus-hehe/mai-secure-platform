import { useState, useEffect, useCallback, useMemo } from 'react';
import {
    Landmark, Search, History, FilePlus2,
    ChevronDown, ChevronRight, Archive, Download, Loader2,
} from 'lucide-react';
import PageHeader from '../../components/ui/PageHeader';
import Badge      from '../../components/ui/Badge';
import Button     from '../../components/ui/Button';
import Modal      from '../../components/ui/Modal';
import Input      from '../../components/ui/Input';
import { useAuth }  from '../../context/AuthContext';
import { useToast } from '../../context/ToastContext';
import { formatDateTime } from '../../utils/format';
import type { DocCategory } from '../../types';
import api from '../../api/client';
import { apiErrorMessage } from '../../api/errors';

const CATEGORY_LABELS: Record<DocCategory, string> = {
    ORDIN:      'Ordin intern',
    REGULAMENT: 'Regulament',
    PROCEDURA:  'Procedură operațională',
    ALTA:       'Alt document',
};

const CATEGORY_BADGE: Record<DocCategory, 'blue' | 'gold' | 'green' | 'gray'> = {
    ORDIN:      'blue',
    REGULAMENT: 'gold',
    PROCEDURA:  'green',
    ALTA:       'gray',
};

interface DocVersion {
    version:     number;
    fileName:    string;
    sha256:      string;
    uploadedAt:  string;
    uploadedBy:  string;
    changeNotes: string;
    isArchived:  boolean;
}

interface Doc {
    id:             string;
    title:          string;
    number:         string;
    category:       string;
    keywords:       string[];
    currentVersion: number;
    publishedBy:    string;
    publishedAt:    string;
    versions:       DocVersion[];
}

export default function DocumentsPage() {
    const { hasRole } = useAuth();
    const toast = useToast();

    const [docs,         setDocs]         = useState<Doc[]>([]);
    const [loading,      setLoading]      = useState(true);
    const [query,        setQuery]        = useState('');
    const [expanded,     setExpanded]     = useState<string | null>(null);
    const [publishOpen,  setPublishOpen]  = useState(false);
    const [updateTarget, setUpdateTarget] = useState<string | null>(null);
    const [publishing,   setPublishing]   = useState(false);
    const [updating,     setUpdating]     = useState(false);

    // Formular publicare
    const [title,      setTitle]      = useState('');
    const [number,     setNumber]     = useState('');
    const [category,   setCategory]   = useState<DocCategory>('ORDIN');
    const [keywords,   setKeywords]   = useState('');
    const [docFile,    setDocFile]    = useState<File | null>(null);
    const [updateFile, setUpdateFile] = useState<File | null>(null);
    const [changeNote, setChangeNote] = useState('');

    const canPublish = hasRole('SEF_DIRECTIE');

    // ── Citire documente ─────────────────────────────────────────────────
    // Toate apelurile trec prin clientul `api`, care ataseaza tokenul si il
    // reimprospateaza singur. Varianta veche construia antetul din user.token,
    // camp care nu mai exista pe obiectul User de la migrarea la refresh tokens.
    const fetchDocs = useCallback(async (search?: string) => {
        setLoading(true);
        try {
            const { data } = await api.get<Doc[]>('/Documents', {
                params: search ? { search } : undefined,
            });
            setDocs(data);
        } catch {
            toast.error('Nu s-au putut încărca documentele.');
        } finally {
            setLoading(false);
        }
    }, [toast]);

    useEffect(() => { fetchDocs(); }, [fetchDocs]);

    // Filtrare client-side pe căutare (rapid, fără re-fetch)
    const results = useMemo(() => {
        if (!query) return docs;
        const q = query.toLowerCase();
        return docs.filter(d =>
            d.title.toLowerCase().includes(q)         ||
            d.number.toLowerCase().includes(q)        ||
            d.keywords.some(k => k.toLowerCase().includes(q))
        );
    }, [docs, query]);

    // ── Publicare document nou ───────────────────────────────────────────
    const handlePublish = async () => {
        if (!title || !docFile) return;
        setPublishing(true);
        try {
            const fd = new FormData();
            fd.append('title',    title);
            fd.append('number',   number);
            fd.append('category', category);
            fd.append('keywords', keywords);
            fd.append('file',     docFile);

            await api.post('/Documents', fd);

            toast.success(`Documentul „${title}" a fost publicat cu succes.`);
            setPublishOpen(false);
            setTitle(''); setNumber(''); setKeywords(''); setDocFile(null); setCategory('ORDIN');
            fetchDocs();
        } catch (e: unknown) {
            toast.error(apiErrorMessage(e, 'Documentul nu a putut fi publicat.'));
        } finally {
            setPublishing(false);
        }
    };

    // ── Versiune nouă ────────────────────────────────────────────────────
    const handleUpdate = async () => {
        if (!updateTarget || !updateFile) return;
        setUpdating(true);
        try {
            const fd = new FormData();
            fd.append('file',        updateFile);
            fd.append('changeNotes', changeNote);

            await api.post(`/Documents/${updateTarget}/versions`, fd);

            toast.info('Versiune nouă publicată — versiunea anterioară a fost arhivată.');
            setUpdateTarget(null); setUpdateFile(null); setChangeNote('');
            fetchDocs();
        } catch (e: unknown) {
            toast.error(apiErrorMessage(e, 'Versiunea nouă nu a putut fi publicată.'));
        } finally {
            setUpdating(false);
        }
    };

    // ── Descărcare versiune curentă ──────────────────────────────────────
    const handleDownload = async (doc: Doc) => {
        try {
            const { data: blob } = await api.get<Blob>(`/Documents/${doc.id}/download`, {
                responseType: 'blob',
                timeout: 300_000,
            });
            const url = URL.createObjectURL(blob);
            const a    = document.createElement('a');
            a.href = url; a.download = doc.title;
            a.click();
            URL.revokeObjectURL(url);
        } catch {
            toast.error('Fișierul nu a putut fi descărcat.');
        }
    };

    // ── UI ───────────────────────────────────────────────────────────────
    return (
        <div className="space-y-6">
            <PageHeader
                title="Documente normative"
                subtitle="Ordine interne, regulamente și proceduri — cu versionare automată"
                actions={canPublish && (
                    <Button onClick={() => setPublishOpen(true)}>
                        <FilePlus2 size={16} /> Publicare document
                    </Button>
                )}
            />

            {/* Căutare */}
            <div className="relative w-full max-w-md">
                <Search size={15} className="absolute left-3 top-1/2 -translate-y-1/2 text-mai-300" />
                <input value={query} onChange={e => setQuery(e.target.value)}
                       placeholder="Căutare după titlu, număr sau cuvinte cheie…"
                       className="w-full rounded-lg border border-mai-200 bg-white pl-9 pr-3 py-2.5 text-sm
                        focus:outline-none focus:ring-2 focus:ring-mai-500" />
            </div>

            {/* Loading */}
            {loading && (
                <div className="flex items-center gap-3 text-mai-400 py-4">
                    <Loader2 size={18} className="animate-spin" />
                    <span className="text-sm">Se încarcă documentele…</span>
                </div>
            )}

            {/* Lista */}
            {!loading && results.length === 0 && (
                <div className="bg-white rounded-xl shadow-card border border-mai-100/50 py-14 text-center">
                    <Landmark size={36} className="mx-auto text-mai-200 mb-3" />
                    <p className="text-sm font-medium text-mai-400">
                        {query ? 'Niciun document găsit.' : 'Niciun document publicat.'}
                    </p>
                    {canPublish && !query && (
                        <p className="text-xs text-mai-300 mt-1">
                            Publicați primul document folosind butonul de mai sus.
                        </p>
                    )}
                </div>
            )}

            {!loading && results.length > 0 && (
                <div className="space-y-3">
                    {results.map(d => (
                        <div key={d.id}
                             className="bg-white rounded-xl shadow-card border border-mai-100/50 overflow-hidden">

                            {/* Header card */}
                            <button
                                onClick={() => setExpanded(expanded === d.id ? null : d.id)}
                                className="w-full flex items-center gap-4 px-5 py-4 text-left hover:bg-mai-50/50 transition-colors">
                                {expanded === d.id
                                    ? <ChevronDown  size={18} className="text-mai-400 shrink-0" />
                                    : <ChevronRight size={18} className="text-mai-400 shrink-0" />}

                                <div className="w-10 h-10 rounded-lg bg-mai-50 text-mai-600 flex items-center justify-center shrink-0">
                                    <Landmark size={18} />
                                </div>

                                <div className="flex-1 min-w-0">
                                    <p className="font-semibold text-mai-900 truncate">{d.title}</p>
                                    <p className="text-xs text-mai-400 mt-0.5">
                                        Nr. {d.number} · Publicat de {d.publishedBy} · {formatDateTime(d.publishedAt)}
                                    </p>
                                </div>

                                <div className="flex items-center gap-2 shrink-0">
                                    <Badge tone={CATEGORY_BADGE[d.category as DocCategory] ?? 'gray'}>
                                        {CATEGORY_LABELS[d.category as DocCategory] ?? d.category}
                                    </Badge>
                                    <span className="text-xs text-mai-400 bg-mai-50 px-2 py-1 rounded-full">
                                        v{d.currentVersion}
                                    </span>
                                </div>
                            </button>

                            {/* Body expandat */}
                            {expanded === d.id && (
                                <div className="px-5 pb-5 border-t border-mai-100 pt-4 space-y-4">
                                    {d.keywords.length > 0 && (
                                        <div className="flex flex-wrap gap-1.5">
                                            {d.keywords.map(k => (
                                                <span key={k}
                                                      className="text-xs bg-mai-50 text-mai-600 px-2.5 py-1 rounded-full border border-mai-100">
                                                    {k}
                                                </span>
                                            ))}
                                        </div>
                                    )}

                                    {/* Versiuni */}
                                    <div>
                                        <p className="text-xs font-semibold text-mai-700 flex items-center gap-1.5 mb-2">
                                            <History size={13} /> Istoric versiuni
                                        </p>
                                        <div className="space-y-1.5">
                                            {d.versions.map(v => (
                                                <div key={v.version}
                                                     className={`flex items-center gap-3 px-3 py-2.5 rounded-lg text-sm
                                                        ${v.isArchived ? 'bg-mai-50/50 text-mai-400' : 'bg-mai-700 text-white'}`}>
                                                    <span className="font-mono text-xs font-semibold">v{v.version}</span>
                                                    <span className="flex-1 truncate">{v.fileName}</span>
                                                    {v.changeNotes && (
                                                        <span className="text-xs opacity-70 hidden sm:block">{v.changeNotes}</span>
                                                    )}
                                                    {v.isArchived
                                                        ? <Archive size={13} className="text-mai-300" />
                                                        : <Badge tone="green">Curent</Badge>}
                                                    <span className="text-xs opacity-70">{formatDateTime(v.uploadedAt)}</span>
                                                </div>
                                            ))}
                                        </div>
                                    </div>

                                    {/* Acțiuni */}
                                    <div className="flex gap-2 pt-1">
                                        <Button variant="ghost" className="text-xs px-3 py-1.5"
                                                onClick={() => handleDownload(d)}>
                                            <Download size={13} /> Descarcă
                                        </Button>
                                        {canPublish && (
                                            <Button variant="secondary" className="text-xs px-3 py-1.5"
                                                    onClick={() => setUpdateTarget(d.id)}>
                                                <History size={13} /> Versiune nouă
                                            </Button>
                                        )}
                                    </div>
                                </div>
                            )}
                        </div>
                    ))}
                </div>
            )}

            {/* Modal: publicare document */}
            <Modal open={publishOpen} title="Publicare document normativ"
                   onClose={() => { setPublishOpen(false); setTitle(''); setNumber(''); setKeywords(''); setDocFile(null); setCategory('ORDIN'); }}>
                <div className="space-y-4">
                    <Input id="title" label="Titlu document *" value={title}
                           onChange={e => setTitle(e.target.value)} required
                           placeholder="ex: Regulament privind accesul la date" />
                    <Input id="number" label="Număr / referință" value={number}
                           onChange={e => setNumber(e.target.value)}
                           placeholder="ex: MAI-2026-142" />
                    <div>
                        <label className="block text-sm font-medium text-mai-800 mb-1.5">Categorie</label>
                        <select value={category} onChange={e => setCategory(e.target.value as DocCategory)}
                                className="w-full rounded-lg border border-mai-200 px-3.5 py-2.5 text-sm
                                focus:outline-none focus:ring-2 focus:ring-mai-500">
                            {Object.entries(CATEGORY_LABELS).map(([v, l]) => (
                                <option key={v} value={v}>{l}</option>
                            ))}
                        </select>
                    </div>
                    <Input id="keywords" label="Cuvinte cheie (separate prin virgulă)" value={keywords}
                           onChange={e => setKeywords(e.target.value)}
                           placeholder="ex: securitate, acces, date personale" />
                    <div>
                        <label className="block text-sm font-medium text-mai-800 mb-1.5">Fișier *</label>
                        <label className="flex items-center gap-3 px-4 py-3 rounded-lg border border-dashed
                            border-mai-200 hover:border-mai-400 cursor-pointer bg-mai-50/50 transition-colors">
                            <input type="file" className="hidden" accept=".pdf,.doc,.docx"
                                   onChange={e => setDocFile(e.target.files?.[0] ?? null)} />
                            <FilePlus2 size={18} className="text-mai-400" />
                            <span className="text-sm text-mai-500">
                                {docFile ? docFile.name : 'Selectează fișier PDF sau DOC(X)'}
                            </span>
                        </label>
                    </div>
                    <Button onClick={handlePublish} disabled={!title || !docFile || publishing}
                            className="w-full flex items-center justify-center gap-2">
                        <FilePlus2 size={15} />
                        {publishing ? 'Se publică…' : 'Publică documentul'}
                    </Button>
                </div>
            </Modal>

            {/* Modal: versiune nouă */}
            <Modal open={!!updateTarget} title="Adaugă versiune nouă"
                   onClose={() => { setUpdateTarget(null); setUpdateFile(null); setChangeNote(''); }}>
                <div className="space-y-4">
                    <p className="text-sm text-mai-500">
                        Versiunea anterioară va fi arhivată automat. Încărcați fișierul actualizat:
                    </p>
                    <label className="flex items-center gap-3 px-4 py-3 rounded-lg border border-dashed
                        border-mai-200 hover:border-mai-400 cursor-pointer bg-mai-50/50 transition-colors">
                        <input type="file" className="hidden" accept=".pdf,.doc,.docx"
                               onChange={e => setUpdateFile(e.target.files?.[0] ?? null)} />
                        <FilePlus2 size={18} className="text-mai-400" />
                        <span className="text-sm text-mai-500">
                            {updateFile ? updateFile.name : 'Selectează fișier PDF sau DOC(X)'}
                        </span>
                    </label>
                    <Input id="changeNote" label="Note modificări (opțional)" value={changeNote}
                           onChange={e => setChangeNote(e.target.value)}
                           placeholder="ex: Actualizat capitolul 3 privind GDPR" />
                    <Button onClick={handleUpdate} disabled={!updateFile || updating}
                            className="w-full flex items-center justify-center gap-2">
                        <History size={15} />
                        {updating ? 'Se publică…' : 'Publică versiunea nouă'}
                    </Button>
                </div>
            </Modal>
        </div>
    );
}