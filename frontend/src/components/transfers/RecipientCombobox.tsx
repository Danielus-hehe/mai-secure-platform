import { useEffect, useId, useMemo, useRef, useState, type KeyboardEvent } from 'react';
import { Search, X, Check } from 'lucide-react';
import type { Recipient } from '../../api/transfers';

/**
 * Alegerea destinatarului prin căutare, în locul unui <select> cu toată lista.
 *
 * Într-un minister cu sute de angajați, un <select> cu toate conturile e
 * inutilizabil și, în plus, afișează la deschidere tot personalul cu
 * departamentul fiecăruia. Aici lista apare doar după ce utilizatorul începe
 * să scrie și se limitează la primele rezultate.
 *
 * Căutarea ignoră diacriticele și majusculele: „sef” găsește „Șef Direcție”,
 * „it” găsește „Direcția IT”. Se caută în nume, username și departament.
 *
 * Tastatură: ↑/↓ mută selecția, Enter alege, Escape închide lista.
 * Rolurile ARIA (combobox/listbox/option) fac componenta utilizabilă și cu
 * cititor de ecran.
 */

interface Props {
    recipients: Recipient[];
    value: string;
    onChange: (recipientId: string) => void;
    disabled?: boolean;
    /** Minimul de caractere înainte de a afișa rezultate. 0 = arată și fără text. */
    minChars?: number;
    /** Câte rezultate se afișează cel mult. */
    maxResults?: number;
}

/** „Șef Direcție” → „sef directie”, pentru comparații fără diacritice. */
const normalize = (text: string): string =>
    text
        .normalize('NFD')
        .replace(/[\u0300-\u036f]/g, '')
        .toLowerCase()
        .trim();

