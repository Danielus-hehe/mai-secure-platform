/**
 * Valori pentru <input type="datetime-local">.
 *
 * Câmpul lucrează în ORA LOCALĂ, fără fus orar: „2026-09-26T14:30”. Varianta
 * veche folosea date.toISOString().slice(0, 16), care dă ora UTC - la Chișinău
 * (UTC+3 vara) formularul afișa o expirare cu trei ore mai devreme decât cea
 * dorită, iar `min` bloca ultimele trei ore ale zilei.
 */

const pad = (n: number): string => String(n).padStart(2, '0');

/** Date → „yyyy-MM-ddTHH:mm” în ora locală a browserului. */
export function toDateTimeLocalValue(date: Date): string {
    return (
        `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}` +
        `T${pad(date.getHours())}:${pad(date.getMinutes())}`
    );
}

/** „yyyy-MM-ddTHH:mm” (ora locală) → ISO 8601 în UTC, pentru API. */
export function dateTimeLocalToIso(value: string): string | undefined {
    if (!value) return undefined;
    // Constructorul Date interpretează formatul fără fus orar ca oră LOCALĂ.
    const date = new Date(value);
    return Number.isNaN(date.getTime()) ? undefined : date.toISOString();
}

/** Acum + un număr de zile, în valoarea pentru datetime-local. */
export function daysFromNowLocal(days: number, from: Date = new Date()): string {
    const d = new Date(from);
    d.setDate(d.getDate() + days);
    return toDateTimeLocalValue(d);
}
