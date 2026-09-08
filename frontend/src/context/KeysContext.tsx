/**
 * Contextul cheilor criptografice.
 *
 * Ține cheile private DESCUIATE, dar exclusiv în memoria filei de browser:
 * niciun localStorage, niciun sessionStorage, niciun cookie. La refresh, la
 * închiderea tabului sau la delogare dispar și trebuie descuiate din nou cu
 * parola. Asta e intenționat — o cheie privată persistată în localStorage ar fi
 * citibilă de orice XSS și ar anula garanția end-to-end.
 *
 * Cheile sunt importate în WebCrypto ca NON-EXTRACTABLE: nici măcar codul
 * aplicației nu le mai poate exporta după import. Poate doar să le folosească
 * pentru decriptare și semnare.
 */

import {
    createContext,
    useCallback,
    useContext,
    useEffect,
    useMemo,
    useRef,
    useState,
    type ReactNode,
} from 'react';
import api from '../api/client';
import { useAuth } from './AuthContext';
import {
    generateKeyBundle,
    unlockKeys,
    importEncryptionPublicKey,
    keyFingerprint,
    type PublishedKeyBundle,
    type UnlockedKeys,
} from '../crypto/E2ee';

/**
 * loading  — se interoghează serverul
 * absent   — contul nu are încă chei; trebuie generate
 * locked   — cheile există pe server, dar nu sunt descuiate în fila asta
 * unlocked — gata de lucru
 * error    — serverul nu a răspuns
 */
export type KeysStatus = 'loading' | 'absent' | 'locked' | 'unlocked' | 'error';

interface ServerBundle extends PublishedKeyBundle {
    hasKeys: boolean;
    fingerprint?: string;
    keysCreatedAt?: string;
}

interface KeysContextValue {
    status: KeysStatus;
    /** Amprenta cheii publice proprii, de comparat în afara aplicației. */
    fingerprint: string;
    /** Cheile private descuiate. Null cât timp status !== 'unlocked'. */
    keys: UnlockedKeys | null;
    /** Propria cheie publică de criptare — necesară ca să-ți poți deschide fișierele trimise. */
    myEncryptionPublicKey: CryptoKey | null;
    error: string | null;

    generate: (password: string) => Promise<void>;
    unlock: (password: string) => Promise<void>;
    lock: () => void;
    reload: () => Promise<void>;
}

const KeysContext = createContext<KeysContextValue | undefined>(undefined);

export function KeysProvider({ children }: { children: ReactNode }) {
    const { user, isAuthenticated } = useAuth();

    const [status, setStatus] = useState<KeysStatus>('loading');
    const [error, setError] = useState<string | null>(null);
    const [fingerprint, setFingerprint] = useState('');
    const [keys, setKeys] = useState<UnlockedKeys | null>(null);
    const [myEncryptionPublicKey, setMyEncryptionPublicKey] = useState<CryptoKey | null>(null);

    // Pachetul criptat, ca să nu-l cerem din nou de la server la fiecare încercare
    // de descuiere. Nu conține nimic exploatabil fără parolă.
    const bundleRef = useRef<ServerBundle | null>(null);

    const lock = useCallback(() => {
        bundleRef.current = null;
        setKeys(null);
        setMyEncryptionPublicKey(null);
        setFingerprint('');
        setStatus('locked');
    }, []);

    const reload = useCallback(async () => {
        if (!isAuthenticated) {
            setStatus('locked');
            return;
        }

        setStatus('loading');
        setError(null);

        try {
            const { data } = await api.get<ServerBundle>('/Keys/me');

            if (!data.hasKeys) {
                bundleRef.current = null;
                setStatus('absent');
                return;
            }

            bundleRef.current = data;
            setFingerprint(data.fingerprint ?? (await keyFingerprint(data.publicKeyEncryption)));
            setStatus('locked');
        } catch (err) {
            console.error('Nu s-a putut citi pachetul de chei:', err);
            setError('Serverul nu a putut fi contactat pentru pachetul de chei.');
            setStatus('error');
        }
    }, [isAuthenticated]);

    // La schimbarea contului (login, logout, alt utilizator) se ia totul de la zero.
    useEffect(() => {
        if (!isAuthenticated) {
            bundleRef.current = null;
            setKeys(null);
            setMyEncryptionPublicKey(null);
            setFingerprint('');
            setStatus('locked');
            return;
        }
        void reload();
    }, [isAuthenticated, user?.id, reload]);

    // ── Generare, la prima autentificare ─────────────────────────────────────

    const generate = useCallback(async (password: string) => {
        setError(null);

        // Parola se verifică ÎNTÂI pe server. Dacă utilizatorul tastează greșit,
        // cheile s-ar încuia cu o parolă inexistentă și ar deveni imposibil de
        // descuiat la următoarea autentificare — pierdere permanentă și tăcută.
        const { data: check } = await api.post<{ valid: boolean }>(
            '/Keys/verify-password',
            { password }
        );

        if (!check.valid) {
            throw new Error('Parola nu corespunde contului.');
        }

        // Câteva secunde de calcul: RSA-3072 caută numere prime mari.
        const bundle = await generateKeyBundle(password);

        await api.post('/Keys', bundle);

        const unlocked = await unlockKeys(password, bundle);
        const publicKey = await importEncryptionPublicKey(bundle.publicKeyEncryption);

        bundleRef.current = { ...bundle, hasKeys: true };
        setKeys(unlocked);
        setMyEncryptionPublicKey(publicKey);
        setFingerprint(await keyFingerprint(bundle.publicKeyEncryption));
        setStatus('unlocked');
    }, []);

    // ── Descuiere ────────────────────────────────────────────────────────────

    const unlock = useCallback(async (password: string) => {
        setError(null);

        let bundle = bundleRef.current;
        if (!bundle) {
            const { data } = await api.get<ServerBundle>('/Keys/me');
            if (!data.hasKeys) {
                setStatus('absent');
                throw new Error('Contul nu are chei înregistrate.');
            }
            bundle = data;
            bundleRef.current = data;
        }

        // Aruncă dacă parola e greșită: tagul AES-GCM nu se verifică, deci
        // decriptarea eșuează în loc să producă chei aleatorii.
        const unlocked = await unlockKeys(password, bundle);
        const publicKey = await importEncryptionPublicKey(bundle.publicKeyEncryption);

        setKeys(unlocked);
        setMyEncryptionPublicKey(publicKey);
        setFingerprint(bundle.fingerprint ?? (await keyFingerprint(bundle.publicKeyEncryption)));
        setStatus('unlocked');
    }, []);

    const value = useMemo<KeysContextValue>(
        () => ({
            status,
            fingerprint,
            keys,
            myEncryptionPublicKey,
            error,
            generate,
            unlock,
            lock,
            reload,
        }),
        [status, fingerprint, keys, myEncryptionPublicKey, error, generate, unlock, lock, reload]
    );

    return <KeysContext.Provider value={value}>{children}</KeysContext.Provider>;
}

export function useKeys() {
    const context = useContext(KeysContext);
    if (!context) throw new Error('useKeys trebuie folosit în interiorul KeysProvider');
    return context;
}