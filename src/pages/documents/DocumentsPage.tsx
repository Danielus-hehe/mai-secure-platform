import { useMemo, useState } from 'react';
import { Landmark, Search, History, FilePlus2, ChevronDown, ChevronRight, Bell } from 'lucide-react';
import PageHeader from '../../components/ui/PageHeader';
import Badge from '../../components/ui/Badge';
import Button from '../../components/ui/Button';
import Modal from '../../components/ui/Modal';
import Input from '../../components/ui/Input';
import { documentStore } from '../../api/mockStore';
import { useAuth } from '../../context/AuthContext';
import { formatDateTime } from '../../utils/format';
import type { DocCategory } from '../../types';

const CATEGORY_LABELS: Record<DocCategory, string> = {
    ORDIN: 'Ordin intern', REGULAMENT: 'Regulament',
    PROCEDURA: 'Procedură operațională', ALTA: 'Alt document',
};

export default function DocumentsPage() {
    const { user, hasRole } = useAuth();
    const [docs, setDocs] = useState(() => documentStore.getAll());
    const [query, setQuery] = useState('');
    const [expanded, setExpanded] = useState<string | null>(null);
    const [publishOpen, setPublishOpen] = useState(false);

    const [title, setTitle] = useState('');
    const [number, setNumber] = useState('');
    const [category, setCategory] = useState<DocCategory>('ORDIN');
    const [keywords, setKeywords] = useState('');
    const [docFile, setDocFile] = useState<File | null>(null);

    const canPublish = hasRole('SEF_DIRECTIE');

    const results = useMemo(() => documentStore.search(query), [docs, query]);

    const handlePublish = () => {
        if (!title || !docFile || !user) return;
        documentStore.publish(
            { title, category, number: number || '—',
                keywords: keywords.split(',').map((k) => k.trim()).filter(Boolean) },
            docFile.name, user.fullName,
        );
        setDocs(documentStore.getAll());
        setPublishOpen(false);
        setTitle(''); setNumber(''); setKeywords(''); setDocFile(null);
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

            <div className="relative w-full max-w-md">
                <Search size={15} className="absolute left-3 top-1/2 -translate-y-1/2 text-mai-300" />
                <input value={query} onChange={(e) => setQuery(e.target.value)}
                       placeholder="Căutare după titlu, număr sau cuvinte cheie…"
                       className="w-full rounded-lg border border-mai-200 bg-white pl-9 pr-3 py-2.5 text-sm
            focus:outline-none focus:ring-2 focus:ring-mai-500" />
            </div>

            <div className="space-y-3">
                {results.map((d) => (
                    <div key={d.id} className="bg-white rounded-xl shadow-card border border-mai-100/50 overflow-hidden">
                        <button onClick={() => setExpanded(expanded === d.id ? null : d.id)}
                                className="w-full flex items-center gap-4 px-5 py-4 text-left hover:bg-mai-50/50">
                            {expanded === d.id
                                ? <ChevronDown size={18} className="text-mai-400 shrink-0" />
                                : <ChevronRight size={18} className="text-mai-400 shrink-0" />}
                            <div className="w-10 h-10 rounded-lg bg-mai-50 text-mai-600 flex items-center justify-center shrink-0">
                                <Landmark size={18} />
                            </div>
                            <div className="flex-1 min-w-0">
                                <p className="font-semibold text-mai-900 truncate">{d.title}</p>
                                <p className="text-xs text-mai-400 mt-0.5">
                                    {d.number} · v{d.currentVersion} · publicat de {d.publishedBy} · {formatDateTime(d.publishedAt)}
                                </p>
                            </div>
                            <Badge tone="blue">{CATEGORY_LABELS[d.category]}</Badge>
                        </button>

                        {expanded === d.id && (
                            <div className="border-t border-mai-100 bg-mai-50/40 px-5 py-4 space-y-4">
                                <div className="flex flex-wrap gap-1.5">
                                    {d.keywords.map((k) => <Badge key={k} tone="gray">{k}</Badge>)}
                                </div>
                                <div>
                                    <p className="text-xs font-semibold text-mai-700 uppercase tracking-wide mb-2
                    flex items-center gap-1.5">
                                        <History size={13} /> Istoric versiuni
                                    </p>
                                    <div className="space-y-1.5">
                                        {d.versions.map((v) => (
                                            <div key={v.version}
                                                 className="flex items-center justify-between bg-white rounded-lg
                          border border-mai-100 px-3.5 py-2.5 text-sm">
                                                <div className="flex items-center gap-3">
                                                    <Badge tone={v.isArchived ? 'gray' : 'green'}>v{v.version}</Badge>
                                                    <span className="font-medium text-mai-900">{v.fileName}</span>
                                                    {v.isArchived && <span className="text-xs text-mai-400">arhivat</span>}
                                                </div>
                                                <span className="text-xs text-mai-400">
                          {v.uploadedBy} · {formatDateTime(v.uploadedAt)}
                        </span>
                                            </div>
                                        ))}
                                    </div>
                                </div>
                                <p className="text-xs text-mai-400 flex items-center gap-1.5">
                                    <Bell size={12} /> Utilizatorii din direcție au fost notificați automat la publicare.
                                </p>
                            </div>
                        )}
                    </div>
                ))}
                {results.length === 0 && (
                    <p className="text-center text-sm text-mai-400 py-10">
                        Niciun document găsit pentru „{query}".
                    </p>
                )}
            </div>

            {/* Dialog publicare */}
            <Modal open={publishOpen} title="Publicare document normativ" onClose={() => setPublishOpen(false)}>
                <div className="space-y-4">
                    <Input id="doc-title" label="Titlu" value={title} onChange={(e) => setTitle(e.target.value)} />
                    <div className="grid grid-cols-2 gap-4">
                        <Input id="doc-number" label="Număr (ex: OI-160/2025)"
                               value={number} onChange={(e) => setNumber(e.target.value)} />
                        <label className="block">
                            <span className="block text-sm font-medium text-mai-800 mb-1.5">Categorie</span>
                            <select value={category} onChange={(e) => setCategory(e.target.value as DocCategory)}
                                    className="w-full rounded-lg border border-mai-200 px-3 py-2.5 text-sm
                  focus:outline-none focus:ring-2 focus:ring-mai-500">
                                {Object.entries(CATEGORY_LABELS).map(([v, l]) => (
                                    <option key={v} value={v}>{l}</option>
                                ))}
                            </select>
                        </label>
                    </div>
                    <Input id="doc-keywords" label="Cuvinte cheie (separate prin virgulă)"
                           value={keywords} onChange={(e) => setKeywords(e.target.value)}
                           placeholder="ex: disciplină, ordine internă" />
                    <label className="block rounded-lg border-2 border-dashed border-mai-200 p-5
            text-center text-sm text-mai-500 cursor-pointer hover:border-mai-400">
                        <input type="file" className="hidden" accept=".pdf"
                               onChange={(e) => setDocFile(e.target.files?.[0] ?? null)} />
                        {docFile ? docFile.name : 'Selectează fișierul PDF'}
                    </label>
                    <Button onClick={handlePublish} disabled={!title || !docFile} className="w-full">
                        Publicare (versiunea 1)
                    </Button>
                </div>
            </Modal>
        </div>
    );
}