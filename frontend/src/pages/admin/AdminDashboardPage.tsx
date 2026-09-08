import { useCallback, useEffect, useState } from 'react';
import {
    Users, ShieldAlert, FileText, HardDrive, Lock, KeyRound,
    UserPlus, Eye, EyeOff, RefreshCw, Timer, AlertTriangle, CheckCircle2,
} from 'lucide-react';
import {
    ResponsiveContainer,
    AreaChart, Area,
    BarChart, Bar,
    PieChart, Pie, Cell,
    XAxis, YAxis, CartesianGrid, Tooltip, Legend,
} from 'recharts';

import PageHeader from '../../components/ui/PageHeader';
import Button     from '../../components/ui/Button';
import Modal      from '../../components/ui/Modal';
import Input      from '../../components/ui/Input';
import { useToast } from '../../context/ToastContext';
import { ROLE_LABELS } from '../../utils/constants';
import { formatFileSize, formatDateTime } from '../../utils/format';
import type { Role } from '../../types';
import api from '../../api/client';
import { apiErrorMessage } from '../../api/errors';
import { fetchAdminStats, runExpirationJob, type AdminStats } from '../../api/stats';
import axios from 'axios';

// UserRole enum backend: Utilizator=1, SefDirectie=2, Administrator=3
const ROLE_TO_NUM: Record<Role, number> = {
    UTILIZATOR:    1,
    SEF_DIRECTIE:  2,
    ADMINISTRATOR: 3,
};

const CHART_COLORS = ['#2a5a99', '#d4a935', '#4d7fbd', '#b3891f', '#7fa8d6'];

interface CreateForm {
    fullName: string; username: string; password: string;
    email: string; department: string; role: Role;
}
const EMPTY: CreateForm = {
    fullName: '', username: '', password: '',
    email: '', department: '', role: 'UTILIZATOR',
};

/** Card de metrica. `hint` apare sub valoare si explica de ce conteaza cifra. */
function Metric({
                    label, value, hint, icon: Icon, tone = 'blue',
                }: {
    label: string;
    value: string | number;
    hint?: string;
    icon: typeof Users;
    tone?: 'blue' | 'gold' | 'red' | 'green';
}) {
    const tones = {
        blue:  'bg-mai-50 text-mai-600',
        gold:  'bg-gold-500/10 text-gold-600',
        red:   'bg-red-50 text-red-600',
        green: 'bg-emerald-50 text-emerald-600',
    };

    return (
        <div className="bg-white p-5 rounded-2xl shadow-card border border-mai-100 flex items-start justify-between gap-3">
            <div className="min-w-0">
                <p className="text-xs text-mai-500 font-medium">{label}</p>
                <p className="text-2xl font-bold text-mai-900 mt-1 leading-none">{value}</p>
                {hint && <p className="text-[11px] text-mai-400 mt-2 leading-snug">{hint}</p>}
            </div>
            <div className={`p-3 rounded-xl shrink-0 ${tones[tone]}`}>
                <Icon size={22} />
            </div>
        </div>
    );
}

function Panel({ title, subtitle, children, actions }: {
    title: string;
    subtitle?: string;
    children: React.ReactNode;
    actions?: React.ReactNode;
}) {
    return (
        <div className="bg-white p-6 rounded-2xl shadow-card border border-mai-100 space-y-4">
            <div className="flex items-start justify-between gap-4">
                <div>
                    <h2 className="text-lg font-bold text-mai-900">{title}</h2>
                    {subtitle && <p className="text-xs text-mai-500 mt-0.5">{subtitle}</p>}
                </div>
                {actions}
            </div>
            {children}
        </div>
    );
}

