/**
 * Criptare end-to-end pentru transferurile de fisiere.
 *
 * Model criptografic (envelope encryption / criptare cu plic):
 *
 *   1. Fiecare utilizator are DOUA perechi de chei, generate in browser:
 *      - RSA-OAEP 3072 : impacheteaza cheia de fisier (confidentialitate)
 *      - RSA-PSS  3072 : semneaza continutul (autenticitate + non-repudiere)
 *      Perechi separate pentru ca reutilizarea unei chei RSA pentru si criptare
 *      si semnare este o slabiciune cunoscuta, iar WebCrypto oricum nu permite.
 *
 *   2. Cheile private se exporta PKCS8, se pun intr-un pachet JSON si se
 *      cripteaza cu AES-256-GCM folosind o cheie derivata din parola
 *      (PBKDF2-SHA256, 600.000 iteratii — pragul OWASP 2023). Serverul
 *      primeste doar blobul criptat; nu poate scoate cheile din el.
 *
 *   3. La trimiterea unui fisier:
 *      - se genereaza o cheie AES-256-GCM aleatorie (DEK), unica pe transfer
 *      - fisierul se cripteaza cu DEK
 *      - DEK se impacheteaza de doua ori: cu cheia publica a destinatarului
 *        SI cu a expeditorului (altfel expeditorul nu si-ar mai putea citi
 *        propriile fisiere trimise)
 *      - SHA-256 al continutului in clar se semneaza cu RSA-PSS
 *
 *   4. La primire: se despacheteaza DEK cu cheia privata proprie, se decripteaza,
 *      se recalculeaza SHA-256 si se verifica semnatura.
 *
 * Cele trei proprietati obtinute, cu numele lor:
 *   - CONFIDENTIALITATE — AES-256-GCM; serverul stocheaza doar cifrotext
 *   - INTEGRITATE       — tag-ul de autentificare GCM; orice bit modificat
 *                         face decriptarea sa esueze, nu sa produca gunoi
 *   - AUTENTICITATE si NON-REPUDIERE — semnatura RSA-PSS; dovedeste cine a
 *                         trimis si ca nu s-a schimbat nimic pe drum
 *
 * LIMITARE, de mentionat in raport: la login parola ajunge la server (unde e
 * verificata cu Argon2id). Un server compromis ar putea, teoretic, sa derive
 * cheia de impachetare din ea in acel moment. Varianta completa foloseste
 * derivari separate — un authHash trimis la server si o cheie de impachetare
 * care nu pleaca niciodata din browser. Vezi sectiunea de upgrade din README.
 */

// ── Parametri ────────────────────────────────────────────────────────────────

const PBKDF2_ITERATIONS = 600_000;   // OWASP 2023 pentru PBKDF2-HMAC-SHA256
const RSA_MODULUS_BITS = 3072;       // ~128 biti de securitate simetrica
const AES_KEY_BITS = 256;
const IV_BYTES = 12;                 // 96 biti — dimensiunea recomandata pentru GCM
const SALT_BYTES = 16;

export const CRYPTO_SUITE = 'AES-256-GCM+RSA-OAEP-3072+RSA-PSS-3072';

// ── Utilitare de codare ──────────────────────────────────────────────────────

export function toBase64(buffer: ArrayBuffer | Uint8Array): string {
    const bytes = buffer instanceof Uint8Array ? buffer : new Uint8Array(buffer);
    let binary = '';
    // In bucati, altfel String.fromCharCode(...) depaseste limita de argumente
    // pe fisiere mari si arunca RangeError.
    const CHUNK = 0x8000;
    for (let i = 0; i < bytes.length; i += CHUNK) {
        binary += String.fromCharCode(...bytes.subarray(i, i + CHUNK));
    }
    return btoa(binary);
}

export function fromBase64(value: string): Uint8Array {
    const binary = atob(value);
    const bytes = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
    return bytes;
}

export function toHex(buffer: ArrayBuffer): string {
    return Array.from(new Uint8Array(buffer))
        .map((b) => b.toString(16).padStart(2, '0'))
        .join('');
}

export async function sha256Hex(data: ArrayBuffer): Promise<string> {
    return toHex(await crypto.subtle.digest('SHA-256', data));
}

function assertSecureContext(): void {
    if (!crypto?.subtle) {
        throw new Error(
            'WebCrypto indisponibil. Aplicația trebuie servită prin HTTPS sau de pe localhost.'
        );
    }
}

// ── Tipuri ───────────────────────────────────────────────────────────────────

