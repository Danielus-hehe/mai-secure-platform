import { useMemo, useState } from 'react';
import { Landmark, Search, History, FilePlus2, ChevronDown, ChevronRight, Archive } from 'lucide-react';
import PageHeader from '../../components/ui/PageHeader';
import Badge from '../../components/ui/Badge';
import Button from '../../components/ui/Button';
import Modal from '../../components/ui/Modal';
import Input from '../../components/ui/Input';
import { documentStore } from '../../api/mockStore';
import { useAuth } from '../../context/AuthContext';
import { useToast } from '../../context/ToastContext';
import { formatDateTime } from '../../utils/format';
import type { DocCategory } from '../../types';

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

export default function DocumentsPage() {
    const { user, hasRole } = useAuth();
    const toast = useToast();
    const [docs, setDocs] = useState(() => documentStore.getAll());
    const [query, setQuery] = useState('');
    const [expanded, setExpanded] = useState<string | null>(null);
    const [publishOpen, setPublishOpen] = useState(false);
    const [updateTarget, setUpdateTarget] = useState<string | null>(null);

    const [title, setTitle]       = useState('');
    const [number, setNumber]     = useState('');
    const [category, setCategory] = useState<DocCategory>('ORDIN');
    const [keywords, setKeywords] = useState('');
    const [docFile, setDocFile]   = useState<File | null>(null);
    const [updateFile, setUpdateFile] = useState<File | null>(null);

    const canPublish = hasRole('SEF_DIRECTIE');
    const results    = useMemo(() => documentStore.search(query), [docs, query]);

    const resetForm = () => {
        setTitle(''); setNumber(''); setKeywords(''); setDocFile(null);
        setCategory('ORDIN');
    };

    const handlePublish = () => {
        if (!title || !docFile || !user) return;
        documentStore.publish(
            { title, category, number: number || '—',
              keywords: keywords.split(',').map(k => k.trim()).filter(Boolean) },
            docFile.name, user.fullName,
        );
        setDocs(documentStore.getAll());
        setPublishOpen(false);
        resetForm();
        toast.success(`Documentul „${title}" a fost publicat cu succes.`);
    };

    const handleUpdate = () => {
        if (!updateTarget || !updateFile || !user) return;
        documentStore.addNewVersion(updateTarget, updateFile.name, user.fullName);
        setDocs(documentStore.getAll());
        setUpdateTarget(null);
        setUpdateFile(null);
        toast.info('Versiune nouă publicată — versiunea anterioară a fost arhivată.');
    };

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
                <input
                    value={query}
                    onChange={e => setQuery(e.target.value)}
                    placeholder="Căutare după titlu, număr sau cuvinte cheie…"
                    className="w-full rounded-lg border border-mai-200 bg-white pl-9 pr-3 py-2.5 text-sm
                        focus:outline-none focus:ring-2 focus:ring-mai-500"
                />
            </div>

            {/* Lista documente */}
            {results.length === 0 ? (
                <div className="bg-white rounded-xl shadow-card border border-mai-100/50 py-14 text-center">
                    <Landmark size={36} className="mx-auto text-mai-200 mb-3" />
                    <p className="text-sm font-medium text-mai-400">
                        {query ? 'Niciun document găsit pentru căutarea efectuată.' : 'Niciun document publicat.'}
                    </p>
                    {canPublish && !query && (
                        <p className="text-xs text-mai-300 mt-1">
                            Publicați primul document folosind butonul de mai sus.
                        </p>
                    )}
                </div>
            ) : (
                <div className="space-y-3">
                    {results.map(d => (
                        <div key={d.id}
                            className="bg-white rounded-xl shadow-card border border-mai-100/50 overflow-hidden">
                            {/* Header card */}
                            <button
                                onClick={() => setExpanded(expanded === d.id ? null : d.id)}
                                className="w-full flex items-center gap-4 px-5 py-4 text-left hover:bg-mai-50/50 transition-colors"
                            >
                                {expanded === d.id
                                    ? <ChevronDown size={18} className="text-mai-400 shrink-0" />
                                    : <ChevronRight size={18} className="text-mai-400 shrink-0" />}

                                <div className="w-10 h-10 rounded-lg bg-mai-50 text-mai-600
                                    flex items-center justify-center shrink-0">
                                    <Landmark size={18} />
                                </div>

                                <div className="flex-1 min-w-0">
                                    <p className="font-semibold text-mai-900 truncate">{d.title}</p>
                                    <p className="text-xs text-mai-400 mt-0.5">
                                        Nr. {d.number} · Publicat de {d.publishedBy} · {formatDateTime(d.publishedAt)}
                                    </p>
                                </div>

                                <div className="flex items-center gap-2 shrink-0">
                                    <Badge tone={CATEGORY_BADGE[d.category]}>
                                        {CATEGORY_LABELS[d.category]}
                                    </Badge>
                                    <span className="text-xs text-mai-400 bg-mai-50 px-2 py-1 rounded-full">
                                        v{d.currentVersion}
                                    </span>
                                </div>
                            </button>

                            {/* Body expandat */}
                            {expanded === d.id && (
                                <div className="px-5 pb-5 border-t border-mai-100 pt-4 space-y-4">
                                    {/* Cuvinte cheie */}
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
                                                        ${v.isArchived
                                                            ? 'bg-mai-50/50 text-mai-400'
                                                            : 'bg-mai-700 text-white'}`}>
                                                    <span className="font-mono text-xs font-semibold">
                                                        v{v.version}
                                                    </span>
                                                    <span className="flex-1 truncate">{v.fileName}</span>
                                                    {v.isArchived
                                                        ? <Archive size={13} className="text-mai-300" />
                                                        : <Badge tone="green">Curent</Badge>}
                                                    <span className="text-xs opacity-70">
                                                        {formatDateTime(v.uploadedAt)}
                                                    </span>
                                                </div>
                                            ))}
                                        </div>
                                    </div>

                                    {/* Acțiuni */}
                                    {canPublish && (
                                        <div className="flex gap-2 pt-1">
                                            <Button
                                                variant="secondary"
                                                className="text-xs px-3 py-1.5"
                                                onClick={() => setUpdateTarget(d.id)}
                                            >
                                                <History size={13} /> Versiune nouă
                                            </Button>
                                        </div>
                                    )}
                                </div>
                            )}
                        </div>
                    ))}
                </div>
            )}

            {/* Modal: publicare document */}
            <Modal open={publishOpen} title="Publicare document normativ" onClose={() => { setPublishOpen(false); resetForm(); }}>
                <div className="space-y-4">
                    <Input id="title" label="Titlu document" value={title}
                        onChange={e => setTitle(e.target.value)} required
                        placeholder="ex: Regulament privind accesul la date" />
                    <Input id="number" label="Număr / referință" value={number}
                        onChange={e => setNumber(e.target.value)}
                        placeholder="ex: MAI-2025-142" />
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
                        <label className="block text-sm font-medium text-mai-800 mb-1.5">Fișier</label>
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
                    <Button onClick={handlePublish} disabled={!title || !docFile} className="w-full">
                        <FilePlus2 size={15} /> Publică documentul
                    </Button>
                </div>
            </Modal>

            {/* Modal: versiune nouă */}
            <Modal open={!!updateTarget} title="Adaugă versiune nouă" onClose={() => { setUpdateTarget(null); setUpdateFile(null); }}>
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
                    <Button onClick={handleUpdate} disabled={!updateFile} className="w-full">
                        <History size={15} /> Publică versiunea nouă
                    </Button>
                </div>
            </Modal>
        </div>
    );
}
