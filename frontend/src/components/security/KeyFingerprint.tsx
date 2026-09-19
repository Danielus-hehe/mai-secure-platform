/**
 * Amprenta cheilor, ascunsă implicit și afișată la cerere.
 *
 * Amprenta NU e un secret: e făcută tocmai ca să fie citită cu voce tare și
 * comparată cu un coleg, pe alt canal, ca să se excludă substituirea cheilor de
 * către server. Ascunderea ține de ecran - la o prezentare sau într-un birou
 * deschis, un șir lung de caractere nu are ce căuta permanent la vedere. De
 * aceea se ascunde singură după un timp și poate fi copiată când e afișată.
 *
 * Forma mascată păstrează grupurile de 4 caractere: lățimea nu se schimbă la
 * afișare, iar utilizatorul vede că în spate e o amprentă, nu un câmp gol.
 */

import { useEffect, useState } from 'react';
import { Check, Copy, Eye, EyeOff } from 'lucide-react';

interface KeyFingerprintProps {
    /** Amprenta, în grupuri de câte 4 caractere hexazecimale. */
    value: string;
    /** După câte secunde se ascunde din nou. */
    autoHideSeconds?: number;
}

export default function KeyFingerprint({ value, autoHideSeconds = 30 }: KeyFingerprintProps) {
    const [visible, setVisible] = useState(false);
    const [copied, setCopied] = useState(false);

    useEffect(() => {
        if (!visible) return;
        const timer = window.setTimeout(() => setVisible(false), autoHideSeconds * 1000);
        return () => window.clearTimeout(timer);
    }, [visible, autoHideSeconds]);

    useEffect(() => {
        if (!copied) return;
        const timer = window.setTimeout(() => setCopied(false), 2000);
        return () => window.clearTimeout(timer);
    }, [copied]);

    if (!value) return <span className="font-mono">-</span>;

    const masked = value.replace(/[0-9A-Fa-f]/g, '•');

    const copy = async () => {
        try {
            await navigator.clipboard.writeText(value);
            setCopied(true);
        } catch {
            // Clipboard-ul cere context securizat (HTTPS sau localhost). Fără el,
            // amprenta rămâne oricum afișată și poate fi citită de pe ecran.
        }
    };

    const iconButton =
        'inline-flex h-6 w-6 shrink-0 items-center justify-center rounded-md text-mai-400 ' +
        'transition-colors hover:bg-mai-100 hover:text-mai-700 ' +
        'dark:hover:bg-mai-700 dark:hover:text-mai-100 ' +
        'focus:outline-none focus-visible:ring-2 focus-visible:ring-mai-500/40';

    return (
        <span className="inline-flex max-w-full flex-wrap items-center gap-1 align-middle">
            <button
                type="button"
                onClick={() => setVisible(v => !v)}
                aria-pressed={visible}
                title={visible ? 'Ascunde amprenta' : 'Afișează amprenta'}
                className="rounded-md px-1 py-0.5 text-left font-mono font-semibold tracking-wide
                           text-mai-700 transition-colors hover:bg-mai-100
                           dark:text-mai-200 dark:hover:bg-mai-700
                           focus:outline-none focus-visible:ring-2 focus-visible:ring-mai-500/40 break-all"
            >
                <span className="sr-only">{visible ? 'Amprentă afișată: ' : 'Amprentă ascunsă. Apăsați pentru afișare.'}</span>
                <span aria-hidden={!visible}>{visible ? value : masked}</span>
            </button>

            <button
                type="button"
                onClick={() => setVisible(v => !v)}
                aria-label={visible ? 'Ascunde amprenta' : 'Afișează amprenta'}
                title={visible ? 'Ascunde amprenta' : 'Afișează amprenta'}
                className={iconButton}
            >
                {visible ? <EyeOff size={14} /> : <Eye size={14} />}
            </button>

            {visible && (
                <button
                    type="button"
                    onClick={() => void copy()}
                    aria-label="Copiază amprenta"
                    title={copied ? 'Copiată' : 'Copiază amprenta'}
                    className={iconButton}
                >
                    {copied
                        ? <Check size={14} className="text-green-600 dark:text-green-400" />
                        : <Copy size={14} />}
                </button>
            )}
        </span>
    );
}
