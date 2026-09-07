import { useState, useEffect, useCallback } from 'react';
import { UserPlus, Power, KeyRound, Loader2 } from 'lucide-react';
import PageHeader    from '../../components/ui/PageHeader';
import Badge         from '../../components/ui/Badge';
import Button        from '../../components/ui/Button';
import Modal         from '../../components/ui/Modal';
import Input         from '../../components/ui/Input';
import ConfirmDialog from '../../components/ui/ConfirmDialog';
import { ROLE_LABELS, ROLE_BADGE_CLASSES } from '../../utils/constants';
import { formatDateTime } from '../../utils/format';
import { useToast }  from '../../context/ToastContext';
import { useAuth }   from '../../context/AuthContext';
import type { Role, User } from '../../types';

const API = 'http://localhost:5000';

// Backend UserRole enum: Utilizator=1, SefDirectie=2, Administrator=3
const ROLE_NUM: Record<number, Role> = {
    1: 'UTILIZATOR',
    2: 'SEF_DIRECTIE',
    3: 'ADMINISTRATOR',
};
const ROLE_STR: Record<Role, number> = {
    UTILIZATOR:    1,
    SEF_DIRECTIE:  2,
    ADMINISTRATOR: 3,
};

/** Răspunsul brut de la /api/Users — role vine ca număr */
interface ApiUser {
    id: string; fullName: string; username: string;
    email: string; role: number; department: string;
    isActive: boolean; createdAt: string;
}

/** Convertim role numeric → string enum folosit de frontend */
const toUser = (u: ApiUser): User => ({
    ...u,
    role: ROLE_NUM[u.role] ?? 'UTILIZATOR',
});

interface CreateForm {
    fullName: string; username: string; password: string;
    email: string; department: string; role: Role;
}
const EMPTY_FORM: CreateForm = {
    fullName: '', username: '', password: '',
    email: '', department: '', role: 'UTILIZATOR',
};

