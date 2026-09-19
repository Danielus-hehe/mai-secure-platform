import { useCallback, useEffect, useRef, useState } from 'react';
import {
    Download, CheckCheck, Send, Pencil, Trash2, Ban, Loader2, BarChart3, FileText, Clock, Eye,
} from 'lucide-react';
import Modal from '../ui/Modal';
import Button from '../ui/Button';
import Badge from '../ui/Badge';
import { useToast } from '../../context/ToastContext';
import { apiErrorMessage } from '../../api/errors';
import { formatDateTime, formatFileSize, truncateSha } from '../../utils/format';
import {
    getInternalDocument, getDocumentReport, downloadInternalDocument, acknowledgeInternalDocument,
    publishInternalDocument, repealInternalDocument, deleteInternalDocument,
    InternalDocumentStatus, DISTRIBUTION_LABELS, STATUS_LABELS,
    type InternalDocumentDetail, type DocumentReport,
} from '../../api/internalDocuments';

interface Props {
    documentId: string;
    onClose: () => void;
    /** Lista din spate trebuie reîncărcată (stare, confirmări). */
    onChanged: () => void;
    onEdit: (doc: InternalDocumentDetail) => void;
}

const STATUS_TONE: Record<InternalDocumentStatus, 'gray' | 'green' | 'red'> = {
    [InternalDocumentStatus.Draft]:     'gray',
    [InternalDocumentStatus.Published]: 'green',
    [InternalDocumentStatus.Repealed]:  'red',
};

const pct = (part: number, total: number) => (total === 0 ? 0 : Math.round((part / total) * 100));

