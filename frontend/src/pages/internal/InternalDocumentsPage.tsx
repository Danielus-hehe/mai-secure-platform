/**
 * Documente interne: dispoziții, note, circulare distribuite pe structura
 * organizatorică, cu confirmare „Luat la cunoștință”.
 *
 *   • „Primite” — documentele publicate care mi-au fost distribuite;
 *     filtrul „De confirmat” le arată doar pe cele care îmi cer confirmarea.
 *   • „Create de mine” — ciornele și documentele publicate de mine, cu
 *     progresul confirmărilor.
 */

import { useCallback, useEffect, useRef, useState } from 'react';
import { FileStack, Plus, Search, Loader2, CheckCheck, Clock, Inbox, PenLine } from 'lucide-react';
import PageHeader from '../../components/ui/PageHeader';
import Button from '../../components/ui/Button';
import Badge from '../../components/ui/Badge';
import EmptyState from '../../components/ui/EmptyState';
import Pagination from '../../components/ui/Pagination';
import DocumentEditorModal from '../../components/internal/DocumentEditorModal';
import DocumentDetailModal from '../../components/internal/DocumentDetailModal';
import { useToast } from '../../context/ToastContext';
import { formatDateTime } from '../../utils/format';
import type { PagedResult } from '../../api/transfers';
import {
    listInternalDocuments, InternalDocumentStatus, STATUS_LABELS, DISTRIBUTION_LABELS,
    type InternalDocumentListItem, type InternalDocumentDetail,
} from '../../api/internalDocuments';

type Box = 'inbox' | 'authored';

const EMPTY: PagedResult<InternalDocumentListItem> = {
    items: [], totalCount: 0, page: 1, pageSize: 25, totalPages: 0, hasPrevious: false, hasNext: false,
};

const statusBadge = (d: InternalDocumentListItem) => {
    if (d.status === InternalDocumentStatus.Draft)    return <Badge tone="gray">Ciornă</Badge>;
    if (d.status === InternalDocumentStatus.Repealed) return <Badge tone="red">Abrogat</Badge>;
    return <Badge tone="green">{STATUS_LABELS[d.status]}</Badge>;
};

/** Starea mea (la primite) sau progresul confirmărilor (la create de mine). */
function Progress({ d, box }: { d: InternalDocumentListItem; box: Box }) {
    if (box === 'authored') {
        if (d.status === InternalDocumentStatus.Draft || d.recipientCount === null) return <span className="text-xs text-mai-400">—</span>;
        const done = d.requiresAcknowledgement ? d.acknowledgedCount ?? 0 : d.openedCount ?? 0;
        return (
            <span className={`text-xs ${done === d.recipientCount ? 'text-green-600 dark:text-green-400' : 'text-mai-600 dark:text-mai-300'}`}>
                {done} / {d.recipientCount} {d.requiresAcknowledgement ? 'confirmări' : 'deschideri'}
            </span>
        );
    }

    if (d.requiresAcknowledgement && d.status === InternalDocumentStatus.Published) {
        return d.myAcknowledgedAt ? (
            <span className="inline-flex items-center gap-1 text-xs text-green-600 dark:text-green-400">
                <CheckCheck size={12} /> Confirmat {formatDateTime(d.myAcknowledgedAt)}
            </span>
        ) : (
            <span className="inline-flex items-center gap-1 text-xs font-medium text-amber-600 dark:text-amber-400">
                <Clock size={12} /> De confirmat
            </span>
        );
    }

    return d.myOpenedAt
        ? <span className="text-xs text-mai-500 dark:text-mai-400">Deschis {formatDateTime(d.myOpenedAt)}</span>
        : <span className="text-xs text-mai-400">Nedeschis</span>;
}

