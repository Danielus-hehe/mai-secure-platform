import { useCallback, useEffect, useState } from 'react';
import axios from 'axios';
import { RefreshCw, ShieldAlert, Smartphone } from 'lucide-react';
import CollapsibleSection from './CollapsibleSection';
import { fetchTwoFactorCoverage, type TwoFactorCoverage, type TwoFactorPerson } from '../../api/stats';
import { ROLE_BADGE_CLASSES, ROLE_LABELS } from '../../utils/constants';
import { formatDateTime } from '../../utils/format';
import type { Role } from '../../types';

/**
 * Acoperirea cu autentificare în doi pași, pe panoul de administrare.
 *
 * Alertele arată doar riscul mare (conturi privilegiate fără 2FA). Panoul acesta
 * răspunde la întrebarea practică de dinaintea activării obligației
 * (TWOFACTOR_REQUIRED_PRIVILEGED): cine l-a activat deja și cine încă nu. Cele
 * două liste sunt pliabile și închise implicit, ca panoul să ocupe un singur
 * rând pe secțiune până când administratorul chiar are nevoie de nume.
 *
 * Un șef de direcție vede doar oamenii subdiviziunii lui (filtrat pe server).
 */

/** Rolul din backend ("SefDirectie") → cheia folosită în interfață ("SEF_DIRECTIE"). */
const ROLE_FROM_API: Record<string, Role> = {
    Administrator: 'ADMINISTRATOR',
    SefDirectie: 'SEF_DIRECTIE',
    Utilizator: 'UTILIZATOR',
};

type SectionKey = 'disabled' | 'enabled';

export default function TwoFactorCoveragePanel() {
    const [data, setData] = useState<TwoFactorCoverage | null>(null);
    const [error, setError] = useState<string | null>(null);
    const [loading, setLoading] = useState(true);
    const [reloadKey, setReloadKey] = useState(0);
    const [open, setOpen] = useState<Record<SectionKey, boolean>>({ disabled: false, enabled: false });

    // Starea se schimbă doar în callback-urile cererii, nu sincron în efect:
    // efectul pornește cererea și o anulează dacă panoul dispare între timp.
    useEffect(() => {
        const controller = new AbortController();

        fetchTwoFactorCoverage(controller.signal)
            .then(result => {
                setData(result);
                setError(null);
            })
            .catch((err: unknown) => {
                if (axios.isCancel(err)) return;
                setError('Starea 2FA nu a putut fi încărcată.');
            })
            .finally(() => {
                if (!controller.signal.aborted) setLoading(false);
            });

        return () => controller.abort();
    }, [reloadKey]);

    const reload = useCallback(() => {
        setLoading(true);
        setReloadKey(k => k + 1);
    }, []);

    const toggle = (key: SectionKey) => setOpen(current => ({ ...current, [key]: !current[key] }));

    const percent = data && data.total > 0 ? Math.round((data.enabledCount / data.total) * 100) : 0;

    return (
        <section className="rounded-xl border border-mai-100 bg-white p-5
            dark:border-mai-700 dark:bg-mai-800">

            <header className="mb-4 flex items-start justify-between gap-4">
                <div>
                    <h2 className="flex items-center gap-2 text-base font-semibold
                        text-mai-800 dark:text-mai-100">
                        <Smartphone size={18} />
                        Autentificare în doi pași
                    </h2>
                    <p className="mt-1 text-sm text-mai-500 dark:text-mai-400">
                        Cine are și cine nu are al doilea factor activat (doar conturile active).
                    </p>
                </div>

                <button
                    type="button"
                    onClick={reload}
                    className="rounded-lg p-2 text-mai-400 transition hover:bg-mai-50
                        hover:text-mai-600 dark:hover:bg-mai-700 dark:hover:text-mai-200"
                    aria-label="Reîncarcă starea 2FA"
                >
                    <RefreshCw size={16} className={loading ? 'animate-spin' : ''} />
                </button>
            </header>

            {error && (
                <p className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700
                    dark:bg-red-900/30 dark:text-red-300">
                    {error}
                </p>
            )}

            {loading && !data && !error && (
                <p className="py-6 text-center text-sm text-mai-400">Se încarcă…</p>
            )}

            {data && (
                <>
                    {/* Proporția, ca o bară: se citește dintr-o privire. */}
                    <div className="mb-1 flex items-baseline justify-between text-sm">
                        <span className="font-semibold text-mai-800 dark:text-mai-100">
                            {data.enabledCount} din {data.total} conturi
                        </span>
                        <span className="text-mai-500 dark:text-mai-400">{percent}%</span>
                    </div>
                    <div
                        className="h-2 overflow-hidden rounded-full bg-mai-100 dark:bg-mai-700"
                        role="progressbar"
                        aria-valuenow={percent}
                        aria-valuemin={0}
                        aria-valuemax={100}
                        aria-label="Conturi cu 2FA activat"
                    >
                        <div className="h-full rounded-full bg-green-500 transition-all" style={{ width: `${percent}%` }} />
                    </div>

                    {data.privilegedWithoutCount > 0 && (
                        <p className="mt-3 flex items-start gap-2 rounded-lg bg-amber-50 px-3 py-2 text-xs
                            text-amber-800 dark:bg-amber-900/20 dark:text-amber-300">
                            <ShieldAlert size={14} className="mt-0.5 shrink-0" />
                            {data.privilegedWithoutCount === 1
                                ? '1 cont privilegiat nu are 2FA.'
                                : `${data.privilegedWithoutCount} conturi privilegiate nu au 2FA.`}
                            {' '}Activarea obligației (TWOFACTOR_REQUIRED_PRIVILEGED) le-ar bloca
                            accesul la funcțiile de administrare până la configurare.
                        </p>
                    )}

                    <div className="mt-4 space-y-2">
                        <CollapsibleSection
                            open={open.disabled}
                            onToggle={() => toggle('disabled')}
                            dotClassName="bg-amber-500"
                            title="Fără 2FA activat"
                            hint="Protejați doar de parolă"
                            count={data.disabledCount}
                        >
                            <PeopleList people={data.disabled} empty="Toate conturile active au 2FA activat." />
                        </CollapsibleSection>

                        <CollapsibleSection
                            open={open.enabled}
                            onToggle={() => toggle('enabled')}
                            dotClassName="bg-green-500"
                            title="Cu 2FA activat"
                            hint="Parolă și cod din aplicația de autentificare"
                            count={data.enabledCount}
                        >
                            <PeopleList people={data.enabled} empty="Niciun cont nu are încă 2FA activat." />
                        </CollapsibleSection>
                    </div>
                </>
            )}
        </section>
    );
}

