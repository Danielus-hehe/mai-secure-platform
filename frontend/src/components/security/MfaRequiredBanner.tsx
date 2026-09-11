/**
 * Anunț afișat conturilor privilegiate fără al doilea factor, când serverul are
 * TwoFactor:RequiredForPrivilegedRoles = true.
 *
 * Paginile de administrare primesc atunci 403 de la API. Fără anunț,
 * utilizatorul ar vedea doar erori pe fiecare pagină, fără să știe cauza și
 * pasul următor. Cu opțiunea dezactivată (implicit), anunțul nu apare niciodată.
 */

import { Link } from 'react-router-dom';
import { ShieldAlert } from 'lucide-react';
import { useAuth } from '../../context/AuthContext';

export default function MfaRequiredBanner() {
    const { user } = useAuth();

    if (!user?.mfaEnrollmentRequired) return null;

    return (
        <div
            role="status"
            className="mb-4 flex flex-col gap-3 rounded-xl border border-amber-200 dark:border-amber-700/50
                       bg-amber-50 dark:bg-amber-900/20 px-4 py-3 sm:flex-row sm:items-center sm:justify-between"
        >
            <div className="flex items-start gap-3">
                <ShieldAlert size={18} className="mt-0.5 shrink-0 text-amber-600 dark:text-amber-400" />
                <p className="text-sm leading-relaxed text-amber-900 dark:text-amber-200">
                    Rolul dumneavoastră cere autentificare în doi pași. Până o activați,
                    paginile de administrare și publicarea documentelor sunt blocate.
                </p>
            </div>
            <Link
                to="/profile"
                className="shrink-0 rounded-lg bg-amber-600 px-3 py-2 text-center text-sm font-semibold text-white
                           transition-colors hover:bg-amber-500 dark:bg-amber-700 dark:hover:bg-amber-600"
            >
                Activează 2FA
            </Link>
        </div>
    );
}
