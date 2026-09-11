import { useState, useEffect, useCallback } from 'react';
import {
    ArrowLeftRight, ArrowDownLeft, ArrowUpRight, Landmark, Users, ShieldAlert,
    Clock, FileCheck, Inbox, Loader2,
} from 'lucide-react';
import PageHeader from '../../components/ui/PageHeader';
import StatCard   from '../../components/ui/StatCard';
import Badge      from '../../components/ui/Badge';
import { useAuth }  from '../../context/AuthContext';
import { formatDateTime, formatFileSize } from '../../utils/format';
import { apiErrorMessage } from '../../api/errors';
import { fetchDashboardStats, type DashboardStats, type RecentTransfer } from '../../api/stats';

// Backend: TransferStatus.ToString(), plus „Expired” calculat pentru transferurile
// în așteptare trecute de termen.
const STATUS_LABEL: Record<string, string> = {
    Pending:    'În așteptare',
    Downloaded: 'Confirmat',
    Expired:    'Expirat',
    Revoked:    'Retras',
};
const STATUS_TONE: Record<string, 'green' | 'gold' | 'red' | 'gray'> = {
    Pending:    'gold',
    Downloaded: 'green',
    Expired:    'red',
    Revoked:    'gray',
};

/** Cealaltă parte a transferului, din perspectiva utilizatorului curent. */
function Counterpart({ transfer }: { transfer: RecentTransfer }) {
    const sent = transfer.direction === 'sent';
    const Icon = sent ? ArrowUpRight : ArrowDownLeft;

    return (
        <span className="inline-flex items-center gap-1.5">
            <Icon
                size={14}
                className={sent ? 'text-mai-400' : 'text-green-600 dark:text-green-400'}
                aria-hidden="true"
            />
            {sent ? `Către ${transfer.recipientName}` : `De la ${transfer.senderName}`}
        </span>
    );
}

