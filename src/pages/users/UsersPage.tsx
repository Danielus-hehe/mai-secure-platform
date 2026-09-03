import { useState } from 'react';
import { UserPlus, Power, KeyRound } from 'lucide-react';
import PageHeader from '../../components/ui/PageHeader';
import Badge from '../../components/ui/Badge';
import Button from '../../components/ui/Button';
import Modal from '../../components/ui/Modal';
import Input from '../../components/ui/Input';
import ConfirmDialog from '../../components/ui/ConfirmDialog';
import { userStore } from '../../api/mockStore';
import { ROLE_LABELS, ROLE_BADGE_CLASSES } from '../../utils/constants';
import { formatDateTime } from '../../utils/format';
import { useToast } from '../../context/ToastContext';
import type { Role, User } from '../../types';

export default function UsersPage() {
    const toast = useToast();
    const [users, setUsers]       = useState(() => userStore.getAll());
    const [createOpen, setCreateOpen] = useState(false);
    const [confirmTarget, setConfirmTarget] = useState<User | null>(null);
    const [form, setForm] = useState({
        fullName: '', username: '', email: '',
        role: 'UTILIZATOR' as Role, department: '',
    });

    const refresh = () => setUsers(userStore.getAll());

    const handleCreate = () => {
        if (!form.fullName || !form.username) return;
        userStore.create({ ...form });
        refresh();
        setCreateOpen(false);
        setForm({ fullName: '', username: '', email: '', role: 'UTILIZATOR', department: '' });
        toast.success(`Contul @${form.username} a fost creat cu succes.`);
    };

    const handleToggleActive = (u: User) => {
        if (u.isActive) {
            setConfirmTarget(u);
        } else {
            userStore.setActive(u.id, true);
            refresh();
            toast.info(`Contul ${u.fullName} a fost activat.`);
        }
    };

    const handleConfirmDeactivate = () => {
        if (!confirmTarget) return;
        userStore.setActive(confirmTarget.id, false);
        refresh();
        toast.warning(`Contul ${confirmTarget.fullName} a fost dezactivat.`);
        setConfirmTarget(null);
    };

    const handleRoleChange = (u: User, newRole: Role) => {
        userStore.changeRole(u.id, newRole);
        refresh();
        toast.info(`Rolul lui ${u.fullName} schimbat în ${ROLE_LABELS[newRole]}.`);
    };

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
                            {users.map((u: User) => (
                                <tr key={u.id} className="hover:bg-mai-100/60 transition-colors">
                                    <td className="px-5 py-3.5">
                                        <p className="font-medium text-mai-900">{u.fullName}</p>
                                        <p className="text-xs text-mai-400">@{u.username}</p>
                                    </td>
                                    <td className="px-5 py-3.5 text-mai-500 whitespace-nowrap">{u.department}</td>
                                    <td className="px-5 py-3.5">
                                        <select value={u.role}
                                            onChange={e => handleRoleChange(u, e.target.value as Role)}
                                            className={`rounded-full px-2.5 py-1 text-[11px] font-semibold
                                                border-0 cursor-pointer focus:outline-none focus:ring-2
                                                focus:ring-mai-500 transition-opacity hover:opacity-80
                                                ${ROLE_BADGE_CLASSES[u.role]}`}>
                                            {Object.entries(ROLE_LABELS).map(([v, l]) => (
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
                                        <Button variant="ghost" className="px-2 py-1.5" title="Resetează parola"
                                            onClick={() => toast.info(`Link de resetare trimis la ${u.email || u.username}.`)}>
                                            <KeyRound size={14} />
                                        </Button>
                                        <Button
                                            variant={u.isActive ? 'danger' : 'secondary'}
                                            className="px-2.5 py-1.5"
                                            onClick={() => handleToggleActive(u)}>
                                            <Power size={13} />
                                            {u.isActive ? 'Dezactivează' : 'Activează'}
                                        </Button>
                                    </td>
                                </tr>
                            ))}
                        </tbody>
                    </table>
                </div>
            </div>

            {/* Modal: creare cont */}
            <Modal open={createOpen} title="Creare cont nou" onClose={() => setCreateOpen(false)}>
                <div className="space-y-4">
                    <Input id="fullName" label="Nume complet" value={form.fullName}
                        onChange={e => setForm(f => ({ ...f, fullName: e.target.value }))}
                        placeholder="ex: Ion Popescu" required />
                    <Input id="username" label="Nume de utilizator" value={form.username}
                        onChange={e => setForm(f => ({ ...f, username: e.target.value }))}
                        placeholder="ex: ion.popescu" required />
                    <Input id="email" label="Adresă e-mail" type="email" value={form.email}
                        onChange={e => setForm(f => ({ ...f, email: e.target.value }))}
                        placeholder="ex: ion.popescu@mai.gov.md" />
                    <Input id="department" label="Direcție / Departament" value={form.department}
                        onChange={e => setForm(f => ({ ...f, department: e.target.value }))}
                        placeholder="ex: Direcția TIC" />
                    <div>
                        <label className="block text-sm font-medium text-mai-800 mb-1.5">Rol</label>
                        <select value={form.role}
                            onChange={e => setForm(f => ({ ...f, role: e.target.value as Role }))}
                            className="w-full rounded-lg border border-mai-200 px-3.5 py-2.5 text-sm
                                focus:outline-none focus:ring-2 focus:ring-mai-500 hover:border-mai-300 transition-colors">
                            {Object.entries(ROLE_LABELS).map(([v, l]) => (
                                <option key={v} value={v}>{l}</option>
                            ))}
                        </select>
                    </div>
                    <Button onClick={handleCreate} disabled={!form.fullName || !form.username} className="w-full">
                        <UserPlus size={15} /> Creează cont
                    </Button>
                </div>
            </Modal>

            {/* Confirm dezactivare */}
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
