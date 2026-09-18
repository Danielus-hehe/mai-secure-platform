import { useState, useEffect, useCallback, useRef } from 'react';
import { UserPlus, Power, KeyRound, Loader2, Search, Unlock, Mail} from 'lucide-react';
import PageHeader    from '../../components/ui/PageHeader';
import Badge         from '../../components/ui/Badge';
import Button        from '../../components/ui/Button';
import Modal         from '../../components/ui/Modal';
import Input         from '../../components/ui/Input';
import ConfirmDialog from '../../components/ui/ConfirmDialog';
import Pagination    from '../../components/ui/Pagination';
import { ROLE_LABELS, ROLE_BADGE_CLASSES } from '../../utils/constants';
import { formatDateTime } from '../../utils/format';
import { useToast }  from '../../context/ToastContext';
import api from '../../api/client';
import { apiErrorMessage } from '../../api/errors';
import type { Role, User } from '../../types';

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
    isActive: boolean; emailConfirmed: boolean; createdAt: string;
    isLockedOut: boolean; lockoutEndsAt: string | null;
    lastLoginAt: string | null;
}

/** Forma reală a răspunsului: obiect paginat, nu array. */
interface PagedUsers {
    items: ApiUser[];
    totalCount: number;
    page: number;
    pageSize: number;
    totalPages: number;
    hasPrevious: boolean;
    hasNext: boolean;
}

type AppUser = User & {
    emailConfirmed: boolean;
    isLockedOut: boolean;
    lockoutEndsAt: string | null;
    lastLoginAt: string | null;
};