export default function DashboardPage() {
    const { user } = useAuth();
    const [stats,   setStats]   = useState<DashboardStats | null>(null);
    const [loading, setLoading] = useState(true);
    const [error,   setError]   = useState('');

    const loadStats = useCallback(async (signal?: AbortSignal) => {
        setLoading(true);
        setError('');
        try {
            setStats(await fetchDashboardStats(signal));
        } catch (err) {
            if (signal?.aborted) return;
            // Fără cifre inventate: un 0 afișat după o eroare arată ca o
            // informație reală („niciun transfer”).
            setStats(null);
            setError(apiErrorMessage(err, 'Statisticile nu au putut fi încărcate.'));
        } finally {
            if (!signal?.aborted) setLoading(false);
        }
    }, []);

    useEffect(() => {
        const controller = new AbortController();
        void loadStats(controller.signal);
        return () => controller.abort();
    }, [loadStats]);

    // null = rolul nu are acces la metrica de securitate: cardul nu se afișează.
    const showFailedLogins = stats?.failedLoginsLast24h != null;

    return (
        <div className="space-y-4 sm:space-y-6">
            <PageHeader
                title={`Bun venit, ${user?.fullName ?? user?.username ?? ''}`}
                subtitle={`${user?.department ?? ''} · Prezentare generală a activității`}
            />

            {error && (
                <div className="flex flex-col gap-3 rounded-xl border border-red-100 dark:border-red-800
                    bg-red-50 dark:bg-red-900/20 px-4 py-3 sm:flex-row sm:items-center sm:justify-between">
                    <p className="text-sm text-red-700 dark:text-red-400">{error}</p>
                    <button
                        type="button"
                        onClick={() => void loadStats()}
                        className="text-sm font-semibold text-red-700 underline-offset-2 hover:underline dark:text-red-300"
                    >
                        Reîncearcă
                    </button>
                </div>
            )}

            {/* Stat cards */}
            {loading ? (
                <div className="flex items-center gap-3 text-mai-400 py-4">
                    <Loader2 size={18} className="animate-spin" />
                    <span className="text-sm">Se încarcă statisticile…</span>
                </div>
            ) : stats && (
                <div className={`grid grid-cols-1 sm:grid-cols-2 gap-4 ${showFailedLogins ? 'xl:grid-cols-4' : 'xl:grid-cols-3'}`}>
                    <StatCard
                        label="Fișiere care vă așteaptă"
                        value={stats.awaitingMyDownload}
                        icon={Inbox}
                        tone="blue"
                    />
                    <StatCard
                        label="Documente normative"
                        value={stats.totalDocuments}
                        icon={Landmark}
                        tone="gold"
                    />
                    <StatCard
                        label="Utilizatori activi"
                        value={stats.activeUsers}
                        icon={Users}
                        tone="green"
                    />
                    {showFailedLogins && (
                        <StatCard
                            label="Auth. eșuate (24h)"
                            value={stats.failedLoginsLast24h ?? 0}
                            icon={ShieldAlert}
                            tone="red"
                        />
                    )}
                </div>
            )}

            {/* Ultimele transferuri ale utilizatorului */}
            <div className="bg-white dark:bg-mai-800 rounded-xl shadow-card dark:shadow-none
                border border-mai-100/50 dark:border-mai-700 overflow-hidden">
                <div className="flex items-center justify-between px-5 py-4 border-b border-mai-100 dark:border-mai-700">
                    <h2 className="font-semibold text-mai-900 dark:text-white flex items-center gap-2">
                        <Clock size={16} className="text-mai-400" /> Ultimele dumneavoastră transferuri
                    </h2>
                    {stats && (
                        <span className="text-xs text-mai-400 bg-mai-50 dark:bg-mai-700 px-2.5 py-1 rounded-full">
                            {stats.myTransfersTotal} în total
                        </span>
                    )}
                </div>

                {loading ? (
                    <div className="flex items-center justify-center gap-3 py-14 text-mai-400">
                        <Loader2 size={18} className="animate-spin" />
                        <span className="text-sm">Se încarcă transferurile…</span>
                    </div>
                ) : !stats?.recentTransfers.length ? (
                    <div className="py-14 text-center">
                        <ArrowLeftRight size={36} className="mx-auto text-mai-200 dark:text-mai-600 mb-3" />
                        <p className="text-sm font-medium text-mai-400">Niciun transfer încă</p>
                        <p className="text-xs text-mai-300 dark:text-mai-500 mt-1">
                            Trimiteți primul fișier din secțiunea Transferuri
                        </p>
                    </div>
                ) : (
                    <div className="overflow-x-auto">
                        <table className="w-full text-sm">
                            <thead>
                            <tr className="bg-mai-50 dark:bg-mai-900 text-left text-xs uppercase tracking-wide text-mai-500 dark:text-mai-400">
                                <th className="px-5 py-3 font-semibold">Fișier</th>
                                <th className="px-5 py-3 font-semibold">Trimis / primit</th>
                                <th className="px-5 py-3 font-semibold">Dimensiune</th>
                                <th className="px-5 py-3 font-semibold">Data</th>
                                <th className="px-5 py-3 font-semibold">Status</th>
                            </tr>
                            </thead>
                            <tbody className="divide-y divide-mai-50 dark:divide-mai-700">
                            {stats.recentTransfers.map(t => (
                                <tr key={t.id} className="hover:bg-mai-100/60 dark:hover:bg-mai-700/40 transition-colors">
                                    <td className="px-5 py-3.5 font-medium text-mai-900 dark:text-mai-100 whitespace-nowrap">
                                        {t.fileName}
                                    </td>
                                    <td className="px-5 py-3.5 text-mai-500 dark:text-mai-300 whitespace-nowrap">
                                        <Counterpart transfer={t} />
                                    </td>
                                    <td className="px-5 py-3.5 text-mai-500 dark:text-mai-300 whitespace-nowrap">
                                        {formatFileSize(t.fileSize)}
                                    </td>
                                    <td className="px-5 py-3.5 text-mai-500 dark:text-mai-300 whitespace-nowrap">
                                        {formatDateTime(t.createdAt)}
                                    </td>
                                    <td className="px-5 py-3.5">
                                        <Badge tone={STATUS_TONE[t.status] ?? 'gold'}>
                                            {t.status === 'Downloaded' && (
                                                <FileCheck size={11} className="mr-1" />
                                            )}
                                            {STATUS_LABEL[t.status] ?? t.status}
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