/** Detaliile unui document intern, acțiunile permise și raportul pentru autor. */
export default function DocumentDetailModal({ documentId, onClose, onChanged, onEdit }: Props) {
    const toast = useToast();

    const [doc, setDoc] = useState<InternalDocumentDetail | null>(null);
    const [report, setReport] = useState<DocumentReport | null>(null);
    const [tab, setTab] = useState<'details' | 'report'>('details');
    const [busy, setBusy] = useState<string | null>(null);

    // onClose vine ca funcție nouă la fiecare randare a paginii; ținută într-un
    // ref, nu reîncarcă documentul de fiecare dată când lista din spate se schimbă.
    const onCloseRef = useRef(onClose);
    useEffect(() => { onCloseRef.current = onClose; }, [onClose]);

    const reload = useCallback(async () => {
        try {
            setDoc(await getInternalDocument(documentId));
        } catch (e) {
            toast.error(apiErrorMessage(e, 'Documentul nu a putut fi încărcat.'));
            onCloseRef.current();
        }
    }, [documentId, toast]);

    useEffect(() => { void reload(); }, [reload]);

    useEffect(() => {
        if (tab !== 'report' || !doc?.canViewReport) return;
        let cancelled = false;
        getDocumentReport(documentId)
            .then((r) => { if (!cancelled) setReport(r); })
            .catch((e) => { if (!cancelled) toast.error(apiErrorMessage(e, 'Raportul nu a putut fi încărcat.')); });
        return () => { cancelled = true; };
    }, [tab, doc?.canViewReport, documentId, toast]);

    const run = async (key: string, action: () => Promise<{ message: string } | void>, after?: () => void) => {
        setBusy(key);
        try {
            const result = await action();
            if (result && 'message' in result) toast.success(result.message);
            await reload();
            onChanged();
            after?.();
        } catch (e) {
            toast.error(apiErrorMessage(e, 'Operația nu a reușit.'));
        } finally {
            setBusy(null);
        }
    };

    if (!doc) {
        return (
            <Modal open wide title="Document intern" onClose={onClose}>
                <div className="flex items-center justify-center gap-3 py-12 text-mai-400">
                    <Loader2 size={18} className="animate-spin" /> Se încarcă…
                </div>
            </Modal>
        );
    }

    const isRepealed = doc.status === InternalDocumentStatus.Repealed;

    return (
        <Modal open wide title={doc.title} onClose={onClose}>
            <div className="space-y-5">
                {/* ── Antet ───────────────────────────────────────────────── */}
                <div className="flex flex-wrap items-center gap-2 text-xs text-mai-500 dark:text-mai-400">
                    <Badge tone={STATUS_TONE[doc.status]}>{STATUS_LABELS[doc.status]}</Badge>
                    {doc.number && <span className="font-mono">{doc.number}</span>}
                    <span>·</span>
                    <span>{doc.authorName}{doc.authorUnitName ? `, ${doc.authorUnitName}` : ''}</span>
                    <span>·</span>
                    <span>{doc.publishedAt ? `publicat ${formatDateTime(doc.publishedAt)}` : `creat ${formatDateTime(doc.createdAt)}`}</span>
                </div>

                {isRepealed && (
                    <div className="rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-800
                        dark:border-red-800/50 dark:bg-red-900/20 dark:text-red-300">
                        <strong>Document abrogat</strong> la {formatDateTime(doc.repealedAt!)}
                        {doc.repealedReason ? ` — ${doc.repealedReason}` : ''}. Rămâne vizibil ca istoric și nu mai cere confirmare.
                    </div>
                )}

                {doc.canViewReport && (
                    <div className="flex gap-1 border-b border-mai-100 dark:border-mai-700">
                        {(['details', 'report'] as const).map((t) => (
                            <button
                                key={t}
                                type="button"
                                onClick={() => setTab(t)}
                                className={`-mb-px border-b-2 px-3 py-2 text-sm font-medium transition-colors
                                    ${tab === t
                                        ? 'border-mai-600 text-mai-900 dark:border-mai-300 dark:text-white'
                                        : 'border-transparent text-mai-400 hover:text-mai-700 dark:hover:text-mai-200'}`}
                            >
                                {t === 'details' ? 'Detalii' : 'Raport „Luat la cunoștință”'}
                            </button>
                        ))}
                    </div>
                )}

                {tab === 'details' ? (
                    <>
                        {doc.summary && (
                            <p className="whitespace-pre-line text-sm leading-relaxed text-mai-700 dark:text-mai-200">{doc.summary}</p>
                        )}

                        <dl className="grid grid-cols-1 gap-x-6 gap-y-3 text-sm sm:grid-cols-2">
                            <div>
                                <dt className="text-xs uppercase tracking-wide text-mai-400">Fișier</dt>
                                <dd className="mt-0.5 flex items-center gap-1.5 text-mai-800 dark:text-mai-100">
                                    <FileText size={14} className="shrink-0 text-mai-400" />
                                    <span className="truncate">{doc.fileName}</span>
                                    <span className="shrink-0 text-xs text-mai-400">{formatFileSize(doc.fileSize)}</span>
                                </dd>
                                <dd className="font-mono text-[11px] text-mai-400" title={doc.sha256}>SHA-256 {truncateSha(doc.sha256)}</dd>
                            </div>
                            <div>
                                <dt className="text-xs uppercase tracking-wide text-mai-400">Distribuție</dt>
                                <dd className="mt-0.5 text-mai-800 dark:text-mai-100">
                                    {DISTRIBUTION_LABELS[doc.distributionMode].title}
                                    {doc.targetUnits.length > 0 && (
                                        <span className="block text-xs text-mai-500 dark:text-mai-400">
                                            {doc.targetUnits.map((u) => u.name).join('; ')}
                                            {doc.includeSubunits ? ' (cu subunități)' : ''}
                                        </span>
                                    )}
                                    {doc.targetUsers.length > 0 && (
                                        <span className="block text-xs text-mai-500 dark:text-mai-400">
                                            {doc.targetUsers.map((u) => u.name).join(', ')}
                                        </span>
                                    )}
                                </dd>
                            </div>
                            {doc.isRecipient && (
                                <div>
                                    <dt className="text-xs uppercase tracking-wide text-mai-400">Starea mea</dt>
                                    <dd className="mt-0.5 space-y-0.5 text-xs">
                                        <p className={doc.myOpenedAt ? 'text-mai-700 dark:text-mai-200' : 'text-amber-600 dark:text-amber-400'}>
                                            <Eye size={12} className="mr-1 inline" />
                                            {doc.myOpenedAt ? `Deschis ${formatDateTime(doc.myOpenedAt)}` : 'Nedeschis încă'}
                                        </p>
                                        {doc.requiresAcknowledgement && (
                                            <p className={doc.myAcknowledgedAt ? 'text-green-600 dark:text-green-400' : 'text-amber-600 dark:text-amber-400'}>
                                                <CheckCheck size={12} className="mr-1 inline" />
                                                {doc.myAcknowledgedAt
                                                    ? `Luat la cunoștință ${formatDateTime(doc.myAcknowledgedAt)}`
                                                    : 'Confirmarea „Luat la cunoștință” este așteptată'}
                                            </p>
                                        )}
                                    </dd>
                                </div>
                            )}
                            {doc.counts && doc.status !== InternalDocumentStatus.Draft && (
                                <div>
                                    <dt className="text-xs uppercase tracking-wide text-mai-400">Destinatari</dt>
                                    <dd className="mt-0.5 text-xs text-mai-700 dark:text-mai-200">
                                        {doc.counts.total} în total · {doc.counts.opened} au deschis
                                        {doc.requiresAcknowledgement && ` · ${doc.counts.acknowledged} au confirmat`}
                                    </dd>
                                </div>
                            )}
                        </dl>

                        {doc.canAcknowledge && !doc.myOpenedAt && (
                            <p className="rounded-lg border border-mai-100 bg-mai-50 px-4 py-2.5 text-xs text-mai-600
                                dark:border-mai-700 dark:bg-mai-900 dark:text-mai-300">
                                Descărcați și citiți documentul; abia apoi puteți confirma că l-ați luat la cunoștință.
                            </p>
                        )}

                        {/* ── Acțiuni ────────────────────────────────────────── */}
                        <div className="flex flex-wrap justify-end gap-2 border-t border-mai-100 pt-4 dark:border-mai-700">
                            {doc.canDelete && (
                                <Button
                                    variant="ghost"
                                    className="text-red-600 hover:bg-red-50 dark:text-red-400 dark:hover:bg-red-900/30"
                                    disabled={busy !== null}
                                    onClick={() => {
                                        if (!window.confirm('Ștergeți definitiv această ciornă?')) return;
                                        void run('delete', () => deleteInternalDocument(doc.id), onClose);
                                    }}
                                >
                                    <Trash2 size={15} /> Șterge ciorna
                                </Button>
                            )}
                            {doc.canRepeal && (
                                <Button
                                    variant="ghost"
                                    className="text-red-600 hover:bg-red-50 dark:text-red-400 dark:hover:bg-red-900/30"
                                    disabled={busy !== null}
                                    onClick={() => {
                                        const reason = window.prompt('Motivul abrogării (opțional):');
                                        if (reason === null) return;
                                        void run('repeal', () => repealInternalDocument(doc.id, reason || undefined));
                                    }}
                                >
                                    {busy === 'repeal' ? <Loader2 size={15} className="animate-spin" /> : <Ban size={15} />} Abrogă
                                </Button>
                            )}
                            {doc.canEdit && (
                                <Button variant="secondary" disabled={busy !== null} onClick={() => onEdit(doc)}>
                                    <Pencil size={15} /> Modifică
                                </Button>
                            )}
                            <Button
                                variant="secondary"
                                disabled={busy !== null}
                                onClick={() => void run('download', () => downloadInternalDocument(doc.id, doc.fileName))}
                            >
                                {busy === 'download' ? <Loader2 size={15} className="animate-spin" /> : <Download size={15} />} Descarcă
                            </Button>
                            {doc.canAcknowledge && (
                                <Button
                                    disabled={busy !== null || !doc.myOpenedAt}
                                    title={doc.myOpenedAt ? undefined : 'Deschideți întâi documentul'}
                                    onClick={() => void run('ack', () => acknowledgeInternalDocument(doc.id))}
                                >
                                    {busy === 'ack' ? <Loader2 size={15} className="animate-spin" /> : <CheckCheck size={15} />} Luat la cunoștință
                                </Button>
                            )}
                            {doc.canPublish && (
                                <Button
                                    disabled={busy !== null}
                                    onClick={() => {
                                        if (!window.confirm('Publicați documentul? Lista destinatarilor se fixează acum și documentul nu mai poate fi modificat — doar abrogat.')) return;
                                        void run('publish', () => publishInternalDocument(doc.id));
                                    }}
                                >
                                    {busy === 'publish' ? <Loader2 size={15} className="animate-spin" /> : <Send size={15} />} Publică
                                </Button>
                            )}
                        </div>
                    </>
                ) : (
                    <ReportView report={report} />
                )}
            </div>
        </Modal>
    );
}

