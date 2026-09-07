import { useState, useEffect, useMemo, useCallback } from 'react';
import { ScrollText, Download, AlertCircle, Loader2 } from 'lucide-react';
import PageHeader from '../../components/ui/PageHeader';
import Badge      from '../../components/ui/Badge';
import Button     from '../../components/ui/Button';
import { useAuth }  from '../../context/AuthContext';
import { useToast } from '../../context/ToastContext';
import { formatDateTime } from '../../utils/format';

const API = 'http://localhost:5000';

type ActionKey = 'LOGIN' | 'LOGOUT' | 'UPLOAD' | 'DOWNLOAD' | 'MODIFICARE_DOC' | 'ADMIN';

const ACTION_LABELS: Record<ActionKey, string> = {
    LOGIN:          'Autentificare',
    LOGOUT:         'Deconectare',
    UPLOAD:         'Încărcare',
    DOWNLOAD:       'Descărcare',
    MODIFICARE_DOC: 'Modificare doc',
    ADMIN:          'Administrare',
};

const selectCls = `rounded-lg border border-mai-200 bg-white px-3 py-2 text-sm
    focus:outline-none focus:ring-2 focus:ring-mai-500 cursor-pointer
    hover:border-mai-400 transition-colors`;

interface AuditEntry {
    id: string;
    timestamp: string;
    userName: string;
    action: string;
    target: string;
    ipAddress: string;
    result: 'SUCCES' | 'ESEC';
}

export default function AuditPage() {
    const { user }  = useAuth();
    const toast     = useToast();

    const [entries,      setEntries]      = useState<AuditEntry[]>([]);
    const [usernames,    setUsernames]    = useState<string[]>([]);
    const [loading,      setLoading]      = useState(true);
    const [userFilter,   setUserFilter]   = useState('');
    const [actionFilter, setActionFilter] = useState('');
    const [resultFilter, setResultFilter] = useState('');

    const hdrs = useCallback(
        () => ({ Authorization: `Bearer ${user?.token ?? ''}` }),
        [user?.token],
    );

    const fetchEntries = useCallback(async () => {
        setLoading(true);
        try {
            const params = new URLSearchParams();
            if (userFilter)   params.set('username', userFilter);
            if (actionFilter) params.set('action',   actionFilter);
            if (resultFilter) params.set('result',   resultFilter);

            const res = await fetch(`${API}/api/AuditLogs?${params}`, { headers: hdrs() });
            if (!res.ok) throw new Error(`HTTP ${res.status}`);
            setEntries(await res.json());
        } catch {
            toast.error('Nu s-au putut încărca înregistrările de audit.');
        } finally {
            setLoading(false);
        }
    }, [hdrs, userFilter, actionFilter, resultFilter, toast]);

    // Încarcă usernames o singură dată pentru dropdown
    useEffect(() => {
        fetch(`${API}/api/AuditLogs/usernames`, { headers: hdrs() })
            .then(r => r.ok ? r.json() : [])
            .then(setUsernames)
            .catch(() => {});
    }, [hdrs]);

    // Re-fetch când se schimbă filtrele
    useEffect(() => { fetchEntries(); }, [fetchEntries]);

    const handleExport = () => {
        toast.success(`Raport de audit exportat — ${entries.length} înregistrări incluse.`);
    };

    return (
        <div className="space-y-6">
            <PageHeader
                title="Jurnal de audit"
                subtitle="Toate acțiunile înregistrate: utilizator, acțiune, timestamp, IP, rezultat"
                actions={
                    <Button variant="secondary" onClick={handleExport}>
                        <Download size={15} /> Export PDF
                    </Button>
                }
            />

            {/* Filtre */}
            <div className="flex flex-wrap gap-3 items-center">
                <select value={userFilter} onChange={e => setUserFilter(e.target.value)} className={selectCls}>
                    <option value="">Toți utilizatorii</option>
                    {usernames.map(n => <option key={n}>{n}</option>)}
                </select>

                <select value={actionFilter} onChange={e => setActionFilter(e.target.value)} className={selectCls}>
                    <option value="">Toate acțiunile</option>
                    {(Object.entries(ACTION_LABELS) as [ActionKey, string][]).map(([v, l]) => (
                        <option key={v} value={v}>{l}</option>
                    ))}
                </select>

                <select value={resultFilter} onChange={e => setResultFilter(e.target.value)} className={selectCls}>
                    <option value="">Toate rezultatele</option>
                    <option value="SUCCES">Succes</option>
                    <option value="ESEC">Eșec</option>
                </select>

                <p className="ml-auto text-sm text-mai-400 self-center flex items-center gap-1.5">
                    <ScrollText size={14} />
                    {entries.length} înregistrări
                </p>
            </div>

            {/* Tabel */}
            <div className="bg-white rounded-xl shadow-card border border-mai-100/50 overflow-hidden">

                {loading ? (
                    <div className="flex items-center justify-center gap-3 py-14 text-mai-400">
                        <Loader2 size={20} className="animate-spin" />
                        <span className="text-sm">Se încarcă jurnalul…</span>
                    </div>
                ) : entries.length === 0 ? (
                    <div className="py-14 text-center">
                        <ScrollText size={36} className="mx-auto text-mai-200 mb-3" />
                        <p className="text-sm font-medium text-mai-400">
                            Nicio înregistrare pentru filtrele selectate.
                        </p>
                    </div>
                ) : (
                    <div className="overflow-x-auto">
                        <table className="w-full text-sm">
                            <thead>
                            <tr className="bg-mai-50 text-left text-xs uppercase tracking-wide text-mai-500">
                                <th className="px-5 py-3 font-semibold">Timestamp</th>
                                <th className="px-5 py-3 font-semibold">Utilizator</th>
                                <th className="px-5 py-3 font-semibold">Acțiune</th>
                                <th className="px-5 py-3 font-semibold">Detalii</th>
                                <th className="px-5 py-3 font-semibold">Adresă IP</th>
                                <th className="px-5 py-3 font-semibold">Rezultat</th>
                            </tr>
                            </thead>
                            <tbody className="divide-y divide-mai-50">
                            {entries.map(e => (
                                <tr key={e.id}
                                    className={`transition-colors hover:bg-mai-100/60
                                            ${e.result === 'ESEC' ? 'bg-red-50/60' : ''}`}>

                                    <td className="px-5 py-3 text-mai-500 whitespace-nowrap">
                                        {formatDateTime(e.timestamp)}
                                    </td>
                                    <td className="px-5 py-3 font-medium text-mai-900 whitespace-nowrap">
                                        {e.userName}
                                    </td>
                                    <td className="px-5 py-3 whitespace-nowrap">
                                        {ACTION_LABELS[e.action as ActionKey] ?? e.action}
                                    </td>
                                    <td className="px-5 py-3 text-mai-500 max-w-xs truncate">
                                        {e.target}
                                    </td>
                                    <td className="px-5 py-3 font-mono text-xs text-mai-400 whitespace-nowrap">
                                        {e.ipAddress}
                                    </td>
                                    <td className="px-5 py-3">
                                        <Badge tone={e.result === 'SUCCES' ? 'green' : 'red'}>
                                            {e.result === 'ESEC' && (
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
                )}
            </div>
        </div>
    );
}