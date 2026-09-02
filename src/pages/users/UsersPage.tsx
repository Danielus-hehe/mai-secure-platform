import { useState } from 'react';
import { UserPlus, Power, KeyRound } from 'lucide-react';
import PageHeader from '../../components/ui/PageHeader';
import Badge from '../../components/ui/Badge';
import Button from '../../components/ui/Button';
import Modal from '../../components/ui/Modal';
import Input from '../../components/ui/Input';
import { userStore } from '../../api/mockStore';
import { ROLE_LABELS, ROLE_BADGE_CLASSES } from '../../utils/constants';
import { formatDateTime } from '../../utils/format';
import type { Role, User } from '../../types';

export default function UsersPage() {
    const [users, setUsers] = useState(() => userStore.getAll());
    const [createOpen, setCreateOpen] = useState(false);
    const [form, setForm] = useState({ fullName: '', username: '', email: '', role: 'UTILIZATOR' as Role, department: '' });

    const refresh = () => setUsers(userStore.getAll());

    const handleCreate = () => {
        if (!form.fullName || !form.username) return;
        userStore.create({ ...form });
        refresh();
        setCreateOpen(false);
        setForm({ fullName: '', username: '', email: '', role: 'UTILIZATOR', department: '' });
    };

    return (
        <div className="space-y-6">
            <PageHeader title="Gestiune utilizatori"
                        subtitle="Conturile sunt create exclusiv de administrator — fără auto-înregistrare"
                        actions={<Button onClick={() => setCreateOpen(true)}><UserPlus size={16} /> Cont nou</Button>} />

            <div className="bg-white rounded-xl shadow-card border border-mai-100/50 overflow-hidden">
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
                        <tr key={u.id} className="hover:bg-mai-50/50">
                            <td className="px-5 py-3.5">
                                <p className="font-medium text-mai-900">{u.fullName}</p>
                                <p className="text-xs text-mai-400">@{u.username}</p>
                            </td>
                            <td className="px-5 py-3.5 text-mai-500">{u.department}</td>
                            <td className="px-5 py-3.5">
                                <select value={u.role}
                                        onChange={(e) => { userStore.changeRole(u.id, e.target.value as Role); refresh(); }}
                                        className={`rounded-full px-2.5 py-1 text-[11px] font-semibold border-0 cursor-pointer
                      focus:outline-none focus:ring-2 focus:ring-mai-500
                      ${ROLE_BADGE_CLASSES[u.role]}`}>
                                    {Object.entries(ROLE_LABELS).map(([v, l]) => <option key={v} value={v}>{l}</option>)}
                                </select>
                            </td>
                            <td className="px-5 py-3.5 text-xs text-mai-400">{formatDateTime(u.createdAt)}</td>
                            <td className="px-5 py-3.5">
                                <Badge tone={u.isActive ? 'green' : 'gray'}>
                                    {u.isActive ? 'Activ' : 'Dezactivat'}
                                </Badge>
                            </td>
                            <td className="px-5 py-3.5 text-right whitespace-nowrap space-x-1">
                                <Button variant="ghost" className="px-2 py-1.5" title="Resetează parola">
                                    <KeyRound size={14} />
                                </Button>
                                <Button variant={u.isActive ? 'danger' : 'secondary'} className="px-2.5 py-1.5"
                                        onClick={() => { userStore.setActive(u.id, !u.isActive); refresh(); }}>
                                    <Power size={13} /> {u.isActive ? 'Dezactivează' : 'Activează'}
                                </Button>
                            </td>
                        </tr>
                    ))}
                    </tbody>
                </table>
            </div>

            <Modal open={createOpen} title="Creare cont nou" onClose={() => setCreateOpen(false)}>
                <div className="space-y-4">
                    <Input id="nu-name" label="Nume complet" value={form.fullName}
                           onChange={(e) => setForm({ ...form, fullName: e.target.value })} />
                    <div className="grid grid-cols-2 gap-4">
                        <Input id="nu-username" label="Nume de utilizator" value={form.username}
                               onChange={(e) => setForm({ ...form, username: e.target.value })} />
                        <Input id="nu-email" label="E-mail intern" type="email" value={form.email}
                               onChange={(e) => setForm({ ...form, email: e.target.value })} />
                    </div>
                    <div className="grid grid-cols-2 gap-4">
                        <Input id="nu-dept" label="Direcție" value={form.department}
                               onChange={(e) => setForm({ ...form, department: e.target.value })} />
                        <label className="block">
                            <span className="block text-sm font-medium text-mai-800 mb-1.5">Rol</span>
                            <select value={form.role} onChange={(e) => setForm({ ...form, role: e.target.value as Role })}
                                    className="w-full rounded-lg border border-mai-200 px-3 py-2.5 text-sm
                  focus:outline-none focus:ring-2 focus:ring-mai-500">
                                {Object.entries(ROLE_LABELS).map(([v, l]) => <option key={v} value={v}>{l}</option>)}
                            </select>
                        </label>
                    </div>
                    <p className="text-xs text-mai-400">
                        Parola inițială va fi generată automat și comunicată utilizatorului de administrator.
                    </p>
                    <Button onClick={handleCreate} className="w-full" disabled={!form.fullName || !form.username}>
                        Creare cont
                    </Button>
                </div>
            </Modal>
        </div>
    );
}