/** Convertim role numeric → string enum folosit de frontend */
const toUser = (u: ApiUser): AppUser => ({
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

    const [users,         setUsers]         = useState<AppUser[]>([]);
    const [total,         setTotal]         = useState(0);
    const [totalPages,    setTotalPages]    = useState(0);
    const [page,          setPage]          = useState(1);
    const [pageSize,      setPageSize]      = useState(25);
    const [searchInput,   setSearchInput]   = useState('');
    const [search,        setSearch]        = useState('');

    const [loading,       setLoading]       = useState(true);
    const [createOpen,    setCreateOpen]    = useState(false);
    const [createLoading, setCreateLoading] = useState(false);
    const [confirmTarget, setConfirmTarget] = useState<AppUser | null>(null);
    const [form, setForm] = useState<CreateForm>(EMPTY_FORM);

    // Căutarea pleacă abia după ce utilizatorul se oprește din tastat.
    useEffect(() => {
        const timer = window.setTimeout(() => {
            setSearch(searchInput.trim());
            setPage(1);
        }, 350);
        return () => window.clearTimeout(timer);
    }, [searchInput]);

    const abortRef = useRef<AbortController | null>(null);

    /* ── Citire utilizatori din DB ───────────────────────────────────── */
    const fetchUsers = useCallback(async () => {
        abortRef.current?.abort();
        const controller = new AbortController();
        abortRef.current = controller;

        setLoading(true);
        try {
            // API-ul întoarce PagedResult<UserDto>, adică un OBIECT cu `items`.
            // Codul vechi îl citea ca array și apela `.map` direct pe el, deci
            // arunca TypeError și lista rămânea goală cu mesajul „Nu s-au putut
            // încărca utilizatorii".
            const { data } = await api.get<PagedUsers>('/Users', {
                params: {
                    search: search || undefined,
                    page,
                    pageSize,
                },
                signal: controller.signal,
            });

            setUsers((data.items ?? []).map(toUser));
            setTotal(data.totalCount ?? 0);
            setTotalPages(data.totalPages ?? 0);
        } catch (e: unknown) {
            if (controller.signal.aborted) return;
            toast.error(apiErrorMessage(e, 'Nu s-au putut încărca utilizatorii.'));
        } finally {
            if (!controller.signal.aborted) setLoading(false);
        }
    }, [search, page, pageSize, toast]);

    useEffect(() => {
        void fetchUsers();
        return () => abortRef.current?.abort();
    }, [fetchUsers]);

    /* ── Creare utilizator nou ───────────────────────────────────────── */
    const handleCreate = async () => {
        if (!form.fullName || !form.username || !form.password) return;
        setCreateLoading(true);
        try {
            const { data } = await api.post<{ message?: string }>('/Users', {
                fullName:   form.fullName,
                username:   form.username,
                password:   form.password,
                email:      form.email,
                department: form.department,
                role:       ROLE_STR[form.role],
            });

            // Mesajul serverului spune și că utilizatorul își va schimba parola
            // la prima autentificare — util de transmis odată cu parola inițială.
            toast.success(data?.message ?? `Contul @${form.username} a fost creat.`);
            setCreateOpen(false);
            setForm(EMPTY_FORM);
            setPage(1);
            void fetchUsers();
        } catch (e: unknown) {
            toast.error(apiErrorMessage(e, 'Contul nu a putut fi creat.'));
        } finally {
            setCreateLoading(false);
        }
    };

    /* ── Schimbare rol ───────────────────────────────────────────────── */
    const handleRoleChange = async (u: AppUser, newRole: Role) => {
        try {
            await api.patch(`/Users/${u.id}/role`, { role: ROLE_STR[newRole] });
            toast.info(`Rolul lui ${u.fullName} → ${ROLE_LABELS[newRole]}.`);
            void fetchUsers();
        } catch (e: unknown) {
            toast.error(apiErrorMessage(e, 'Rolul nu a putut fi schimbat.'));
        }
    };

    /* ── Dezactivare / Activare ──────────────────────────────────────── */
    const handleToggleActive = (u: AppUser) => {
        if (u.isActive) {
            setConfirmTarget(u);           // cere confirmare
        } else {
            void activateUser(u);
        }
    };

    /* ── Retrimitere invitație de activare ───────────────────────────── */
    const handleResendInvitation = async (u: AppUser) => {
        if (!confirm(`Retrimiți invitația de activare la ${u.email || u.username}?`)) return;
        try {
            const { data } = await api.post<{ message: string }>(
                `/Users/${u.id}/resend-invitation`
            );
            toast.success(data.message ?? 'Invitație retrimisă.');
        } catch (e: unknown) {
            toast.error(apiErrorMessage(e, 'Invitația nu a putut fi retrimisă.'));
        }
    };

    const activateUser = async (u: AppUser) => {
        try {
            await api.patch(`/Users/${u.id}/activate`);
            toast.info(`Contul ${u.fullName} a fost activat.`);
            void fetchUsers();
        } catch (e: unknown) {
            toast.error(apiErrorMessage(e, 'Contul nu a putut fi activat.'));
        }
    };

    const handleConfirmDeactivate = async () => {
        if (!confirmTarget) return;
        try {
            await api.patch(`/Users/${confirmTarget.id}/deactivate`);
            toast.warning(`Contul ${confirmTarget.fullName} a fost dezactivat.`);
            void fetchUsers();
        } catch (e: unknown) {
            toast.error(apiErrorMessage(e, 'Contul nu a putut fi dezactivat.'));
        } finally {
            setConfirmTarget(null);
        }
    };

    /* ── Deblocare cont după prea multe încercări eșuate ─────────────── */
    const handleUnlock = async (u: AppUser) => {
        try {
            await api.post(`/Users/${u.id}/unlock`);
            toast.success(`Contul @${u.username} a fost deblocat.`);
            void fetchUsers();
        } catch (e: unknown) {
            toast.error(apiErrorMessage(e, 'Contul nu a putut fi deblocat.'));
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

            {/* Căutare server-side */}
            <div className="relative w-full sm:max-w-md">
                <Search size={16} className="absolute left-3 top-1/2 -translate-y-1/2 text-mai-300 dark:text-mai-500" />
                <input
                    value={searchInput}
                    onChange={e => setSearchInput(e.target.value)}
                    placeholder="Caută după nume, utilizator, email sau direcție…"
                    className="w-full rounded-lg border border-mai-200 dark:border-mai-600 bg-white dark:bg-mai-800 dark:text-mai-200 py-2 pl-9 pr-3 text-sm
                               dark:focus:border-mai-400 focus:border-mai-500 focus:outline-none focus:ring-2 focus:ring-mai-500/20"
                />
            </div>

            <div className="bg-white dark:bg-mai-800 rounded-xl shadow-card dark:shadow-none border border-mai-100/50 dark:border-mai-700 overflow-hidden">

                {/* Loading */}
                {loading && (
                    <div className="flex items-center justify-center gap-3 py-16 text-mai-400">
                        <Loader2 size={20} className="animate-spin" />
                        <span className="text-sm">Se încarcă utilizatorii…</span>
                    </div>
                )}

                {/* Gol */}
                {!loading && users.length === 0 && (
                    <div className="text-center py-16 text-mai-400 dark:text-mai-400 text-sm">
                        {search
                            ? 'Niciun utilizator nu corespunde căutării.'
                            : 'Niciun utilizator găsit în baza de date.'}
                    </div>
                )}

                {/* Tabelul */}
                {!loading && users.length > 0 && (
                    <>
                        <div className="overflow-x-auto">
                            <table className="w-full text-sm">
                                <thead>
                                <tr className="bg-mai-50 dark:bg-mai-900 text-left text-xs uppercase tracking-wide text-mai-500">
                                    <th className="px-5 py-3 font-semibold">Utilizator</th>
                                    <th className="px-5 py-3 font-semibold">Direcție</th>
                                    <th className="px-5 py-3 font-semibold">Rol</th>
                                    <th className="px-5 py-3 font-semibold">Creat la</th>
                                    <th className="px-5 py-3 font-semibold">Status</th>
                                    <th className="px-5 py-3 font-semibold text-right">Acțiuni</th>
                                </tr>
                                </thead>
                                <tbody className="divide-y divide-mai-50 dark:divide-mai-700">
                                {users.map(u => (
                                    <tr key={u.id} className="hover:bg-mai-100/60 dark:hover:bg-mai-700/40 transition-colors">

                                        <td className="px-5 py-3.5">
                                            <p className="font-medium text-mai-900 dark:text-white">{u.fullName || u.username}</p>
                                            <p className="text-xs text-mai-400 dark:text-mai-500">@{u.username}</p>
                                        </td>

                                        <td className="px-5 py-3.5 text-mai-500 dark:text-mai-300 whitespace-nowrap">
                                            {u.department || '—'}
                                        </td>

                                        {/* Dropdown rol — schimbă direct în DB */}
                                        <td className="px-5 py-3.5">
                                            <select
                                                value={u.role}
                                                onChange={e => void handleRoleChange(u, e.target.value as Role)}
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

                                        <td className="px-5 py-3.5 text-xs text-mai-400 dark:text-mai-500 whitespace-nowrap">
                                            {formatDateTime(u.createdAt)}
                                        </td>

                                        <td className="px-5 py-3.5">
                                            <div className="flex flex-col gap-1">
                                                <Badge tone={u.isActive ? 'green' : 'gray'}>
                                                    {u.isActive ? 'Activ' : 'Dezactivat'}
                                                </Badge>
                                                {!u.emailConfirmed && (
                                                    <Badge tone="amber">Neactivat</Badge>
                                                )}
                                                {u.isLockedOut && (
                                                    <Badge tone="red">Blocat</Badge>
                                                )}
                                            </div>
                                        </td>

                                        <td className="px-5 py-3.5 text-right whitespace-nowrap space-x-1">
                                            {u.isLockedOut && (
                                                <Button variant="secondary" className="px-2 py-1.5"
                                                        title="Deblochează contul"
                                                        onClick={() => void handleUnlock(u)}>
                                                    <Unlock size={14} />
                                                </Button>
                                            )}
                                            {!u.emailConfirmed && u.email && (
                                                <Button variant="ghost" className="px-2 py-1.5 text-amber-600 dark:text-amber-400"
                                                        title="Retrimite email de activare"
                                                        onClick={() => void handleResendInvitation(u)}>
                                                    <Mail size={14} />
                                                </Button>
                                            )}
                                            <Button variant="ghost" className="px-2 py-1.5"
                                                    title="Resetare parolă (din contul de administrator)"
                                                    onClick={() => toast.info(
                                                        `Resetarea parolei pentru @${u.username} se face din secțiunea de administrare.`
                                                    )}>
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

                        <Pagination
                            page={page}
                            pageSize={pageSize}
                            totalCount={total}
                            totalPages={totalPages}
                            onPageChange={setPage}
                            onPageSizeChange={size => { setPageSize(size); setPage(1); }}
                            itemLabel="utilizatori"
                        />
                    </>
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
                           placeholder="Minim 12 caractere, cu majusculă, cifră și simbol" required />

                    <Input id="email" label="Adresă e-mail" type="email"
                           value={form.email}
                           onChange={e => setForm(f => ({ ...f, email: e.target.value }))}
                           placeholder="ex: ion.popescu@mai.gov.md" />

                    <Input id="department" label="Direcție / Departament"
                           value={form.department}
                           onChange={e => setForm(f => ({ ...f, department: e.target.value }))}
                           placeholder="ex: Direcția TIC" />

                    <div>
                        <label className="block text-sm font-medium text-mai-800 dark:text-mai-200 mb-1.5">Rol</label>
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

                    <div className="rounded-lg bg-mai-50 dark:bg-mai-900 border border-mai-100 dark:border-mai-700 px-3.5 py-2.5">
                        <p className="text-xs leading-relaxed text-mai-500 dark:text-mai-400">
                            Contul nou nu are chei criptografice. Ele se generează automat la prima
                            autentificare a utilizatorului. Până atunci, nu i se pot trimite fișiere
                            criptate și nu apare în lista de destinatari.
                        </p>
                    </div>

                    <Button
                        onClick={() => void handleCreate()}
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
                onConfirm={() => void handleConfirmDeactivate()}
                onCancel={() => setConfirmTarget(null)}
            />

        </div>
    );
}