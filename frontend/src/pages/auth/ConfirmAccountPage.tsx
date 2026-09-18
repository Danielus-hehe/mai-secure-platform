import { useState, useEffect, useCallback } from 'react';
import { useSearchParams, useNavigate } from 'react-router-dom';
import {
    ShieldCheck, AlertCircle, CheckCircle2,
    Eye, EyeOff, Loader2, RefreshCw,
} from 'lucide-react';

// ── Tipuri ─────────────────────────────────────────────────────────────────────
interface TokenInfo {
    username: string;
    email: string;
}

type PageState = 'loading' | 'ready' | 'invalid' | 'submitting' | 'success';

// ── Forța parolei ─────────────────────────────────────────────────────────────
function passwordStrength(pw: string): 0 | 1 | 2 | 3 | 4 {
    let s = 0;
    if (pw.length >= 8)  s++;
    if (pw.length >= 12) s++;
    if (/[A-Z]/.test(pw) && /[a-z]/.test(pw)) s++;
    if (/\d/.test(pw) && /[^A-Za-z0-9]/.test(pw)) s++;
    return s as 0 | 1 | 2 | 3 | 4;
}

const STRENGTH_COLORS = [
    '',
    'bg-red-500',
    'bg-orange-400',
    'bg-yellow-400',
    'bg-green-500',
] as const;

const STRENGTH_LABELS = ['', 'Slabă', 'Acceptabilă', 'Bună', 'Puternică'] as const;

