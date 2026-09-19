import { useEffect, useState } from 'react';
import { NavLink, useLocation } from 'react-router-dom';
import {
    LayoutDashboard, ArrowLeftRight, Landmark, FileStack, Network,
    Users, ScrollText, ShieldCheck, UserCircle, X,
} from 'lucide-react';
import { useAuth } from '../../context/AuthContext';
import { getPendingAcknowledgements } from '../../api/internalDocuments';

const NAV_MAIN = [
    // FIX: era to: '/' care redirecta la /login; acum merge corect la /dashboard
    { to: '/dashboard',  label: 'Panou principal',        icon: LayoutDashboard, end: true },
    { to: '/transfers',  label: 'Transferuri securizate',  icon: ArrowLeftRight   },
    { to: '/documents',  label: 'Documente normative',     icon: Landmark         },
    { to: '/internal-documents', label: 'Documente interne', icon: FileStack       },
];

const NAV_SEF = [
    { to: '/audit',      label: 'Jurnal de audit',         icon: ScrollText       },
];

const NAV_ADMIN = [
    { to: '/users',      label: 'Gestiune utilizatori',    icon: Users            },
    { to: '/org-units',  label: 'Structura organizatorică', icon: Network         },
    { to: '/admin',      label: 'Administrare & rapoarte',  icon: ShieldCheck      },
];

export default function Sidebar({ open, onClose }: { open: boolean; onClose: () => void }) {
    const { user } = useAuth();

    // role este string normalizat din AuthContext ('ADMINISTRATOR', 'SEF_DIRECTIE', 'UTILIZATOR')
    const isAdmin = user?.role === 'ADMINISTRATOR';
    const isSef   = isAdmin || user?.role === 'SEF_DIRECTIE';

    // Câte documente interne îmi cer „Luat la cunoștință”. Se reîmprospătează la
    // fiecare schimbare de pagină: după o confirmare, insigna scade imediat ce
    // utilizatorul navighează, fără un sondaj continuu al serverului.
    const location = useLocation();
    const [pendingAck, setPendingAck] = useState(0);
    useEffect(() => {
        if (!user) return;
        let cancelled = false;
        getPendingAcknowledgements()
            .then((n) => { if (!cancelled) setPendingAck(n); })
            .catch(() => { /* insigna e opțională */ });
        return () => { cancelled = true; };
    }, [user, location.pathname]);

    const cls = ({ isActive }: { isActive: boolean }) =>
        `flex items-center gap-3 rounded-lg px-3 py-2.5 text-sm transition-colors ${
            isActive
                ? 'bg-white/20 text-white font-semibold shadow-sm'
                : 'text-mai-200 hover:bg-white/15 hover:text-white'
        }`;

    return (
        <>
            {open && (
                <div className="fixed inset-0 z-40 bg-black/40 lg:hidden" onClick={onClose} />
            )}
            <aside className={`fixed inset-y-0 left-0 z-50 w-64 bg-mai-900
                border-r border-mai-700 flex flex-col
                transform transition-transform lg:translate-x-0
                ${open ? 'translate-x-0' : '-translate-x-full'}`}>

                {/* Header */}
                <div className="flex items-center gap-3 px-5 h-16 border-b border-mai-700 shrink-0">
                    <div className="w-10 h-10 rounded-full bg-gold-500 flex items-center justify-center shadow-sm">
                        <span className="text-mai-900 font-bold text-sm">MAI</span>
                    </div>
                    <div className="leading-tight">
                        <p className="text-white text-sm font-bold">SGDM</p>
                        <p className="text-mai-200 text-[11px]">Ministerul Afacerilor Interne</p>
                    </div>
                    <button onClick={onClose} className="ml-auto text-mai-200 hover:text-white lg:hidden">
                        <X size={18} />
                    </button>
                </div>

                {/* Nav */}
                <nav className="flex-1 overflow-y-auto p-4 space-y-1">
                    {NAV_MAIN.map(item => (
                        <NavLink key={item.to} to={item.to} end={item.end} className={cls}>
                            <item.icon size={18} />
                            {item.label}
                            {item.to === '/internal-documents' && pendingAck > 0 && (
                                <span
                                    className="ml-auto rounded-full bg-gold-500 px-2 py-0.5 text-[10px] font-bold text-mai-900"
                                    title={`${pendingAck} documente de confirmat`}
                                >
                                    {pendingAck}
                                </span>
                            )}
                        </NavLink>
                    ))}

                    {isSef && (
                        <>
                            <p className="px-3 pt-5 pb-1.5 text-[11px] uppercase tracking-wider text-mai-400 font-semibold">
                                Supervizare
                            </p>
                            {NAV_SEF.map(item => (
                                <NavLink key={item.to} to={item.to} className={cls}>
                                    <item.icon size={18} />
                                    {item.label}
                                </NavLink>
                            ))}
                        </>
                    )}

                    {isAdmin && (
                        <>
                            <p className="px-3 pt-5 pb-1.5 text-[11px] uppercase tracking-wider text-mai-400 font-semibold">
                                Administrare
                            </p>
                            {NAV_ADMIN.map(item => (
                                <NavLink key={item.to} to={item.to} className={cls}>
                                    <item.icon size={18} />
                                    {item.label}
                                </NavLink>
                            ))}
                        </>
                    )}
                </nav>

                {/* Footer — profil + info */}
                <div className="border-t border-mai-700 p-4 space-y-1 shrink-0">
                    <NavLink to="/profile" className={cls}>
                        <UserCircle size={18} />
                        Profilul meu
                    </NavLink>
                    <p className="px-3 pt-2 text-[10px] text-mai-500 leading-relaxed">
                        Acces restricționat — rețea intranet MAI
                    </p>
                </div>
            </aside>
        </>
    );
}