import { useState, useEffect, useCallback, useRef } from 'react';
import { ScrollText, Download, AlertCircle, Loader2, Search, X } from 'lucide-react';
import PageHeader from '../../components/ui/PageHeader';
import Badge from '../../components/ui/Badge';
import Button from '../../components/ui/Button';
import Pagination, { type PagedResult } from '../../components/ui/Pagination';
import { api } from '../../api/client';
import { useAuth } from '../../context/AuthContext';
import { useToast } from '../../context/ToastContext';
import { formatDateTime } from '../../utils/format';

type ActionKey =
    | 'LOGIN' | 'LOGOUT' | 'UPLOAD' | 'DOWNLOAD' | 'MODIFICARE_DOC'
    | 'TRANSFER' | 'SECURITATE' | 'STRUCTURA' | 'DOC_INTERN' | 'ADMIN';

const ACTION_LABELS: Record<ActionKey, string> = {
    LOGIN: 'Autentificare',
    LOGOUT: 'Deconectare',
    UPLOAD: 'Încărcare',
    DOWNLOAD: 'Descărcare',
    MODIFICARE_DOC: 'Modificare doc',
    TRANSFER: 'Transfer',
    SECURITATE: 'Securitate cont',
    STRUCTURA: 'Structură organizatorică',
    DOC_INTERN: 'Document intern',
    ADMIN: 'Administrare',
};

const selectCls = `rounded-lg border border-mai-200 dark:border-mai-600 bg-white dark:bg-mai-800 dark:text-mai-200 px-3 py-2 text-sm
    focus:outline-none focus:ring-2 focus:ring-mai-500 cursor-pointer
    hover:border-mai-400 dark:hover:border-mai-500 transition-colors`;

const inputCls = `rounded-lg border border-mai-200 dark:border-mai-600 bg-white dark:bg-mai-800 dark:text-mai-200 px-3 py-2 text-sm
    focus:outline-none focus:ring-2 focus:ring-mai-500
    hover:border-mai-400 dark:hover:border-mai-500 transition-colors`;

/**
 * Rezultatul unei operatii, asa cum il trimite backend-ul.
 *
 * ATENTIE nu e nici succes, nici esec: operatia a reusit, dar merita privita —
 * 2FA dezactivat, reset administrativ, semnatura invalida la descarcare. Inainte
 * de migrarea pe coloana `Result`, aceste randuri erau afisate ca simplu SUCCES
 * si se pierdeau in lista.
 */
type AuditResult = 'SUCCES' | 'ESEC' | 'ATENTIE';

const RESULT_TONE: Record<AuditResult, 'green' | 'red' | 'gold'> = {
    SUCCES:  'green',
    ESEC:    'red',
    ATENTIE: 'gold',
};

/** Fundalul randului. Succesul nu primeste niciunul: e cazul normal. */
const RESULT_ROW_CLASS: Record<AuditResult, string> = {
    SUCCES:  '',
    ESEC:    'bg-red-50/60 dark:bg-red-900/20',
    ATENTIE: 'bg-amber-50/60 dark:bg-amber-900/20',
};

interface AuditEntry {
    id: string;
    timestamp: string;
    userName: string;
    action: string;
    target: string;
    ipAddress: string;
    result: AuditResult;
}

