import { useEffect, useRef, useState, type FormEvent } from 'react';
import { useNavigate } from 'react-router-dom';
import { Lock, User, EyeOff, Eye, Smartphone, ArrowLeft, Send, BookOpen, ClipboardCheck, type LucideIcon } from 'lucide-react';
import { useAuth } from '../../context/AuthContext';
import { useToast } from '../../context/ToastContext';
import Button from '../../components/ui/Button';
import Input from '../../components/ui/Input';
import { apiErrorMessage } from '../../api/errors';
import { getLoginInfo, type DirectoryLoginInfo } from '../../api/directory';
import type { TwoFactorChallenge } from '../../api/twoFactor';

/**
 * Ce face platforma, în ordinea în care o folosește un angajat: trimite și
 * primește fișiere, consultă actele normative, confirmă documentele interne.
 * Textele descriu funcții care există în aplicație (destinatari multipli,
 * termen de valabilitate, confirmare de primire, versiuni, „Luat la
 * cunoștință”), ca pagina de start să nu promită nimic ce nu se găsește după
 * autentificare.
 */
const PURPOSES: ReadonlyArray<{ icon: LucideIcon; title: string; text: string }> = [
    {
        icon: Send,
        title: 'Transfer de fișiere între colegi',
        text: 'Trimiteți un fișier unuia sau mai multor colegi, cu termen de valabilitate și confirmare de primire.',
    },
    {
        icon: BookOpen,
        title: 'Registrul actelor normative',
        text: 'Ordine, instrucțiuni și regulamente în vigoare, cu istoricul tuturor versiunilor.',
    },
    {
        icon: ClipboardCheck,
        title: 'Documente interne ale subdiviziunii',
        text: 'Dispozițiile conducerii ajung la destinatari, iar confirmarea „Luat la cunoștință” rămâne înregistrată.',
    },
];

