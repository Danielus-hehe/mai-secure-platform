/**
 * Pagina publică deschisă din linkul de resetare trimis de administrator.
 *
 * Tokenul se verifică la deschidere (fără să fie consumat), ca utilizatorul
 * să vadă pentru ce cont schimbă parola și dacă cheile de criptare se vor
 * regenera. Consumarea se face la trimiterea formularului.
 */

import { useEffect, useState, type FormEvent } from 'react';
import { Link, useNavigate, useSearchParams } from 'react-router-dom';
import { KeyRound, Eye, EyeOff, Loader2, AlertCircle, AlertTriangle, CheckCircle2 } from 'lucide-react';
import Button from '../../components/ui/Button';
import Input from '../../components/ui/Input';
import { apiErrorMessage } from '../../api/errors';
import { checkPasswordResetToken, completePasswordReset, type PasswordResetTokenInfo } from '../../api/users';
import { formatDateTime } from '../../utils/format';

type State = 'checking' | 'ready' | 'invalid' | 'done';

export default function ResetPasswordPage() {
    const [params] = useSearchParams();
    const navigate = useNavigate();
    const token = params.get('token') ?? '';

    const [state, setState] = useState<State>(token ? 'checking' : 'invalid');
    const [info, setInfo] = useState<PasswordResetTokenInfo | null>(null);
    const [error, setError] = useState<string | null>(token ? null : 'Linkul nu conține tokenul de resetare.');
    const [errors, setErrors] = useState<string[]>([]);

    const [password, setPassword] = useState('');
    const [confirm, setConfirm] = useState('');
    const [show, setShow] = useState(false);
    const [submitting, setSubmitting] = useState(false);

    useEffect(() => {
        if (!token) return;
        let cancelled = false;
        checkPasswordResetToken(token)
            .then((i) => { if (!cancelled) { setInfo(i); setState('ready'); } })
            .catch((e) => {
                if (cancelled) return;
                setError(apiErrorMessage(e, 'Linkul de resetare nu este valid sau a expirat.'));
                setState('invalid');
            });
        return () => { cancelled = true; };
    }, [token]);

    const mismatch = confirm.length > 0 && password !== confirm;

    const handleSubmit = async (e: FormEvent) => {
        e.preventDefault();
        if (!password || password !== confirm) return;
        setSubmitting(true);
        setError(null);
        setErrors([]);
        try {
            await completePasswordReset(token, password, confirm);
            setState('done');
        } catch (err) {
            setError(apiErrorMessage(err, 'Parola nu a putut fi schimbată.'));
            const data = (err as { response?: { data?: { errors?: unknown } } }).response?.data;
            if (Array.isArray(data?.errors)) setErrors(data.errors as string[]);
        } finally {
            setSubmitting(false);
        }
    };

    return (
        <div className="flex min-h-screen items-center justify-center bg-mai-50 p-4 dark:bg-mai-950">
            <div className="w-full max-w-md">
                <div className="mb-6 flex items-center justify-center gap-3">
                    <div className="flex h-11 w-11 items-center justify-center rounded-full bg-gold-500 text-sm font-bold text-mai-900">MAI</div>
                    <div className="leading-tight">
                        <p className="font-bold text-mai-900 dark:text-white">SGDM</p>
                        <p className="text-xs text-mai-500 dark:text-mai-400">Ministerul Afacerilor Interne</p>
                    </div>
                </div>

                <div className="rounded-2xl bg-white p-5 shadow-card dark:bg-mai-800 dark:shadow-none sm:p-8">
                    {state === 'checking' && (
                        <div className="flex items-center justify-center gap-3 py-10 text-mai-400">
                            <Loader2 size={18} className="animate-spin" /> Se verifică linkul…
                        </div>
                    )}

                    {state === 'invalid' && (
                        <div className="space-y-4 text-center">
                            <AlertCircle size={36} className="mx-auto text-red-500" />
                            <h1 className="text-lg font-bold text-mai-900 dark:text-white">Link nevalid</h1>
                            <p className="text-sm text-mai-500 dark:text-mai-400">{error}</p>
                            <p className="text-xs text-mai-400">
                                Un link de resetare se poate folosi o singură dată și expiră după câteva ore.
                                Cereți administratorului unul nou.
                            </p>
                            <Link to="/login" className="inline-block text-sm font-medium text-mai-600 hover:underline dark:text-mai-300">
                                Înapoi la autentificare
                            </Link>
                        </div>
                    )}

                    {state === 'done' && (
                        <div className="space-y-4 text-center">
                            <CheckCircle2 size={36} className="mx-auto text-green-500" />
                            <h1 className="text-lg font-bold text-mai-900 dark:text-white">Parola a fost schimbată</h1>
                            <p className="text-sm text-mai-500 dark:text-mai-400">
                                Toate sesiunile anterioare au fost închise și ați primit o confirmare pe email.
                                {info?.hasKeys && ' La autentificare veți genera chei de criptare noi.'}
                            </p>
                            <Button className="w-full justify-center" onClick={() => navigate('/login', { replace: true })}>
                                Mergi la autentificare
                            </Button>
                        </div>
                    )}

                    {state === 'ready' && info && (
                        <>
                            <h1 className="flex items-center gap-2 text-xl font-bold text-mai-900 dark:text-white">
                                <KeyRound size={20} /> Parolă nouă
                            </h1>
                            <p className="mb-5 mt-1 text-sm text-mai-400">
                                Cont: <strong className="text-mai-700 dark:text-mai-200">{info.fullName}</strong> (@{info.username}).
                                Linkul expiră la {formatDateTime(info.expiresAt)}.
                            </p>

                            {info.hasKeys && (
                                <div className="mb-5 flex items-start gap-2.5 rounded-lg border border-amber-200 bg-amber-50 px-3.5 py-2.5
                                    dark:border-amber-700/50 dark:bg-amber-900/20">
                                    <AlertTriangle size={15} className="mt-0.5 shrink-0 text-amber-600 dark:text-amber-400" />
                                    <p className="text-xs leading-relaxed text-amber-800 dark:text-amber-300">
                                        Cheile de criptare ale contului sunt protejate cu parola veche. După schimbare
                                        veți genera chei noi, iar fișierele criptate primite anterior nu vor mai putea fi
                                        deschise — expeditorii le pot retrimite.
                                    </p>
                                </div>
                            )}

                            <form onSubmit={handleSubmit} className="space-y-4">
                                <div className="relative">
                                    <button
                                        type="button"
                                        tabIndex={-1}
                                        onClick={() => setShow((v) => !v)}
                                        className="absolute right-3.5 top-[42px] z-10 text-mai-300 hover:text-mai-500 dark:text-mai-500"
                                        aria-label={show ? 'Ascunde parola' : 'Arată parola'}
                                    >
                                        {show ? <EyeOff size={16} /> : <Eye size={16} />}
                                    </button>
                                    <div className="pr-10">
                                        <Input
                                            id="new-password"
                                            label="Parolă nouă"
                                            type={show ? 'text' : 'password'}
                                            autoComplete="new-password"
                                            value={password}
                                            onChange={(e) => setPassword(e.target.value)}
                                            placeholder="Minim 12 caractere, cu majusculă, cifră și simbol"
                                            required
                                        />
                                    </div>
                                </div>
                                <Input
                                    id="confirm-password"
                                    label="Confirmați parola"
                                    type={show ? 'text' : 'password'}
                                    autoComplete="new-password"
                                    value={confirm}
                                    onChange={(e) => setConfirm(e.target.value)}
                                    error={mismatch ? 'Parolele nu coincid.' : undefined}
                                    required
                                />

                                {error && (
                                    <div className="rounded-lg border border-red-200 bg-red-50 px-3.5 py-2.5 text-xs text-red-700
                                        dark:border-red-800/50 dark:bg-red-900/20 dark:text-red-300">
                                        <p>{error}</p>
                                        {errors.length > 0 && (
                                            <ul className="mt-1 list-inside list-disc">
                                                {errors.map((m) => <li key={m}>{m}</li>)}
                                            </ul>
                                        )}
                                    </div>
                                )}

                                <Button type="submit" className="w-full justify-center" disabled={submitting || !password || password !== confirm}>
                                    {submitting ? <Loader2 size={15} className="animate-spin" /> : <KeyRound size={15} />}
                                    Stabilește parola
                                </Button>
                            </form>
                        </>
                    )}
                </div>
            </div>
        </div>
    );
}