export default function RecipientCombobox({
    recipients,
    value,
    onChange,
    disabled = false,
    minChars = 1,
    maxResults = 8,
}: Props) {
    const [query, setQuery] = useState('');
    const [open, setOpen] = useState(false);
    const [active, setActive] = useState(0);

    const inputRef = useRef<HTMLInputElement>(null);
    const listRef = useRef<HTMLUListElement>(null);
    const listId = useId();

    const selected = useMemo(
        () => recipients.find((r) => r.id === value) ?? null,
        [recipients, value]
    );

    // Indexul de căutare se calculează o singură dată pe listă, nu la fiecare tastă.
    const indexed = useMemo(
        () =>
            recipients.map((r) => ({
                recipient: r,
                haystack: normalize(`${r.fullName} ${r.username} ${r.department ?? ''}`),
            })),
        [recipients]
    );

    const results = useMemo(() => {
        const q = normalize(query);
        if (q.length < minChars) return [];

        // Fiecare cuvânt din căutare trebuie să apară: „ion it” → Ion din Direcția IT.
        const terms = q.split(/\s+/).filter(Boolean);

        return indexed
            .filter(({ haystack }) => terms.every((t) => haystack.includes(t)))
            .slice(0, maxResults)
            .map(({ recipient }) => recipient);
    }, [indexed, query, minChars, maxResults]);

    // Elementul activ rămâne vizibil la navigarea cu săgețile.
    useEffect(() => {
        const el = listRef.current?.children[active] as HTMLElement | undefined;
        el?.scrollIntoView({ block: 'nearest' });
    }, [active]);

    const choose = (r: Recipient) => {
        onChange(r.id);
        setQuery('');
        setOpen(false);
    };

    const clear = () => {
        onChange('');
        setQuery('');
        setOpen(true);
        // Focus înapoi în câmp, ca utilizatorul să poată scrie imediat alt nume.
        requestAnimationFrame(() => inputRef.current?.focus());
    };

    const onKeyDown = (e: KeyboardEvent<HTMLInputElement>) => {
        if (e.key === 'ArrowDown') {
            e.preventDefault();
            setOpen(true);
            setActive((i) => Math.min(i + 1, Math.max(results.length - 1, 0)));
        } else if (e.key === 'ArrowUp') {
            e.preventDefault();
            setActive((i) => Math.max(i - 1, 0));
        } else if (e.key === 'Enter') {
            // Enter nu trebuie să trimită formularul părinte cât timp lista e deschisă.
            if (open && results[active]) {
                e.preventDefault();
                choose(results[active]);
            }
        } else if (e.key === 'Escape') {
            setOpen(false);
        }
    };

    // ── Destinatar deja ales: afișăm „cardul”, cu buton de schimbare ──────
    if (selected) {
        return (
            <div
                className="flex items-center justify-between gap-3 rounded-lg border border-mai-300
                    bg-mai-50 px-3.5 py-2.5 dark:border-mai-600 dark:bg-mai-800"
            >
                <div className="min-w-0">
                    <p className="truncate text-sm font-medium text-mai-800 dark:text-mai-100">
                        {selected.fullName}
                    </p>
                    <p className="truncate text-xs text-mai-500 dark:text-mai-400">
                        @{selected.username}
                        {selected.department ? ` · ${selected.department}` : ''}
                    </p>
                </div>
                <button
                    type="button"
                    onClick={clear}
                    disabled={disabled}
                    className="shrink-0 rounded-md p-1.5 text-mai-500 transition hover:bg-mai-100
                        hover:text-mai-700 disabled:opacity-50 dark:hover:bg-mai-700 dark:hover:text-mai-200"
                    aria-label="Schimbă destinatarul"
                    title="Schimbă destinatarul"
                >
                    <X className="h-4 w-4" />
                </button>
            </div>
        );
    }

    // ── Căutare ────────────────────────────────────────────────────────────
    const showList = open && query.trim().length >= minChars;

    return (
        <div className="relative">
            <Search
                className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-mai-400"
                aria-hidden="true"
            />
            <input
                ref={inputRef}
                id="recipient"
                type="text"
                role="combobox"
                aria-expanded={showList}
                aria-controls={listId}
                aria-autocomplete="list"
                aria-activedescendant={showList && results[active] ? `${listId}-${active}` : undefined}
                autoComplete="off"
                spellCheck={false}
                placeholder="Căutați după nume, username sau departament…"
                value={query}
                disabled={disabled}
                onChange={(e) => {
                    setQuery(e.target.value);
                    setOpen(true);
                    // Lista s-a schimbat: selecția activă revine pe primul rezultat.
                    setActive(0);
                }}
                onFocus={() => setOpen(true)}
                // Întârzierea lasă click-ul pe opțiune să se înregistreze înainte de închidere.
                onBlur={() => setTimeout(() => setOpen(false), 120)}
                onKeyDown={onKeyDown}
                className="w-full rounded-lg border border-mai-200 bg-white py-2.5 pl-9 pr-3.5 text-sm
                    transition placeholder:text-mai-300 focus:border-mai-500 focus:outline-none
                    focus:ring-2 focus:ring-mai-500 disabled:opacity-60 dark:border-mai-600
                    dark:bg-mai-800 dark:text-mai-100 dark:placeholder:text-mai-500
                    dark:focus:border-mai-400 dark:focus:ring-mai-400"
            />

            {showList && (
                <ul
                    ref={listRef}
                    id={listId}
                    role="listbox"
                    className="absolute z-20 mt-1 max-h-64 w-full overflow-y-auto rounded-lg border
                        border-mai-200 bg-white py-1 shadow-lg dark:border-mai-600 dark:bg-mai-800"
                >
                    {results.length === 0 ? (
                        <li className="px-3.5 py-2.5 text-sm text-mai-400">
                            Niciun coleg cu chei de criptare nu corespunde căutării.
                        </li>
                    ) : (
                        results.map((r, i) => (
                            <li
                                key={r.id}
                                id={`${listId}-${i}`}
                                role="option"
                                aria-selected={i === active}
                                // onMouseDown, nu onClick: se execută înaintea blur-ului.
                                onMouseDown={(e) => {
                                    e.preventDefault();
                                    choose(r);
                                }}
                                onMouseEnter={() => setActive(i)}
                                className={`flex cursor-pointer items-center justify-between gap-3 px-3.5 py-2
                                    ${i === active
                                        ? 'bg-mai-100 dark:bg-mai-700'
                                        : 'hover:bg-mai-50 dark:hover:bg-mai-700/60'}`}
                            >
                                <div className="min-w-0">
                                    <p className="truncate text-sm font-medium text-mai-800 dark:text-mai-100">
                                        {r.fullName}
                                    </p>
                                    <p className="truncate text-xs text-mai-500 dark:text-mai-400">
                                        @{r.username}
                                        {r.department ? ` · ${r.department}` : ''}
                                    </p>
                                </div>
                                {i === active && (
                                    <Check className="h-4 w-4 shrink-0 text-mai-500" aria-hidden="true" />
                                )}
                            </li>
                        ))
                    )}
                </ul>
            )}

            {!showList && recipients.length > 0 && (
                <p className="mt-1.5 text-xs text-mai-400">
                    {recipients.length} {recipients.length === 1 ? 'coleg disponibil' : 'colegi disponibili'} —
                    începeți să scrieți pentru a căuta.
                </p>
            )}
        </div>
    );
}
