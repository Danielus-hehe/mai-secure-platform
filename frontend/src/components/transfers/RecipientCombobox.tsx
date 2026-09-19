import { useEffect, useId, useMemo, useRef, useState, type KeyboardEvent } from 'react';
import { Search, X, Check } from 'lucide-react';
import type { Recipient } from '../../api/transfers';

/**
 * Alegerea destinatarilor prin căutare, cu selecție multiplă.
 *
 * Într-un minister cu sute de angajați, un <select> cu toate conturile e
 * inutilizabil și, în plus, afișează la deschidere tot personalul cu
 * departamentul fiecăruia. Aici lista apare doar după ce utilizatorul începe
 * să scrie și se limitează la primele rezultate.
 *
 * Căutarea ignoră diacriticele și majusculele: „sef” găsește „Șef Direcție”,
 * „it” găsește „Direcția IT”. Se caută în nume, username și departament.
 *
 * Destinatarii aleși apar ca etichete deasupra câmpului; Backspace pe câmpul
 * gol îl scoate pe ultimul. Aceeași componentă servește la trimitere și la
 * forward — la forward, `exclude` ascunde pe cei care au deja acces.
 *
 * Tastatură: ↑/↓ mută selecția, Enter alege, Escape închide lista.
 */

interface Props {
    recipients: Recipient[];
    value: string[];
    onChange: (recipientIds: string[]) => void;
    disabled?: boolean;
    /** Câți destinatari se pot alege cel mult. */
    max?: number;
    /** Utilizatori care nu apar în rezultate (au deja acces la transfer). */
    exclude?: string[];
    /** Minimul de caractere înainte de a afișa rezultate. */
    minChars?: number;
    /** Câte rezultate se afișează cel mult. */
    maxResults?: number;
    inputId?: string;
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
    max = 20,
    exclude = [],
    minChars = 1,
    maxResults = 8,
    inputId = 'recipient',
}: Props) {
    const [query, setQuery] = useState('');
    const [open, setOpen] = useState(false);
    const [active, setActive] = useState(0);

    const inputRef = useRef<HTMLInputElement>(null);
    const listRef = useRef<HTMLUListElement>(null);
    const listId = useId();

    const byId = useMemo(() => new Map(recipients.map((r) => [r.id, r])), [recipients]);

    const selected = useMemo(
        () => value.map((id) => byId.get(id)).filter((r): r is Recipient => r !== undefined),
        [value, byId]
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

    const hidden = useMemo(() => new Set([...value, ...exclude]), [value, exclude]);
    const full = value.length >= max;

    const results = useMemo(() => {
        const q = normalize(query);
        if (q.length < minChars) return [];

        // Fiecare cuvânt din căutare trebuie să apară: „ion it” → Ion din Direcția IT.
        const terms = q.split(/\s+/).filter(Boolean);

        return indexed
            .filter(({ recipient }) => !hidden.has(recipient.id))
            .filter(({ haystack }) => terms.every((t) => haystack.includes(t)))
            .slice(0, maxResults)
            .map(({ recipient }) => recipient);
    }, [indexed, hidden, query, minChars, maxResults]);

    // Elementul activ rămâne vizibil la navigarea cu săgețile.
    useEffect(() => {
        const el = listRef.current?.children[active] as HTMLElement | undefined;
        el?.scrollIntoView({ block: 'nearest' });
    }, [active]);

    const choose = (r: Recipient) => {
        if (full || value.includes(r.id)) return;
        onChange([...value, r.id]);
        setQuery('');
        setActive(0);
        // Rămânem în câmp: următorul destinatar se poate căuta imediat.
        requestAnimationFrame(() => inputRef.current?.focus());
    };

    const remove = (id: string) => {
        onChange(value.filter((v) => v !== id));
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
        } else if (e.key === 'Backspace' && query === '' && value.length > 0) {
            remove(value[value.length - 1]);
        }
    };

    const showList = open && !full && query.trim().length >= minChars;
    const available = recipients.filter((r) => !hidden.has(r.id)).length;

    return (
        <div className="space-y-2">
            {selected.length > 0 && (
                <ul className="flex flex-wrap gap-1.5" aria-label="Destinatari aleși">
                    {selected.map((r) => (
                        <li
                            key={r.id}
                            className="inline-flex max-w-full items-center gap-1.5 rounded-full border border-mai-200
                                bg-mai-50 py-1 pl-3 pr-1.5 text-xs dark:border-mai-600 dark:bg-mai-700/50"
                            title={`@${r.username}${r.department ? ` · ${r.department}` : ''}`}
                        >
                            <span className="truncate font-medium text-mai-800 dark:text-mai-100">
                                {r.fullName}
                            </span>
                            <button
                                type="button"
                                onClick={() => remove(r.id)}
                                disabled={disabled}
                                className="rounded-full p-0.5 text-mai-400 transition hover:bg-mai-200
                                    hover:text-mai-700 disabled:opacity-50 dark:hover:bg-mai-600 dark:hover:text-mai-100"
                                aria-label={`Elimină ${r.fullName}`}
                            >
                                <X className="h-3 w-3" />
                            </button>
                        </li>
                    ))}
                </ul>
            )}

            <div className="relative">
                <Search
                    className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-mai-400"
                    aria-hidden="true"
                />
                <input
                    ref={inputRef}
                    id={inputId}
                    type="text"
                    role="combobox"
                    aria-expanded={showList}
                    aria-controls={listId}
                    aria-autocomplete="list"
                    aria-activedescendant={showList && results[active] ? `${listId}-${active}` : undefined}
                    autoComplete="off"
                    spellCheck={false}
                    placeholder={
                        full
                            ? `Maxim ${max} destinatari`
                            : selected.length > 0
                                ? 'Adăugați încă un destinatar…'
                                : 'Căutați după nume, username sau departament…'
                    }
                    value={query}
                    disabled={disabled || full}
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
                        aria-multiselectable="true"
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
            </div>

            {!showList && (
                <p className="text-xs text-mai-400">
                    {full
                        ? `S-a atins limita de ${max} destinatari.`
                        : available > 0
                            ? `${available} ${available === 1 ? 'coleg disponibil' : 'colegi disponibili'} — ` +
                              'începeți să scrieți pentru a căuta.'
                            : 'Niciun alt coleg cu chei de criptare disponibil.'}
                </p>
            )}
        </div>
    );
}
