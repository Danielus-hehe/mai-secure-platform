/**
 * Schimbarea obligatorie a parolei.
 *
 * Apare când parola contului a fost stabilită de un administrator: la crearea
 * contului sau după o resetare. Până la schimbare, nicio altă pagină nu se
 * randează - nici poarta cheilor.
 *
 * Motivul ține de criptarea end-to-end. Cheile private se încuie cu o cheie
 * derivată din parolă. Dacă utilizatorul și-ar genera cheile cu parola primită
 * de la administrator, acesta le-ar putea descuia din baza de date oricând,
 * fără ca amprenta sau jurnalul să arate ceva. Serverul refuză oricum
 * înregistrarea cheilor cât timp parola e temporară; ecranul de aici e partea
 * vizibilă a aceleiași reguli.
 *
 * După schimbare, serverul închide toate sesiunile, deci utilizatorul se
 * autentifică din nou, de data asta cu parola proprie.
 */

import { useState, type FormEvent } from 'react';
import { Outlet } from 'react-router-dom';
import { LockKeyhole, Loader2, LogOut, RefreshCw } from 'lucide-react';
import Button from '../ui/Button';
import api from '../../api/client';
import { apiErrorMessage } from '../../api/errors';
import { useAuth } from '../../context/AuthContext';
import { useToast } from '../../context/ToastContext';
import { rewrapKeysForNewPassword, type RewrapPayload } from '../../crypto/passwordChange';
import type { PublishedKeyBundle } from '../../crypto/E2ee';

interface ServerBundle extends PublishedKeyBundle {
    hasKeys: boolean;
}

const MIN_LENGTH = 12;

