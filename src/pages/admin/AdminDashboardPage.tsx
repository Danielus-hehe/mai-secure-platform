import { BarChart, Bar, XAxis, YAxis, Tooltip, ResponsiveContainer,
    CartesianGrid, PieChart, Pie, Cell, Legend } from 'recharts';
import PageHeader from '../../components/ui/PageHeader';
import StatCard from '../../components/ui/StatCard';
import { ArrowLeftRight, ShieldAlert, Users, HardDrive } from 'lucide-react';
import { auditStore, transferStore } from '../../api/mockStore';

const ACTION_COLORS = ['#143461', '#2a5a99', '#4d7fbd', '#d4a935', '#b3891f', '#7fa8d6', '#7f1d1d'];

export default function AdminDashboardPage() {
    const audit = auditStore.getAll();
    const transfers = transferStore.getAll();

    // transferuri pe zi (ultimele 7 zile)
    const byDay = Array.from({ length: 7 }, (_, i) => {
        const d = new Date();
        d.setDate(d.getDate() - (6 - i));
        const key = d.toISOString().slice(0, 10);
        return {
            ziua: d.toLocaleDateString('ro-RO', { weekday: 'short' }),
            transferuri: transfers.filter((t) => t.createdAt.slice(0, 10) === key).length,
            autentificari: audit.filter((a) => a.action === 'LOGIN' && a.timestamp.slice(0, 10) === key).length,
        };
    });

    // distribuția acțiunilor
    const byAction = Object.entries(
        audit.reduce<Record<string, number>>((acc, e) => {
            acc[e.action] = (acc[e.action] ?? 0) + 1; return acc;
        }, {})
    ).map(([name, value]) => ({ name, value }));

    const failedLogins = audit.filter((a) => a.action === 'LOGIN' && a.result === 'ESEC').length;

    return (
        <div className="space-y-6">
            <PageHeader title="Administrare & rapoarte"
                        subtitle="Statistici de sistem pentru direcția IT" />

            <div className="grid grid-cols-1 sm:grid-cols-2 xl:grid-cols-4 gap-4">
                <StatCard label="Total transferuri" value={transfers.length} icon={ArrowLeftRight} />
                <StatCard label="Înregistrări audit" value={audit.length} icon={HardDrive} tone="gold" />
                <StatCard label="Autentificări eșuate" value={failedLogins} icon={ShieldAlert} tone="red" />
                <StatCard label="Conturi în sistem" value={6} icon={Users} tone="green" />
            </div>

            <div className="grid grid-cols-1 lg:grid-cols-5 gap-4">
                <div className="lg:col-span-3 bg-white rounded-xl shadow-card border border-mai-100/50 p-5">
                    <h3 className="font-semibold text-mai-900 mb-4">Activitate — ultimele 7 zile</h3>
                    <ResponsiveContainer width="100%" height={280}>
                        <BarChart data={byDay}>
                            <CartesianGrid strokeDasharray="3 3" stroke="#d9e6f5" />
                            <XAxis dataKey="ziua" tick={{ fontSize: 12 }} />
                            <YAxis allowDecimals={false} tick={{ fontSize: 12 }} />
                            <Tooltip />
                            <Legend />
                            <Bar dataKey="transferuri" name="Transferuri" fill="#143461" radius={[4, 4, 0, 0]} />
                            <Bar dataKey="autentificari" name="Autentificări" fill="#d4a935" radius={[4, 4, 0, 0]} />
                        </BarChart>
                    </ResponsiveContainer>
                </div>

                <div className="lg:col-span-2 bg-white rounded-xl shadow-card border border-mai-100/50 p-5">
                    <h3 className="font-semibold text-mai-900 mb-4">Distribuția acțiunilor (audit)</h3>
                    <ResponsiveContainer width="100%" height={280}>
                        <PieChart>
                            <Pie data={byAction} dataKey="value" nameKey="name" innerRadius={55} outerRadius={90} paddingAngle={3}>
                                {byAction.map((_, i) => <Cell key={i} fill={ACTION_COLORS[i % ACTION_COLORS.length]} />)}
                            </Pie>
                            <Tooltip />
                            <Legend wrapperStyle={{ fontSize: 11 }} />
                        </PieChart>
                    </ResponsiveContainer>
                </div>
            </div>
        </div>
    );
}