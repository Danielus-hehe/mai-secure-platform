/**
 * Amprente SHA-256 calculate în browser, pentru verificarea fișierelor
 * descărcate față de valoarea din registru.
 *
 * Folosite la documentele normative ȘI la cele interne. Înainte, verificarea
 * exista doar pe pagina documentelor normative; la cele interne amprenta se
 * afișa, dar nimic nu o compara cu fișierul primit. Cine putea scrie în MinIO
 * putea înlocui un document intern cu altul criptat valid fără să fie observat.
 */

/** SHA-256 în hexazecimal cu litere mici. Aruncă dacă browserul nu oferă WebCrypto. */
export async function sha256(blob: Blob): Promise<string> {
    const hashBuffer = await crypto.subtle.digest('SHA-256', await blob.arrayBuffer());
    return Array.from(new Uint8Array(hashBuffer))
        .map((b) => b.toString(16).padStart(2, '0'))
        .join('');
}

/**
 * SHA-256, sau null dacă browserul nu oferă WebCrypto (pagină deschisă pe
 * HTTP, în afara lui localhost). Lipsa verificării se raportează separat de
 * o nepotrivire: nu e același lucru cu un fișier alterat.
 */
export async function sha256IfAvailable(blob: Blob): Promise<string | null> {
    if (!globalThis.crypto?.subtle) return null;
    return sha256(blob);
}

/** Fișierul primit nu corespunde amprentei din registru. Nu se salvează pe disc. */
export class IntegrityError extends Error {
    constructor(message: string) {
        super(message);
        this.name = 'IntegrityError';
    }
}

/** Rezultatul unei verificări reușite sau imposibile; nepotrivirea aruncă IntegrityError. */
export type IntegrityCheck = 'verified' | 'unavailable' | 'no-reference';

/**
 * Compară fișierul cu amprenta așteptată. Aruncă IntegrityError la
 * nepotrivire, ca apelantul să nu poată salva din greșeală fișierul alterat.
 */
export async function checkIntegrity(blob: Blob, expectedSha256?: string | null): Promise<IntegrityCheck> {
    const expected = expectedSha256?.trim().toLowerCase();
    if (!expected) return 'no-reference';

    const actual = await sha256IfAvailable(blob);
    if (actual === null) return 'unavailable';

    if (actual !== expected) {
        throw new IntegrityError(
            'Fișierul descărcat NU corespunde amprentei SHA-256 din registru și nu a fost salvat. ' +
            'Documentul poate fi alterat. Anunțați administratorul.'
        );
    }

    return 'verified';
}

/** Salvează un Blob pe disc sub numele dat. */
export function saveBlob(blob: Blob, fileName: string): void {
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = fileName;
    document.body.appendChild(a);
    a.click();
    a.remove();
    // Revocarea imediată poate anula descărcarea în unele browsere (Firefox, Safari).
    window.setTimeout(() => URL.revokeObjectURL(url), 1000);
}