export default function PasswordChangeGate() {
    const { user, logout } = useAuth();
    const toast = useToast();

    const [currentPassword, setCurrentPassword] = useState('');
    const [newPassword, setNewPassword] = useState('');
    const [confirmPassword, setConfirmPassword] = useState('');
    const [busy, setBusy] = useState(false);
    const [error, setError] = useState('');

    /**
     * Cazul rar în care contul are deja chei (flag setat manual, de exemplu).
     * Dacă parola s-a schimbat, dar reîmpachetarea cheilor a eșuat, pachetul
     * calculat rămâne aici pentru reîncercare. Fără el, cheile ar rămâne
     * încuiate cu o parolă care nu mai există.
     */
    const [pendingRewrap, setPendingRewrap] =
        useState<{ payload: RewrapPayload; password: string } | null>(null);

    if (!user?.mustChangePassword) return <Outlet />;

    const finish = async () => {
        toast.success('Parola a fost schimbată. Autentificați-vă cu parola nouă.');
        await logout();
    };

    const syncKeys = async (payload: RewrapPayload, password: string) => {
        await api.patch('/Keys/rewrap', { ...payload, currentPassword: password });
        setPendingRewrap(null);
    };

    const handleRetrySync = async () => {
        if (!pendingRewrap) return;
        setBusy(true);
        setError('');
        try {
            await syncKeys(pendingRewrap.payload, pendingRewrap.password);
            await finish();
        } catch (err) {
            setError(apiErrorMessage(err, 'Sincronizarea cheilor a eșuat din nou.'));
        } finally {
            setBusy(false);
        }
    };

    const handleSubmit = async (event: FormEvent) => {
        event.preventDefault();
        setError('');

        if (newPassword.length < MIN_LENGTH) {
            setError(`Parola nouă trebuie să aibă minim ${MIN_LENGTH} caractere.`);
            return;
        }
        if (newPassword !== confirmPassword) {
            setError('Parolele noi nu coincid.');
            return;
        }
        if (newPassword === currentPassword) {
            setError('Parola nouă trebuie să fie diferită de cea primită de la administrator.');
            return;
        }

        setBusy(true);
        try {
            const { data: bundle } = await api.get<ServerBundle>('/Keys/me');

            // Reîmpachetarea se calculează ÎNAINTE de schimbarea parolei pe server:
            // dacă parola curentă e greșită, aflăm local, fără să fi modificat nimic.
            const payload = bundle.hasKeys
                ? await rewrapKeysForNewPassword(currentPassword, newPassword, bundle)
                : null;

            await api.patch('/Auth/change-password', { currentPassword, newPassword });

            if (payload) {
                try {
                    await syncKeys(payload, newPassword);
                } catch (syncError) {
                    setPendingRewrap({ payload, password: newPassword });
                    setError(
                        'Parola a fost schimbată, dar cheile nu s-au sincronizat. ' +
                        'Nu închideți pagina și apăsați „Reîncearcă sincronizarea”.'
                    );
                    console.error('Rewrap esuat dupa schimbarea parolei:', syncError);
                    return;
                }
            }

            setCurrentPassword('');
            setNewPassword('');
            setConfirmPassword('');
            await finish();
        } catch (err) {
            setError(apiErrorMessage(err, 'Parola nu a putut fi schimbată.'));
        } finally {
            setBusy(false);
        }
    };

    const inputClass =
        'w-full rounded-lg border border-mai-200 dark:border-mai-600 bg-white dark:bg-mai-800 dark:text-mai-100 px-3 py-2.5 text-sm ' +
        'focus:border-mai-500 focus:outline-none focus:ring-2 focus:ring-mai-500/20 ' +
        'disabled:bg-mai-50 dark:disabled:bg-mai-900 disabled:text-mai-400';

    const labelClass = 'mb-1.5 block text-sm font-medium text-mai-700 dark:text-mai-200';

    return (
        <div className="flex min-h-[70vh] items-center justify-center px-4">
            <div className="w-full max-w-md rounded-xl border border-mai-100 dark:border-mai-700 bg-white dark:bg-mai-800 p-8 shadow-sm">

                <div className="mb-6 text-center">
                    <div className="mx-auto mb-4 flex h-14 w-14 items-center justify-center rounded-full bg-mai-50 dark:bg-mai-700">
                        <LockKeyhole size={26} className="text-mai-600 dark:text-mai-400" />
                    </div>

                    <h1 className="text-xl font-bold text-mai-900 dark:text-white">
                        Alegeți o parolă nouă
                    </h1>

                    <p className="mt-2 text-sm leading-relaxed text-mai-400 dark:text-mai-300">
                        Parola cu care v-ați autentificat a fost stabilită de administrator.
                        Înainte de a continua, alegeți una pe care o cunoașteți doar
                        dumneavoastră. Cu ea se vor proteja și cheile de criptare ale contului.
                    </p>
                </div>

                {pendingRewrap ? (
                    <div className="space-y-4">
                        <p className="rounded-lg bg-red-50 dark:bg-red-900/30 px-3 py-2 text-sm text-red-700 dark:text-red-400">
                            {error}
                        </p>
                        <Button onClick={() => void handleRetrySync()} disabled={busy} className="w-full">
                            {busy
                                ? <><Loader2 size={16} className="animate-spin" /> Se sincronizează…</>
                                : <><RefreshCw size={16} /> Reîncearcă sincronizarea</>}
                        </Button>
                    </div>
                ) : (
                    <form onSubmit={handleSubmit} className="space-y-4">
                        <div>
                            <label htmlFor="pwd-current" className={labelClass}>
                                Parola primită de la administrator
                            </label>
                            <input
                                id="pwd-current"
                                type="password"
                                autoComplete="current-password"
                                autoFocus
                                required
                                disabled={busy}
                                value={currentPassword}
                                onChange={(e) => setCurrentPassword(e.target.value)}
                                className={inputClass}
                            />
                        </div>

                        <div>
                            <label htmlFor="pwd-new" className={labelClass}>
                                Parola nouă
                            </label>
                            <input
                                id="pwd-new"
                                type="password"
                                autoComplete="new-password"
                                required
                                disabled={busy}
                                value={newPassword}
                                onChange={(e) => setNewPassword(e.target.value)}
                                className={inputClass}
                                aria-describedby="pwd-new-hint"
                            />
                            <p id="pwd-new-hint" className="mt-1.5 text-xs text-mai-400 dark:text-mai-400">
                                Minim {MIN_LENGTH} caractere, cu majuscule, minuscule, cifre și un simbol.
                            </p>
                        </div>

                        <div>
                            <label htmlFor="pwd-confirm" className={labelClass}>
                                Confirmați parola nouă
                            </label>
                            <input
                                id="pwd-confirm"
                                type="password"
                                autoComplete="new-password"
                                required
                                disabled={busy}
                                value={confirmPassword}
                                onChange={(e) => setConfirmPassword(e.target.value)}
                                className={inputClass}
                            />
                        </div>

                        {error && (
                            <p role="alert" className="rounded-lg bg-red-50 dark:bg-red-900/30 px-3 py-2 text-sm text-red-700 dark:text-red-400">
                                {error}
                            </p>
                        )}

                        <Button
                            type="submit"
                            disabled={busy || !currentPassword || !newPassword || !confirmPassword}
                            className="w-full"
                        >
                            {busy
                                ? <><Loader2 size={16} className="animate-spin" /> Se schimbă parola…</>
                                : 'Schimbă parola'}
                        </Button>
                    </form>
                )}

                <button
                    type="button"
                    onClick={() => void logout()}
                    className="mt-6 flex w-full items-center justify-center gap-2 text-xs
                               text-mai-400 dark:text-mai-500 transition-colors hover:text-mai-700 dark:hover:text-mai-200"
                >
                    <LogOut size={13} />
                    Deconectare
                </button>
            </div>
        </div>
    );
}
