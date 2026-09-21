/**
 * Active Directory (doar Administrator).
 *
 * Trei lucruri, în ordinea în care se fac la punerea în funcțiune:
 *   1. se verifică starea configurării și conexiunea la controlerul de domeniu;
 *   2. se previzualizează importul structurii din unitățile organizatorice;
 *   3. se confirmă exact ce se importă.
 *
 * Importul nu se aplică niciodată singur. Structura organizatorică decide cine
 * primește documentele interne, deci nu se rescrie automat pe baza a ce se
 * întâmplă să conțină AD-ul într-o zi anume.
 */

import { useCallback, useEffect, useMemo, useState } from 'react';
import {
    Network, RefreshCw, ServerCog, ShieldCheck, ShieldAlert, Loader2,
    Download, CheckSquare, Square, AlertTriangle, Copy,
} from 'lucide-react';
import PageHeader from '../../components/ui/PageHeader';
import Button from '../../components/ui/Button';
import Badge from '../../components/ui/Badge';
import EmptyState from '../../components/ui/EmptyState';
import { useToast } from '../../context/ToastContext';
import { apiErrorMessage } from '../../api/errors';
import {
    getStatus, testConnection, previewOrgUnits, importOrgUnits,
    type DirectoryStatus, type DirectoryProbe, type OrgUnitImportPreview,
} from '../../api/directory';

const ACTION_TONE = {
    Create: 'green',
    Update: 'amber',
    Unchanged: 'gray',
} as const;

const ACTION_LABEL = {
    Create: 'se creează',
    Update: 'se actualizează',
    Unchanged: 'neschimbată',
} as const;

