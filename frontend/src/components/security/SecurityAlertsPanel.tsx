import { useCallback, useEffect, useState } from 'react';
import { AlertTriangle, RefreshCw, ShieldCheck } from 'lucide-react';
import api from '../../api/client';

/**
 * Alertele de securitate de pe panoul de administrare.
 *
 * Rostul: jurnalul de audit are mii de rânduri, iar nimeni nu le citește. Aici
 * apar doar tiparele care merită atenție acum — conturi forțate, autentificări
 * cu cod de recuperare, semnături invalide, conturi privilegiate fără 2FA.
 *
 * Toate interogările din spate se sprijină pe indexul Action+Result+Timestamp
 * adăugat odată cu migrarea rezultatului pe coloană.
 */

interface SecurityAlert {
    severity: 'high' | 'medium' | 'low';
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

export default function SecurityAlertsPanel() {
    const [data, setData] = useState<AlertsResponse | null>(null);
    const [loading, setLoading] = useState(true);
    const [error, setError] = useState<string | null>(null);

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
                <ul className="space-y-2">
                    {data.alerts.map((alert, index) => (
                        <li
                            // Alertele nu au id propriu: sunt calculate la fiecare
                            // cerere, nu stocate. Indexul e stabil pentru o listă
                            // care se reîncarcă întreagă și nu se reordonează local.
                            key={`${alert.category}-${alert.username ?? 'global'}-${index}`}
                            className={`rounded-lg border p-3 ${SEVERITY_STYLE[alert.severity]}`}
                        >
                            <div className="flex items-start gap-2.5">
                                <span
                                    className={`mt-1.5 h-2 w-2 shrink-0 rounded-full
                                        ${SEVERITY_DOT[alert.severity]}`}
                                    aria-hidden
                                />
                                <div className="min-w-0">
                                    <p className="text-sm font-semibold text-mai-800 dark:text-mai-100">
                                        {alert.title}
                                    </p>
                                    <p className="mt-0.5 text-xs text-mai-600 dark:text-mai-300">
                                        {alert.detail}
                                    </p>
                                    <p className="mt-1 text-[11px] uppercase tracking-wide text-mai-400">
                                        {alert.category} · {SEVERITY_LABEL[alert.severity]}
                                    </p>
                                </div>
                            </div>
                        </li>
                    ))}
                </ul>
            )}

            {data && (
                <p className="mt-3 text-right text-[11px] text-mai-400">
                    Generat {new Date(data.generatedAt).toLocaleTimeString('ro-MD')}
                </p>
            )}
        </section>
    );
}
