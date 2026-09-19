import { useCallback, useEffect, useState } from 'react';
import {
    ShieldCheck, ShieldOff, Smartphone, KeyRound, Copy, Check,
    AlertTriangle, Loader2, RefreshCw,
} from 'lucide-react';
import axios from 'axios';

import Button from '../ui/Button';
import Input from '../ui/Input';
import { useToast } from '../../context/ToastContext';
import { apiErrorMessage } from '../../api/errors';
import {
    getTwoFactorStatus,
    startTwoFactorSetup,
    enableTwoFactor,
    cancelTwoFactorSetup,
    disableTwoFactor,
    regenerateRecoveryCodes,
    type TwoFactorStatus,
    type TwoFactorSetup,
} from '../../api/twoFactor';

/**
 * Cardul de autentificare in doi pasi din pagina de profil.
 *
 * Se monteaza cu o singura linie in ProfilePage:
 *
 *   import TwoFactorSettings from '../../components/security/TwoFactorSettings';
 *   ...
 *   <TwoFactorSettings />
 *
 * Complet autonom: isi citeste singur starea, nu primeste props si nu depinde de
 * restul paginii. 2FA este OPTIONAL - pana cand utilizatorul apasa butonul, nu se
 * schimba nimic la contul lui.
 *
 * Codul QR se deseneaza cu `qrcode.react`:
 *
 *   npm install qrcode.react
 *
 * Daca preferi sa nu adaugi dependinta, sterge importul si blocul <QRCodeSVG>;
 * secretul afisat manual, in grupuri de 4, ramane suficient pentru inrolare in
 * orice aplicatie de autentificare.
 */
import { QRCodeSVG } from 'qrcode.react';

type Stage = 'idle' | 'password' | 'scan' | 'codes' | 'disable';

