import { Menu, LogOut, Bell } from 'lucide-react';
import { useAuth } from '../../context/AuthContext';
import { ROLE_LABELS, ROLE_BADGE_CLASSES } from '../../utils/constants';

export default function TopBar({ onMenuClick }: { onMenuClick: () => void }) {
    const { user, logout } = useAuth();

    return (
        <header className="h-16 bg-white border-b border-mai-100 flex items-center px-6 gap-4 sticky top-0 z-30">
            <button onClick={onMenuClick} className="lg:hidden text-mai-700">
                <Menu size={22} />
            </button>

            <p className="text-sm text-mai-800 font-medium">
                Sistem de Gestiune Documente și Transferuri Securizate
            </p>

            <div className="ml-auto flex items-center gap-4">
                <button className="relative text-mai-700 hover:text-mai-900">
                    <Bell size={20} />
                    <span className="absolute -top-1 -right-1 w-2.5 h-2.5 rounded-full bg-gold-500" />
                </button>

                <div className="h-8 w-px bg-mai-100" />

                <div className="text-right leading-tight">
                    <p className="text-sm font-semibold text-mai-900">{user?.fullName}</p>
                    <span
                        className={`inline-block text-[10px] px-2 py-0.5 rounded-full font-semibold ${
                            user ? ROLE_BADGE_CLASSES[user.role] : ''
                        }`}
                    >
            {user ? ROLE_LABELS[user.role] : ''}
          </span>
                </div>

                <button
                    onClick={logout}
                    className="flex items-center gap-2 text-sm text-mai-700 hover:text-red-600"
                >
                    <LogOut size={18} />
                    <span className="hidden sm:inline">Deconectare</span>
                </button>
            </div>
        </header>
    );
}