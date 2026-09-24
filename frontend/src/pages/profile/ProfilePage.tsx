import { useEffect, useState } from 'react';
import { Building2, KeyRound, ShieldCheck, Fingerprint, CalendarClock } from 'lucide-react';
import KeyFingerprint from '../../components/security/KeyFingerprint';
import PageHeader from '../../components/ui/PageHeader';
import Button     from '../../components/ui/Button';
import Input      from '../../components/ui/Input';
import { useAuth }  from '../../context/AuthContext';
import { useToast } from '../../context/ToastContext';
import { useKeys }  from '../../context/KeysContext';
import { ROLE_LABELS, ROLE_BADGE_CLASSES } from '../../utils/constants';
import { formatDateTime } from '../../utils/format';
import api from '../../api/client';
import { apiErrorMessage } from '../../api/errors';
import { rewrapKeysForNewPassword, type RewrapPayload } from '../../crypto/passwordChange';
import SessionsPanel from '../../components/security/SessionsPanel';
import type { PublishedKeyBundle } from '../../crypto/E2ee';

interface ServerBundle extends PublishedKeyBundle {
    hasKeys: boolean;
    fingerprint?: string;
    keysCreatedAt?: string;
}

export default function ProfilePage() {
    const { user, logout } = useAuth();
    const toast     = useToast();
    const { fingerprint } = useKeys();

    const [currentPw,  setCurrentPw]  = useState('');
    const [newPw,      setNewPw]      = useState('');
    const [confirmPw,  setConfirmPw]  = useState('');
    const [saving,     setSaving]     = useState(false);
    const [keysCreatedAt, setKeysCreatedAt] = useState<string | null>(null);

    useEffect(() => {
        void (async () => {
            try {
                const { data } = await api.get<ServerBundle>('/Keys/me');
                if (data.hasKeys && data.keysCreatedAt) setKeysCreatedAt(data.keysCreatedAt);
            } catch {
                // Informație secundară: absența ei nu merită un mesaj de eroare.
            }
        })();
    }, []);

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
        if (newPw.length < 12) {
            toast.warning('Parola nouă trebuie să aibă minim 12 caractere.');
            return;
        }
        if (newPw !== confirmPw) {
            toast.error('Parolele noi nu coincid. Verificați și reîncercați.');
            return;
        }
        if (newPw === currentPw) {
            toast.warning('Parola nouă trebuie să fie diferită de cea curentă.');
            return;
        }

        setSaving(true);
        try {
            // ── Pasul 1: pachetul de chei ────────────────────────────────
            const { data: bundle } = await api.get<ServerBundle>('/Keys/me');

            let payload: RewrapPayload | null = null;

            if (bundle.hasKeys) {
                // Reîmpachetarea se face ÎNAINTE de schimbarea parolei pe server.
                // Dacă parola curentă e greșită, aflăm aici, local, fără să fi
                // modificat nimic. Ordinea inversă ar lăsa cheile încuiate cu o
                // parolă care nu mai există.
                payload = await rewrapKeysForNewPassword(currentPw, newPw, bundle);
            }

            // ── Pasul 2: parola și cheile, într-o singură cerere ─────────
            // Serverul salvează hash-ul nou și pachetul reîmpachetat în aceeași
            // tranzacție: nu mai există starea „parolă schimbată, chei încuiate
            // cu cea veche”, deci nici ecranul de reîncercare a sincronizării.
            await api.patch('/Auth/change-password', {
                currentPassword: currentPw,
                newPassword:     newPw,
                keys:            payload ?? undefined,
            });

            // Serverul a închis toate sesiunile, inclusiv pe aceasta: tokenul
            // curent e deja respins. Utilizatorul se autentifică din nou, cu
            // parola nouă, care îi descuie și cheile.
            setCurrentPw(''); setNewPw(''); setConfirmPw('');
            toast.success(
                bundle.hasKeys
                    ? 'Parola a fost schimbată și cheile reîmpachetate. Autentificați-vă cu parola nouă.'
                    : 'Parola a fost schimbată. Autentificați-vă cu parola nouă.'
            );
            await logout();
        } catch (e: unknown) {
            toast.error(apiErrorMessage(e, 'Parola nu a putut fi schimbată.'));
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

            <div className="grid grid-cols-1 lg:grid-cols-3 gap-4 sm:gap-6">

                {/* ── Card stânga: date cont ─────────────────────────── */}
                <div className="bg-white dark:bg-mai-800 rounded-xl shadow-card dark:shadow-none border border-mai-100/50 dark:border-mai-700 p-6
                    flex flex-col items-center text-center gap-5">

                    <div className="w-20 h-20 rounded-full bg-mai-700 dark:bg-mai-600 flex items-center
                        justify-center text-white text-2xl font-bold select-none shadow-md">
                        {initials}
                    </div>

                    <div>
                        <p className="text-lg font-bold text-mai-900 dark:text-white dark:text-white">{user.fullName}</p>
                        <p className="text-sm text-mai-400 dark:text-mai-500 mt-0.5">@{user.username}</p>
                    </div>

                    <span className={`text-xs px-3 py-1.5 rounded-full font-semibold ${ROLE_BADGE_CLASSES[user.role]}`}>
                        {ROLE_LABELS[user.role]}
                    </span>

                    {/*
                      Email și data creării contului nu ajung în sesiune: răspunsul
                      de login conține doar id, username, fullName, department și
                      rol. Codul vechi le citea oricum de pe obiectul User, deci
                      compilarea eșua. Le-am înlocuit cu informația criptografică,
                      care e mai relevantă aici și e disponibilă oricărui cont.
                    */}
                    <div className="w-full border-t border-mai-100 dark:border-mai-700 pt-4 space-y-3.5 text-left">
                        <div className="flex items-start gap-2.5">
                            <Building2 size={15} className="text-mai-400 mt-0.5 shrink-0" />
                            <div>
                                <p className="text-[11px] text-mai-400 uppercase tracking-wide">Direcție</p>
                                <p className="text-sm text-mai-800 dark:text-mai-200 font-medium break-all">
                                    {user.department || '-'}
                                </p>
                            </div>
                        </div>

                        <div className="flex items-start gap-2.5">
                            <Fingerprint size={15} className="text-mai-400 mt-0.5 shrink-0" />
                            <div className="min-w-0">
                                <p className="text-[11px] text-mai-400 uppercase tracking-wide">Amprentă chei</p>
                                <div className="text-xs text-mai-800 dark:text-mai-200 font-medium">
                                    <KeyFingerprint value={fingerprint || ''} />
                                </div>
                            </div>
                        </div>

                        <div className="flex items-start gap-2.5">
                            <CalendarClock size={15} className="text-mai-400 mt-0.5 shrink-0" />
                            <div>
                                <p className="text-[11px] text-mai-400 uppercase tracking-wide">Chei generate</p>
                                <p className="text-sm text-mai-800 dark:text-mai-200 font-medium">
                                    {keysCreatedAt ? formatDateTime(keysCreatedAt) : '-'}
                                </p>
                            </div>
                        </div>
                    </div>

                    <div className="w-full rounded-lg bg-green-50 dark:bg-green-900/30 border border-green-100 dark:border-green-800 px-3 py-2.5 text-center">
                        <p className="text-xs font-semibold text-green-700 dark:text-green-400">● Cont activ</p>
                    </div>
                </div>

                {/* ── Coloana dreapta ────────────────────────────────── */}
                <div className="lg:col-span-2 space-y-6">

                    {/* Securitate cont */}
                    <div className="bg-white dark:bg-mai-800 rounded-xl shadow-card dark:shadow-none border border-mai-100/50 dark:border-mai-700 p-6">
                        <h2 className="font-semibold text-mai-900 dark:text-white mb-1 flex items-center gap-2">
                            <ShieldCheck size={16} className="text-mai-400" />
                            Securitate cont
                        </h2>
                        <p className="text-xs text-mai-400 dark:text-mai-500 mb-4">
                            Parola este stocată ca hash Argon2id. Fișierele sunt criptate end-to-end:
                            serverul nu poate citi conținutul transferurilor.
                        </p>
                        <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
                            {[
                                { label: 'Hash parolă',    value: 'Argon2id',            tone: 'text-green-600' },
                                { label: 'Token acces',    value: 'JWT, 15 min',         tone: 'text-green-600' },
                                { label: 'Criptare fișiere', value: 'AES-256-GCM + RSA', tone: 'text-green-600' },
                                { label: 'Autentificare',  value: '2FA dezactivat',      tone: 'text-amber-600' },
                            ].map(({ label, value, tone }) => (
                                <div key={label} className="rounded-xl bg-mai-50 dark:bg-mai-900 px-4 py-3">
                                    <p className="text-[11px] text-mai-400 uppercase tracking-wide">{label}</p>
                                    <p className={`text-sm font-semibold mt-0.5 ${tone}`}>{value}</p>
                                </div>
                            ))}
                        </div>
                    </div>

                    {/*
                      Schimbarea parolei există doar pentru conturile locale.
                      Pentru un cont de domeniu, parola nu e la noi: un formular
                      care ar părea să o schimbe ar fi o minciună de interfață -
                      utilizatorul ar crede că a schimbat-o și s-ar autentifica
                      în continuare cu cea din AD.
                    */}
                    {user.isDirectoryAccount ? (
                        <div className="bg-white dark:bg-mai-800 rounded-xl shadow-card dark:shadow-none border border-mai-100/50 dark:border-mai-700 p-6">
                            <h2 className="font-semibold text-mai-900 dark:text-white mb-2 flex items-center gap-2">
                                <KeyRound size={16} className="text-mai-400" />
                                Parola contului
                            </h2>
                            <p className="text-sm leading-relaxed text-mai-500 dark:text-mai-300">
                                Contul dumneavoastră este de domeniu (Active Directory). Parola se
                                schimbă acolo - de pe o stație din domeniu sau prin portalul
                                instituției - nu din această aplicație.
                            </p>
                            <p className="mt-3 text-sm leading-relaxed text-mai-500 dark:text-mai-300">
                                După schimbare, la prima autentificare vi se cere o singură dată
                                parola veche, ca să se reîmpacheteze cheile private de criptare.
                                Cheile publice rămân aceleași, deci fișierele primite până acum
                                rămân accesibile.
                            </p>
                        </div>
                    ) : (
                    <div className="bg-white dark:bg-mai-800 rounded-xl shadow-card dark:shadow-none border border-mai-100/50 dark:border-mai-700 p-6">
                        <h2 className="font-semibold text-mai-900 dark:text-white mb-4 flex items-center gap-2">
                            <KeyRound size={16} className="text-mai-400" />
                            Schimbare parolă
                        </h2>

                        <div className="space-y-4 max-w-sm">
                            <Input id="currentPw" label="Parola curentă" type="password"
                                   value={currentPw} onChange={e => setCurrentPw(e.target.value)}
                                   placeholder="••••••••" />
                            <Input id="newPw" label="Parola nouă" type="password"
                                   value={newPw} onChange={e => setNewPw(e.target.value)}
                                   placeholder="Minim 12 caractere" />
                            <Input id="confirmPw" label="Confirmă parola nouă" type="password"
                                   value={confirmPw} onChange={e => setConfirmPw(e.target.value)}
                                   placeholder="••••••••" />

                            <div className="rounded-lg bg-mai-50 dark:bg-mai-900 border border-mai-100 dark:border-mai-700 px-3.5 py-2.5">
                                <p className="text-xs leading-relaxed text-mai-500 dark:text-mai-400">
                                    Minim 12 caractere, cu literă mare, literă mică, cifră și simbol.
                                    La schimbare, cheile private se reîmpachetează automat cu parola
                                    nouă, iar sesiunile de pe alte dispozitive sunt deconectate.
                                </p>
                            </div>

                            <Button
                                onClick={() => void handlePasswordChange()}
                                disabled={saving || !currentPw || !newPw || !confirmPw}
                                className="flex items-center gap-2"
                            >
                                <KeyRound size={15} />
                                {saving ? 'Se actualizează…' : 'Actualizează parola'}
                            </Button>
                        </div>
                    </div>
                    )}

                    {/*
                      Sesiunile active stau lângă schimbarea parolei pentru că
                      răspund aceleiași întrebări - cine mai are acces la contul
                      meu - iar cele două acțiuni se folosesc de obicei împreună:
                      cine închide o sesiune necunoscută vrea imediat după și să-și
                      schimbe parola.
                    */}
                    <SessionsPanel />
                </div>
            </div>
        </div>
    );
}