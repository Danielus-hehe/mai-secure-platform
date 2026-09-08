import { useState } from 'react';
import {
    Users, ShieldAlert, FileText, HardDrive,
    Key, UserPlus, Eye, EyeOff,
} from 'lucide-react';
import PageHeader from '../../components/ui/PageHeader';
import Button     from '../../components/ui/Button';
import Modal      from '../../components/ui/Modal';
import Input      from '../../components/ui/Input';
import { useToast } from '../../context/ToastContext';
import { ROLE_LABELS } from '../../utils/constants';
import type { Role } from '../../types';
import api from '../../api/client';
import { apiErrorMessage } from '../../api/errors';

// UserRole enum backend: Utilizator=1, SefDirectie=2, Administrator=3
const ROLE_TO_NUM: Record<Role, number> = {
    UTILIZATOR:    1,
    SEF_DIRECTIE:  2,
    ADMINISTRATOR: 3,
};

interface CreateForm {
    fullName: string; username: string; password: string;
    email: string; department: string; role: Role;
}
const EMPTY: CreateForm = {
    fullName: '', username: '', password: '',
    email: '', department: '', role: 'UTILIZATOR',
};

export default function AdminDashboardPage() {
    const toast = useToast();

    const [open, setOpen]       = useState(false);
    const [loading, setLoading] = useState(false);
    const [showPass, setShowPass] = useState(false);
    const [form, setForm]       = useState<CreateForm>(EMPTY);

    const set = (f: keyof CreateForm) =>
        (e: React.ChangeEvent<HTMLInputElement | HTMLSelectElement>) =>
            setForm(prev => ({ ...prev, [f]: e.target.value }));

    const handleClose = () => { setOpen(false); setForm(EMPTY); setShowPass(false); };

    const handleCreate = async () => {
        if (!form.fullName || !form.username || !form.password) return;
        setLoading(true);
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
        } catch (e: unknown) {
            toast.error(apiErrorMessage(e, 'Contul nu a putut fi creat.'));
        } finally {
            setLoading(false);
        }
    };

    const stats = [
        { title: 'Utilizatori Activi',     value: '42',     icon: Users      },
        { title: 'Transferuri Securizate',  value: '1,284',  icon: FileText   },
        { title: 'Stocare Criptată',        value: '45.8 GB', icon: HardDrive },
        { title: 'Integritate Sistem',      value: '100%',   icon: ShieldAlert },
    ];

    const isValid = !!form.fullName.trim() && !!form.username.trim() && !!form.password.trim();

    return (
        <div className="space-y-6">

            {/* Header */}
            <div>
                <PageHeader
                    title="Panou Administrare Sistem"
                    actions={
                        <Button className="flex items-center gap-2" onClick={() => setOpen(true)}>
                            <UserPlus size={16} /> Adaugă Utilizator
                        </Button>
                    }
                />
                <p className="text-sm text-mai-500 mt-1">
                    Gestiune utilizatori, configurare politici de securitate și monitorizare servere.
                </p>
            </div>

            {/* Metrici */}
            <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-4">
                {stats.map(s => {
                    const Icon = s.icon;
                    return (
                        <div key={s.title}
                             className="bg-white p-5 rounded-2xl shadow-card border border-mai-100 flex items-center justify-between">
                            <div>
                                <p className="text-xs text-mai-500 font-medium">{s.title}</p>
                                <p className="text-2xl font-bold text-mai-900 mt-1">{s.value}</p>
                            </div>
                            <div className="p-3 bg-mai-50 rounded-xl text-mai-600">
                                <Icon size={24} />
                            </div>
                        </div>
                    );
                })}
            </div>

            {/* Politici + Backend */}
            <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
                <div className="bg-white p-6 rounded-2xl shadow-card border border-mai-100 space-y-4">
                    <h2 className="text-lg font-bold text-mai-900 flex items-center gap-2">
                        <ShieldAlert size={20} className="text-gold-500" /> Politici Securitate
                    </h2>
                    <p className="text-xs text-mai-500">
                        Configurări globale ale criptării AES-256 și reguli de acces intranet.
                    </p>
                    <div className="space-y-3 pt-2">
                        <div className="flex items-center justify-between p-3 bg-mai-50 rounded-xl">
                            <span className="text-sm font-medium text-mai-900">Rotire chei master JWT</span>
                            <Button variant="secondary" className="text-xs py-1.5 px-3 flex items-center gap-1">
                                <Key size={14} /> Execută
                            </Button>
                        </div>
                        <div className="flex items-center justify-between p-3 bg-mai-50 rounded-xl">
                            <span className="text-sm font-medium text-mai-900">Forțează Deconectare</span>
                            <Button variant="danger" className="text-xs py-1.5 px-3">
                                Resetează Sesiuni
                            </Button>
                        </div>
                    </div>
                </div>

                <div className="lg:col-span-2 bg-white p-6 rounded-2xl shadow-card border border-mai-100 space-y-4">
                    <h2 className="text-lg font-bold text-mai-900">Stare Module Backend & Services</h2>
                    <div className="divide-y divide-mai-100">
                        {[
                            { name: 'API Gateway (.NET 8)',               latency: '12ms' },
                            { name: 'Bază de Date (Supabase / PostgreSQL)', latency: '24ms' },
                            { name: 'Serviciu Criptare AES-256-GCM',      latency: '5ms'  },
                            { name: 'Jurnal Audit & Trasabilitate',        latency: '18ms' },
                        ].map(m => (
                            <div key={m.name} className="py-3 flex items-center justify-between">
                                <span className="text-sm font-semibold text-mai-900">{m.name}</span>
                                <div className="flex items-center gap-4 text-xs">
                                    <span className="text-mai-400">Pings: {m.latency}</span>
                                    <span className="px-2.5 py-1 rounded-full bg-emerald-50 text-emerald-700 font-medium">
                                        Online
                                    </span>
                                </div>
                            </div>
                        ))}
                    </div>
                </div>
            </div>

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
                                   placeholder="Minim 8 caractere" required />
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
                            {form.role === 'ADMINISTRATOR' && '⚠ Accés complet la sistem.'}
                            {form.role === 'SEF_DIRECTIE'  && 'Vizualizare audit + gestiune documente.'}
                            {form.role === 'UTILIZATOR'    && 'Transferuri securizate și documente normative.'}
                        </p>
                    </div>

                    <Button onClick={handleCreate} disabled={!isValid || loading} className="w-full mt-2 flex items-center justify-center gap-2">
                        <UserPlus size={15} />
                        {loading ? 'Se creează…' : 'Creează cont'}
                    </Button>
                    <p className="text-center text-xs text-mai-400">* Câmpuri obligatorii</p>
                </div>
            </Modal>

        </div>
    );
}