import { ArrowLeftRight, Landmark, Users, ShieldAlert, Clock, FileCheck } from 'lucide-react';
import PageHeader from '../../components/ui/PageHeader';
import StatCard from '../../components/ui/StatCard';
import Badge from '../../components/ui/Badge';
import { transferStore, documentStore, userStore, auditStore } from '../../api/mockStore';
import { useAuth } from '../../context/AuthContext';
import { formatDateTime, formatFileSize } from '../../utils/format';

export default function DashboardPage() {
    const { user } = useAuth();

    // Stats derivate din store — nu mai sunt hardcodate
    const transfers  = transferStore.getAll();
    const docs       = documentStore.getAll();
    const users      = userStore.getAll();
    const audit      = auditStore.getAll();

    const pending    = transfers.filter(t => t.status === 'IN_ASTEPTARE').length;
    const activeUsers = users.filter(u => u.isActive).length;
    const yesterday  = new Date(Date.now() - 24 * 3600_000);
    const failedLogins = audit.filter(a =>
        a.action === 'LOGIN' && a.result === 'ESEC' && new Date(a.timestamp) > yesterday
    ).length;

    const recent = transfers.slice(0, 5);

    return (
        <div className="space-y-6">
            <PageHeader
                title={`Bun venit, ${user?.fullName ?? ''}`}
                subtitle={`${user?.department ?? ''} · Prezentare generală a activității`}
            />

            {/* Stat cards */}
            <div className="grid grid-cols-1 sm:grid-cols-2 xl:grid-cols-4 gap-4">
                <StatCard label="Transferuri în așteptare" value={pending}       icon={ArrowLeftRight} tone="blue"  />
                <StatCard label="Documente normative"       value={docs.length}  icon={Landmark}       tone="gold"  />
                <StatCard label="Utilizatori activi"        value={activeUsers}  icon={Users}          tone="green" />
                <StatCard label="Auth. eșuate (24h)"        value={failedLogins} icon={ShieldAlert}    tone="red"   />
            </div>

            {/* Ultimele transferuri */}
            <div className="bg-white rounded-xl shadow-card border border-mai-100/50 overflow-hidden">
                <div className="flex items-center justify-between px-5 py-4 border-b border-mai-100">
                    <h2 className="font-semibold text-mai-900 flex items-center gap-2">
                        <Clock size={16} className="text-mai-400" /> Ultimele transferuri
                    </h2>
                    <span className="text-xs text-mai-400 bg-mai-50 px-2.5 py-1 rounded-full">
                        {transfers.length} total
                    </span>
                </div>

                {recent.length === 0 ? (
                    <div className="py-14 text-center">
                        <ArrowLeftRight size={36} className="mx-auto text-mai-200 mb-3" />
                        <p className="text-sm font-medium text-mai-400">Niciun transfer înregistrat</p>
                        <p className="text-xs text-mai-300 mt-1">Trimiteți primul fișier din secțiunea Transferuri</p>
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
                                {recent.map(f => (
                                    <tr key={f.id} className="hover:bg-mai-50/50 transition-colors">
                                        <td className="px-5 py-3.5 font-medium text-mai-900 whitespace-nowrap">
                                            {f.fileName}
                                        </td>
                                        <td className="px-5 py-3.5 text-mai-500 whitespace-nowrap">
                                            {f.sender.fullName} → {f.recipient.fullName}
                                        </td>
                                        <td className="px-5 py-3.5 text-mai-500 whitespace-nowrap">
                                            {formatFileSize(f.sizeBytes)}
                                        </td>
                                        <td className="px-5 py-3.5 text-mai-500 whitespace-nowrap">
                                            {formatDateTime(f.createdAt)}
                                        </td>
                                        <td className="px-5 py-3.5">
                                            <Badge tone={
                                                f.status === 'CONFIRMAT' ? 'green' :
                                                f.status === 'EXPIRAT'   ? 'red'   : 'gold'
                                            }>
                                                {f.status === 'CONFIRMAT' ? (
                                                    <><FileCheck size={11} className="mr-1" /> Confirmat</>
                                                ) : f.status === 'EXPIRAT' ? 'Expirat' : 'În așteptare'}
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
