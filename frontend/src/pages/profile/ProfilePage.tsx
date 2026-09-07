import { useState } from 'react';
import { Mail, Building2, Calendar, KeyRound, ShieldCheck } from 'lucide-react';
import PageHeader from '../../components/ui/PageHeader';
import Button     from '../../components/ui/Button';
import Input      from '../../components/ui/Input';
import { useAuth }  from '../../context/AuthContext';
import { useToast } from '../../context/ToastContext';
import { ROLE_LABELS, ROLE_BADGE_CLASSES } from '../../utils/constants';
import { formatDateTime } from '../../utils/format';

const API = 'http://localhost:5000';

export default function ProfilePage() {
    const { user }  = useAuth();
    const toast     = useToast();

    const [currentPw,  setCurrentPw]  = useState('');
    const [newPw,      setNewPw]      = useState('');
    const [confirmPw,  setConfirmPw]  = useState('');
    const [saving,     setSaving]     = useState(false);

    if (!user) return null;

    const initials = user.fullName
        .split(' ')
        .map(w => w[0] ?? '')
        .join('')
        .toUpperCase()
        .slice(0, 2) || user.username.slice(0, 2).toUpperCase();

    const handlePasswordChange = async () => {
        if (!currentPw || !newPw || !confirmPw) {
            toast.warning('Completați toate câmpurile.');
            return;
        }
        if (newPw.length < 8) {
            toast.warning('Parola nouă trebuie să aibă minim 8 caractere.');
            return;
        }
        if (newPw !== confirmPw) {
            toast.error('Parolele noi nu coincid. Verificați și reîncercați.');
            return;
        }

        setSaving(true);
        try {
            const res = await fetch(`${API}/api/Auth/change-password`, {
                method: 'PATCH',
                headers: {
                    'Content-Type':  'application/json',
                    'Authorization': `Bearer ${user.token ?? ''}`,
                },
                body: JSON.stringify({
                    currentPassword: currentPw,
                    newPassword:     newPw,
                }),
            });

            if (!res.ok) {
                const err = await res.json().catch(() => ({ message: `HTTP ${res.status}` }));
                throw new Error(err.message);
            }

            setCurrentPw(''); setNewPw(''); setConfirmPw('');
            toast.success('Parola a fost actualizată cu succes.');
        } catch (e: unknown) {
            toast.error(`Eroare: ${e instanceof Error ? e.message : 'Eroare necunoscută'}`);
        } finally {
            setSaving(false);
        }
    };

    return (
        <div className="space-y-6">
            <PageHeader
                title="Profilul meu"
                subtitle="Informațiile contului și setările de securitate"
            />

            <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">

                {/* ── Card stânga: date cont ─────────────────────────── */}
                <div className="bg-white rounded-xl shadow-card border border-mai-100/50 p-6
                    flex flex-col items-center text-center gap-5">

                    {/* Avatar inițiale */}
                    <div className="w-20 h-20 rounded-full bg-mai-700 flex items-center
                        justify-center text-white text-2xl font-bold select-none shadow-md">
                        {initials}
                    </div>

                    <div>
                        <p className="text-lg font-bold text-mai-900">{user.fullName}</p>
                        <p className="text-sm text-mai-400 mt-0.5">@{user.username}</p>
                    </div>

                    <span className={`text-xs px-3 py-1.5 rounded-full font-semibold ${ROLE_BADGE_CLASSES[user.role]}`}>
                        {ROLE_LABELS[user.role]}
                    </span>

                    {/* Detalii */}
                    <div className="w-full border-t border-mai-100 pt-4 space-y-3.5 text-left">
                        {([
                            { icon: Mail,      label: 'Email',      value: user.email || '—'             },
                            { icon: Building2, label: 'Direcție',   value: user.department || '—'        },
                            { icon: Calendar,  label: 'Cont creat', value: formatDateTime(user.createdAt) },
                        ] as const).map(({ icon: Icon, label, value }) => (
                            <div key={label} className="flex items-start gap-2.5">
                                <Icon size={15} className="text-mai-400 mt-0.5 shrink-0" />
                                <div>
                                    <p className="text-[11px] text-mai-400 uppercase tracking-wide">{label}</p>
                                    <p className="text-sm text-mai-800 font-medium break-all">{value}</p>
                                </div>
                            </div>
                        ))}
                    </div>

                    {/* Stare cont */}
                    <div className="w-full rounded-lg bg-green-50 border border-green-100 px-3 py-2.5 text-center">
                        <p className="text-xs font-semibold text-green-700">● Cont activ</p>
                    </div>
                </div>

                {/* ── Coloana dreapta ────────────────────────────────── */}
                <div className="lg:col-span-2 space-y-6">

                    {/* Securitate cont */}
                    <div className="bg-white rounded-xl shadow-card border border-mai-100/50 p-6">
                        <h2 className="font-semibold text-mai-900 mb-1 flex items-center gap-2">
                            <ShieldCheck size={16} className="text-mai-400" />
                            Securitate cont
                        </h2>
                        <p className="text-xs text-mai-400 mb-4">
                            Accesul la sistem este protejat prin autentificare JWT cu expirare la 8h.
                        </p>
                        <div className="grid grid-cols-2 gap-3">
                            {[
                                { label: 'Autentificare',    value: '2FA dezactivat',  tone: 'text-amber-600' },
                                { label: 'Token JWT',        value: 'Activ (8h)',       tone: 'text-green-600' },
                                { label: 'Sesiuni active',   value: '1',               tone: 'text-mai-700'   },
                                { label: 'Ultim login',      value: 'Această sesiune', tone: 'text-mai-700'   },
                            ].map(({ label, value, tone }) => (
                                <div key={label} className="rounded-xl bg-mai-50 px-4 py-3">
                                    <p className="text-[11px] text-mai-400 uppercase tracking-wide">{label}</p>
                                    <p className={`text-sm font-semibold mt-0.5 ${tone}`}>{value}</p>
                                </div>
                            ))}
                        </div>
                    </div>

                    {/* Schimbare parolă */}
                    <div className="bg-white rounded-xl shadow-card border border-mai-100/50 p-6">
                        <h2 className="font-semibold text-mai-900 mb-4 flex items-center gap-2">
                            <KeyRound size={16} className="text-mai-400" />
                            Schimbare parolă
                        </h2>
                        <div className="space-y-4 max-w-sm">
                            <Input id="currentPw" label="Parola curentă" type="password"
                                   value={currentPw} onChange={e => setCurrentPw(e.target.value)}
                                   placeholder="••••••••" />
                            <Input id="newPw" label="Parola nouă" type="password"
                                   value={newPw} onChange={e => setNewPw(e.target.value)}
                                   placeholder="Minim 8 caractere" />
                            <Input id="confirmPw" label="Confirmă parola nouă" type="password"
                                   value={confirmPw} onChange={e => setConfirmPw(e.target.value)}
                                   placeholder="••••••••" />

                            <div className="rounded-lg bg-mai-50 border border-mai-100 px-3.5 py-2.5">
                                <p className="text-xs text-mai-500">
                                    Parola trebuie să aibă minim 8 caractere.
                                    După schimbare, sesiunile rămân active până la expirarea token-ului.
                                </p>
                            </div>

                            <Button
                                onClick={handlePasswordChange}
                                disabled={saving || !currentPw || !newPw || !confirmPw}
                                className="flex items-center gap-2"
                            >
                                <KeyRound size={15} />
                                {saving ? 'Se actualizează…' : 'Actualizează parola'}
                            </Button>
                        </div>
                    </div>
                </div>
            </div>
        </div>
    );
}