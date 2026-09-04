import { NavLink } from 'react-router-dom';
import {
    LayoutDashboard, ArrowLeftRight, Landmark,
    Users, ScrollText, ShieldCheck, UserCircle, X,
} from 'lucide-react';
import { useAuth } from '../../context/AuthContext';

const NAV_MAIN = [
    { to: '/',          label: 'Panou principal',       icon: LayoutDashboard, end: true },
    { to: '/transfers', label: 'Transferuri securizate', icon: ArrowLeftRight  },
    { to: '/documents', label: 'Documente normative',    icon: Landmark        },
];

const NAV_SEF = [
    { to: '/audit',     label: 'Jurnal de audit',        icon: ScrollText      },
];

const NAV_ADMIN = [
    { to: '/users',     label: 'Gestiune utilizatori',   icon: Users           },
    { to: '/admin',     label: 'Administrare & rapoarte', icon: ShieldCheck     },
];

export default function Sidebar({ open, onClose }: { open: boolean; onClose: () => void }) {
    const { user } = useAuth();
    const isAdmin = user?.role === 'ADMINISTRATOR';
    const isSef   = isAdmin || user?.role === 'SEF_DIRECTIE';

    // hover:bg-white/15 — mult mai vizibil decât white/5
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