export default function UsersPage() {
    const toast = useToast();
    const { user: me } = useAuth();

    const [users,         setUsers]         = useState<User[]>([]);
    const [loading,       setLoading]       = useState(true);
    const [createOpen,    setCreateOpen]    = useState(false);
    const [createLoading, setCreateLoading] = useState(false);
    const [confirmTarget, setConfirmTarget] = useState<User | null>(null);
    const [form, setForm] = useState<CreateForm>(EMPTY_FORM);

    /** Headers comune pentru toate request-urile autentificate */
    const hdrs = useCallback(
        () => ({
            'Content-Type': 'application/json',
            'Authorization': `Bearer ${me?.token ?? ''}`,
        }),
        [me?.token],
    );

    /* ── Citire utilizatori din DB ───────────────────────────────────── */
    const fetchUsers = useCallback(async () => {
        setLoading(true);
        try {
            const res = await fetch(`${API}/api/Users`, { headers: hdrs() });
            if (!res.ok) throw new Error(`HTTP ${res.status}`);
            const data: ApiUser[] = await res.json();
            setUsers(data.map(toUser));
        } catch {
            toast.error('Nu s-au putut încărca utilizatorii.');
        } finally {
            setLoading(false);
        }
    }, [hdrs, toast]);

    useEffect(() => { fetchUsers(); }, [fetchUsers]);

    /* ── Creare utilizator nou ───────────────────────────────────────── */
    const handleCreate = async () => {
        if (!form.fullName || !form.username || !form.password) return;
        setCreateLoading(true);
        try {
            const res = await fetch(`${API}/api/Users`, {
                method: 'POST',
                headers: hdrs(),
                body: JSON.stringify({
                    fullName:   form.fullName,
                    username:   form.username,
                    password:   form.password,
                    email:      form.email,
                    department: form.department,
                    role:       ROLE_STR[form.role],
                }),
            });
            if (!res.ok) {
                const err = await res.json().catch(() => ({ message: `HTTP ${res.status}` }));
                throw new Error(err.message);
            }
            toast.success(`Contul @${form.username} a fost creat.`);
            setCreateOpen(false);
            setForm(EMPTY_FORM);
            fetchUsers();
        } catch (e: unknown) {
            toast.error(`Eroare: ${e instanceof Error ? e.message : 'Eroare necunoscută'}`);
        } finally {
            setCreateLoading(false);
        }
    };

    /* ── Schimbare rol ───────────────────────────────────────────────── */
    const handleRoleChange = async (u: User, newRole: Role) => {
        try {
            const res = await fetch(`${API}/api/Users/${u.id}/role`, {
                method: 'PATCH',
                headers: hdrs(),
                body: JSON.stringify({ role: ROLE_STR[newRole] }),
            });
            if (!res.ok) throw new Error(`HTTP ${res.status}`);
            toast.info(`Rolul lui ${u.fullName} → ${ROLE_LABELS[newRole]}.`);
            fetchUsers();
        } catch {
            toast.error('Eroare la schimbarea rolului.');
        }
    };

    /* ── Dezactivare / Activare ──────────────────────────────────────── */
    const handleToggleActive = (u: User) => {
        if (u.isActive) {
            setConfirmTarget(u);           // cere confirmare
        } else {
            activateUser(u);
        }
    };

    const activateUser = async (u: User) => {
        try {
            const res = await fetch(`${API}/api/Users/${u.id}/activate`, {
                method: 'PATCH', headers: hdrs(),
            });
            if (!res.ok) throw new Error(`HTTP ${res.status}`);
            toast.info(`Contul ${u.fullName} a fost activat.`);
            fetchUsers();
        } catch {
            toast.error('Eroare la activarea contului.');
        }
    };

    const handleConfirmDeactivate = async () => {
        if (!confirmTarget) return;
        try {
            const res = await fetch(`${API}/api/Users/${confirmTarget.id}/deactivate`, {
                method: 'PATCH', headers: hdrs(),
            });
            if (!res.ok) throw new Error(`HTTP ${res.status}`);
            toast.warning(`Contul ${confirmTarget.fullName} a fost dezactivat.`);
            fetchUsers();
        } catch {
            toast.error('Eroare la dezactivarea contului.');
        } finally {
            setConfirmTarget(null);
        }
    };

    /* ── UI ──────────────────────────────────────────────────────────── */
    return (
        <div className="space-y-6">

            <PageHeader
                title="Gestiune utilizatori"
                subtitle="Conturile sunt create exclusiv de administrator — fără auto-înregistrare"
                actions={
                    <Button onClick={() => setCreateOpen(true)}>
                        <UserPlus size={16} /> Cont nou
                    </Button>
                }
            />

            <div className="bg-white rounded-xl shadow-card border border-mai-100/50 overflow-hidden">

                {/* Loading */}
                {loading && (
                    <div className="flex items-center justify-center gap-3 py-16 text-mai-400">
                        <Loader2 size={20} className="animate-spin" />
                        <span className="text-sm">Se încarcă utilizatorii…</span>
                    </div>
                )}

                {/* Gol */}
                {!loading && users.length === 0 && (
                    <div className="text-center py-16 text-mai-400 text-sm">
                        Niciun utilizator găsit în baza de date.
                    </div>
                )}

                {/* Tabelul */}
                {!loading && users.length > 0 && (
                    <div className="overflow-x-auto">
                        <table className="w-full text-sm">
                            <thead>
                            <tr className="bg-mai-50 text-left text-xs uppercase tracking-wide text-mai-500">
                                <th className="px-5 py-3 font-semibold">Utilizator</th>
                                <th className="px-5 py-3 font-semibold">Direcție</th>
                                <th className="px-5 py-3 font-semibold">Rol</th>
                                <th className="px-5 py-3 font-semibold">Creat la</th>
                                <th className="px-5 py-3 font-semibold">Status</th>
                                <th className="px-5 py-3 font-semibold text-right">Acțiuni</th>
                            </tr>
                            </thead>
                            <tbody className="divide-y divide-mai-50">
                            {users.map(u => (
                                <tr key={u.id} className="hover:bg-mai-100/60 transition-colors">

                                    <td className="px-5 py-3.5">
                                        <p className="font-medium text-mai-900">{u.fullName || u.username}</p>
                                        <p className="text-xs text-mai-400">@{u.username}</p>
                                    </td>

                                    <td className="px-5 py-3.5 text-mai-500 whitespace-nowrap">
                                        {u.department || '—'}
                                    </td>

                                    {/* Dropdown rol — schimbă direct în DB */}
                                    <td className="px-5 py-3.5">
                                        <select
                                            value={u.role}
                                            onChange={e => handleRoleChange(u, e.target.value as Role)}
                                            className={`rounded-full px-2.5 py-1 text-[11px] font-semibold
                                                    border-0 cursor-pointer focus:outline-none focus:ring-2
                                                    focus:ring-mai-500 transition-opacity hover:opacity-80
                                                    ${ROLE_BADGE_CLASSES[u.role]}`}
                                        >
                                            {(Object.entries(ROLE_LABELS) as [Role, string][]).map(([v, l]) => (
                                                <option key={v} value={v}>{l}</option>
                                            ))}
                                        </select>
                                    </td>

                                    <td className="px-5 py-3.5 text-xs text-mai-400 whitespace-nowrap">
                                        {formatDateTime(u.createdAt)}
                                    </td>

                                    <td className="px-5 py-3.5">
                                        <Badge tone={u.isActive ? 'green' : 'gray'}>
                                            {u.isActive ? 'Activ' : 'Dezactivat'}
                                        </Badge>
                                    </td>

                                    <td className="px-5 py-3.5 text-right whitespace-nowrap space-x-1">
                                        <Button variant="ghost" className="px-2 py-1.5"
                                                title="Resetează parola"
                                                onClick={() => toast.info(`Link de resetare trimis la ${u.email || u.username}.`)}>
                                            <KeyRound size={14} />
                                        </Button>
                                        <Button
                                            variant={u.isActive ? 'danger' : 'secondary'}
                                            className="px-2.5 py-1.5"
                                            onClick={() => handleToggleActive(u)}
                                        >
                                            <Power size={13} />
                                            {u.isActive ? 'Dezactivează' : 'Activează'}
                                        </Button>
                                    </td>

                                </tr>
                            ))}
                            </tbody>
                        </table>
                    </div>
                )}
            </div>

            {/* ─── Modal: Creare cont nou ─── */}
            <Modal open={createOpen} title="Creare cont nou" onClose={() => { setCreateOpen(false); setForm(EMPTY_FORM); }}>
                <div className="space-y-4">

                    <Input id="fullName" label="Nume complet *"
                           value={form.fullName}
                           onChange={e => setForm(f => ({ ...f, fullName: e.target.value }))}
                           placeholder="ex: Ion Popescu" required />

                    <Input id="username" label="Nume de utilizator *"
                           value={form.username}
                           onChange={e => setForm(f => ({ ...f, username: e.target.value }))}
                           placeholder="ex: ion.popescu" required />

                    <Input id="password" label="Parolă *" type="password"
                           value={form.password}
                           onChange={e => setForm(f => ({ ...f, password: e.target.value }))}
                           placeholder="Minim 8 caractere" required />

                    <Input id="email" label="Adresă e-mail" type="email"
                           value={form.email}
                           onChange={e => setForm(f => ({ ...f, email: e.target.value }))}
                           placeholder="ex: ion.popescu@mai.gov.md" />

                    <Input id="department" label="Direcție / Departament"
                           value={form.department}
                           onChange={e => setForm(f => ({ ...f, department: e.target.value }))}
                           placeholder="ex: Direcția TIC" />

                    <div>
                        <label className="block text-sm font-medium text-mai-800 mb-1.5">Rol</label>
                        <select
                            value={form.role}
                            onChange={e => setForm(f => ({ ...f, role: e.target.value as Role }))}
                            className="w-full rounded-lg border border-mai-200 px-3.5 py-2.5 text-sm
                                focus:outline-none focus:ring-2 focus:ring-mai-500
                                hover:border-mai-300 transition-colors bg-white"
                        >
                            {(Object.entries(ROLE_LABELS) as [Role, string][]).map(([v, l]) => (
                                <option key={v} value={v}>{l}</option>
                            ))}
                        </select>
                    </div>

                    <Button
                        onClick={handleCreate}
                        disabled={!form.fullName || !form.username || !form.password || createLoading}
                        className="w-full flex items-center justify-center gap-2"
                    >
                        <UserPlus size={15} />
                        {createLoading ? 'Se creează…' : 'Creează cont'}
                    </Button>
                </div>
            </Modal>

            {/* ─── Confirmare dezactivare ─── */}
            <ConfirmDialog
                open={!!confirmTarget}
                title="Dezactivare cont"
                message={`Ești sigur că vrei să dezactivezi contul lui ${confirmTarget?.fullName}? Utilizatorul nu va mai putea accesa sistemul.`}
                confirmLabel="Dezactivează"
                variant="danger"
                onConfirm={handleConfirmDeactivate}
                onCancel={() => setConfirmTarget(null)}
            />

        </div>
    );
}