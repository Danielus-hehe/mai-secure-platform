import { useState, useEffect, useCallback } from 'react';
import { ArrowLeftRight, Landmark, Users, ShieldAlert, Clock, FileCheck, Loader2 } from 'lucide-react';
import PageHeader from '../../components/ui/PageHeader';
import StatCard   from '../../components/ui/StatCard';
import Badge      from '../../components/ui/Badge';
import { useAuth }  from '../../context/AuthContext';
import { formatDateTime, formatFileSize } from '../../utils/format';
import api from '../../api/client';

// Backend TransferStatus.ToString(): "Pending" | "Downloaded" | "Expired"
const STATUS_LABEL: Record<string, string> = {
    Pending:    'În așteptare',
    Downloaded: 'Confirmat',
    Expired:    'Expirat',
};
const STATUS_TONE: Record<string, 'green' | 'gold' | 'red'> = {
    Pending:    'gold',
    Downloaded: 'green',
    Expired:    'red',
};

interface RecentTransfer {
    id: string;
    fileName: string;
    fileSize: number;
    senderName: string;
    recipientName: string;
    status: string;
    createdAt: string;
}

interface Stats {
    activeUsers:         number;
    totalTransfers:      number;
    pendingTransfers:    number;
    totalDocuments:      number;
    failedLoginsLast24h: number;
    recentTransfers:     RecentTransfer[];
}

export default function DashboardPage() {
    const { user } = useAuth();
    const [stats,   setStats]   = useState<Stats | null>(null);
    const [loading, setLoading] = useState(true);

    const fetchStats = useCallback(async () => {
        setLoading(true);
        try {
            // Prin clientul `api`: tokenul se ataseaza si se reimprospateaza
            // automat. Varianta veche citea user.token, care nu mai exista pe
            // obiectul User, deci trimitea mereu "Bearer undefined".
            const { data } = await api.get<Stats>('/Stats');
            setStats(data);
        } catch {
            // Dacă API-ul nu e disponibil, afișăm zerouri
            setStats({
                activeUsers: 0, totalTransfers: 0, pendingTransfers: 0,
                totalDocuments: 0, failedLoginsLast24h: 0, recentTransfers: [],
            });
        } finally {
            setLoading(false);
        }
    }, []);

    useEffect(() => { fetchStats(); }, [fetchStats]);

    return (
        <div className="space-y-6">
            <PageHeader
                title={`Bun venit, ${user?.fullName ?? user?.username ?? ''}`}
                subtitle={`${user?.department ?? ''} · Prezentare generală a activității`}
            />

            {/* Stat cards */}
            {loading ? (
                <div className="flex items-center gap-3 text-mai-400 py-4">
                    <Loader2 size={18} className="animate-spin" />
                    <span className="text-sm">Se încarcă statisticile…</span>
                </div>
            ) : (
                <div className="grid grid-cols-1 sm:grid-cols-2 xl:grid-cols-4 gap-4">
                    <StatCard
                        label="Transferuri în așteptare"
                        value={stats?.pendingTransfers ?? 0}
                        icon={ArrowLeftRight}
                        tone="blue"
                    />
                    <StatCard
                        label="Documente normative"
                        value={stats?.totalDocuments ?? 0}
                        icon={Landmark}
                        tone="gold"
                    />
                    <StatCard
                        label="Utilizatori activi"
                        value={stats?.activeUsers ?? 0}
                        icon={Users}
                        tone="green"
                    />
                    <StatCard
                        label="Auth. eșuate (24h)"
                        value={stats?.failedLoginsLast24h ?? 0}
                        icon={ShieldAlert}
                        tone="red"
                    />
                </div>
            )}

            {/* Ultimele transferuri */}
            <div className="bg-white rounded-xl shadow-card border border-mai-100/50 overflow-hidden">
                <div className="flex items-center justify-between px-5 py-4 border-b border-mai-100">
                    <h2 className="font-semibold text-mai-900 flex items-center gap-2">
                        <Clock size={16} className="text-mai-400" /> Ultimele transferuri
                    </h2>
                    <span className="text-xs text-mai-400 bg-mai-50 px-2.5 py-1 rounded-full">
                        {stats?.totalTransfers ?? 0} total
                    </span>
                </div>

                {loading ? (
                    <div className="flex items-center justify-center gap-3 py-14 text-mai-400">
                        <Loader2 size={18} className="animate-spin" />
                        <span className="text-sm">Se încarcă transferurile…</span>
                    </div>
                ) : !stats?.recentTransfers.length ? (
                    <div className="py-14 text-center">
                        <ArrowLeftRight size={36} className="mx-auto text-mai-200 mb-3" />
                        <p className="text-sm font-medium text-mai-400">Niciun transfer înregistrat</p>
                        <p className="text-xs text-mai-300 mt-1">
                            Trimiteți primul fișier din secțiunea Transferuri
                        </p>
                    </div>
                ) : (
                    <div className="overflow-x-auto">
                        <table className="w-full text-sm">
                            <thead>
                            <tr className="bg-mai-50 text-left text-xs uppercase tracking-wide text-mai-500">
                                <th className="px-5 py-3 font-semibold">Fișier</th>
                                <th className="px-5 py-3 font-semibold">Expeditor → Destinatar</th>
                                <th className="px-5 py-3 font-semibold">Dimensiune</th>
                                <th className="px-5 py-3 font-semibold">Data</th>
                                <th className="px-5 py-3 font-semibold">Status</th>
                            </tr>
                            </thead>
                            <tbody className="divide-y divide-mai-50">
                            {stats.recentTransfers.map(t => (
                                <tr key={t.id} className="hover:bg-mai-100/60 transition-colors">
                                    <td className="px-5 py-3.5 font-medium text-mai-900 whitespace-nowrap">
                                        {t.fileName}
                                    </td>
                                    <td className="px-5 py-3.5 text-mai-500 whitespace-nowrap">
                                        {t.senderName} → {t.recipientName}
                                    </td>
                                    <td className="px-5 py-3.5 text-mai-500 whitespace-nowrap">
                                        {formatFileSize(t.fileSize)}
                                    </td>
                                    <td className="px-5 py-3.5 text-mai-500 whitespace-nowrap">
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