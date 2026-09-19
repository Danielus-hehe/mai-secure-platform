import { useCallback, useEffect, useState } from 'react';
import { Laptop, LogOut, RefreshCw, ShieldAlert } from 'lucide-react';
import { sessionsApi, type SessionInfo } from '../../api/sessions';
import Badge from '../ui/Badge';
import Button from '../ui/Button';
import EmptyState from '../ui/EmptyState';

/**
 * Sesiunile active ale utilizatorului, cu revocare de la distanță.
 *
 * Se montează în pagina de profil, lângă setările 2FA: amândouă răspund la
 * aceeași întrebare - „cine mai are acces la contul meu”.
 *
 * Nu afișează niciodată hash-ul tokenului. Serverul nici nu îl trimite: e
 * singura valoare din rând cu care se poate face ceva.
 */
export default function SessionsPanel() {
    const [sessions, setSessions] = useState<SessionInfo[]>([]);
    const [loading, setLoading] = useState(true);
    const [busyId, setBusyId] = useState<string | null>(null);
    const [error, setError] = useState<string | null>(null);
    const [notice, setNotice] = useState<string | null>(null);

    const load = useCallback(async () => {
        setError(null);
        try {
            setSessions(await sessionsApi.list());
        } catch {
            setError('Sesiunile nu au putut fi încărcate.');
        } finally {
            setLoading(false);
        }
    }, []);

    useEffect(() => { void load(); }, [load]);

    async function handleRevoke(session: SessionInfo) {
        // Confirmarea numește dispozitivul, nu doar „această sesiune”. Într-o
        // listă cu patru intrări similare, un dialog generic nu ajută pe nimeni
        // să verifice că a apăsat pe rândul corect.
        const ok = window.confirm(
            `Închideți sesiunea de pe ${session.device}` +
            `${session.ipAddress ? ` (${session.ipAddress})` : ''}?\n\n` +
            'Dispozitivul va fi deconectat în cel mult 15 minute.',
        );
        if (!ok) return;

        setBusyId(session.id);
        setError(null);
        try {
            const result = await sessionsApi.revoke(session.id);
            setNotice(result.message);
            await load();
        } catch {
            setError('Sesiunea nu a putut fi închisă.');
        } finally {
            setBusyId(null);
        }
    }

    async function handleRevokeOthers() {
        const ok = window.confirm(
            'Închideți toate celelalte sesiuni?\n\n' +
            'Veți rămâne conectat doar pe acest dispozitiv.',
        );
        if (!ok) return;

        setBusyId('others');
        setError(null);
        try {
            const result = await sessionsApi.revokeOthers();
            setNotice(result.message);
            await load();
        } catch (e) {
            setError(e instanceof Error ? e.message : 'Sesiunile nu au putut fi închise.');
        } finally {
            setBusyId(null);
        }
    }

    const active = sessions.filter((s) => s.isActive);
    const closed = sessions.filter((s) => !s.isActive);
    const otherActive = active.filter((s) => !s.isCurrent).length;

    return (
        <section className="rounded-xl border border-mai-100 bg-white p-5
            dark:border-mai-700 dark:bg-mai-800">

            <header className="mb-4 flex items-start justify-between gap-4">
                <div>
                    <h2 className="flex items-center gap-2 text-base font-semibold
                        text-mai-800 dark:text-mai-100">
                        <Laptop size={18} />
                        Sesiuni active
                    </h2>
                    <p className="mt-1 text-sm text-mai-500 dark:text-mai-400">
                        Dispozitivele de pe care sunteți autentificat. Dacă vedeți ceva
                        ce nu recunoașteți, închideți sesiunea și schimbați-vă parola.
                    </p>
                </div>

                <button
                    type="button"
                    onClick={() => void load()}
                    className="rounded-lg p-2 text-mai-400 transition hover:bg-mai-50
                        hover:text-mai-600 dark:hover:bg-mai-700 dark:hover:text-mai-200"
                    aria-label="Reîncarcă lista"
                >
                    <RefreshCw size={16} />
                </button>
            </header>

            {error && (
                <p className="mb-3 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700
                    dark:bg-red-900/30 dark:text-red-300">
                    {error}
                </p>
            )}

            {notice && !error && (
                <p className="mb-3 rounded-lg bg-green-50 px-3 py-2 text-sm text-green-700
                    dark:bg-green-900/30 dark:text-green-300">
                    {notice}
                </p>
            )}

            {loading ? (
                <p className="py-6 text-center text-sm text-mai-400">Se încarcă…</p>
            ) : active.length === 0 ? (
                <EmptyState icon={Laptop} title="Nicio sesiune activă" />
            ) : (
                <ul className="divide-y divide-mai-100 dark:divide-mai-700">
                    {active.map((s) => (
                        <li key={s.id} className="flex items-center justify-between gap-4 py-3">
                            <div className="min-w-0">
                                <p className="flex items-center gap-2 text-sm font-medium
                                    text-mai-800 dark:text-mai-100">
                                    {s.device}
                                    {s.isCurrent && <Badge tone="green">Acest dispozitiv</Badge>}
                                </p>
                                <p className="mt-0.5 truncate text-xs text-mai-400">
                                    {s.ipAddress ?? 'IP necunoscut'}
                                    {' · deschisă '}{formatDate(s.createdAt)}
                                    {' · activă '}{formatRelative(s.lastSeenAt)}
                                </p>
                            </div>

                            {/*
                              Sesiunea curentă nu are buton de închidere: pentru ea
                              acțiunea corectă e deconectarea obișnuită, care șterge
                              și tokenurile locale. Un „revocă” aici ar lăsa clientul
                              cu tokenuri moarte în localStorage.
                            */}
                            {!s.isCurrent && (
                                <button
                                    type="button"
                                    disabled={busyId !== null}
                                    onClick={() => void handleRevoke(s)}
                                    className="shrink-0 rounded-lg px-3 py-1.5 text-xs font-semibold
                                        text-red-600 transition hover:bg-red-50 disabled:opacity-50
                                        dark:text-red-400 dark:hover:bg-red-900/30"
                                >
                                    {busyId === s.id ? 'Se închide…' : 'Închide'}
                                </button>
                            )}
                        </li>
                    ))}
                </ul>
            )}

            {otherActive > 0 && (
                <div className="mt-4 border-t border-mai-100 pt-4 dark:border-mai-700">
                    <Button
                        variant="secondary"
                        disabled={busyId !== null}
                        onClick={() => void handleRevokeOthers()}
                    >
                        <LogOut size={15} className="mr-2" />
                        {busyId === 'others'
                            ? 'Se închid…'
                            : `Închide celelalte ${otherActive} sesiuni`}
                    </Button>
                </div>
            )}

            {closed.length > 0 && (
                <details className="mt-4 border-t border-mai-100 pt-4 dark:border-mai-700">
                    <summary className="cursor-pointer text-sm font-medium text-mai-500
                        dark:text-mai-400">
                        Sesiuni închise recent ({closed.length})
                    </summary>

                    <ul className="mt-2 space-y-1.5">
                        {closed.map((s) => (
                            <li key={s.id} className="flex items-center gap-2 text-xs text-mai-400">
                                <ShieldAlert size={13} className="shrink-0" />
                                <span className="truncate">
                                    {s.device} · {s.ipAddress ?? 'IP necunoscut'} ·{' '}
                                    {s.revokedReason ?? 'expirată'} ·{' '}
                                    {formatDate(s.revokedAt ?? s.expiresAt)}
                                </span>
                            </li>
                        ))}
                    </ul>
                </details>
            )}
        </section>
    );
}

function formatDate(iso: string): string {
    return new Date(iso).toLocaleString('ro-MD', {
        day: '2-digit', month: '2-digit', year: 'numeric',
        hour: '2-digit', minute: '2-digit',
    });
}

/**
 * „acum 3 ore”. Pentru ultima activitate, distanța în timp spune mai mult decât
 * o dată absolută: utilizatorul vrea să știe dacă sesiunea e vie, nu la ce oră
 * exactă a fost folosită.
 */
function formatRelative(iso: string): string {
    const diffMs = Date.now() - new Date(iso).getTime();
    const minutes = Math.floor(diffMs / 60000);

    if (minutes < 2) return 'acum';
    if (minutes < 60) return `acum ${minutes} min`;

    const hours = Math.floor(minutes / 60);
    if (hours < 24) return `acum ${hours} h`;

    const days = Math.floor(hours / 24);
    return days === 1 ? 'ieri' : `acum ${days} zile`;
}