export default function AuditPage() {
    const { hasRole } = useAuth();
    const toast = useToast();

    const [entries, setEntries] = useState<AuditEntry[]>([]);
    const [usernames, setUsernames] = useState<string[]>([]);
    const [loading, setLoading] = useState(true);
    const [exporting, setExporting] = useState(false);

    const [userFilter, setUserFilter] = useState('');
    const [actionFilter, setActionFilter] = useState('');
    const [resultFilter, setResultFilter] = useState('');
    const [dateFrom, setDateFrom] = useState('');
    const [dateTo, setDateTo] = useState('');

    // Doua stari pentru cautare: ce tasteaza userul si ce s-a trimis la server.
    const [searchInput, setSearchInput] = useState('');
    const [search, setSearch] = useState('');

    const [page, setPage] = useState(1);
    const [pageSize, setPageSize] = useState(25);
    const [totalCount, setTotalCount] = useState(0);
    const [totalPages, setTotalPages] = useState(0);

    // Debounce 400 ms: fara el, fiecare tasta declanseaza un query pe toata tabela.
    useEffect(() => {
        const t = setTimeout(() => setSearch(searchInput.trim()), 400);
        return () => clearTimeout(t);
    }, [searchInput]);

    // Orice schimbare de filtru readuce la pagina 1 — altfel ramai pe pagina 8
    // a unui rezultat care are acum 2 pagini si vezi tabel gol.
    const isFirstRender = useRef(true);
    useEffect(() => {
        if (isFirstRender.current) {
            isFirstRender.current = false;
            return;
        }
        setPage(1);
    }, [userFilter, actionFilter, resultFilter, search, dateFrom, dateTo, pageSize]);

    const buildParams = useCallback(() => {
        const params = new URLSearchParams();
        if (userFilter) params.set('username', userFilter);
        if (actionFilter) params.set('action', actionFilter);
        if (resultFilter) params.set('result', resultFilter);
        if (search) params.set('search', search);
        if (dateFrom) params.set('from', dateFrom);
        if (dateTo) params.set('to', dateTo);
        return params;
    }, [userFilter, actionFilter, resultFilter, search, dateFrom, dateTo]);

    const fetchEntries = useCallback(async () => {
        setLoading(true);
        try {
            const params = buildParams();
            params.set('page', String(page));
            params.set('pageSize', String(pageSize));

            const { data } = await api.get<PagedResult<AuditEntry>>(`/AuditLogs?${params}`);
            setEntries(data.items);
            setTotalCount(data.totalCount);
            setTotalPages(data.totalPages);
        } catch {
            toast.error('Nu s-au putut încărca înregistrările de audit.');
        } finally {
            setLoading(false);
        }
    }, [buildParams, page, pageSize, toast]);

    useEffect(() => {
        api.get<string[]>('/AuditLogs/usernames')
            .then((r) => setUsernames(r.data))
            .catch(() => {});
    }, []);

    useEffect(() => {
        fetchEntries();
    }, [fetchEntries]);

    /**
     * Descarcarea nu se poate face cu un simplu <a href> pentru ca endpointul
     * cere header Authorization. Luam blob-ul prin axios si il salvam local.
     */
    const handleExport = async (format: 'xlsx' | 'csv') => {
        setExporting(true);
        try {
            const params = buildParams();
            params.set('format', format);

            const res = await api.get(`/AuditLogs/export?${params}`, { responseType: 'blob' });

            // Numele fisierului vine din Content-Disposition; daca lipseste, generam unul.
            const disposition = res.headers['content-disposition'] as string | undefined;
            const match = disposition?.match(/filename="?([^";]+)"?/);
            const fileName =
                match?.[1] ?? `jurnal_audit_${new Date().toISOString().slice(0, 10)}.${format}`;

            const url = URL.createObjectURL(res.data as Blob);
            const link = document.createElement('a');
            link.href = url;
            link.download = fileName;
            document.body.appendChild(link);
            link.click();
            link.remove();
            URL.revokeObjectURL(url);

            toast.success(`Jurnal exportat — ${totalCount} înregistrări.`);
        } catch (err: unknown) {
            // Backendul refuza exporturile peste 50.000 de randuri; mesajul vine ca blob JSON.
            let message = 'Exportul a eșuat.';
            const blob = (err as { response?: { data?: Blob } })?.response?.data;
            if (blob instanceof Blob) {
                try {
                    const parsed = JSON.parse(await blob.text());
                    if (parsed?.message) message = parsed.message;
                } catch {
                    /* raspuns care nu e JSON — pastram mesajul generic */
                }
            }
            toast.error(message);
        } finally {
            setExporting(false);
        }
    };

    const resetFilters = () => {
        setUserFilter('');
        setActionFilter('');
        setResultFilter('');
        setSearchInput('');
        setDateFrom('');
        setDateTo('');
    };

    const hasFilters =
        !!userFilter || !!actionFilter || !!resultFilter || !!searchInput || !!dateFrom || !!dateTo;

    const canExport = hasRole('SEF_DIRECTIE');

    return (
        <div className="space-y-6">
            <PageHeader
                title="Jurnal de audit"
                subtitle="Toate acțiunile înregistrate: utilizator, acțiune, timestamp, IP, rezultat"
                actions={
                    canExport ? (
                        <div className="flex gap-2">
                            <Button
                                variant="secondary"
                                onClick={() => handleExport('xlsx')}
                                disabled={exporting || totalCount === 0}
                            >
                                {exporting ? (
                                    <Loader2 size={15} className="animate-spin" />
                                ) : (
                                    <Download size={15} />
                                )}{' '}
                                Export Excel
                            </Button>
                            <Button
                                variant="secondary"
                                onClick={() => handleExport('csv')}
                                disabled={exporting || totalCount === 0}
                            >
                                <Download size={15} /> CSV
                            </Button>
                        </div>
                    ) : undefined
                }
            />

            {/* Filtre */}
            <div className="space-y-3">
                <div className="flex flex-col sm:flex-row sm:flex-wrap items-stretch sm:items-center gap-3">
                    <div className="relative">
                        <Search
                            size={15}
                            className="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-mai-300"
                        />
                        <input
                            type="text"
                            value={searchInput}
                            onChange={(e) => setSearchInput(e.target.value)}
                            placeholder="Caută în utilizator, detalii, IP…"
                            className={`${inputCls} w-full sm:w-72 pl-9 dark:text-mai-200`}
                        />
                    </div>

                    <select
                        value={userFilter}
                        onChange={(e) => setUserFilter(e.target.value)}
                        className={selectCls}
                    >
                        <option value="">Toți utilizatorii</option>
                        {usernames.map((n) => (
                            <option key={n}>{n}</option>
                        ))}
                    </select>

                    <select
                        value={actionFilter}
                        onChange={(e) => setActionFilter(e.target.value)}
                        className={selectCls}
                    >
                        <option value="">Toate acțiunile</option>
                        {(Object.entries(ACTION_LABELS) as [ActionKey, string][]).map(([v, l]) => (
                            <option key={v} value={v}>
                                {l}
                            </option>
                        ))}
                    </select>

                    <select
                        value={resultFilter}
                        onChange={(e) => setResultFilter(e.target.value)}
                        className={selectCls}
                    >
                        <option value="">Toate rezultatele</option>
                        <option value="SUCCES">Succes</option>
                        <option value="ESEC">Eșec</option>
                        <option value="ATENTIE">Atenție</option>
                    </select>
                </div>

                <div className="flex flex-col sm:flex-row sm:flex-wrap items-stretch sm:items-center gap-3">
                    <label className="flex items-center gap-2 text-sm text-mai-500 dark:text-mai-400 dark:text-mai-400">
                        De la
                        <input
                            type="date"
                            value={dateFrom}
                            onChange={(e) => setDateFrom(e.target.value)}
                            className={inputCls}
                        />
                    </label>

                    <label className="flex items-center gap-2 text-sm text-mai-500 dark:text-mai-400 dark:text-mai-400">
                        Până la
                        <input
                            type="date"
                            value={dateTo}
                            onChange={(e) => setDateTo(e.target.value)}
                            className={inputCls}
                        />
                    </label>

                    {hasFilters && (
                        <button
                            onClick={resetFilters}
                            className="inline-flex items-center gap-1 text-sm text-mai-400 hover:text-mai-600"
                        >
                            <X size={14} /> Resetează filtrele
                        </button>
                    )}

                    <p className="ml-auto flex items-center gap-1.5 self-center text-sm text-mai-400 dark:text-mai-400">
                        <ScrollText size={14} />
                        {totalCount.toLocaleString('ro-RO')} înregistrări
                    </p>
                </div>
            </div>

            {/* Tabel */}
            <div className="overflow-hidden rounded-xl border border-mai-100/50 dark:border-mai-700 bg-white dark:bg-mai-800 shadow-card dark:shadow-none">
                {loading ? (
                    <div className="flex items-center justify-center gap-3 py-14 text-mai-400 dark:text-mai-400">
                        <Loader2 size={20} className="animate-spin" />
                        <span className="text-sm">Se încarcă jurnalul…</span>
                    </div>
                ) : entries.length === 0 ? (
                    <div className="py-14 text-center">
                        <ScrollText size={36} className="mx-auto mb-3 text-mai-200" />
                        <p className="text-sm font-medium text-mai-400 dark:text-mai-400">
                            Nicio înregistrare pentru filtrele selectate.
                        </p>
                    </div>
                ) : (
                    <>
                        <div className="overflow-x-auto">
                            <table className="w-full text-sm">
                                <thead>
                                <tr className="bg-mai-50 dark:bg-mai-900 text-left text-xs uppercase tracking-wide text-mai-500 dark:text-mai-400 dark:text-mai-400">
                                    <th className="px-5 py-3 font-semibold">Timestamp</th>
                                    <th className="px-5 py-3 font-semibold">Utilizator</th>
                                    <th className="px-5 py-3 font-semibold">Acțiune</th>
                                    <th className="px-5 py-3 font-semibold">Detalii</th>
                                    <th className="px-5 py-3 font-semibold">Adresă IP</th>
                                    <th className="px-5 py-3 font-semibold">Rezultat</th>
                                </tr>
                                </thead>
                                <tbody className="divide-y divide-mai-50 dark:divide-mai-700">
                                {entries.map((e) => (
                                    <tr
                                        key={e.id}
                                        className={`transition-colors hover:bg-mai-100/60 dark:hover:bg-mai-700/40
                                            ${RESULT_ROW_CLASS[e.result] ?? ''}`}
                                    >
                                        <td className="whitespace-nowrap px-5 py-3 text-mai-500 dark:text-mai-400 dark:text-mai-400">
                                            {formatDateTime(e.timestamp)}
                                        </td>
                                        <td className="whitespace-nowrap px-5 py-3 font-medium text-mai-900 dark:text-mai-100 dark:text-white">
                                            {e.userName}
                                        </td>
                                        <td className="whitespace-nowrap px-5 py-3">
                                            {ACTION_LABELS[e.action as ActionKey] ?? e.action}
                                        </td>
                                        <td className="max-w-xs truncate px-5 py-3 text-mai-500 dark:text-mai-400 dark:text-mai-400">
                                            {e.target}
                                        </td>
                                        <td className="whitespace-nowrap px-5 py-3 font-mono text-xs text-mai-400 dark:text-mai-500 dark:text-mai-400">
                                            {e.ipAddress}
                                        </td>
                                        <td className="px-5 py-3">
                                            <Badge tone={RESULT_TONE[e.result] ?? 'gray'}>
                                                {e.result !== 'SUCCES' && (
                                                    <AlertCircle size={11} className="mr-1" />
                                                )}
                                                {e.result}
                                            </Badge>
                                        </td>
                                    </tr>
                                ))}
                                </tbody>
                            </table>
                        </div>

                        <Pagination
                            page={page}
                            pageSize={pageSize}
                            totalCount={totalCount}
                            totalPages={totalPages}
                            onPageChange={setPage}
                            onPageSizeChange={setPageSize}
                            itemLabel="înregistrări"
                        />
                    </>
                )}
            </div>
        </div>
    );
}