function PeopleList({ people, empty }: { people: TwoFactorPerson[]; empty: string }) {
    if (people.length === 0)
        return <p className="py-2 text-sm text-mai-500 dark:text-mai-400">{empty}</p>;

    return (
        // Înălțime limitată: la câteva sute de conturi, lista nu împinge restul
        // panoului de administrare în jos, ci se derulează în interiorul ei.
        <ul className="max-h-80 divide-y divide-mai-100 overflow-y-auto dark:divide-mai-700">
            {people.map(person => {
                const role = ROLE_FROM_API[person.role] ?? 'UTILIZATOR';
                const privileged = role !== 'UTILIZATOR';

                return (
                    <li key={person.id} className="flex items-center gap-3 py-2">
                        <div className="min-w-0 flex-1">
                            <p className="truncate text-sm font-medium text-mai-800 dark:text-mai-100">
                                {person.fullName || person.username}
                                {!person.enabled && privileged && (
                                    <span className="ml-2 text-[11px] font-semibold uppercase text-red-600 dark:text-red-400">
                                        privilegiat
                                    </span>
                                )}
                            </p>
                            <p className="truncate text-xs text-mai-500 dark:text-mai-400">
                                @{person.username}
                                {person.unit && ` · ${person.unit}`}
                                {person.isDirectoryAccount && ' · cont AD'}
                                {person.enabled && person.enrolledAt && ` · activat ${formatDateTime(person.enrolledAt)}`}
                            </p>
                        </div>
                        <span className={`shrink-0 rounded-full px-2 py-0.5 text-[11px] font-semibold ${ROLE_BADGE_CLASSES[role]}`}>
                            {ROLE_LABELS[role]}
                        </span>
                    </li>
                );
            })}
        </ul>
    );
}
