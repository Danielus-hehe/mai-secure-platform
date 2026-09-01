import { ArrowLeftRight, Landmark, Users, ShieldAlert, Clock, FileCheck } from 'lucide-react';
import PageHeader from '../../components/ui/PageHeader';
import StatCard from '../../components/ui/StatCard';
import Badge from '../../components/ui/Badge';
import { MOCK_TRANSFERS } from '../../utils/mockData';
import { useAuth } from '../../context/AuthContext';
import { formatDateTime, formatFileSize } from '../../utils/format';

export default function DashboardPage() {
    const { user } = useAuth();

    return (
        <div className="space-y-6">
            <PageHeader
                title={`Bun venit, ${user?.fullName ?? ''}`}
                subtitle={`${user?.department ?? ''} · Prezentare generală a activității`}
            />

            <div className="grid grid-cols-1 sm:grid-cols-2 xl:grid-cols-4 gap-4">
                <StatCard label="Transferuri active" value={2} icon={ArrowLeftRight} tone="blue" />
                <StatCard label="Documente normative" value={3} icon={Landmark} tone="gold" />
                <StatCard label="Utilizatori activi" value={3} icon={Users} tone="green" />
                <StatCard label="Autentificări eșuate (24h)" value={1} icon={ShieldAlert} tone="red" />
            </div>

            {/* Ultimele transferuri */}
            <div className="bg-white rounded-xl shadow-card border border-mai-100/50 overflow-hidden">
                <div className="flex items-center justify-between px-5 py-4 border-b border-mai-100">
                    <h2 className="font-semibold text-mai-900 flex items-center gap-2">
                        <Clock size={16} className="text-mai-400" /> Ultimele transferuri
                    </h2>
                </div>
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
                    {MOCK_TRANSFERS.map((f) => (
                        <tr key={f.id} className="hover:bg-mai-50/50">
                            <td className="px-5 py-3.5 font-medium text-mai-900">{f.fileName}</td>
                            <td className="px-5 py-3.5 text-mai-500">
                                {f.sender.fullName} → {f.recipient.fullName}
                            </td>
                            <td className="px-5 py-3.5 text-mai-500">{formatFileSize(f.sizeBytes)}</td>
                            <td className="px-5 py-3.5 text-mai-500">{formatDateTime(f.createdAt)}</td>
                            <td className="px-5 py-3.5">
                                <Badge tone={f.status === 'CONFIRMAT' ? 'green' : 'gold'}>
                                    {f.status === 'CONFIRMAT' ? (
                                        <><FileCheck size={11} className="mr-1" /> Confirmat</>
                                    ) : 'În așteptare'}
                                </Badge>
                            </td>
                        </tr>
                    ))}
                    </tbody>
                </table>
            </div>
        </div>
    );
}