/**
 * Reîmpachetarea cheilor private la schimbarea parolei.
 *
 * PROBLEMA pe care o rezolvă acest modul:
 *
 * Cheile private ale utilizatorului sunt încuiate cu o cheie AES derivată
 * PBKDF2 din parola contului. Dacă utilizatorul își schimbă parola și nimeni nu
 * reîmpachetează blobul, cheile rămân încuiate cu parola VECHE - pe care nimeni
 * nu o mai știe. Rezultatul: toate fișierele primite până atunci devin
 * imposibil de deschis, definitiv, iar utilizatorul află abia la următoarea
 * autentificare.
 *
 * De ce nu se poate folosi `unlockKeys` din E2ee.ts: acolo cheile se importă
 * intenționat ca NON-EXTRACTABLE, tocmai ca să nu poată fi scoase din browser.
 * Pentru reîmpachetare avem nevoie de octeții PKCS8 bruți, deci decriptăm
 * blobul separat, îl reîncuiem imediat cu parola nouă și nu îl ținem nicăieri
 * mai mult decât durează operația.
 */

import { fromBase64, toBase64, type PublishedKeyBundle } from './E2ee';

const PBKDF2_ITERATIONS = 600_000;   // OWASP 2023 pentru PBKDF2-HMAC-SHA256
const AES_KEY_BITS = 256;
const IV_BYTES = 12;
const SALT_BYTES = 16;

/** Câmpurile trimise la PATCH /api/Keys/rewrap. Cheile publice NU se schimbă. */
export interface RewrapPayload {
    encryptedPrivateBundle: string;
    keyDerivationSalt: string;
    keyDerivationIterations: number;
    wrapIv: string;
}

async function deriveWrappingKey(
    password: string,
    salt: Uint8Array,
    iterations: number
): Promise<CryptoKey> {
    const material = await crypto.subtle.importKey(
        'raw',
        new TextEncoder().encode(password),
        'PBKDF2',
        false,
        ['deriveKey']
    );

    return crypto.subtle.deriveKey(
        { name: 'PBKDF2', salt: salt as BufferSource, iterations, hash: 'SHA-256' },
        material,
        { name: 'AES-GCM', length: AES_KEY_BITS },
        false,
        ['encrypt', 'decrypt']
    );
}

/**
 * Descuie blobul cu parola veche și îl reîncuie cu cea nouă.
 *
 * Salt și IV noi la fiecare reîmpachetare: reutilizarea unei perechi
 * (cheie, IV) în AES-GCM este o slăbiciune criptografică gravă, iar cheia se
 * schimbă oricum odată cu parola.
 *
 * Aruncă dacă parola veche e greșită - tagul GCM nu se verifică, deci
 * decriptarea eșuează în loc să producă gunoi care ar fi salvat pe server.
 */
export async function rewrapKeysForNewPassword(
    oldPassword: string,
    newPassword: string,
    bundle: PublishedKeyBundle
): Promise<RewrapPayload> {
    if (!crypto?.subtle) {
        throw new Error('WebCrypto indisponibil. Aplicația trebuie servită prin HTTPS sau de pe localhost.');
    }

    const oldSalt = fromBase64(bundle.keyDerivationSalt);
    const oldIv   = fromBase64(bundle.wrapIv);

    const oldKey = await deriveWrappingKey(
        oldPassword,
        oldSalt,
        bundle.keyDerivationIterations || PBKDF2_ITERATIONS
    );

    let plainBundle: ArrayBuffer;
    try {
        plainBundle = await crypto.subtle.decrypt(
            { name: 'AES-GCM', iv: oldIv as BufferSource },
            oldKey,
            fromBase64(bundle.encryptedPrivateBundle) as BufferSource
        );
    } catch {
        throw new Error(
            'Cheile private nu au putut fi descuiate cu parola curentă. ' +
            'Parola nu a fost schimbată.'
        );
    }

    const newSalt = crypto.getRandomValues(new Uint8Array(SALT_BYTES));
    const newIv   = crypto.getRandomValues(new Uint8Array(IV_BYTES));
    const newKey  = await deriveWrappingKey(newPassword, newSalt, PBKDF2_ITERATIONS);

    const rewrapped = await crypto.subtle.encrypt(
        { name: 'AES-GCM', iv: newIv as BufferSource },
        newKey,
        plainBundle
    );

    return {
        encryptedPrivateBundle: toBase64(rewrapped),
        keyDerivationSalt: toBase64(newSalt),
        keyDerivationIterations: PBKDF2_ITERATIONS,
        wrapIv: toBase64(newIv),
    };
}