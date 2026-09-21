/**
 * Poarta criptografică.
 *
 * Nicio pagină din aplicație nu se randează până când cheile utilizatorului nu
 * sunt descuiate în fila curentă. Trei stări posibile:
 *
 *   absent   → contul e nou: se generează perechile RSA (durează câteva secunde)
 *   locked   → cheile există pe server, dar nu în memoria filei: se cer o dată
 *   unlocked → aplicația funcționează normal
 *
 * Motivul pentru care se cere parola încă o dată după login: parola nu se
 * păstrează nicăieri după autentificare, iar cheia care descuie cheile private
 * se derivă exclusiv din ea, în browser. Dacă am ține parola în memorie de la
 * login, am face-o disponibilă oricărui script din pagină pe toată durata
 * sesiunii.
 */

import { useEffect, useState, type FormEvent } from 'react';
import { Outlet } from 'react-router-dom';
import { KeyRound, Loader2, ShieldCheck, ShieldAlert, LogOut, RotateCw } from 'lucide-react';
import Button from '../ui/Button';
import { useKeys } from '../../context/KeysContext';
import { useAuth } from '../../context/AuthContext';
import { apiErrorMessage } from '../../api/errors';

export default function KeysGate() {
    const { status, generate, unlock, rewrap, reload, error, rewrapRequired } = useKeys();
    const { logout, user } = useAuth();

    const [password, setPassword] = useState('');
    const [confirmPassword, setConfirmPassword] = useState('');
    const [oldPassword, setOldPassword] = useState('');
    const [busy, setBusy] = useState(false);
    const [localError, setLocalError] = useState('');

    /**
     * Modul „reîmpachetare”: cheile private sunt încuiate cu o parolă veche,
     * iar parola de azi nu le mai descuie. Se întâmplă când parola contului s-a
     * schimbat în afara aplicației - în Active Directory - sau când un cont
     * local tocmai a fost legat de domeniu.
     *
     * Serverul ne spune singur când e cazul (rewrapRequired), dar lăsăm și
     * intrarea manuală: la conturile legate de administrator, momentul
     * schimbării parolei nu e cunoscut, deci detectarea automată nu are pe ce
     * să se bazeze.
     */
    const [rewrapMode, setRewrapMode] = useState(false);

    useEffect(() => {
        if (rewrapRequired) setRewrapMode(true);
    }, [rewrapRequired]);

    if (status === 'unlocked') return <Outlet />;

    if (status === 'loading') {
        return (
            <div className="flex min-h-[60vh] items-center justify-center">
                <div className="flex items-center gap-3 text-mai-500 dark:text-mai-400 dark:text-mai-300">
                    <Loader2 size={20} className="animate-spin" />
                    <span className="text-sm">Se verifică cheile criptografice…</span>
                </div>
            </div>
        );
    }

    const isGenerating = status === 'absent';

    const handleSubmit = async (event: FormEvent) => {
        event.preventDefault();
        setLocalError('');

        if (isGenerating && password !== confirmPassword) {
            setLocalError('Cele două parole nu coincid.');
            return;
        }

        if (rewrapMode && !isGenerating && !oldPassword) {
            setLocalError('Introduceți parola veche, cea cu care au fost încuiate cheile.');
            return;
        }

        setBusy(true);
        try {
            if (isGenerating) {
                await generate(password);
            } else if (rewrapMode) {
                await rewrap(oldPassword, password);
            } else {
                await unlock(password);
            }
            setPassword('');
            setConfirmPassword('');
            setOldPassword('');
        } catch (err) {
            // apiErrorMessage citește mesajul trimis de server (ex. 403 cu parolă
            // temporară, 429 la prea multe încercări). Pentru erorile locale
            // (parolă greșită, WebCrypto indisponibil) întoarce mesajul Error.
            setLocalError(apiErrorMessage(err, 'Operația criptografică a eșuat.'));
        } finally {
            setBusy(false);
        }
    };

    if (status === 'error') {
        return (
            <div className="flex min-h-[60vh] items-center justify-center px-4">
                <div className="w-full max-w-md rounded-xl border border-red-100 dark:border-red-800 bg-white dark:bg-mai-800 p-8 text-center shadow-sm">
                    <ShieldAlert size={32} className="mx-auto mb-4 text-red-500" />
                    <p className="font-semibold text-mai-900 dark:text-white">Cheile nu au putut fi citite</p>
                    <p className="mt-2 text-sm text-mai-400 dark:text-mai-300">
                        {error ?? 'Serverul nu a răspuns.'}
                    </p>
                    <Button className="mt-6 w-full" onClick={() => void reload()}>
                        Reîncearcă
                    </Button>
                </div>
            </div>
        );
    }

    const inputClass =
        'w-full rounded-lg border border-mai-200 dark:border-mai-600 bg-white dark:bg-mai-800 dark:text-mai-100 px-3 py-2.5 text-sm ' +
        'focus:border-mai-500 focus:outline-none focus:ring-2 focus:ring-mai-500/20 ' +
        'disabled:bg-mai-50 dark:disabled:bg-mai-900 disabled:text-mai-400';

    return (
        <div className="flex min-h-[70vh] items-center justify-center px-4">
            <div className="w-full max-w-md rounded-xl border border-mai-100 dark:border-mai-700 bg-white dark:bg-mai-800 p-8 shadow-sm">

                <div className="mb-6 text-center">
                    <div className="mx-auto mb-4 flex h-14 w-14 items-center justify-center rounded-full bg-mai-50 dark:bg-mai-700">
                        {isGenerating
                            ? <KeyRound size={26} className="text-mai-600 dark:text-mai-400" />
                            : rewrapMode
                                ? <RotateCw size={26} className="text-amber-600 dark:text-amber-400" />
                                : <ShieldCheck size={26} className="text-mai-600 dark:text-mai-400" />}
                    </div>

                    <h1 className="text-xl font-bold text-mai-900 dark:text-white">
                        {isGenerating
                            ? 'Generarea cheilor criptografice'
                            : rewrapMode
                                ? 'Reîmpachetarea cheilor'
                                : 'Deblocarea cheilor'}
                    </h1>

                    <p className="mt-2 text-sm leading-relaxed text-mai-400 dark:text-mai-300">
                        {isGenerating ? (
                            <>
                                Contul dumneavoastră nu are încă chei. Se generează două perechi
                                RSA-3072 direct în browser: una pentru criptare, una pentru
                                semnătură. Cheile private nu părăsesc niciodată acest calculator
                                în formă necriptată.
                            </>
                        ) : rewrapMode ? (
                            <>
                                Parola contului s-a schimbat, dar cheile private au rămas încuiate
                                cu cea veche. Introduceți-le pe amândouă o singură dată: cheile se
                                descuie cu parola veche și se reîncuie cu cea de acum, tot în
                                browser. Cheile publice nu se schimbă, deci fișierele primite până
                                acum rămân accesibile.
                            </>
                        ) : (
                            <>
                                Cheile private sunt încuiate cu parola contului. Introduceți-o
                                pentru a le descuia în această filă. Nu se salvează nicăieri și
                                se pierd la închiderea browserului.
                            </>
                        )}
                    </p>
                </div>

                {isGenerating && (
                    <div className="mb-5 rounded-lg border border-amber-100 dark:border-amber-700/50 bg-amber-50 dark:bg-amber-900/20 px-4 py-3">
                        <p className="text-xs leading-relaxed text-amber-800 dark:text-amber-300">
                            <strong>Important.</strong> Folosiți exact parola cu care v-ați
                            autentificat. Dacă vă pierdeți parola, fișierele primite până atunci
                            devin imposibil de deschis - nici administratorul nu le poate
                            recupera, pentru că nici el nu are cheile.
                        </p>
                    </div>
                )}

                {rewrapMode && !isGenerating && (
                    <div className="mb-5 rounded-lg border border-amber-100 bg-amber-50 px-4 py-3 dark:border-amber-700/50 dark:bg-amber-900/20">
                        <p className="text-xs leading-relaxed text-amber-800 dark:text-amber-300">
                            <strong>Atenție.</strong> Dacă nu vă mai amintiți parola veche, cheile
                            nu mai pot fi descuiate de nimeni - nici de administrator. Fișierele
                            primite până acum vor trebui retrimise de expeditori, după ce
                            administratorul vă resetează cheile.
                        </p>
                    </div>
                )}

                <form onSubmit={handleSubmit} className="space-y-4">
                    {rewrapMode && !isGenerating && (
                        <div>
                            <label htmlFor="keys-old-password" className="mb-1.5 block text-sm font-medium text-mai-700 dark:text-mai-200">
                                Parola veche (cea cu care au fost încuiate cheile)
                            </label>
                            <input
                                id="keys-old-password"
                                type="password"
                                autoComplete="off"
                                required
                                disabled={busy}
                                value={oldPassword}
                                onChange={(e) => setOldPassword(e.target.value)}
                                className={inputClass}
                            />
                        </div>
                    )}

                    <div>
                        <label htmlFor="keys-password" className="mb-1.5 block text-sm font-medium text-mai-700 dark:text-mai-200">
                            {rewrapMode && !isGenerating ? 'Parola actuală' : 'Parola contului'}
                            {user?.username ? ` (@${user.username})` : ''}
                            {user?.isDirectoryAccount ? ' - cont de domeniu' : ''}
                        </label>
                        <input
                            id="keys-password"
                            type="password"
                            autoComplete="current-password"
                            autoFocus
                            required
                            disabled={busy}
                            value={password}
                            onChange={(e) => setPassword(e.target.value)}
                            className={inputClass}
                        />
                    </div>

                    {isGenerating && (
                        <div>
                            <label htmlFor="keys-password-confirm" className="mb-1.5 block text-sm font-medium text-mai-700 dark:text-mai-200">
                                Confirmați parola
                            </label>
                            <input
                                id="keys-password-confirm"
                                type="password"
                                autoComplete="current-password"
                                required
                                disabled={busy}
                                value={confirmPassword}
                                onChange={(e) => setConfirmPassword(e.target.value)}
                                className={inputClass}
                            />
                        </div>
                    )}

                    {(localError || error) && (
                        <p className="rounded-lg bg-red-50 dark:bg-red-900/30 px-3 py-2 text-sm text-red-700 dark:text-red-400">
                            {localError || error}
                        </p>
                    )}

                    <Button type="submit" disabled={busy || !password} className="w-full">
                        {busy ? (
                            <>
                                <Loader2 size={16} className="animate-spin" />
                                {isGenerating
                                    ? 'Se generează cheile…'
                                    : rewrapMode ? 'Se reîmpachetează…' : 'Se descuie…'}
                            </>
                        ) : (
                            <>
                                {isGenerating
                                    ? 'Generează cheile'
                                    : rewrapMode ? 'Reîmpachetează cheile' : 'Deblochează'}
                            </>
                        )}
                    </Button>

                    {busy && isGenerating && (
                        <p className="text-center text-xs text-mai-400 dark:text-mai-300">
                            Generarea RSA-3072 durează câteva secunde. Nu închideți fila.
                        </p>
                    )}
                </form>

                {!isGenerating && (
                    <button
                        type="button"
                        disabled={busy}
                        onClick={() => {
                            setRewrapMode(!rewrapMode);
                            setLocalError('');
                        }}
                        className="mt-5 w-full text-center text-xs text-mai-500 underline-offset-2
                                   transition-colors hover:text-mai-700 hover:underline
                                   disabled:opacity-50 dark:text-mai-400 dark:hover:text-mai-200"
                    >
                        {rewrapMode
                            ? 'Înapoi la deblocarea obișnuită'
                            : 'Parola a fost schimbată în afara aplicației (Active Directory)?'}
                    </button>
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