/** Ce se trimite la server la inregistrarea cheilor. Nimic din el nu e secret. */
export interface PublishedKeyBundle {
    publicKeyEncryption: string;   // SPKI, base64
    publicKeySigning: string;      // SPKI, base64
    encryptedPrivateBundle: string;// AES-GCM(JSON cu cele doua PKCS8), base64
    keyDerivationSalt: string;     // base64
    keyDerivationIterations: number;
    wrapIv: string;                // base64
    suite: string;
}

/** Cheile private descuiate, tinute doar in memoria filei. */
export interface UnlockedKeys {
    decryptionKey: CryptoKey;  // RSA-OAEP private
    signingKey: CryptoKey;     // RSA-PSS private
}

/** Metadatele criptografice ale unui transfer. */
export interface TransferCryptoEnvelope {
    iv: string;                      // base64
    encryptedKeyForRecipient: string;// base64
    encryptedKeyForSender: string;   // base64
    signature: string;               // base64
    ciphertextSha256: string;        // hex
    suite: string;
}

// ── Generarea si descuierea cheilor ──────────────────────────────────────────

/**
 * Deriva cheia care impacheteaza cheile private, pornind de la parola.
 * Salt-ul este per utilizator si se stocheaza alaturi de blob.
 */
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
 * Genereaza ambele perechi de chei si produce pachetul care se trimite la server.
 * Se apeleaza o singura data per utilizator, la prima autentificare.
 * Dureaza cateva secunde — generarea RSA-3072 nu e instantanee.
 */
export async function generateKeyBundle(password: string): Promise<PublishedKeyBundle> {
    assertSecureContext();

    const [encPair, signPair] = await Promise.all([
        crypto.subtle.generateKey(
            {
                name: 'RSA-OAEP',
                modulusLength: RSA_MODULUS_BITS,
                publicExponent: new Uint8Array([1, 0, 1]),
                hash: 'SHA-256',
            },
            true,
            ['encrypt', 'decrypt']
        ) as Promise<CryptoKeyPair>,
        crypto.subtle.generateKey(
            {
                name: 'RSA-PSS',
                modulusLength: RSA_MODULUS_BITS,
                publicExponent: new Uint8Array([1, 0, 1]),
                hash: 'SHA-256',
            },
            true,
            ['sign', 'verify']
        ) as Promise<CryptoKeyPair>,
    ]);

    const [encPub, encPriv, signPub, signPriv] = await Promise.all([
        crypto.subtle.exportKey('spki', encPair.publicKey),
        crypto.subtle.exportKey('pkcs8', encPair.privateKey),
        crypto.subtle.exportKey('spki', signPair.publicKey),
        crypto.subtle.exportKey('pkcs8', signPair.privateKey),
    ]);

    const privateBundle = new TextEncoder().encode(
        JSON.stringify({
            decryption: toBase64(encPriv),
            signing: toBase64(signPriv),
        })
    );

    const salt = crypto.getRandomValues(new Uint8Array(SALT_BYTES));
    const iv = crypto.getRandomValues(new Uint8Array(IV_BYTES));
    const wrappingKey = await deriveWrappingKey(password, salt, PBKDF2_ITERATIONS);

    const wrapped = await crypto.subtle.encrypt(
        { name: 'AES-GCM', iv: iv as BufferSource },
        wrappingKey,
        privateBundle as BufferSource
    );

    return {
        publicKeyEncryption: toBase64(encPub),
        publicKeySigning: toBase64(signPub),
        encryptedPrivateBundle: toBase64(wrapped),
        keyDerivationSalt: toBase64(salt),
        keyDerivationIterations: PBKDF2_ITERATIONS,
        wrapIv: toBase64(iv),
        suite: CRYPTO_SUITE,
    };
}

/**
 * Descuie cheile private folosind parola. Esueaza daca parola e gresita —
 * tag-ul GCM nu se verifica si decriptarea arunca, nu produce chei gresite.
 */