export default function InternalDocumentsPage() {
    const toast = useToast();

    const [box, setBox] = useState<Box>('inbox');
    const [pendingOnly, setPendingOnly] = useState(false);
    const [status, setStatus] = useState<InternalDocumentStatus | ''>('');
    const [searchInput, setSearchInput] = useState('');
    const [search, setSearch] = useState('');
    const [page, setPage] = useState(1);
    const [pageSize, setPageSize] = useState(25);

    const [data, setData] = useState(EMPTY);
    const [loading, setLoading] = useState(true);

    const [openId, setOpenId] = useState<string | null>(null);
    const [editor, setEditor] = useState<{ existing: InternalDocumentDetail | null } | null>(null);

    useEffect(() => {
        const t = window.setTimeout(() => { setSearch(searchInput.trim()); setPage(1); }, 350);
        return () => window.clearTimeout(t);
    }, [searchInput]);

    const abortRef = useRef<AbortController | null>(null);

    const load = useCallback(async () => {
        abortRef.current?.abort();
        const controller = new AbortController();
        abortRef.current = controller;
        setLoading(true);
        try {
            setData(await listInternalDocuments(
                { box, search, status, pending: box === 'inbox' && pendingOnly, page, pageSize },
                controller.signal
            ));
        } catch {
            if (controller.signal.aborted) return;
            toast.error('Documentele nu au putut fi încărcate.');
        } finally {
            if (!controller.signal.aborted) setLoading(false);
        }
    }, [box, search, status, pendingOnly, page, pageSize, toast]);

    useEffect(() => {
        void load();
        return () => abortRef.current?.abort();
    }, [load]);

    const switchBox = (b: Box) => {
        setBox(b);
        setPage(1);
        setStatus('');
        setPendingOnly(false);
    };

    const tabCls = (active: boolean) =>
        `inline-flex items-center gap-2 border-b-2 px-4 py-2.5 text-sm font-medium transition-colors ${
            active
                ? 'border-mai-600 text-mai-900 dark:border-mai-300 dark:text-white'
                : 'border-transparent text-mai-400 hover:text-mai-700 dark:hover:text-mai-200'
        }`;

    const selectCls = 'rounded-lg border border-mai-200 dark:border-mai-600 bg-white dark:bg-mai-800 dark:text-mai-200 ' +
        'px-3 py-2 text-sm focus:border-mai-500 focus:outline-none focus:ring-2 focus:ring-mai-500/20';

    return (
        <div className="space-y-5">
            <PageHeader
                title="Documente interne"
                subtitle="Distribuite pe structura organizatorică, cu confirmare de luare la cunoștință."
                actions={
                    <Button onClick={() => setEditor({ existing: null })}>
                        <Plus size={16} /> Document nou
                    </Button>
                }
            />

            <div className="flex border-b border-mai-100 dark:border-mai-700">
                <button type="button" className={tabCls(box === 'inbox')} onClick={() => switchBox('inbox')}>
                    <Inbox size={15} /> Primite
                </button>
                <button type="button" className={tabCls(box === 'authored')} onClick={() => switchBox('authored')}>
                    <PenLine size={15} /> Create de mine
                </button>
            </div>

            <div className="flex flex-col items-stretch gap-3 sm:flex-row sm:flex-wrap sm:items-center">
                <div className="relative w-full sm:min-w-56 sm:flex-1">
                    <Search size={16} className="absolute left-3 top-1/2 -translate-y-1/2 text-mai-300 dark:text-mai-500" />
                    <input
                        value={searchInput}
                        onChange={(e) => setSearchInput(e.target.value)}
                        placeholder="Caută după titlu, număr, rezumat sau autor…"
                        className="w-full rounded-lg border border-mai-200 bg-white py-2 pl-9 pr-3 text-sm placeholder:text-mai-300
                            focus:border-mai-500 focus:outline-none focus:ring-2 focus:ring-mai-500/20 dark:border-mai-600
                            dark:bg-mai-800 dark:text-mai-100 dark:placeholder:text-mai-500"
                    />
                </div>

                <select
                    value={status === '' ? '' : String(status)}
                    onChange={(e) => { setStatus(e.target.value === '' ? '' : Number(e.target.value) as InternalDocumentStatus); setPage(1); }}
                    className={selectCls}
                    aria-label="Stare"
                >
                    <option value="">Orice stare</option>
                    {box === 'authored' && <option value={InternalDocumentStatus.Draft}>Ciorne</option>}
                    <option value={InternalDocumentStatus.Published}>Publicate</option>
                    <option value={InternalDocumentStatus.Repealed}>Abrogate</option>
                </select>

                {box === 'inbox' && (
                    <label className="inline-flex cursor-pointer items-center gap-2 text-sm text-mai-700 dark:text-mai-200">
                        <input
                            type="checkbox"
                            checked={pendingOnly}
                            onChange={(e) => { setPendingOnly(e.target.checked); setPage(1); }}
                            className="h-4 w-4 rounded border-mai-300 text-mai-600 focus:ring-mai-500"
                        />
                        Doar cele de confirmat
                    </label>
                )}
            </div>

            <div className="overflow-hidden rounded-xl border border-mai-100 bg-white dark:border-mai-700 dark:bg-mai-800">
                {loading ? (
                    <div className="flex items-center justify-center gap-3 py-16 text-mai-400">
                        <Loader2 size={18} className="animate-spin" /> Se încarcă…
                    </div>
                ) : data.items.length === 0 ? (
                    <div className="p-6">
                        <EmptyState
                            icon={FileStack}
                            title={box === 'inbox' ? 'Niciun document primit' : 'Niciun document creat'}
                            description={
                                box === 'inbox'
                                    ? (pendingOnly ? 'Nu aveți documente de confirmat.' : 'Documentele distribuite dumneavoastră apar aici.')
                                    : 'Creați un document, alegeți cui se distribuie și publicați-l.'
                            }
                        />
                    </div>
                ) : (
                    <>
                        <div className="overflow-x-auto">
                            <table className="w-full text-sm">
                                <thead>
                                <tr className="border-b border-mai-100 text-left text-xs uppercase tracking-wide text-mai-400 dark:border-mai-700">
                                    <th className="px-4 py-3 font-semibold">Document</th>
                                    <th className="px-4 py-3 font-semibold">{box === 'inbox' ? 'Autor' : 'Distribuție'}</th>
                                    <th className="px-4 py-3 font-semibold">Data</th>
                                    <th className="px-4 py-3 font-semibold">Stare</th>
                                    <th className="px-4 py-3 font-semibold">{box === 'inbox' ? 'Situația mea' : 'Progres'}</th>
                                </tr>
                                </thead>
                                <tbody>
                                {data.items.map((d) => (
                                    <tr
                                        key={d.id}
                                        onClick={() => setOpenId(d.id)}
                                        className="cursor-pointer border-b border-mai-50 hover:bg-mai-50/50 dark:border-mai-700 dark:hover:bg-mai-700/40"
                                    >
                                        <td className="px-4 py-3">
                                            <p className="font-medium text-mai-900 dark:text-mai-100">{d.title}</p>
                                            <p className="text-xs text-mai-400">
                                                {d.number && <span className="mr-2 font-mono">{d.number}</span>}
                                                {d.summary && <span className="line-clamp-1">{d.summary}</span>}
                                            </p>
                                        </td>
                                        <td className="px-4 py-3 text-mai-600 dark:text-mai-300">
                                            {box === 'inbox' ? (
                                                <>
                                                    <p>{d.authorName}</p>
                                                    {d.authorUnitName && <p className="text-xs text-mai-400">{d.authorUnitName}</p>}
                                                </>
                                            ) : (
                                                <span className="text-xs">{DISTRIBUTION_LABELS[d.distributionMode].title}</span>
                                            )}
                                        </td>
                                        <td className="whitespace-nowrap px-4 py-3 text-xs text-mai-500 dark:text-mai-300">
                                            {formatDateTime(d.publishedAt ?? d.createdAt)}
                                        </td>
                                        <td className="px-4 py-3">{statusBadge(d)}</td>
                                        <td className="px-4 py-3"><Progress d={d} box={box} /></td>
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
                            itemLabel="documente"
                        />
                    </>
                )}
            </div>

            {openId && (
                <DocumentDetailModal
                    documentId={openId}
                    onClose={() => setOpenId(null)}
                    onChanged={() => void load()}
                    onEdit={(doc) => { setOpenId(null); setEditor({ existing: doc }); }}
                />
            )}

            {editor && (
                <DocumentEditorModal
                    existing={editor.existing}
                    onClose={() => setEditor(null)}
                    onSaved={(id) => {
                        setEditor(null);
                        if (box !== 'authored') switchBox('authored'); else void load();
                        setOpenId(id);
                    }}
                />
            )}
        </div>
    );
}