function ReportView({ report }: { report: DocumentReport | null }) {
    if (!report) {
        return (
            <div className="flex items-center justify-center gap-3 py-10 text-mai-400">
                <Loader2 size={18} className="animate-spin" /> Se încarcă raportul…
            </div>
        );
    }

    const done = report.requiresAcknowledgement ? report.acknowledged : report.opened;

    return (
        <div className="space-y-5">
            <div className="grid grid-cols-3 gap-3 text-center">
                {[
                    { label: 'Destinatari', value: report.total },
                    { label: 'Au deschis', value: report.opened },
                    { label: 'Au confirmat', value: report.requiresAcknowledgement ? report.acknowledged : '—' },
                ].map((m) => (
                    <div key={m.label} className="rounded-lg border border-mai-100 px-3 py-3 dark:border-mai-700">
                        <p className="text-xl font-bold text-mai-900 dark:text-white">{m.value}</p>
                        <p className="text-xs text-mai-400">{m.label}</p>
                    </div>
                ))}
            </div>

            <div>
                <div className="mb-1 flex justify-between text-xs text-mai-500 dark:text-mai-400">
                    <span className="inline-flex items-center gap-1"><BarChart3 size={12} /> Progres</span>
                    <span>{pct(done, report.total)}%</span>
                </div>
                <div className="h-2 overflow-hidden rounded-full bg-mai-100 dark:bg-mai-700">
                    <div className="h-full bg-green-500" style={{ width: `${pct(done, report.total)}%` }} />
                </div>
            </div>

            {/* Pe subdiviziuni */}
            <div className="overflow-x-auto">
                <table className="w-full text-sm">
                    <thead>
                    <tr className="border-b border-mai-100 text-left text-xs uppercase tracking-wide text-mai-400 dark:border-mai-700">
                        <th className="py-2 pr-3 font-semibold">Subdiviziune</th>
                        <th className="py-2 pr-3 text-right font-semibold">Total</th>
                        <th className="py-2 pr-3 text-right font-semibold">Deschis</th>
                        {report.requiresAcknowledgement && <th className="py-2 text-right font-semibold">Confirmat</th>}
                    </tr>
                    </thead>
                    <tbody>
                    {report.byUnit.map((u) => (
                        <tr key={u.orgUnitId ?? 'none'} className="border-b border-mai-50 dark:border-mai-700/60">
                            <td className="py-2 pr-3 text-mai-800 dark:text-mai-100">{u.name}</td>
                            <td className="py-2 pr-3 text-right">{u.total}</td>
                            <td className="py-2 pr-3 text-right">{u.opened}</td>
                            {report.requiresAcknowledgement && (
                                <td className="py-2 text-right">
                                    <span className={u.acknowledged === u.total ? 'text-green-600 dark:text-green-400' : ''}>
                                        {u.acknowledged} ({pct(u.acknowledged, u.total)}%)
                                    </span>
                                </td>
                            )}
                        </tr>
                    ))}
                    </tbody>
                </table>
            </div>

            {/* Pe persoane — cei care n-au confirmat apar primii */}
            <div className="max-h-72 overflow-y-auto rounded-lg border border-mai-100 dark:border-mai-700">
                <table className="w-full text-sm">
                    <thead className="sticky top-0 bg-white dark:bg-mai-800">
                    <tr className="border-b border-mai-100 text-left text-xs uppercase tracking-wide text-mai-400 dark:border-mai-700">
                        <th className="px-3 py-2 font-semibold">Persoană</th>
                        <th className="px-3 py-2 font-semibold">Deschis</th>
                        {report.requiresAcknowledgement && <th className="px-3 py-2 font-semibold">Luat la cunoștință</th>}
                    </tr>
                    </thead>
                    <tbody>
                    {report.recipients.map((r) => (
                        <tr key={r.userId} className="border-b border-mai-50 dark:border-mai-700/60">
                            <td className="px-3 py-2">
                                <p className="text-mai-800 dark:text-mai-100">
                                    {r.name}
                                    {!r.isActive && <span className="ml-2 text-[11px] text-mai-400">(cont dezactivat)</span>}
                                </p>
                                <p className="text-xs text-mai-400">{r.unitName}</p>
                            </td>
                            <td className="px-3 py-2 text-xs">
                                {r.openedAt
                                    ? <span className="text-mai-700 dark:text-mai-200">{formatDateTime(r.openedAt)}</span>
                                    : <span className="inline-flex items-center gap-1 text-amber-600 dark:text-amber-400"><Clock size={11} /> nu</span>}
                            </td>
                            {report.requiresAcknowledgement && (
                                <td className="px-3 py-2 text-xs">
                                    {r.acknowledgedAt
                                        ? <span className="inline-flex items-center gap-1 text-green-600 dark:text-green-400"><CheckCheck size={11} /> {formatDateTime(r.acknowledgedAt)}</span>
                                        : <span className="text-amber-600 dark:text-amber-400">așteptat</span>}
                                </td>
                            )}
                        </tr>
                    ))}
                    </tbody>
                </table>
            </div>
            <p className="text-[11px] text-mai-400">
                Subdiviziunea afișată este cea a destinatarului la data publicării.
            </p>
        </div>
    );
}