export async function unlockKeys(
    password: string,
    bundle: PublishedKeyBundle
): Promise<UnlockedKeys> {
    assertSecureContext();

    const salt = fromBase64(bundle.keyDerivationSalt);
    const iv = fromBase64(bundle.wrapIv);
    const wrappingKey = await deriveWrappingKey(
        password,
        salt,
        bundle.keyDerivationIterations || PBKDF2_ITERATIONS
    );

    let plainBundle: ArrayBuffer;
    try {
        plainBundle = await crypto.subtle.decrypt(
            { name: 'AES-GCM', iv: iv as BufferSource },
            wrappingKey,
            fromBase64(bundle.encryptedPrivateBundle) as BufferSource
        );
    } catch {
        throw new Error(
            'Cheile private nu au putut fi descuiate. Parola nu corespunde cheilor înregistrate.'
        );
    }

    const parsed = JSON.parse(new TextDecoder().decode(plainBundle)) as {
        decryption: string;
        signing: string;
    };

    const [decryptionKey, signingKey] = await Promise.all([
        crypto.subtle.importKey(
            'pkcs8',
            fromBase64(parsed.decryption) as BufferSource,
            { name: 'RSA-OAEP', hash: 'SHA-256' },
            false,   // non-extractable: nu mai poate fi exportata din browser
            ['decrypt']
        ),
        crypto.subtle.importKey(
            'pkcs8',
            fromBase64(parsed.signing) as BufferSource,
            { name: 'RSA-PSS', hash: 'SHA-256' },
            false,
            ['sign']
        ),
    ]);

    return { decryptionKey, signingKey };
}

/**
 * Re-impacheteaza cheile private cu o parola noua. Se apeleaza OBLIGATORIU
 * la schimbarea parolei — altfel cheile raman incuiate cu cea veche si
 * utilizatorul isi pierde accesul la toate fisierele primite.
 */
export async function rewrapPrivateKeys(
    keys: { decryptionPkcs8: ArrayBuffer; signingPkcs8: ArrayBuffer },
    newPassword: string
): Promise<Pick<PublishedKeyBundle, 'encryptedPrivateBundle' | 'keyDerivationSalt' | 'keyDerivationIterations' | 'wrapIv'>> {
    const privateBundle = new TextEncoder().encode(
        JSON.stringify({
            decryption: toBase64(keys.decryptionPkcs8),
            signing: toBase64(keys.signingPkcs8),
        })
    );

    const salt = crypto.getRandomValues(new Uint8Array(SALT_BYTES));
    const iv = crypto.getRandomValues(new Uint8Array(IV_BYTES));
    const wrappingKey = await deriveWrappingKey(newPassword, salt, PBKDF2_ITERATIONS);

    const wrapped = await crypto.subtle.encrypt(
        { name: 'AES-GCM', iv: iv as BufferSource },
        wrappingKey,
        privateBundle as BufferSource
    );

    return {
        encryptedPrivateBundle: toBase64(wrapped),
        keyDerivationSalt: toBase64(salt),
        keyDerivationIterations: PBKDF2_ITERATIONS,
        wrapIv: toBase64(iv),
    };
}

// ── Import chei publice ──────────────────────────────────────────────────────

export function importEncryptionPublicKey(spkiBase64: string): Promise<CryptoKey> {
    return crypto.subtle.importKey(
        'spki',
        fromBase64(spkiBase64) as BufferSource,
        { name: 'RSA-OAEP', hash: 'SHA-256' },
        false,
        ['encrypt']
    );
}

export function importSigningPublicKey(spkiBase64: string): Promise<CryptoKey> {
    return crypto.subtle.importKey(
        'spki',
        fromBase64(spkiBase64) as BufferSource,
        { name: 'RSA-PSS', hash: 'SHA-256' },
        false,
        ['verify']
    );
}

/**
 * Amprenta unei chei publice, pentru verificare "din afara canalului".
 * Doi utilizatori isi pot compara amprentele la telefon ca sa fie siguri ca
 * serverul nu le-a substituit cheile (atac man-in-the-middle al serverului).
 * Formatata in grupuri de 4 ca sa fie citibila cu voce tare.
 */
export async function keyFingerprint(spkiBase64: string): Promise<string> {
    const digest = await crypto.subtle.digest('SHA-256', fromBase64(spkiBase64) as BufferSource);
    return (
        toHex(digest)
            .slice(0, 32)
            .toUpperCase()
            .match(/.{1,4}/g) ?? []
    ).join(' ');
}

// ── Criptarea si decriptarea fisierelor ──────────────────────────────────────

export interface EncryptedFile {
    ciphertext: Blob;
    envelope: TransferCryptoEnvelope;
    plaintextSha256: string;
}

/**
 * Cripteaza un fisier pentru un destinatar.
 *
 * DEK-ul este aleatoriu si unic per transfer: doua fisiere identice trimise de
 * doua ori produc cifrotexte complet diferite, iar compromiterea unei chei de
 * fisier nu spune nimic despre celelalte.
 */