export default function TwoFactorSettings() {
    const toast = useToast();

    const [status, setStatus]   = useState<TwoFactorStatus | null>(null);
    const [loading, setLoading] = useState(true);
    const [busy, setBusy]       = useState(false);
    const [stage, setStage]     = useState<Stage>('idle');

    const [password, setPassword]   = useState('');
    const [code, setCode]           = useState('');
    const [setup, setSetup]         = useState<TwoFactorSetup | null>(null);
    const [recovery, setRecovery]   = useState<string[]>([]);
    const [copied, setCopied]       = useState(false);
    const [showSecret, setShowSecret] = useState(false);

    const loadStatus = useCallback(async (signal?: AbortSignal) => {
        setLoading(true);
        try {
            const data = await getTwoFactorStatus(signal);
            setStatus(data);

            // O inrolare abandonata la refresh nu trebuie sa blocheze o incercare
            // noua: o anulam pe tacute, ca utilizatorul sa porneasca de la zero.
            if (data.setupInProgress) {
                await cancelTwoFactorSetup().catch(() => undefined);
            }
        } catch (e) {
            if (axios.isCancel(e) || signal?.aborted) return;
            toast.error(apiErrorMessage(e, 'Starea 2FA nu a putut fi citită.'));
        } finally {
            if (!signal?.aborted) setLoading(false);
        }
        // toast e stabil in ToastContext; il lasam in afara listei ca sa nu reincarcam la fiecare render
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, []);

    useEffect(() => {
        const controller = new AbortController();
        loadStatus(controller.signal);
        return () => controller.abort();
    }, [loadStatus]);

    const reset = () => {
        setStage('idle');
        setPassword('');
        setCode('');
        setSetup(null);
        setShowSecret(false);
    };

    // ── Pasul 1: parola → secret ─────────────────────────────────────────────
    const handleStartSetup = async () => {
        setBusy(true);
        try {
            setSetup(await startTwoFactorSetup(password));
            setPassword('');
            setStage('scan');
        } catch (e) {
            toast.error(apiErrorMessage(e, 'Configurarea nu a putut fi inițiată.'));
        } finally {
            setBusy(false);
        }
    };

    // ── Pasul 2: cod → activare ──────────────────────────────────────────────
    const handleEnable = async () => {
        setBusy(true);
        try {
            const result = await enableTwoFactor(code);
            setRecovery(result.recoveryCodes);
            setCode('');
            setSetup(null);
            setStage('codes');
            await loadStatus();
            toast.success('Autentificarea în doi pași este activă.');
        } catch (e) {
            toast.error(apiErrorMessage(e, 'Codul nu a putut fi verificat.'));
        } finally {
            setBusy(false);
        }
    };

    const handleCancelSetup = async () => {
        try { await cancelTwoFactorSetup(); } catch { /* starea locala se reseteaza oricum */ }
        reset();
        await loadStatus();
    };

    // ── Dezactivare ──────────────────────────────────────────────────────────
    const handleDisable = async () => {
        setBusy(true);
        try {
            await disableTwoFactor(password, code);
            reset();
            setRecovery([]);
            await loadStatus();
            toast.success('Autentificarea în doi pași a fost dezactivată.');
        } catch (e) {
            toast.error(apiErrorMessage(e, 'Dezactivarea a eșuat.'));
        } finally {
            setBusy(false);
        }
    };

    // ── Regenerare coduri ────────────────────────────────────────────────────
    const handleRegenerate = async () => {
        setBusy(true);
        try {
            const result = await regenerateRecoveryCodes(password, code);
            setRecovery(result.recoveryCodes);
            setPassword('');
            setCode('');
            setStage('codes');
            await loadStatus();
        } catch (e) {
            toast.error(apiErrorMessage(e, 'Codurile nu au putut fi regenerate.'));
        } finally {
            setBusy(false);
        }
    };

    const copyCodes = async () => {
        try {
            await navigator.clipboard.writeText(recovery.join('\n'));
            setCopied(true);
            window.setTimeout(() => setCopied(false), 2000);
        } catch {
            toast.error('Copierea a eșuat. Selectați codurile manual.');
        }
    };

    // ── Randare ──────────────────────────────────────────────────────────────

    if (loading) {
        return (
            <div className="bg-white dark:bg-mai-800 rounded-2xl shadow-card dark:shadow-none border border-mai-100 dark:border-mai-700 p-6 flex items-center gap-3">
                <Loader2 size={18} className="animate-spin text-mai-400" />
                <span className="text-sm text-mai-500 dark:text-mai-400 dark:text-mai-300">Se verifică setările de securitate…</span>
            </div>
        );
    }

    const enabled = status?.enabled ?? false;

    return (
        <div className="bg-white dark:bg-mai-800 rounded-2xl shadow-card dark:shadow-none border border-mai-100 dark:border-mai-700 p-6 space-y-5">

            {/* Antet */}
            <div className="flex items-start justify-between gap-4">
                <div className="flex items-start gap-3">
                    <div className={`w-10 h-10 rounded-xl flex items-center justify-center shrink-0
                        ${enabled ? 'bg-emerald-50 dark:bg-emerald-900/40 text-emerald-600 dark:text-emerald-400' : 'bg-mai-50 dark:bg-mai-700 text-mai-500 dark:text-mai-300'}`}>
                        {enabled ? <ShieldCheck size={20} /> : <ShieldOff size={20} />}
                    </div>
                    <div>
                        <h2 className="text-lg font-bold text-mai-900 dark:text-white">Autentificare în doi pași</h2>
                        <p className="text-xs text-mai-500 dark:text-mai-400 mt-0.5">
                            Un cod generat pe telefon, cerut după parolă. Opțional.
                        </p>
                    </div>
                </div>

                <span className={`px-2.5 py-1 rounded-full text-xs font-medium shrink-0
                    ${enabled ? 'bg-emerald-50 dark:bg-emerald-900/40 text-emerald-700 dark:text-emerald-400' : 'bg-mai-100 dark:bg-mai-700 text-mai-600 dark:text-mai-300'}`}>
                    {enabled ? 'Activă' : 'Inactivă'}
                </span>
            </div>

            {/* ─── Starea inactivă ─── */}
            {!enabled && stage === 'idle' && (
                <>
                    <p className="text-sm text-mai-600 leading-relaxed">
                        Cu 2FA activ, cineva care vă află parola tot nu poate intra în cont fără
                        telefonul dumneavoastră. Funcționează cu Google Authenticator, Aegis,
                        FreeOTP, Microsoft Authenticator sau 1Password.
                    </p>

                    {status?.recommended && (
                        <p className="text-xs text-gold-600 dark:text-gold-400 bg-gold-500/10 dark:bg-gold-500/15 rounded-lg p-3">
                            Contul dumneavoastră are drepturi extinse. Activarea este recomandată,
                            dar rămâne la alegerea dumneavoastră.
                        </p>
                    )}

                    <Button onClick={() => setStage('password')} className="flex items-center gap-2">
                        <Smartphone size={15} /> Activează
                    </Button>
                </>
            )}

            {/* ─── Pasul 1: parola ─── */}
            {stage === 'password' && (
                <div className="space-y-4">
                    <p className="text-sm text-mai-600 dark:text-mai-300">
                        Confirmați parola. Fără această verificare, cineva care găsește
                        calculatorul deblocat ar putea înrola propriul telefon pe contul dumneavoastră.
                    </p>

                    <Input
                        id="tfa-password" label="Parola curentă" type="password"
                        value={password} onChange={e => setPassword(e.target.value)}
                        autoComplete="current-password" placeholder="••••••••"
                    />

                    <div className="flex gap-2">
                        <Button onClick={handleStartSetup} disabled={busy || !password}>
                            {busy ? 'Se verifică…' : 'Continuă'}
                        </Button>
                        <Button variant="secondary" onClick={reset} disabled={busy}>Renunță</Button>
                    </div>
                </div>
            )}

            {/* ─── Pasul 2: scanare + confirmare ─── */}
            {stage === 'scan' && setup && (
                <div className="space-y-4">
                    <p className="text-sm text-mai-600 dark:text-mai-300">
                        Scanați codul cu aplicația de autentificare, apoi introduceți codul de
                        {' '}{setup.digits} cifre pe care îl afișează.
                    </p>

                    <div className="flex flex-col sm:flex-row gap-5 items-start">
                        <div className="p-4 bg-white border border-mai-200 dark:border-mai-600 rounded-xl shrink-0 mx-auto sm:mx-0">
                            <QRCodeSVG value={setup.otpauthUri} size={168} level="M" />
                        </div>

                        <div className="flex-1 space-y-3 min-w-0">
                            <div>
                                <p className="text-xs text-mai-500 mb-1">Cont</p>
                                <p className="text-sm font-medium text-mai-900 truncate">
                                    {setup.issuer} · {setup.account}
                                </p>
                            </div>

                            <div>
                                <button
                                    type="button"
                                    onClick={() => setShowSecret(v => !v)}
                                    className="text-xs text-mai-600 dark:text-mai-300 hover:text-mai-800 dark:hover:text-white font-medium"
                                >
                                    {showSecret ? 'Ascunde cheia' : 'Nu pot scana codul QR'}
                                </button>

                                {showSecret && (
                                    <div className="mt-2">
                                        <p className="text-xs text-mai-500 mb-1">
                                            Introduceți manual această cheie, tip „time-based”:
                                        </p>
                                        <code className="block text-sm font-mono bg-mai-50 dark:bg-mai-900 rounded-lg p-2.5 break-all text-mai-900 dark:text-white">
                                            {setup.secretFormatted}
                                        </code>
                                    </div>
                                )}
                            </div>

                            <Input
                                id="tfa-code" label="Cod din aplicație"
                                value={code} onChange={e => setCode(e.target.value)}
                                placeholder="000000" inputMode="numeric" autoComplete="one-time-code"
                                className="tracking-[0.3em] text-center"
                            />
                        </div>
                    </div>

                    <p className="text-xs text-mai-500 bg-mai-50 rounded-lg p-3">
                        Activarea vă deconectează de pe celelalte dispozitive. Nimic nu se schimbă
                        până nu introduceți un cod valid - dacă renunțați acum, contul rămâne exact
                        cum era.
                    </p>

                    <div className="flex gap-2">
                        <Button onClick={handleEnable} disabled={busy || code.trim().length < setup.digits}>
                            {busy ? 'Se activează…' : 'Confirmă și activează'}
                        </Button>
                        <Button variant="secondary" onClick={handleCancelSetup} disabled={busy}>Renunță</Button>
                    </div>
                </div>
            )}

            {/* ─── Codurile de recuperare ─── */}
            {stage === 'codes' && recovery.length > 0 && (
                <div className="space-y-4">
                    <div className="flex items-start gap-3 p-3 rounded-xl bg-gold-500/10">
                        <AlertTriangle size={16} className="text-gold-600 mt-0.5 shrink-0" />
                        <p className="text-xs text-gold-700 dark:text-gold-300 leading-relaxed">
                            Salvați aceste coduri acum, într-un loc sigur, în afara telefonului.
                            <strong> Nu vor mai fi afișate niciodată.</strong> Fiecare cod
                            funcționează o singură dată și este singura cale de intrare dacă
                            pierdeți telefonul.
                        </p>
                    </div>

                    <div className="grid grid-cols-2 gap-2">
                        {recovery.map(c => (
                            <code key={c} className="text-sm font-mono bg-mai-50 dark:bg-mai-900 rounded-lg px-3 py-2 text-center text-mai-900 dark:text-white">
                                {c}
                            </code>
                        ))}
                    </div>

                    <div className="flex gap-2">
                        <Button variant="secondary" onClick={copyCodes} className="flex items-center gap-2">
                            {copied ? <Check size={15} /> : <Copy size={15} />}
                            {copied ? 'Copiate' : 'Copiază toate'}
                        </Button>
                        <Button onClick={() => { setRecovery([]); reset(); }}>
                            Le-am salvat
                        </Button>
                    </div>
                </div>
            )}

            {/* ─── Starea activă ─── */}
            {enabled && stage === 'idle' && (
                <>
                    <div className="space-y-2 text-sm">
                        <div className="flex items-center justify-between">
                            <span className="text-mai-500 dark:text-mai-400 dark:text-mai-300">Activată la</span>
                            <span className="font-medium text-mai-900 dark:text-white">
                                {status?.enrolledAt
                                    ? new Date(status.enrolledAt).toLocaleString('ro-RO', {
                                        dateStyle: 'short', timeStyle: 'short',
                                    })
                                    : '-'}
                            </span>
                        </div>
                        <div className="flex items-center justify-between">
                            <span className="text-mai-500 dark:text-mai-400 dark:text-mai-300">Coduri de recuperare rămase</span>
                            <span className={`font-medium ${
                                (status?.remainingRecoveryCodes ?? 0) <= 2 ? 'text-gold-600' : 'text-mai-900'
                            }`}>
                                {status?.remainingRecoveryCodes ?? 0}
                            </span>
                        </div>
                    </div>

                    {(status?.remainingRecoveryCodes ?? 0) <= 2 && (
                        <p className="text-xs text-gold-600 dark:text-gold-400 bg-gold-500/10 dark:bg-gold-500/15 rounded-lg p-3">
                            Vă rămân puține coduri de recuperare. Regenerați-le cât timp mai aveți
                            acces la aplicația de autentificare.
                        </p>
                    )}

                    <div className="flex flex-wrap gap-2">
                        <Button
                            variant="secondary"
                            onClick={() => setStage('disable')}
                            className="flex items-center gap-2"
                        >
                            <RefreshCw size={15} /> Regenerează codurile
                        </Button>
                        <Button variant="danger" onClick={() => setStage('disable')}>
                            Dezactivează
                        </Button>
                    </div>
                </>
            )}

            {/* ─── Dezactivare / regenerare: parolă + cod ─── */}
            {stage === 'disable' && (
                <div className="space-y-4">
                    <p className="text-sm text-mai-600 dark:text-mai-300">
                        Sunt necesare <strong>și parola, și un cod valid</strong>. Dacă ar fi
                        suficientă parola, cineva care v-o află ar putea pur și simplu să oprească
                        al doilea factor - iar 2FA nu ar mai apăra de nimic.
                    </p>

                    <Input
                        id="tfa-dis-password" label="Parola curentă" type="password"
                        value={password} onChange={e => setPassword(e.target.value)}
                        autoComplete="current-password" placeholder="••••••••"
                    />

                    <Input
                        id="tfa-dis-code" label="Cod din aplicație sau cod de recuperare"
                        value={code} onChange={e => setCode(e.target.value)}
                        placeholder="000000 sau XXXX-XXXX" autoComplete="one-time-code"
                    />

                    <div className="flex flex-wrap gap-2">
                        <Button
                            variant="secondary"
                            onClick={handleRegenerate}
                            disabled={busy || !password || !code}
                            className="flex items-center gap-2"
                        >
                            <KeyRound size={15} /> Doar regenerează codurile
                        </Button>
                        <Button variant="danger" onClick={handleDisable} disabled={busy || !password || !code}>
                            {busy ? 'Se procesează…' : 'Dezactivează 2FA'}
                        </Button>
                        <Button variant="secondary" onClick={reset} disabled={busy}>Renunță</Button>
                    </div>
                </div>
            )}
        </div>
    );
}