export default function LoginPage() {
    const { login, verifyTwoFactor } = useAuth();
    const navigate = useNavigate();
    const toast = useToast();

    const [username, setUsername] = useState('');
    const [password, setPassword] = useState('');
    const [showPassword, setShowPassword] = useState(false);
    const [loading, setLoading] = useState(false);

    // ── Pasul doi, doar pentru conturile care si-au activat 2FA ──────────────
    const [challenge, setChallenge] = useState<TwoFactorChallenge | null>(null);
    const [code, setCode] = useState('');
    const [useRecovery, setUseRecovery] = useState(false);
    const [secondsLeft, setSecondsLeft] = useState(0);

    /**
     * Dacă serverul acceptă conturi de domeniu, spunem asta din start. Fără
     * indicație, oamenii încearcă formate greșite („DOMENIU\nume”, adresa de
     * email) până își blochează singuri contul - iar blocarea noastră se
     * aplică și conturilor AD, ca să nu folosim API-ul ca instrument de forță
     * brută împotriva domeniului.
     */
    const [directory, setDirectory] = useState<DirectoryLoginInfo | null>(null);

    useEffect(() => {
        let cancelled = false;
        getLoginInfo()
            .then(info => { if (!cancelled && info.enabled) setDirectory(info); })
            .catch(() => {
                // Informație secundară: pagina de login trebuie să funcționeze
                // și dacă endpointul nu răspunde.
            });
        return () => { cancelled = true; };
    }, []);

    const codeInputRef = useRef<HTMLInputElement>(null);

    const goAfterLogin = (role: string) => {
        navigate(role === 'ADMINISTRATOR' ? '/admin' : '/dashboard', { replace: true });
    };

    // Numaratoare inversa pentru provocare. Utilizatorul trebuie sa vada cat timp
    // mai are: o provocare expirata in tacere l-ar lasa sa tasteze un cod corect
    // si sa primeasca "invalid", fara sa inteleaga de ce.
    useEffect(() => {
        if (!challenge) return;

        const tick = () => {
            const left = Math.max(0, Math.floor((new Date(challenge.expiresAt).getTime() - Date.now()) / 1000));
            setSecondsLeft(left);
            if (left === 0) {
                setChallenge(null);
                setCode('');
                toast.error('Sesiunea de verificare a expirat. Autentificați-vă din nou.');
            }
        };

        tick();
        const id = window.setInterval(tick, 1000);
        return () => window.clearInterval(id);
    }, [challenge, toast]);

    useEffect(() => {
        if (challenge) codeInputRef.current?.focus();
    }, [challenge]);

    const handleSubmit = async (e: FormEvent) => {
        e.preventDefault();
        setLoading(true);

        try {
            const result = await login(username, password);

            if (result.kind === 'twoFactor') {
                setChallenge(result.challenge);
                return;
            }

            goAfterLogin(result.user.role);
        } catch (err: unknown) {
            toast.error(apiErrorMessage(err, 'Nume de utilizator sau parolă incorectă. Încercați din nou.'));
        } finally {
            setLoading(false);
        }
    };

    const handleVerify = async (e: FormEvent) => {
        e.preventDefault();
        if (!challenge) return;

        setLoading(true);
        try {
            const result = await verifyTwoFactor(challenge.challengeToken, code);

            if (result.usedRecoveryCode) {
                toast.success(
                    `Ați folosit un cod de recuperare. Vă mai rămân ${result.remainingRecoveryCodes}. ` +
                    'Reconfigurați 2FA din profil dacă v-ați pierdut telefonul.'
                );
            }

            goAfterLogin(result.user.role);
        } catch (err: unknown) {
            toast.error(apiErrorMessage(err, 'Cod invalid.'));
            setCode('');
            codeInputRef.current?.focus();
        } finally {
            setLoading(false);
        }
    };

    const cancelTwoFactor = () => {
        setChallenge(null);
        setCode('');
        setUseRecovery(false);
        setPassword('');
    };

    return (
        <div className="min-h-screen flex bg-mai-950">
            {/*
              Panoul instituțional.

              Spune la ce folosește platforma, nu cum e protejată. Cine ajunge
              aici e un angajat care trebuie să trimită un fișier sau să citească
              un ordin; detaliile de criptare, integritate și audit îl privesc pe
              administrator și sunt descrise în documentație. Cele trei rânduri
              corespund celor trei secțiuni din meniul aplicației, ca prima pagină
              să fie și o hartă a ei.
            */}
            <div className="hidden lg:flex flex-col justify-between gap-8 w-[45%] p-10 2xl:p-12 bg-gradient-to-br from-mai-900 via-mai-800 to-mai-950 relative overflow-hidden">
                <div className="absolute -bottom-32 -left-32 w-96 h-96 rounded-full border-[40px] border-white/5" />
                <div className="absolute -top-24 -right-24 w-72 h-72 rounded-full border-[30px] border-gold-500/10" />

                <div className="relative flex items-center gap-4">
                    {/* Stema e desenată în negru; invert o face albă pe fundalul închis. */}
                    <img src="/Stema.svg" alt="" aria-hidden="true" className="h-20 w-auto -my-3 -ml-3 invert" />
                    <div className="leading-tight border-l border-white/15 pl-4">
                        <p className="text-white font-bold">Ministerul Afacerilor Interne</p>
                        <p className="text-mai-300 text-xs mt-0.5">al Republicii Moldova</p>
                    </div>
                </div>

                <div className="relative">
                    <h2 className="text-2xl 2xl:text-3xl font-bold text-white leading-snug">
                        Sistem de Gestiune Documente<br />și Transferuri Securizate
                    </h2>
                    <p className="mt-4 text-mai-200 max-w-md leading-relaxed">
                        Documentele de serviciu circulă între subdiviziunile ministerului direct
                        din aplicație, fără stick-uri USB și fără poștă.
                    </p>

                    <ul className="mt-8 2xl:mt-10 space-y-5 2xl:space-y-6 max-w-md">
                        {PURPOSES.map(({ icon: Icon, title, text }) => (
                            <li key={title} className="flex items-start gap-4">
                                <span className="w-10 h-10 rounded-lg bg-white/10 text-gold-400 flex items-center justify-center shrink-0">
                                    <Icon size={20} />
                                </span>
                                <div>
                                    <p className="font-semibold text-white">{title}</p>
                                    <p className="text-sm text-mai-200 leading-relaxed mt-0.5">{text}</p>
                                </div>
                            </li>
                        ))}
                    </ul>
                </div>

                <p className="relative text-mai-400 text-xs leading-relaxed">
                    © 2026 Ministerul Afacerilor Interne al Republicii Moldova<br />
                    Platformă disponibilă doar în rețeaua internă a ministerului
                </p>
            </div>

            {/* Formularul */}
            <div className="flex-1 flex items-center justify-center p-4 sm:p-6 bg-mai-50 dark:bg-mai-950">
                <div className="w-full max-w-md">
                    <div className="lg:hidden flex items-center justify-center gap-3 mb-8">
                        <div className="w-11 h-11 rounded-full bg-gold-500 flex items-center justify-center text-mai-900 font-bold text-sm">
                            MAI
                        </div>
                        <p className="text-mai-900 dark:text-white font-bold">Ministerul Afacerilor Interne</p>
                    </div>

                    <div className="bg-white dark:bg-mai-800 rounded-2xl shadow-card dark:shadow-none p-5 sm:p-8">
                        {!challenge ? (
                            <>
                                <h1 className="text-xl font-bold text-mai-900 dark:text-white">Autentificare</h1>
                                <p className="text-sm text-mai-400 mt-1 mb-6">
                                    Introduceți datele de acces primite de la administratorul de sistem.
                                </p>

                                <form onSubmit={handleSubmit} className="space-y-4">
                                    <div className="relative">
                                        <User size={16} className="absolute left-3.5 top-[42px] text-mai-300 dark:text-mai-500 z-10" />
                                        <div className="pl-9">
                                            <Input
                                                id="username"
                                                label="Nume de utilizator"
                                                value={username}
                                                onChange={e => setUsername(e.target.value)}
                                                placeholder="ex: nume.prenume"
                                                required
                                                autoComplete="username"
                                            />
                                        </div>
                                    </div>

                                    <div className="relative">
                                        <Lock size={16} className="absolute left-3.5 top-[42px] text-mai-300 dark:text-mai-500 z-10" />
                                        <button
                                            type="button"
                                            onClick={() => setShowPassword(v => !v)}
                                            className="absolute right-3.5 top-[42px] text-mai-300 hover:text-mai-500
                                                dark:text-mai-500 dark:hover:text-mai-300 z-10"
                                        >
                                            {showPassword ? <EyeOff size={16} /> : <Eye size={16} />}
                                        </button>
                                        <div className="pl-9 pr-10">
                                            <Input
                                                id="password"
                                                label="Parolă"
                                                type={showPassword ? 'text' : 'password'}
                                                value={password}
                                                onChange={e => setPassword(e.target.value)}
                                                placeholder="••••••••"
                                                required
                                                autoComplete="current-password"
                                            />
                                        </div>
                                    </div>

                                    {/*
                                      Indicația stă sub parolă, lângă butonul de trimitere:
                                      e citită exact când utilizatorul decide ce parolă
                                      tastează, iar numele de utilizator rămâne primul
                                      câmp, fără nimic între el și eticheta lui.
                                    */}
                                    {directory && (
                                        <p className="rounded-lg bg-mai-50 px-3 py-2 text-xs leading-relaxed
                                                      text-mai-500 dark:bg-mai-900/40 dark:text-mai-300">
                                            Se acceptă și contul de domeniu
                                            {directory.domain ? ` ${directory.domain}` : ''}: scrieți doar numele
                                            scurt (<code>nume.prenume</code>). Parola contului de domeniu se
                                            schimbă în Active Directory, nu aici.
                                        </p>
                                    )}

                                    <Button type="submit" disabled={loading} className="w-full mt-2">
                                        {loading ? 'Se autentifică…' : 'Autentificare'}
                                    </Button>
                                </form>
                            </>
                        ) : (
                            <>
                                <div className="flex items-center gap-3 mb-1">
                                    <div className="w-10 h-10 rounded-xl bg-mai-50 dark:bg-mai-700 text-mai-600 dark:text-mai-300
                                        flex items-center justify-center shrink-0">
                                        <Smartphone size={20} />
                                    </div>
                                    <h1 className="text-xl font-bold text-mai-900 dark:text-white">Verificare în doi pași</h1>
                                </div>

                                <p className="text-sm text-mai-400 mt-2 mb-6">
                                    {useRecovery
                                        ? 'Introduceți unul dintre codurile de recuperare salvate la activare. Codul se consumă după folosire.'
                                        : 'Introduceți codul afișat de aplicația de autentificare pentru contul dumneavoastră.'}
                                </p>

                                <form onSubmit={handleVerify} className="space-y-4">
                                    <Input
                                        ref={codeInputRef}
                                        id="code"
                                        label={useRecovery ? 'Cod de recuperare' : 'Cod din aplicație'}
                                        value={code}
                                        onChange={e => setCode(e.target.value)}
                                        placeholder={useRecovery ? 'XXXX-XXXX' : '000000'}
                                        required
                                        autoComplete="one-time-code"
                                        inputMode={useRecovery ? 'text' : 'numeric'}
                                        className={useRecovery ? '' : 'tracking-[0.4em] text-center text-lg'}
                                    />

                                    <div className="flex items-center justify-between text-xs">
                                        <span className={secondsLeft < 30 ? 'text-red-600 dark:text-red-400 font-medium' : 'text-mai-400'}>
                                            Expiră în {Math.floor(secondsLeft / 60)}:
                                            {String(secondsLeft % 60).padStart(2, '0')}
                                        </span>

                                        {challenge.recoveryAvailable && (
                                            <button
                                                type="button"
                                                onClick={() => { setUseRecovery(v => !v); setCode(''); }}
                                                className="text-mai-600 dark:text-mai-300 hover:text-mai-800 dark:hover:text-white font-medium"
                                            >
                                                {useRecovery ? 'Folosesc aplicația' : 'Am pierdut telefonul'}
                                            </button>
                                        )}
                                    </div>

                                    <Button type="submit" disabled={loading || !code.trim()} className="w-full">
                                        {loading ? 'Se verifică…' : 'Confirmă'}
                                    </Button>

                                    <button
                                        type="button"
                                        onClick={cancelTwoFactor}
                                        className="w-full flex items-center justify-center gap-1.5 text-xs
                                            text-mai-400 hover:text-mai-600 dark:hover:text-mai-200 pt-1"
                                    >
                                        <ArrowLeft size={13} /> Înapoi la autentificare
                                    </button>
                                </form>

                                {!challenge.recoveryAvailable && (
                                    <p className="text-xs text-gold-600 dark:text-gold-400 bg-gold-500/10 dark:bg-gold-500/15 rounded-lg p-3 mt-5">
                                        Nu mai aveți coduri de recuperare disponibile. Dacă nu puteți accesa
                                        aplicația de autentificare, contactați administratorul de sistem.
                                    </p>
                                )}
                            </>
                        )}
                    </div>

                    <p className="text-center text-xs text-mai-400 mt-6">
                        Conturile sunt create exclusiv de administratorul de sistem.<br />
                        Utilizarea neautorizată este interzisă și se sancționează conform legislației.
                    </p>
                </div>
            </div>
        </div>
    );
}