export async function encryptFileForRecipient(
    file: File,
    recipientEncryptionPublicKey: CryptoKey,
    senderEncryptionPublicKey: CryptoKey,
    senderSigningKey: CryptoKey
): Promise<EncryptedFile> {
    assertSecureContext();

    const plaintext = await file.arrayBuffer();
    const plaintextSha256 = await sha256Hex(plaintext);

    const dek = await crypto.subtle.generateKey(
        { name: 'AES-GCM', length: AES_KEY_BITS },
        true,
        ['encrypt', 'decrypt']
    );
    const iv = crypto.getRandomValues(new Uint8Array(IV_BYTES));

    // Cifrotextul include tag-ul de autentificare GCM pe ultimii 16 octeti.
    const ciphertext = await crypto.subtle.encrypt(
        { name: 'AES-GCM', iv: iv as BufferSource },
        dek,
        plaintext
    );

    const rawDek = await crypto.subtle.exportKey('raw', dek);

    const [forRecipient, forSender] = await Promise.all([
        crypto.subtle.encrypt({ name: 'RSA-OAEP' }, recipientEncryptionPublicKey, rawDek),
        crypto.subtle.encrypt({ name: 'RSA-OAEP' }, senderEncryptionPublicKey, rawDek),
    ]);

    // Semnam amprenta continutului IN CLAR: dovedeste ce a trimis expeditorul,
    // nu doar ce a ajuns pe server.
    const signature = await crypto.subtle.sign(
        { name: 'RSA-PSS', saltLength: 32 },
        senderSigningKey,
        new TextEncoder().encode(plaintextSha256) as BufferSource
    );

    return {
        ciphertext: new Blob([ciphertext], { type: 'application/octet-stream' }),
        plaintextSha256,
        envelope: {
            iv: toBase64(iv),
            encryptedKeyForRecipient: toBase64(forRecipient),
            encryptedKeyForSender: toBase64(forSender),
            signature: toBase64(signature),
            ciphertextSha256: await sha256Hex(ciphertext),
            suite: CRYPTO_SUITE,
        },
    };
}

export interface DecryptedFile {
    plaintext: ArrayBuffer;
    plaintextSha256: string;
    /** True daca semnatura expeditorului se verifica. False = continut nesigur. */
    signatureValid: boolean;
}

/**
 * Decripteaza si verifica integritatea si autenticitatea.
 *
 * Doua verificari independente:
 *   - tag-ul GCM: daca cifrotextul a fost modificat, decrypt() arunca. Nu exista
 *     scenariu in care sa obtii continut alterat fara sa observi.
 *   - semnatura RSA-PSS: dovedeste ca fisierul vine de la expeditorul declarat
 *     si ca serverul nu l-a inlocuit cu altul criptat corect pentru tine.
 */
export async function decryptTransfer(
    ciphertext: ArrayBuffer,
    envelope: TransferCryptoEnvelope,
    wrappedKeyForMe: string,
    myDecryptionKey: CryptoKey,
    senderSigningPublicKey: CryptoKey
): Promise<DecryptedFile> {
    assertSecureContext();

    let rawDek: ArrayBuffer;
    try {
        rawDek = await crypto.subtle.decrypt(
            { name: 'RSA-OAEP' },
            myDecryptionKey,
            fromBase64(wrappedKeyForMe) as BufferSource
        );
    } catch {
        throw new Error('Cheia de fișier nu a putut fi despachetată. Transferul nu vă este destinat.');
    }

    const dek = await crypto.subtle.importKey(
        'raw',
        rawDek,
        { name: 'AES-GCM' },
        false,
        ['decrypt']
    );

    let plaintext: ArrayBuffer;
    try {
        plaintext = await crypto.subtle.decrypt(
            { name: 'AES-GCM', iv: fromBase64(envelope.iv) as BufferSource },
            dek,
            ciphertext
        );
    } catch {
        throw new Error(
            'Verificarea integrității a eșuat. Fișierul a fost modificat sau este corupt.'
        );
    }

    const plaintextSha256 = await sha256Hex(plaintext);

    const signatureValid = await crypto.subtle.verify(
        { name: 'RSA-PSS', saltLength: 32 },
        senderSigningPublicKey,
        fromBase64(envelope.signature) as BufferSource,
        new TextEncoder().encode(plaintextSha256) as BufferSource
    );

    return { plaintext, plaintextSha256, signatureValid };
}

/** Salveaza continutul decriptat pe disc, sub numele original. */
export function saveDecryptedFile(plaintext: ArrayBuffer, fileName: string): void {
    const url = URL.createObjectURL(new Blob([plaintext]));
    const link = document.createElement('a');
    link.href = url;
    link.download = fileName;
    document.body.appendChild(link);
    link.click();
    link.remove();
    URL.revokeObjectURL(url);
}