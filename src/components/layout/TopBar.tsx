import { Menu, LogOut } from 'lucide-react';
import { useAuth } from '../../context/AuthContext';
import { ROLE_LABELS, ROLE_BADGE_CLASSES } from '../../utils/constants';
import NotificationsDropdown from '../ui/NotificationsDropdown';

export default function TopBar({ onMenuClick }: { onMenuClick: () => void }) {
    const { user, logout } = useAuth();

    return (
        <header className="h-16 bg-white border-b border-mai-100 flex items-center
            px-6 gap-4 sticky top-0 z-30 shadow-sm">
            <button onClick={onMenuClick} className="lg:hidden text-mai-700">
                <Menu size={22} />
            </button>

            <p className="text-sm text-mai-800 font-medium hidden sm:block">
                Sistem de Gestiune Documente și Transferuri Securizate
            </p>

            <div className="ml-auto flex items-center gap-4">
                {/* Notifications bell with dropdown */}
                <NotificationsDropdown />

                <div className="h-8 w-px bg-mai-100" />

                {/* User info */}
                <div className="text-right leading-tight">
                    <p className="text-sm font-semibold text-mai-900">{user?.fullName}</p>
                    <span className={`inline-block text-[10px] px-2 py-0.5 rounded-full font-semibold
                        ${user ? ROLE_BADGE_CLASSES[user.role] : ''}`}>
                        {user ? ROLE_LABELS[user.role] : ''}
                    </span>
                </div>

                {/* Logout */}
                <button
                    onClick={logout}
                    className="flex items-center gap-2 text-sm text-mai-600 hover:text-red-600
                        transition-colors"
                    title="Deconectare"
                >
                    <LogOut size={18} />
                    <span className="hidden sm:inline">Deconectare</span>
                </button>
            </div>
        </header>
    );
}
