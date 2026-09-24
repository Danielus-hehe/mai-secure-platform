import { useCallback, useEffect, useMemo, useState } from 'react';
import { AlertTriangle, RefreshCw, ShieldCheck } from 'lucide-react';
import api from '../../api/client';
import CollapsibleSection from './CollapsibleSection';

/**
 * Alertele de securitate de pe panoul de administrare.
 *
 * Rostul: jurnalul de audit are mii de rânduri, iar nimeni nu le citește. Aici
 * apar doar tiparele care merită atenție acum - conturi forțate, autentificări
 * cu cod de recuperare, semnături invalide, conturi privilegiate fără 2FA.
 *
 * Toate interogările din spate se sprijină pe indexul Action+Result+Timestamp
 * adăugat odată cu migrarea rezultatului pe coloană.
 *
 * Alertele se grupează pe tip, în secțiuni pliabile, închise implicit. Înainte
 * erau o listă plată: zece conturi fără 2FA însemnau zece rânduri, iar o alertă
 * rară și gravă (o semnătură invalidă) ajungea sub ele, în afara ecranului.
 * Acum fiecare tip ocupă un rând cu numărul lui, iar ordinea secțiunilor urmează
 * gravitatea cea mai mare din fiecare.
 */

interface SecurityAlert {
    severity: 'high' | 'medium' | 'low';
    /** Cheia stabilă a tipului, după care se grupează (ex. "privileged-no-2fa"). */
    group: string;
    category: string;
    title: string;
    detail: string;
    username: string | null;
}

interface AlertsResponse {
    generatedAt: string;
    total: number;
    highCount: number;
    alerts: SecurityAlert[];
}

const SEVERITY_STYLE: Record<SecurityAlert['severity'], string> = {
    high: 'border-red-200 bg-red-50 dark:border-red-900/50 dark:bg-red-900/20',
    medium: 'border-amber-200 bg-amber-50 dark:border-amber-900/50 dark:bg-amber-900/20',
    low: 'border-mai-100 bg-mai-50/60 dark:border-mai-700 dark:bg-mai-700/20',
};

const SEVERITY_LABEL: Record<SecurityAlert['severity'], string> = {
    high: 'Ridicat',
    medium: 'Mediu',
    low: 'Informativ',
};

const SEVERITY_DOT: Record<SecurityAlert['severity'], string> = {
    high: 'bg-red-500',
    medium: 'bg-amber-500',
    low: 'bg-mai-300 dark:bg-mai-500',
};

/**
 * Titlul și explicația fiecărui tip. Un tip nou din backend, încă necunoscut
 * aici, apare cu categoria lui ca titlu: nu se pierde, doar arată mai sec.
 */
const GROUPS: Record<string, { title: string; hint: string }> = {
    'failed-logins':      { title: 'Încercări eșuate de autentificare', hint: 'Conturi cu multe parole greșite în ultimele 24 de ore' },
    'locked-accounts':    { title: 'Conturi blocate',                    hint: 'Blocate după încercări eșuate; se deblochează automat' },
    'privileged-no-2fa':  { title: 'Conturi privilegiate fără 2FA',      hint: 'Administratori și șefi protejați doar de parolă' },
    'recovery-codes':     { title: 'Autentificări cu cod de recuperare', hint: 'Posibil telefon pierdut sau aplicație 2FA ștearsă' },
    'bad-signatures':     { title: 'Semnături invalide la descărcare',   hint: 'Autenticitatea expeditorului nu a putut fi dovedită' },
    'token-reuse':        { title: 'Sesiuni folosite de două părți',     hint: 'Refresh token refolosit; sesiunea a fost închisă automat' },
    'concurrency-bursts': { title: 'Cereri simultane respinse',          hint: 'Rafale de cereri paralele de la aceeași adresă' },
    'missing-keys':       { title: 'Utilizatori fără chei de criptare',  hint: 'Nu pot primi fișiere până la prima autentificare' },
};

const SEVERITY_ORDER: Record<SecurityAlert['severity'], number> = { high: 0, medium: 1, low: 2 };

interface AlertGroup {
    key: string;
    title: string;
    hint: string;
    severity: SecurityAlert['severity'];
    alerts: SecurityAlert[];
}

function groupAlerts(alerts: SecurityAlert[]): AlertGroup[] {
    const byKey = new Map<string, AlertGroup>();

    for (const alert of alerts) {
        const key = alert.group || alert.category;
        let group = byKey.get(key);
        if (!group) {
            const meta = GROUPS[key];
            group = {
                key,
                title: meta?.title ?? alert.category,
                hint: meta?.hint ?? '',
                severity: alert.severity,
                alerts: [],
            };
            byKey.set(key, group);
        }
        group.alerts.push(alert);
        if (SEVERITY_ORDER[alert.severity] < SEVERITY_ORDER[group.severity]) group.severity = alert.severity;
    }

    // Gravitatea maximă întâi; la egalitate, grupul mai mare întâi.
    return [...byKey.values()].sort((a, b) =>
        SEVERITY_ORDER[a.severity] - SEVERITY_ORDER[b.severity] || b.alerts.length - a.alerts.length);
}