// ── Componentă principală ──────────────────────────────────────────────────────
export default function ConfirmAccountPage() {
    const [searchParams] = useSearchParams();
    const navigate = useNavigate();
    const token = searchParams.get('token') ?? '';

    const [pageState,       setPageState]       = useState<PageState>('loading');
    const [tokenInfo,       setTokenInfo]       = useState<TokenInfo | null>(null);
    const [apiError,        setApiError]        = useState<string | null>(null);
    const [apiErrors,       setApiErrors]       = useState<string[]>([]);
    const [newPassword,     setNewPassword]     = useState('');
    const [confirmPassword, setConfirmPassword] = useState('');
    const [showNew,         setShowNew]         = useState(false);
    const [showConfirm,     setShowConfirm]     = useState(false);

    // ── Verificare token la mount ────────────────────────────────────────────
    const checkToken = useCallback(async () => {
        setPageState('loading');
        setApiError(null);

        if (!token) {
            setApiError('Nu există token în URL. Verificați linkul din email.');
            setPageState('invalid');
            return;
        }

        try {
            const res = await fetch(
                `/api/Auth/check-invitation?token=${encodeURIComponent(token)}`
            );
            if (!res.ok) {
                const body = await res.json().catch(() => ({}));
                throw new Error(body.error ?? 'Token invalid sau expirat.');
            }
            const info = await res.json() as TokenInfo;
            setTokenInfo(info);
            setPageState('ready');
        } catch (err: unknown) {
            setApiError(err instanceof Error ? err.message : 'Eroare la verificarea linkului.');
            setPageState('invalid');
        }
    }, [token]);

    useEffect(() => { void checkToken(); }, [checkToken]);

    // ── Submit setare parolă ─────────────────────────────────────────────────
    const handleSubmit = async (e: React.FormEvent) => {
        e.preventDefault();
        setApiError(null);
        setApiErrors([]);

        if (newPassword !== confirmPassword) {
            setApiError('Parolele nu coincid.');
            return;
        }

        setPageState('submitting');

        try {
            const res = await fetch('/api/Auth/confirm-invitation', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    token,
                    newPassword,
                    confirmPassword,
                }),
            });

            if (!res.ok) {
                const body = await res.json().catch(() => ({}));
                if (body.errors && Array.isArray(body.errors)) {
                    setApiErrors(body.errors as string[]);
                    setApiError(body.error ?? 'Parola nu respectă cerințele.');
                } else {
                    setApiError(body.error ?? 'A apărut o eroare. Reîncercați.');
                }
                setPageState('ready');
                return;
            }

            setPageState('success');
        } catch {
            setApiError('Eroare de rețea. Verificați conexiunea și reîncercați.');
            setPageState('ready');
        }
    };

    const strength = passwordStrength(newPassword);
    const passwordsMatch = confirmPassword.length > 0 && newPassword === confirmPassword;

    // ── Render ───────────────────────────────────────────────────────────────
    return (
        <div className="min-h-screen bg-gray-100 dark:bg-gray-950 flex items-center justify-center px-4 py-12">
            <div className="w-full max-w-md space-y-6">

                {/* Logo / antet */}
                <div className="text-center">
                    <div className="inline-flex items-center justify-center w-16 h-16
                                    bg-blue-900 dark:bg-blue-800 rounded-2xl shadow-lg mb-4">
                        <ShieldCheck className="w-9 h-9 text-white" />
                    </div>
                    <h1 className="text-2xl font-bold text-blue-900 dark:text-blue-200 tracking-tight">
                        Activare Cont
                    </h1>
                    <p className="text-sm text-gray-500 dark:text-gray-400 mt-1">
                        Platforma Securizată — Ministerul Afacerilor Interne
                    </p>
                </div>

                {/* Card principal */}
                <div className="bg-white dark:bg-gray-900 rounded-2xl shadow-md overflow-hidden
                                border border-gray-200 dark:border-gray-800">

                    {/* ── Loading ─────────────────────────────────────── */}
                    {pageState === 'loading' && (
                        <div className="flex flex-col items-center justify-center py-16 gap-4">
                            <Loader2 className="w-9 h-9 text-blue-900 dark:text-blue-400 animate-spin" />
                            <p className="text-sm text-gray-400">
                                Se verifică linkul de activare…
                            </p>
                        </div>
                    )}

                    {/* ── Token invalid / expirat ─────────────────────── */}
                    {pageState === 'invalid' && (
                        <div className="px-8 py-10 text-center space-y-5">
                            <AlertCircle className="w-14 h-14 text-red-400 mx-auto" />
                            <div>
                                <h2 className="text-lg font-semibold text-gray-800 dark:text-gray-100">
                                    Link invalid sau expirat
                                </h2>
                                <p className="text-sm text-gray-500 dark:text-gray-400 mt-2 leading-relaxed">
                                    {apiError ?? 'Verificați că ați copiat corect linkul din email.'}
                                </p>
                            </div>
                            <div className="flex flex-col gap-2 items-center">
                                <button
                                    onClick={() => void checkToken()}
                                    className="flex items-center gap-2 text-blue-900 dark:text-blue-400
                                               text-sm font-medium hover:underline"
                                >
                                    <RefreshCw className="w-3.5 h-3.5" />
                                    Reîncearcă
                                </button>
                                <button
                                    onClick={() => navigate('/login')}
                                    className="text-sm text-gray-500 hover:text-gray-700 dark:text-gray-400"
                                >
                                    Înapoi la autentificare
                                </button>
                            </div>
                        </div>
                    )}

                    {/* ── Succes ──────────────────────────────────────── */}
                    {pageState === 'success' && (
                        <div className="px-8 py-10 text-center space-y-5">
                            <CheckCircle2 className="w-14 h-14 text-green-500 mx-auto" />
                            <div>
                                <h2 className="text-lg font-semibold text-gray-800 dark:text-gray-100">
                                    Cont activat cu succes!
                                </h2>
                                <p className="text-sm text-gray-500 dark:text-gray-400 mt-2 leading-relaxed">
                                    Parola a fost setată. Vă puteți autentifica acum în platformă.
                                </p>
                            </div>
                            <button
                                onClick={() => navigate('/login')}
                                className="inline-block bg-blue-900 dark:bg-blue-700 text-white
                                           px-7 py-2.5 rounded-lg text-sm font-semibold
                                           hover:bg-blue-800 dark:hover:bg-blue-600
                                           transition-colors active:scale-[.98]"
                            >
                                Mergeți la autentificare
                            </button>
                        </div>
                    )}

                    {/* ── Formular ────────────────────────────────────── */}
                    {(pageState === 'ready' || pageState === 'submitting') && tokenInfo && (
                        <form onSubmit={e => void handleSubmit(e)} className="p-8 space-y-5">

                            {/* Informații cont */}
                            <div className="rounded-xl bg-blue-50 dark:bg-blue-950 border
                                            border-blue-100 dark:border-blue-900 px-4 py-3">
                                <p className="text-[11px] font-semibold text-blue-500
                                              uppercase tracking-widest mb-1">
                                    Cont
                                </p>
                                <p className="text-sm font-bold text-blue-900 dark:text-blue-200">
                                    {tokenInfo.username}
                                </p>
                                <p className="text-xs text-blue-500 dark:text-blue-400">
                                    {tokenInfo.email}
                                </p>
                            </div>

                            <p className="text-sm text-gray-600 dark:text-gray-300 leading-relaxed">
                                Stabiliți o parolă personală. Aceasta trebuie să respecte politica
                                de securitate a platformei (minim 12 caractere, majusculă,
                                cifră și caracter special).
                            </p>

                            {/* Parolă nouă */}
                            <div className="space-y-1.5">
                                <label className="block text-xs font-semibold text-gray-600
                                                  dark:text-gray-300 uppercase tracking-wide">
                                    Parolă nouă
                                </label>
                                <div className="relative">
                                    <input
                                        type={showNew ? 'text' : 'password'}
                                        value={newPassword}
                                        onChange={e => setNewPassword(e.target.value)}
                                        required
                                        autoComplete="new-password"
                                        placeholder="Minim 12 caractere"
                                        className="w-full px-3.5 py-2.5 pr-11 rounded-xl text-sm
                                                   border border-gray-200 dark:border-gray-700
                                                   bg-white dark:bg-gray-800
                                                   text-gray-900 dark:text-gray-100
                                                   placeholder:text-gray-400 dark:placeholder:text-gray-600
                                                   focus:outline-none focus:ring-2 focus:ring-blue-900
                                                   dark:focus:ring-blue-500 focus:border-transparent transition"
                                    />
                                    <button
                                        type="button"
                                        tabIndex={-1}
                                        onClick={() => setShowNew(v => !v)}
                                        className="absolute right-3 top-1/2 -translate-y-1/2
                                                   text-gray-400 hover:text-gray-600
                                                   dark:text-gray-500 dark:hover:text-gray-300"
                                    >
                                        {showNew
                                            ? <EyeOff className="w-4 h-4" />
                                            : <Eye className="w-4 h-4" />}
                                    </button>
                                </div>

                                {/* Indicator forță */}
                                {newPassword.length > 0 && (
                                    <div className="space-y-1 pt-0.5">
                                        <div className="flex gap-1">
                                            {([1, 2, 3, 4] as const).map(i => (
                                                <div
                                                    key={i}
                                                    className={`h-1 flex-1 rounded-full transition-colors duration-300 ${
                                                        strength >= i
                                                            ? STRENGTH_COLORS[strength]
                                                            : 'bg-gray-200 dark:bg-gray-700'
                                                    }`}
                                                />
                                            ))}
                                        </div>
                                        <p className="text-xs text-gray-400 dark:text-gray-500">
                                            Forță:{' '}
                                            <span className="font-medium text-gray-600 dark:text-gray-300">
                                                {STRENGTH_LABELS[strength]}
                                            </span>
                                        </p>
                                    </div>
                                )}
                            </div>

                            {/* Confirmare parolă */}
                            <div className="space-y-1.5">
                                <label className="block text-xs font-semibold text-gray-600
                                                  dark:text-gray-300 uppercase tracking-wide">
                                    Confirmați parola
                                </label>
                                <div className="relative">
                                    <input
                                        type={showConfirm ? 'text' : 'password'}
                                        value={confirmPassword}
                                        onChange={e => setConfirmPassword(e.target.value)}
                                        required
                                        autoComplete="new-password"
                                        placeholder="Repetați parola"
                                        className="w-full px-3.5 py-2.5 pr-11 rounded-xl text-sm
                                                   border border-gray-200 dark:border-gray-700
                                                   bg-white dark:bg-gray-800
                                                   text-gray-900 dark:text-gray-100
                                                   placeholder:text-gray-400 dark:placeholder:text-gray-600
                                                   focus:outline-none focus:ring-2 focus:ring-blue-900
                                                   dark:focus:ring-blue-500 focus:border-transparent transition"
                                    />
                                    <button
                                        type="button"
                                        tabIndex={-1}
                                        onClick={() => setShowConfirm(v => !v)}
                                        className="absolute right-3 top-1/2 -translate-y-1/2
                                                   text-gray-400 hover:text-gray-600
                                                   dark:text-gray-500 dark:hover:text-gray-300"
                                    >
                                        {showConfirm
                                            ? <EyeOff className="w-4 h-4" />
                                            : <Eye className="w-4 h-4" />}
                                    </button>
                                </div>

                                {/* Indicator potrivire */}
                                {confirmPassword.length > 0 && (
                                    <p className={`text-xs font-medium ${
                                        passwordsMatch
                                            ? 'text-green-600 dark:text-green-400'
                                            : 'text-red-500 dark:text-red-400'
                                    }`}>
                                        {passwordsMatch ? '✓ Parolele coincid' : '✗ Parolele nu coincid'}
                                    </p>
                                )}
                            </div>

                            {/* Erori de validare politică */}
                            {apiErrors.length > 0 && (
                                <div className="rounded-xl bg-red-50 dark:bg-red-950 border
                                                border-red-100 dark:border-red-900 p-3.5 space-y-1">
                                    {apiErrors.map((e, i) => (
                                        <div key={i}
                                             className="flex items-start gap-2 text-red-700 dark:text-red-400 text-sm">
                                            <AlertCircle className="w-4 h-4 mt-0.5 flex-shrink-0" />
                                            <span>{e}</span>
                                        </div>
                                    ))}
                                </div>
                            )}

                            {/* Eroare generică */}
                            {apiError && apiErrors.length === 0 && (
                                <div className="flex items-start gap-2 rounded-xl bg-red-50
                                                dark:bg-red-950 border border-red-100
                                                dark:border-red-900 text-red-700
                                                dark:text-red-400 text-sm p-3.5">
                                    <AlertCircle className="w-4 h-4 mt-0.5 flex-shrink-0" />
                                    <span>{apiError}</span>
                                </div>
                            )}

                            {/* Submit */}
                            <button
                                type="submit"
                                disabled={pageState === 'submitting'}
                                className="w-full flex items-center justify-center gap-2
                                           bg-blue-900 dark:bg-blue-700 text-white
                                           py-2.5 rounded-xl text-sm font-semibold
                                           hover:bg-blue-800 dark:hover:bg-blue-600
                                           active:scale-[.98] transition-all
                                           disabled:opacity-60 disabled:cursor-not-allowed"
                            >
                                {pageState === 'submitting' && (
                                    <Loader2 className="w-4 h-4 animate-spin" />
                                )}
                                {pageState === 'submitting'
                                    ? 'Se procesează…'
                                    : 'Activează contul'}
                            </button>
                        </form>
                    )}
                </div>

                {/* Footer */}
                <p className="text-center text-xs text-gray-400 dark:text-gray-600">
                    © {new Date().getFullYear()} Ministerul Afacerilor Interne — Uz Intern Exclusiv
                </p>
            </div>
        </div>
    );
}
