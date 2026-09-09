import { Suspense } from 'react';
import { Outlet } from 'react-router-dom';
import ErrorBoundary from './ErrorBoundary';

/**
 * Învelișul fiecărei pagini: o barieră de erori proprie plus fallback-ul de
 * încărcare pentru chunk-ul lazy.
 *
 * De ce per pagină și nu doar la nivel de AppLayout: un ErrorBoundary așezat în
 * jurul întregului layout prinde orice crash, dar înlocuiește tot ecranul —
 * inclusiv sidebar-ul și butonul de deconectare. Utilizatorul rămâne blocat pe
 * un ecran de eroare din care singura ieșire e F5. Cu bariera aici, o pagină
 * care crapă lasă navigația intactă: se poate merge pe altă rută.
 *
 * `key` pe ErrorBoundary nu e necesar — starea de eroare se resetează prin
 * butonul de reîncercare — dar remontarea la schimbarea rutei ar fi o adăugire
 * naturală dacă apar cazuri în care eroarea persistă între pagini.
 */
export function RouteBoundary() {
    return (
        <ErrorBoundary>
            <Suspense fallback={<RouteFallback />}>
                <Outlet />
            </Suspense>
        </ErrorBoundary>
    );
}

/**
 * Afișat cât timp se descarcă chunk-ul paginii.
 *
 * Înălțime fixă, nu `min-h-screen`: fallback-ul apare în interiorul layout-ului,
 * unde sidebar-ul și topbar-ul sunt deja randate. Un fallback de ecran întreg ar
 * împinge conținutul și ar produce un salt vizibil la finalul încărcării.
 */
function RouteFallback() {
    return (
        <div className="flex min-h-[50vh] items-center justify-center" role="status" aria-live="polite">
            <div className="flex flex-col items-center gap-3">
                <div
                    className="h-8 w-8 animate-spin rounded-full border-2 border-slate-300
                        border-t-slate-700 dark:border-slate-600 dark:border-t-slate-300"
                />
                <p className="text-sm text-slate-500 dark:text-slate-400">Se încarcă pagina…</p>
            </div>
        </div>
    );
}

export default RouteBoundary;
