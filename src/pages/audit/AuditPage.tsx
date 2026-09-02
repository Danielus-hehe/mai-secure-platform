import { useMemo, useState } from 'react';
import { ScrollText, Download } from 'lucide-react';
import PageHeader from '../../components/ui/PageHeader';
import Badge from '../../components/ui/Badge';
import Button from '../../components/ui/Button';
import { auditStore } from '../../api/mockStore';
import { formatDateTime } from '../../utils/format';
import type { AuditEntry } from '../../types';

const ACTION_LABELS: Record<AuditEntry['action'], string> = {
    LOGIN: 'Autentificare', LOGOUT: 'Deconectare', UPLOAD: 'Încărcare',
    DOWNLOAD: 'Descărcare', TRANSFER: 'Transfer', MODIFICARE_DOC: 'Modificare doc',
    ADMIN: 'Administrare',
};

export default function AuditPage() {
    const [entries] = useState(() => auditStore.getAll());
    const [userFilter, setUserFilter] = useState('');
    const [actionFilter, setActionFilter] = useState('');
    const [resultFilter, setResultFilter] = useState('');

    const filtered = useMemo(() => entries
            .filter((e) => !userFilter || e.userName === userFilter)
            .filter((e) => !actionFilter || e.action === actionFilter)
            .filter((e) => !resultFilter || e.result === resultFilter),
        [entries, userFilter, actionFilter, resultFilter]);

    const userNames = [...new Set(entries.map((e) => e.userName))];

    const selectCls = 'rounded-lg border border-mai-200 bg-white px-3 py-2 text-sm\n' +
        '    focus:outline-none focus:ring-2 focus:ring-mai-500';

    return (
        <div className="space-y-6">
            <PageHeader title="Jurnal de audit"
                        subtitle="Toate acțiunile înregistrate: utilizator, acțiune, timestamp, IP, rezultat"
                        actions={<Button variant="secondary"><Download size={15} /> Export PDF</Button>} />

            <div className="flex flex-wrap gap-3">
                <select value={userFilter} onChange={(e) => setUserFilter(e.target.value)} className={selectCls}>
                    <option value="">Toți utilizatorii</option>
                    {userNames.map((n) => <option key={n}>{n}</option>)}
                </select>
                <select value={actionFilter} onChange={(e) => setActionFilter(e.target.value)} className={selectCls}>
                    <option value="">Toate acțiunile</option>
                    {Object.entries(ACTION_LABELS).map(([v, l]) => <option key={v} value={v}>{l}</option>)}
                </select>
                <select value={resultFilter} onChange={(e) => setResultFilter(e.target.value)} className={selectCls}>
                    <option value="">Toate rezultatele</option>
                    <option value="SUCCES">Succes</option>
                    <option value="ESEC">Eșec</option>
                </select>
                <p className="ml-auto text-sm text-mai-400 self-center">
                    <ScrollText size={14} className="inline mr-1" />
                    {filtered.length} înregistrări
                </p>
            </div>

            <div className="bg-white rounded-xl shadow-card border border-mai-100/50 overflow-hidden">
                <table className="w-full text-sm">
                    <thead>
                    <tr className="bg-mai-50 text-left text-xs uppercase tracking-wide text-mai-500">
                        <th className="px-5 py-3 font-semibold">Timestamp</th>
                        <th className="px-5 py-3 font-semibold">Utilizator</th>
                        <th className="px-5 py-3 font-semibold">Acțiune</th>
                        <th className="px-5 py-3 font-semibold">Țintă</th>
                        <th className="px-5 py-3 font-semibold">Adresă IP</th>
                        <th className="px-5 py-3 font-semibold">Rezultat</th>
                    </tr>
                    </thead>
                    <tbody className="divide-y divide-mai-50">
                    {filtered.map((e) => (
                        <tr key={e.id} className={`hover:bg-mai-50/50 ${e.result === 'ESEC' ? 'bg-red-50/50' : ''}`}>
                            <td className="px-5 py-3 text-mai-500 whitespace-nowrap">{formatDateTime(e.timestamp)}</td>
                            <td className="px-5 py-3 font-medium text-mai-900">{e.userName}</td>
                            <td className="px-5 py-3">{ACTION_LABELS[e.action]}</td>
                            <td className="px-5 py-3 text-mai-500">{e.target}</td>
                            <td className="px-5 py-3 font-mono text-xs text-mai-400">{e.ipAddress}</td>
                            <td className="px-5 py-3">
                                <Badge tone={e.result === 'SUCCES' ? 'green' : 'red'}>{e.result}</Badge>
                            </td>
                        </tr>
                    ))}
                    </tbody>
                </table>
            </div>
        </div>
    );
}