export default function SecurityAlertsPanel() {
    const [data, setData] = useState<AlertsResponse | null>(null);
    const [loading, setLoading] = useState(true);
    const [error, setError] = useState<string | null>(null);

    // Secțiunile deschise, după cheie: rămân deschise și după „Reîncarcă”.
    const [openGroups, setOpenGroups] = useState<Set<string>>(() => new Set());

    const groups = useMemo(() => groupAlerts(data?.alerts ?? []), [data]);

    const toggle = useCallback((key: string) => {
        setOpenGroups(current => {
            const next = new Set(current);
            if (next.has(key)) next.delete(key);
            else next.add(key);
            return next;
        });
    }, []);

    const load = useCallback(async () => {
        setError(null);
        try {
            const res = await api.get<AlertsResponse>('/Stats/alerts');
            setData(res.data);
        } catch {
            setError('Alertele nu au putut fi încărcate.');
        } finally {
            setLoading(false);
        }
    }, []);

    useEffect(() => { void load(); }, [load]);

    return (
        <section className="rounded-xl border border-mai-100 bg-white p-5
            dark:border-mai-700 dark:bg-mai-800">

            <header className="mb-4 flex items-start justify-between gap-4">
                <div>
                    <h2 className="flex items-center gap-2 text-base font-semibold
                        text-mai-800 dark:text-mai-100">
                        <AlertTriangle size={18} />
                        Alerte de securitate
                        {data && data.highCount > 0 && (
                            <span className="rounded-full bg-red-100 px-2 py-0.5 text-xs
                                font-bold text-red-700 dark:bg-red-900/40 dark:text-red-300">
                                {data.highCount}
                            </span>
                        )}
                    </h2>
                    <p className="mt-1 text-sm text-mai-500 dark:text-mai-400">
                        Tipare din jurnalul de audit care merită verificate.
                    </p>
                </div>

                <button
                    type="button"
                    onClick={() => void load()}
                    className="rounded-lg p-2 text-mai-400 transition hover:bg-mai-50
                        hover:text-mai-600 dark:hover:bg-mai-700 dark:hover:text-mai-200"
                    aria-label="Reîncarcă alertele"
                >
                    <RefreshCw size={16} />
                </button>
            </header>

            {error && (
                <p className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700
                    dark:bg-red-900/30 dark:text-red-300">
                    {error}
                </p>
            )}

            {loading && !error && (
                <p className="py-6 text-center text-sm text-mai-400">Se analizează jurnalul…</p>
            )}

            {/*
              Absența alertelor e o informație pozitivă și trebuie arătată ca atare.
              Un panou care dispare când nu are conținut lasă impresia că nu
              funcționează.
            */}
            {!loading && !error && data && data.alerts.length === 0 && (
                <div className="flex flex-col items-center gap-2 py-8 text-center">
                    <ShieldCheck size={28} className="text-green-500" />
                    <p className="text-sm font-medium text-mai-700 dark:text-mai-200">
                        Nicio alertă activă
                    </p>
                    <p className="text-xs text-mai-400">
                        Niciun tipar suspect în ultimele 7 zile.
                    </p>
                </div>
            )}

            {!loading && !error && data && data.alerts.length > 0 && (
                <div className="space-y-2">
                    {groups.map(group => (
                        <CollapsibleSection
                            key={group.key}
                            open={openGroups.has(group.key)}
                            onToggle={() => toggle(group.key)}
                            dotClassName={SEVERITY_DOT[group.severity]}
                            title={group.title}
                            hint={group.hint}
                            meta={SEVERITY_LABEL[group.severity]}
                            count={group.alerts.length}
                        >
                            <ul className="space-y-2">
                                {group.alerts.map((alert, index) => (
                                    <li
                                        // Alertele nu au id propriu: sunt calculate la fiecare
                                        // cerere, nu stocate. Indexul e stabil pentru o listă
                                        // care se reîncarcă întreagă și nu se reordonează local.
                                        key={`${alert.username ?? 'global'}-${index}`}
                                        className={`rounded-lg border p-3 ${SEVERITY_STYLE[alert.severity]}`}
                                    >
                                        <p className="text-sm font-semibold text-mai-800 dark:text-mai-100">
                                            {alert.title}
                                        </p>
                                        <p className="mt-0.5 text-xs text-mai-600 dark:text-mai-300">
                                            {alert.detail}
                                        </p>
                                    </li>
                                ))}
                            </ul>
                        </CollapsibleSection>
                    ))}
                </div>
            )}

            {data && (
                <p className="mt-3 text-right text-[11px] text-mai-400">
                    Generat {new Date(data.generatedAt).toLocaleTimeString('ro-MD')}
                </p>
            )}
        </section>
    );
}
