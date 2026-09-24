import { useId, type ReactNode } from 'react';
import { ChevronDown } from 'lucide-react';

/**
 * Secțiune pliabilă folosită de panourile de securitate din administrare.
 *
 * Starea (deschis / închis) o ține părintele, nu componenta: panoul de alerte
 * trebuie să păstreze secțiunile deschise la reîncărcarea datelor, iar lista de
 * 2FA poate deschide automat secțiunea care contează. O componentă cu stare
 * proprie ar pierde ce a deschis utilizatorul la fiecare re-randare a listei.
 *
 * Buton adevărat, cu aria-expanded și aria-controls: se deschide și din
 * tastatură (Tab + Enter), iar cititoarele de ecran anunță starea.
 */
interface CollapsibleSectionProps {
    open: boolean;
    onToggle: () => void;
    /** Punctul colorat din stânga: gravitatea sau starea secțiunii. */
    dotClassName: string;
    title: string;
    count: number;
    /** Text scurt în dreapta titlului (ex. gravitatea). */
    meta?: string;
    hint?: string;
    children: ReactNode;
}

export default function CollapsibleSection({
    open, onToggle, dotClassName, title, count, meta, hint, children,
}: CollapsibleSectionProps) {
    const contentId = useId();

    return (
        <div className="rounded-lg border border-mai-100 dark:border-mai-700">
            <button
                type="button"
                onClick={onToggle}
                aria-expanded={open}
                aria-controls={contentId}
                className="flex w-full items-center gap-3 rounded-lg px-3 py-2.5 text-left transition
                    hover:bg-mai-50 dark:hover:bg-mai-700/40"
            >
                <span className={`h-2 w-2 shrink-0 rounded-full ${dotClassName}`} aria-hidden />
                <span className="min-w-0 flex-1">
                    <span className="block text-sm font-semibold text-mai-800 dark:text-mai-100">
                        {title}
                    </span>
                    {hint && (
                        <span className="block text-xs text-mai-500 dark:text-mai-400">{hint}</span>
                    )}
                </span>
                {meta && (
                    <span className="hidden text-[11px] uppercase tracking-wide text-mai-400 sm:inline">
                        {meta}
                    </span>
                )}
                <span className="rounded-full bg-mai-100 px-2 py-0.5 text-xs font-bold text-mai-700
                    dark:bg-mai-700 dark:text-mai-100">
                    {count}
                </span>
                <ChevronDown
                    size={16}
                    className={`shrink-0 text-mai-400 transition-transform ${open ? 'rotate-180' : ''}`}
                    aria-hidden
                />
            </button>

            {open && (
                <div id={contentId} className="border-t border-mai-100 px-3 py-2.5 dark:border-mai-700">
                    {children}
                </div>
            )}
        </div>
    );
}