export default function AdminDashboardPage() {
    const toast = useToast();

    // ── Statistici ───────────────────────────────────────────────────────────
    const [stats, setStats]       = useState<AdminStats | null>(null);
    const [loadingStats, setLoadingStats] = useState(true);
    const [statsError, setStatsError]     = useState<string | null>(null);
    const [runningJob, setRunningJob]     = useState(false);

    // ── Creare cont ──────────────────────────────────────────────────────────
    const [open, setOpen]         = useState(false);
    const [creating, setCreating] = useState(false);
    const [showPass, setShowPass] = useState(false);
    const [form, setForm]         = useState<CreateForm>(EMPTY);

    const loadStats = useCallback(async (signal?: AbortSignal) => {
        setLoadingStats(true);
        setStatsError(null);
        try {
            const data = await fetchAdminStats(14, signal);
            setStats(data);
            setStatsError(null);
        } catch (e: unknown) {
            // O cerere anulata NU este o eroare.
            //
            // React ruleaza efectele de doua ori in StrictMode (dezvoltare): primul
            // efect porneste cererea, cleanup-ul o anuleaza, al doilea o reia. Cererea
            // anulata respinge cu CanceledError DUPA ce a doua a apelat deja
            // setStatsError(null) — asa aparea bannerul rosu peste date corecte.
            //
            // Aceeasi situatie apare in productie ori de cate ori utilizatorul apasa
            // "Reimprospateaza" de doua ori rapid sau paraseste pagina in timpul unei
            // cereri. Tratam anularea ca pe un non-eveniment.
            if (axios.isCancel(e) || (e as { name?: string })?.name === 'CanceledError' || signal?.aborted) {
                return;
            }

            // Nu inventam zerouri. Un panou de administrare care afiseaza 0 in loc
            // de "nu am putut citi" e mai periculos decat unul gol: administratorul
            // crede ca sistemul e inactiv, cand de fapt API-ul nu raspunde.
            setStatsError(apiErrorMessage(e, 'Statisticile nu au putut fi incarcate.'));
        } finally {
            // Nu stingem indicatorul de incarcare pentru o cerere care a fost
            // inlocuita de alta: altfel scheletul dispare inainte sa vina datele.
            if (!signal?.aborted) setLoadingStats(false);
        }
    }, []);

    useEffect(() => {
        const controller = new AbortController();
        loadStats(controller.signal);
        return () => controller.abort();
    }, [loadStats]);

    const handleRunJob = async () => {
        setRunningJob(true);
        try {
            const result = await runExpirationJob();
            if (result.failed > 0) toast.error(result.message);
            else toast.success(result.message);
            await loadStats();
        } catch (e: unknown) {
            toast.error(apiErrorMessage(e, 'Jobul de expirare nu a putut fi rulat.'));
        } finally {
            setRunningJob(false);
        }
    };

    const set = (f: keyof CreateForm) =>
        (e: React.ChangeEvent<HTMLInputElement | HTMLSelectElement>) =>
            setForm(prev => ({ ...prev, [f]: e.target.value }));

    const handleClose = () => { setOpen(false); setForm(EMPTY); setShowPass(false); };

    const handleCreate = async () => {
        if (!form.fullName || !form.username || !form.password) return;
        setCreating(true);
        try {
            await api.post('/Users', {
                fullName:   form.fullName,
                username:   form.username,
                password:   form.password,
                email:      form.email,
                department: form.department,
                role:       ROLE_TO_NUM[form.role],
            });

            toast.success(`Contul @${form.username} (${ROLE_LABELS[form.role]}) creat cu succes.`);
            handleClose();
            await loadStats();   // cifra de utilizatori se actualizeaza imediat
        } catch (e: unknown) {
            toast.error(apiErrorMessage(e, 'Contul nu a putut fi creat.'));
        } finally {
            setCreating(false);
        }
    };

    const isValid = !!form.fullName.trim() && !!form.username.trim() && !!form.password.trim();

    const job = stats?.expirationJob;
    const jobHealthy = !!job && job.enabled && job.status !== 'Eroare';

    const roleData = stats?.users.byRole.map(r => ({ name: r.label, value: r.count })) ?? [];

    const statusData = stats ? [
        { name: 'In asteptare', value: stats.transfers.pending },
        { name: 'Descarcate',   value: stats.transfers.downloaded },
        { name: 'Expirate',     value: stats.transfers.expired },
    ].filter(d => d.value > 0) : [];

    return (
        <div className="space-y-6">

            {/* Header */}
            <div>
                <PageHeader
                    title="Panou Administrare Sistem"
                    actions={
                        <div className="flex items-center gap-2">
                            <Button
                                variant="secondary"
                                className="flex items-center gap-2"
                                onClick={() => loadStats()}
                                disabled={loadingStats}
                            >
                                <RefreshCw size={16} className={loadingStats ? 'animate-spin' : ''} />
                                Reîmprospătează
                            </Button>
                            <Button className="flex items-center gap-2" onClick={() => setOpen(true)}>
                                <UserPlus size={16} /> Adaugă Utilizator
                            </Button>
                        </div>
                    }
                />
                <p className="text-sm text-mai-500 mt-1">
                    Cifre citite în timp real din baza de date și din depozitul de fișiere.
                    {stats && (
                        <span className="text-mai-400">
                            {' '}Ultima actualizare: {formatDateTime(stats.generatedAt)}.
                        </span>
                    )}
                </p>
            </div>

            {/* Eroare de încărcare */}
            {statsError && (
                <div className="flex items-start gap-3 p-4 rounded-2xl border border-red-200 bg-red-50">
                    <AlertTriangle size={18} className="text-red-600 mt-0.5 shrink-0" />
                    <div className="text-sm text-red-800">
                        <p className="font-semibold">Statisticile nu au putut fi încărcate.</p>
                        <p className="text-red-700 mt-0.5">{statsError}</p>
                    </div>
                </div>
            )}

            {/* Schelet la prima încărcare */}
            {loadingStats && !stats && (
                <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-4">
                    {Array.from({ length: 8 }).map((_, i) => (
                        <div key={i} className="h-[104px] bg-white rounded-2xl border border-mai-100 shadow-card animate-pulse" />
                    ))}
                </div>
            )}

            {stats && (
                <>
                    {/* ─── Metrici ─── */}
                    <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-4">
                        <Metric
                            label="Utilizatori activi"
                            value={stats.users.active}
                            hint={`${stats.users.total} conturi în total, ${stats.users.inactive} dezactivate`}
                            icon={Users}
                        />
                        <Metric
                            label="Transferuri securizate"
                            value={stats.transfers.total}
                            hint={`${stats.transfers.pending} în așteptare, ${stats.transfers.expired} expirate`}
                            icon={FileText}
                        />
                        <Metric
                            label="Cifrotext stocat"
                            value={formatFileSize(stats.storage.storedCiphertextBytes)}
                            hint={`${stats.storage.provider} · bucket ${stats.storage.bucket}`}
                            icon={HardDrive}
                        />
                        <Metric
                            label="Transferuri criptate E2E"
                            value={`${stats.transfers.encryptedPercent}%`}
                            hint={`${stats.transfers.encrypted} din ${stats.transfers.total} folosesc plicul criptografic`}
                            icon={ShieldAlert}
                            tone={stats.transfers.encryptedPercent === 100 ? 'green' : 'gold'}
                        />

                        <Metric
                            label="Autentificări eșuate (24h)"
                            value={stats.security.failedLoginsLast24h}
                            hint={`${stats.security.successfulLoginsLast24h} reușite în același interval`}
                            icon={ShieldAlert}
                            tone={stats.security.failedLoginsLast24h > 20 ? 'red' : 'blue'}
                        />
                        <Metric
                            label="Conturi blocate acum"
                            value={stats.users.locked}
                            hint="Blocare progresivă după încercări eșuate consecutive"
                            icon={Lock}
                            tone={stats.users.locked > 0 ? 'red' : 'blue'}
                        />
                        <Metric
                            label="Conturi fără chei"
                            value={stats.users.withoutKeys}
                            hint="Nu pot primi fișiere criptate până nu își generează cheile"
                            icon={KeyRound}
                            tone={stats.users.withoutKeys > 0 ? 'gold' : 'green'}
                        />
                        <Metric
                            label="Semnături invalide"
                            value={stats.security.invalidSignatures}
                            hint="Descărcări în care clientul a raportat semnătură nevalidă. Așteptat: 0"
                            icon={AlertTriangle}
                            tone={stats.security.invalidSignatures > 0 ? 'red' : 'green'}
                        />
                    </div>

                    {/* ─── Activitate în timp ─── */}
                    <Panel
                        title="Activitate pe transferuri"
                        subtitle={`Ultimele ${stats.windowDays} zile, din jurnalul de audit. Zilele fără activitate sunt incluse explicit, ca axa să nu comprime perioadele inactive.`}
                    >
                        <div className="h-72">
                            <ResponsiveContainer width="100%" height="100%">
                                <AreaChart data={stats.series} margin={{ top: 5, right: 10, left: -20, bottom: 0 }}>
                                    <defs>
                                        <linearGradient id="gUp" x1="0" y1="0" x2="0" y2="1">
                                            <stop offset="5%" stopColor="#2a5a99" stopOpacity={0.35} />
                                            <stop offset="95%" stopColor="#2a5a99" stopOpacity={0} />
                                        </linearGradient>
                                        <linearGradient id="gDown" x1="0" y1="0" x2="0" y2="1">
                                            <stop offset="5%" stopColor="#d4a935" stopOpacity={0.35} />
                                            <stop offset="95%" stopColor="#d4a935" stopOpacity={0} />
                                        </linearGradient>
                                    </defs>
                                    <CartesianGrid strokeDasharray="3 3" stroke="#d9e6f5" vertical={false} />
                                    <XAxis dataKey="label" tick={{ fontSize: 11, fill: '#4d7fbd' }} tickLine={false} axisLine={false} />
                                    <YAxis allowDecimals={false} tick={{ fontSize: 11, fill: '#4d7fbd' }} tickLine={false} axisLine={false} />
                                    <Tooltip
                                        contentStyle={{
                                            borderRadius: 12, border: '1px solid #d9e6f5',
                                            fontSize: 12, boxShadow: '0 1px 3px rgba(10,28,56,0.12)',
                                        }}
                                    />
                                    <Legend wrapperStyle={{ fontSize: 12 }} />
                                    <Area type="monotone" dataKey="uploads" name="Încărcări"
                                          stroke="#2a5a99" strokeWidth={2} fill="url(#gUp)" />
                                    <Area type="monotone" dataKey="downloads" name="Descărcări"
                                          stroke="#d4a935" strokeWidth={2} fill="url(#gDown)" />
                                    <Area type="monotone" dataKey="expirations" name="Expirări"
                                          stroke="#b3cde9" strokeWidth={2} fillOpacity={0} />
                                </AreaChart>
                            </ResponsiveContainer>
                        </div>
                    </Panel>

                    <div className="grid grid-cols-1 lg:grid-cols-2 gap-6">
                        {/* Autentificări eșuate */}
                        <Panel
                            title="Autentificări eșuate pe zi"
                            subtitle="Vârfurile arată încercări de forțare. Cu blocarea progresivă activă, o coloană înaltă înseamnă atac oprit, nu cont compromis."
                        >
                            <div className="h-56">
                                <ResponsiveContainer width="100%" height="100%">
                                    <BarChart data={stats.series} margin={{ top: 5, right: 10, left: -20, bottom: 0 }}>
                                        <CartesianGrid strokeDasharray="3 3" stroke="#d9e6f5" vertical={false} />
                                        <XAxis dataKey="label" tick={{ fontSize: 11, fill: '#4d7fbd' }} tickLine={false} axisLine={false} />
                                        <YAxis allowDecimals={false} tick={{ fontSize: 11, fill: '#4d7fbd' }} tickLine={false} axisLine={false} />
                                        <Tooltip
                                            contentStyle={{ borderRadius: 12, border: '1px solid #d9e6f5', fontSize: 12 }}
                                            formatter={(v) => [v, 'Eșecuri']}
                                        />
                                        <Bar dataKey="failedLogins" name="Eșecuri" fill="#b3891f" radius={[4, 4, 0, 0]} />
                                    </BarChart>
                                </ResponsiveContainer>
                            </div>
                        </Panel>

                        {/* Distribuții */}
                        <Panel title="Distribuții" subtitle="Roluri și starea transferurilor.">
                            <div className="grid grid-cols-2 gap-2">
                                <div className="h-56">
                                    <ResponsiveContainer width="100%" height="100%">
                                        <PieChart>
                                            <Pie data={roleData} dataKey="value" nameKey="name"
                                                 cx="50%" cy="45%" outerRadius={62} innerRadius={34} paddingAngle={2}>
                                                {roleData.map((_, i) => (
                                                    <Cell key={i} fill={CHART_COLORS[i % CHART_COLORS.length]} />
                                                ))}
                                            </Pie>
                                            <Tooltip contentStyle={{ borderRadius: 12, border: '1px solid #d9e6f5', fontSize: 12 }} />
                                            <Legend wrapperStyle={{ fontSize: 11 }} />
                                        </PieChart>
                                    </ResponsiveContainer>
                                </div>
                                <div className="h-56">
                                    <ResponsiveContainer width="100%" height="100%">
                                        <PieChart>
                                            <Pie data={statusData} dataKey="value" nameKey="name"
                                                 cx="50%" cy="45%" outerRadius={62} innerRadius={34} paddingAngle={2}>
                                                {statusData.map((_, i) => (
                                                    <Cell key={i} fill={CHART_COLORS[(i + 1) % CHART_COLORS.length]} />
                                                ))}
                                            </Pie>
                                            <Tooltip contentStyle={{ borderRadius: 12, border: '1px solid #d9e6f5', fontSize: 12 }} />
                                            <Legend wrapperStyle={{ fontSize: 11 }} />
                                        </PieChart>
                                    </ResponsiveContainer>
                                </div>
                            </div>
                        </Panel>
                    </div>

                    <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
                        {/* ─── Jobul de expirare ─── */}
                        <Panel
                            title="Retenție și expirare"
                            subtitle="Jobul care șterge efectiv cifrotextul transferurilor expirate."
                            actions={
                                <span className={`px-2.5 py-1 rounded-full text-xs font-medium shrink-0
                                    ${jobHealthy ? 'bg-emerald-50 text-emerald-700' : 'bg-red-50 text-red-700'}`}>
                                    {job?.status ?? '—'}
                                </span>
                            }
                        >
                            <div className="space-y-2.5 text-sm">
                                <div className="flex items-center justify-between">
                                    <span className="text-mai-500">Interval</span>
                                    <span className="font-medium text-mai-900">
                                        {job ? `${job.intervalMinutes} min` : '—'}
                                    </span>
                                </div>
                                <div className="flex items-center justify-between">
                                    <span className="text-mai-500">Ultima rulare</span>
                                    <span className="font-medium text-mai-900">
                                        {job?.lastRunAt ? formatDateTime(job.lastRunAt) : 'niciodată'}
                                    </span>
                                </div>
                                <div className="flex items-center justify-between">
                                    <span className="text-mai-500">Expirate în total</span>
                                    <span className="font-medium text-mai-900">{job?.expiredTotal ?? 0}</span>
                                </div>
                                <div className="flex items-center justify-between">
                                    <span className="text-mai-500">Obiecte purjate</span>
                                    <span className="font-medium text-mai-900">{job?.purgedTotal ?? 0}</span>
                                </div>
                                <div className="flex items-center justify-between">
                                    <span className="text-mai-500">În coadă acum</span>
                                    <span className={`font-medium ${stats.transfers.awaitingPurge > 0 ? 'text-gold-600' : 'text-mai-900'}`}>
                                        {stats.transfers.awaitingPurge}
                                    </span>
                                </div>
                                <div className="flex items-center justify-between">
                                    <span className="text-mai-500">Expiră în 24h</span>
                                    <span className="font-medium text-mai-900">{stats.transfers.expiringNext24h}</span>
                                </div>
                            </div>

                            {job?.lastError && (
                                <p className="text-xs text-red-700 bg-red-50 border border-red-200 rounded-lg p-2.5">
                                    {job.lastError}
                                </p>
                            )}

                            {!!job?.failedPurgeTotal && (
                                <p className="text-xs text-gold-600 bg-gold-500/10 rounded-lg p-2.5">
                                    {job.failedPurgeTotal} obiecte nu au putut fi șterse din depozit.
                                    Rămân în coadă și se reîncearcă automat.
                                </p>
                            )}

                            <Button
                                variant="secondary"
                                className="w-full flex items-center justify-center gap-2"
                                onClick={handleRunJob}
                                disabled={runningJob || !job?.enabled}
                            >
                                <Timer size={15} className={runningJob ? 'animate-spin' : ''} />
                                {runningJob ? 'Se execută…' : 'Rulează acum'}
                            </Button>
                        </Panel>

                        {/* ─── Module ─── */}
                        <Panel
                            title="Stare module"
                            subtitle="Latențe măsurate la fiecare încărcare a paginii, nu valori fixe."
                        >
                            <div className="divide-y divide-mai-100">
                                {stats.modules.map(m => (
                                    <div key={m.name} className="py-3 flex items-center justify-between gap-3">
                                        <span className="text-sm font-semibold text-mai-900">{m.name}</span>
                                        <div className="flex items-center gap-3 text-xs shrink-0">
                                            {m.latencyMs > 0 && (
                                                <span className="text-mai-400">{m.latencyMs} ms</span>
                                            )}
                                            <span className={`px-2.5 py-1 rounded-full font-medium flex items-center gap-1
                                                ${m.online ? 'bg-emerald-50 text-emerald-700' : 'bg-red-50 text-red-700'}`}>
                                                {m.online
                                                    ? <><CheckCircle2 size={12} /> Online</>
                                                    : <><AlertTriangle size={12} /> Indisponibil</>}
                                            </span>
                                        </div>
                                    </div>
                                ))}
                            </div>

                            <div className="pt-2 text-xs text-mai-500 space-y-1">
                                <p>Descărcare prin URL presemnat: {stats.storage.presignedDownload ? 'activată' : 'dezactivată'}</p>
                                <p>Limită fișier: {stats.storage.maxFileSizeMb} MB</p>
                                <p>Documente normative: {stats.documents.total} ({stats.documents.versions} versiuni)</p>
                            </div>
                        </Panel>

                        {/* ─── Top expeditori ─── */}
                        <Panel
                            title="Cei mai activi expeditori"
                            subtitle={`Ultimele ${stats.windowDays} zile, după numărul de transferuri.`}
                        >
                            {stats.topSenders.length === 0 ? (
                                <p className="text-sm text-mai-400 py-6 text-center">
                                    Niciun transfer în perioada selectată.
                                </p>
                            ) : (
                                <div className="space-y-3">
                                    {stats.topSenders.map((s, i) => {
                                        const max = stats.topSenders[0].count || 1;
                                        return (
                                            <div key={s.userId} className="space-y-1.5">
                                                <div className="flex items-center justify-between text-sm">
                                                    <span className="font-medium text-mai-900 truncate">{s.name}</span>
                                                    <span className="text-mai-500 text-xs shrink-0 ml-2">
                                                        {s.count} · {formatFileSize(s.bytes)}
                                                    </span>
                                                </div>
                                                <div className="h-1.5 bg-mai-50 rounded-full overflow-hidden">
                                                    <div
                                                        className="h-full rounded-full"
                                                        style={{
                                                            width: `${(s.count / max) * 100}%`,
                                                            backgroundColor: CHART_COLORS[i % CHART_COLORS.length],
                                                        }}
                                                    />
                                                </div>
                                            </div>
                                        );
                                    })}
                                </div>
                            )}
                        </Panel>
                    </div>
                </>
            )}

            {/* ─── Modal creare utilizator ─── */}
            <Modal open={open} title="Creare cont nou" onClose={handleClose}>
                <div className="space-y-4">

                    <Input id="fullName" label="Nume complet *"
                           value={form.fullName} onChange={set('fullName')}
                           placeholder="ex: Ion Popescu" required />

                    <Input id="username" label="Nume de utilizator *"
                           value={form.username} onChange={set('username')}
                           placeholder="ex: ion.popescu" required />

                    {/* Parolă cu toggle */}
                    <div className="relative">
                        <button type="button" tabIndex={-1}
                                onClick={() => setShowPass(v => !v)}
                                className="absolute right-3.5 top-[42px] text-mai-300 hover:text-mai-500 z-10">
                            {showPass ? <EyeOff size={16} /> : <Eye size={16} />}
                        </button>
                        <div className="pr-10">
                            <Input id="password" label="Parolă *"
                                   type={showPass ? 'text' : 'password'}
                                   value={form.password} onChange={set('password')}
                                   placeholder="Minim 12 caractere" required />
                        </div>
                    </div>

                    <Input id="email" label="Adresă e-mail" type="email"
                           value={form.email} onChange={set('email')}
                           placeholder="ex: ion.popescu@mai.gov.md" />

                    <Input id="department" label="Direcție / Departament"
                           value={form.department} onChange={set('department')}
                           placeholder="ex: Direcția IT" />

                    {/* Rol */}
                    <div>
                        <label className="block text-sm font-medium text-mai-800 mb-1.5">Rol</label>
                        <select value={form.role} onChange={set('role')}
                                className="w-full rounded-lg border border-mai-200 px-3.5 py-2.5 text-sm
                                focus:outline-none focus:ring-2 focus:ring-mai-500
                                hover:border-mai-300 transition-colors bg-white">
                            {(Object.entries(ROLE_LABELS) as [Role, string][]).map(([v, l]) => (
                                <option key={v} value={v}>{l}</option>
                            ))}
                        </select>
                        <p className="text-xs text-mai-400 mt-1.5">
                            {form.role === 'ADMINISTRATOR' && '⚠ Acces complet la sistem.'}
                            {form.role === 'SEF_DIRECTIE'  && 'Vizualizare audit + gestiune documente.'}
                            {form.role === 'UTILIZATOR'    && 'Transferuri securizate și documente normative.'}
                        </p>
                    </div>

                    <p className="text-xs text-mai-500 bg-mai-50 rounded-lg p-3">
                        Contul nou nu are chei criptografice. Utilizatorul le generează singur,
                        în browser, la prima autentificare. Până atunci nu i se pot trimite fișiere.
                    </p>

                    <Button onClick={handleCreate} disabled={!isValid || creating}
                            className="w-full mt-2 flex items-center justify-center gap-2">
                        <UserPlus size={15} />
                        {creating ? 'Se creează…' : 'Creează cont'}
                    </Button>
                    <p className="text-center text-xs text-mai-400">* Câmpuri obligatorii</p>
                </div>
            </Modal>

        </div>
    );
}