export default function DirectoryPage() {
    const toast = useToast();

    const [status, setStatus] = useState<DirectoryStatus | null>(null);
    const [loading, setLoading] = useState(true);
    const [probe, setProbe] = useState<DirectoryProbe | null>(null);
    const [testing, setTesting] = useState(false);

    const [preview, setPreview] = useState<OrgUnitImportPreview | null>(null);
    const [previewing, setPreviewing] = useState(false);
    const [importing, setImporting] = useState(false);
    const [selected, setSelected] = useState<Set<string>>(new Set());

    const load = useCallback(async () => {
        setLoading(true);
        try {
            setStatus(await getStatus());
        } catch (e: unknown) {
            toast.error(apiErrorMessage(e, 'Starea integrării nu a putut fi citită.'));
        } finally {
            setLoading(false);
        }
    }, [toast]);

    useEffect(() => { void load(); }, [load]);

    const handleTest = async () => {
        setTesting(true);
        try {
            const result = await testConnection();
            setProbe(result);
            if (result.reachable && result.serviceAccountBound) {
                toast.success('Controlerul de domeniu răspunde, contul de serviciu funcționează.');
            } else if (result.reachable) {
                toast.warning('Serverul răspunde, dar contul de serviciu nu s-a autentificat.');
            } else {
                toast.error('Controlerul de domeniu nu a răspuns.');
            }
        } catch (e: unknown) {
            toast.error(apiErrorMessage(e, 'Testul de conexiune a eșuat.'));
        } finally {
            setTesting(false);
        }
    };

    const handlePreview = async () => {
        setPreviewing(true);
        try {
            const result = await previewOrgUnits();
            setPreview(result);

            // Implicit se bifează exact ce s-ar schimba. Rândurile neschimbate
            // rămân în listă ca administratorul să vadă întreaga structură, dar
            // nu au ce fi importate.
            setSelected(new Set(
                result.items.filter((i) => i.action !== 'Unchanged').map((i) => i.dn)
            ));
        } catch (e: unknown) {
            toast.error(apiErrorMessage(e, 'Previzualizarea a eșuat.'));
        } finally {
            setPreviewing(false);
        }
    };

    const handleImport = async () => {
        if (selected.size === 0) {
            toast.warning('Nu ați bifat nicio subdiviziune.');
            return;
        }

        setImporting(true);
        try {
            const result = await importOrgUnits([...selected]);
            toast.success(result.message);
            setPreview(null);
            setSelected(new Set());
            await load();
        } catch (e: unknown) {
            toast.error(apiErrorMessage(e, 'Importul a eșuat.'));
        } finally {
            setImporting(false);
        }
    };

    const toggle = (dn: string) => {
        setSelected((prev) => {
            const next = new Set(prev);
            if (next.has(dn)) next.delete(dn); else next.add(dn);
            return next;
        });
    };

    const changeable = useMemo(
        () => preview?.items.filter((i) => i.action !== 'Unchanged') ?? [],
        [preview]
    );

    if (loading) {
        return (
            <div className="flex min-h-[40vh] items-center justify-center text-mai-400">
                <Loader2 size={20} className="animate-spin" />
            </div>
        );
    }

    return (
        <div className="space-y-6">
            <PageHeader
                title="Active Directory"
                subtitle="Autentificare cu contul de domeniu, roluri din grupuri AD, import al structurii"
                actions={
                    <Button variant="secondary" onClick={() => void load()}>
                        <RefreshCw size={16} />
                        Reîncarcă
                    </Button>
                }
            />

            {!status?.enabled && (
                <div className="rounded-xl border border-amber-200 bg-amber-50 p-5 dark:border-amber-700/50 dark:bg-amber-900/20">
                    <p className="flex items-center gap-2 font-semibold text-amber-800 dark:text-amber-300">
                        <AlertTriangle size={18} />
                        Integrarea este dezactivată
                    </p>
                    <p className="mt-2 text-sm leading-relaxed text-amber-800/90 dark:text-amber-300/90">
                        Conturile locale funcționează normal. Pentru autentificarea cu contul de
                        domeniu, completați secțiunea LDAP din <code>.env</code> (cel puțin
                        LDAP_ENABLED, LDAP_HOST, LDAP_BASE_DN) și reporniți API-ul. Pașii completi,
                        inclusiv ridicarea unui Samba AD de test, sunt în <code>docs/LDAP-AD.md</code>.
                    </p>
                </div>
            )}

            {/* ── Configurarea curentă ───────────────────────────────────── */}
            <section className="rounded-xl border border-mai-100 bg-white p-5 dark:border-mai-700 dark:bg-mai-800">
                <h2 className="flex items-center gap-2 text-sm font-bold text-mai-900 dark:text-white">
                    <ServerCog size={16} />
                    Configurare
                </h2>

                <dl className="mt-4 grid gap-x-6 gap-y-3 sm:grid-cols-2">
                    {Object.entries(status?.configuration ?? {}).map(([key, value]) => (
                        <div key={key} className="min-w-0">
                            <dt className="text-[11px] uppercase tracking-wider text-mai-400">{key}</dt>
                            <dd className="truncate text-sm text-mai-800 dark:text-mai-100" title={value}>
                                {value || '-'}
                            </dd>
                        </div>
                    ))}
                </dl>

                <div className="mt-5 border-t border-mai-100 pt-4 dark:border-mai-700">
                    <p className="text-[11px] uppercase tracking-wider text-mai-400">
                        Grupuri AD mapate pe roluri
                    </p>

                    {status?.roleMappings.length ? (
                        <ul className="mt-2 space-y-1.5">
                            {status.roleMappings.map((m) => (
                                <li key={m.group} className="flex flex-wrap items-center gap-2 text-sm">
                                    <code className="truncate text-mai-700 dark:text-mai-200">{m.group}</code>
                                    <Badge tone="blue">{m.role}</Badge>
                                </li>
                            ))}
                        </ul>
                    ) : (
                        <p className="mt-2 text-sm text-mai-400">
                            Niciun grup mapat: toate conturile de domeniu primesc rolul{' '}
                            <strong>{status?.defaultRole}</strong>.
                        </p>
                    )}
                </div>

                <div className="mt-5 flex flex-wrap gap-4 border-t border-mai-100 pt-4 text-sm dark:border-mai-700">
                    <span className="text-mai-500 dark:text-mai-300">
                        Conturi legate de domeniu: <strong>{status?.linkedAccounts ?? 0}</strong>
                    </span>
                    <span className="text-mai-500 dark:text-mai-300">
                        Subdiviziuni importate din AD: <strong>{status?.importedOrgUnits ?? 0}</strong>
                    </span>
                </div>
            </section>

            {/* ── Testul de conexiune ────────────────────────────────────── */}
            <section className="rounded-xl border border-mai-100 bg-white p-5 dark:border-mai-700 dark:bg-mai-800">
                <div className="flex flex-wrap items-start justify-between gap-3">
                    <div>
                        <h2 className="text-sm font-bold text-mai-900 dark:text-white">Test de conexiune</h2>
                        <p className="mt-1 text-sm text-mai-400">
                            Deschide o conexiune reală, validează certificatul și încearcă bind-ul
                            contului de serviciu.
                        </p>
                    </div>
                    <Button onClick={() => void handleTest()} disabled={testing || !status?.enabled}>
                        {testing ? <Loader2 size={16} className="animate-spin" /> : <ShieldCheck size={16} />}
                        Testează
                    </Button>
                </div>

                {probe && (
                    <div className="mt-4 space-y-2 rounded-lg bg-mai-50 p-4 text-sm dark:bg-mai-900/40">
                        <p className="flex items-center gap-2">
                            {probe.reachable
                                ? <ShieldCheck size={16} className="text-green-600" />
                                : <ShieldAlert size={16} className="text-red-500" />}
                            {probe.reachable ? 'Serverul răspunde și certificatul e acceptat.' : 'Serverul nu a răspuns.'}
                        </p>

                        {probe.reachable && (
                            <p className="text-mai-500 dark:text-mai-300">
                                Cont de serviciu: {probe.serviceAccountBound ? 'autentificat' : 'neautentificat'} ·
                                conturi vizibile: {probe.userCount} · unități organizatorice: {probe.orgUnitCount}
                            </p>
                        )}

                        {probe.serverCertificateThumbprint && (
                            <div className="flex flex-wrap items-center gap-2 text-mai-500 dark:text-mai-300">
                                <span className="break-all">
                                    Amprentă SHA-256: <code>{probe.serverCertificateThumbprint}</code>
                                </span>
                                <button
                                    type="button"
                                    className="inline-flex items-center gap-1 text-xs text-mai-600 hover:underline dark:text-mai-300"
                                    onClick={() => {
                                        void navigator.clipboard?.writeText(probe.serverCertificateThumbprint ?? '');
                                        toast.success('Amprenta a fost copiată.');
                                    }}
                                >
                                    <Copy size={12} />
                                    copiază
                                </button>
                            </div>
                        )}

                        {probe.error && (
                            <p className="rounded bg-red-50 px-3 py-2 text-red-700 dark:bg-red-900/30 dark:text-red-400">
                                {probe.error}
                            </p>
                        )}
                    </div>
                )}
            </section>

            {/* ── Importul structurii ────────────────────────────────────── */}
            <section className="rounded-xl border border-mai-100 bg-white p-5 dark:border-mai-700 dark:bg-mai-800">
                <div className="flex flex-wrap items-start justify-between gap-3">
                    <div>
                        <h2 className="flex items-center gap-2 text-sm font-bold text-mai-900 dark:text-white">
                            <Network size={16} />
                            Import structură din unitățile organizatorice
                        </h2>
                        <p className="mt-1 max-w-2xl text-sm text-mai-400">
                            Importul nu șterge nimic și nu mută utilizatori. Subdiviziunile care
                            nu mai apar în AD sunt doar semnalate.
                        </p>
                    </div>

                    <div className="flex gap-2">
                        <Button
                            variant="secondary"
                            onClick={() => void handlePreview()}
                            disabled={previewing || !status?.enabled}
                        >
                            {previewing ? <Loader2 size={16} className="animate-spin" /> : <RefreshCw size={16} />}
                            Previzualizează
                        </Button>

                        {preview && changeable.length > 0 && (
                            <Button onClick={() => void handleImport()} disabled={importing}>
                                {importing ? <Loader2 size={16} className="animate-spin" /> : <Download size={16} />}
                                Importă ({selected.size})
                            </Button>
                        )}
                    </div>
                </div>

                {preview && (
                    <div className="mt-4 space-y-4">
                        <p className="text-sm text-mai-500 dark:text-mai-300">
                            Sub <code>{preview.searchBase}</code>: {preview.found} unități găsite ·{' '}
                            {preview.toCreate} de creat · {preview.toUpdate} de actualizat ·{' '}
                            {preview.unchanged} neschimbate
                        </p>

                        {preview.warnings.map((w) => (
                            <p
                                key={w}
                                className="rounded-lg bg-amber-50 px-3 py-2 text-sm text-amber-800 dark:bg-amber-900/20 dark:text-amber-300"
                            >
                                {w}
                            </p>
                        ))}

                        {preview.items.length === 0 ? (
                            <EmptyState
                                icon={Network}
                                title="Nicio unitate organizatorică"
                                description="Verificați LDAP_OU_SEARCH_BASE: sub baza configurată nu există obiecte organizationalUnit."
                            />
                        ) : (
                            <ul className="divide-y divide-mai-100 overflow-hidden rounded-lg border border-mai-100 dark:divide-mai-700 dark:border-mai-700">
                                {preview.items.map((item) => {
                                    const selectable = item.action !== 'Unchanged';
                                    const checked = selected.has(item.dn);

                                    return (
                                        <li
                                            key={item.dn}
                                            className="flex items-start gap-3 bg-white px-4 py-3 dark:bg-mai-800"
                                        >
                                            <button
                                                type="button"
                                                disabled={!selectable}
                                                onClick={() => toggle(item.dn)}
                                                className="mt-0.5 text-mai-500 disabled:opacity-30 dark:text-mai-300"
                                                aria-label={checked ? 'Exclude din import' : 'Include în import'}
                                            >
                                                {checked ? <CheckSquare size={16} /> : <Square size={16} />}
                                            </button>

                                            <div className="min-w-0 flex-1">
                                                <div className="flex flex-wrap items-center gap-2">
                                                    <span
                                                        className="truncate text-sm font-medium text-mai-900 dark:text-white"
                                                        style={{ paddingLeft: `${item.depth * 12}px` }}
                                                    >
                                                        {item.name}
                                                    </span>
                                                    <Badge tone={ACTION_TONE[item.action]}>
                                                        {ACTION_LABEL[item.action]}
                                                    </Badge>
                                                    <Badge tone="gray">nivel {item.rank}</Badge>
                                                </div>

                                                <p className="mt-0.5 truncate text-xs text-mai-400" title={item.dn}>
                                                    {item.dn}
                                                </p>

                                                {item.note && (
                                                    <p className="mt-1 text-xs text-amber-700 dark:text-amber-400">
                                                        {item.note}
                                                    </p>
                                                )}
                                            </div>
                                        </li>
                                    );
                                })}
                            </ul>
                        )}
                    </div>
                )}
            </section>
        </div>
    );
}
