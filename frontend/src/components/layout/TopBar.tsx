import { Menu, LogOut, Moon, Sun } from 'lucide-react';
import { useAuth } from '../../context/AuthContext';
import { useTheme } from '../../context/ThemeContext';
import { ROLE_LABELS, ROLE_BADGE_CLASSES } from '../../utils/constants';
import NotificationsDropdown from '../ui/NotificationsDropdown';

export default function TopBar({ onMenuClick }: { onMenuClick: () => void }) {
    const { user, logout } = useAuth();
    const { theme, toggle } = useTheme();

    return (
        <header className="h-14 sm:h-16 bg-white dark:bg-mai-900 border-b border-mai-100 dark:border-mai-700
            flex items-center px-3 sm:px-6 gap-2 sm:gap-4 sticky top-0 z-30 shadow-sm dark:shadow-none">
            <button onClick={onMenuClick} className="lg:hidden text-mai-700 dark:text-mai-200 p-1">
                <Menu size={22} />
            </button>

            <p className="text-sm text-mai-800 dark:text-mai-200 font-medium hidden md:block truncate">
                Sistem de Gestiune Documente și Transferuri Securizate
            </p>

            <div className="ml-auto flex items-center gap-2 sm:gap-4">
                {/* Dark mode toggle */}
                <button
                    onClick={toggle}
                    className="text-mai-500 hover:text-mai-700 dark:text-mai-300 dark:hover:text-white
                        transition-colors p-1.5 rounded-lg hover:bg-mai-100 dark:hover:bg-mai-800"
                    title={theme === 'dark' ? 'Comută pe tema deschisă' : 'Comută pe tema întunecată'}
                >
                    {theme === 'dark' ? <Sun size={18} /> : <Moon size={18} />}
                </button>

                <NotificationsDropdown />

                <div className="h-8 w-px bg-mai-100 dark:bg-mai-700 hidden sm:block" />

                {/* User info — ascuns pe ecrane mici */}
                <div className="text-right leading-tight hidden sm:block">
                    <p className="text-sm font-semibold text-mai-900 dark:text-white">{user?.fullName}</p>
                    <span className={`inline-block text-[10px] px-2 py-0.5 rounded-full font-semibold
                        ${user ? ROLE_BADGE_CLASSES[user.role] : ''}`}>
                        {user ? ROLE_LABELS[user.role] : ''}
                    </span>
                </div>

                <button
                    onClick={logout}
                    className="flex items-center gap-2 text-sm text-mai-600 dark:text-mai-300
                        hover:text-red-600 dark:hover:text-red-400 transition-colors p-1"
                    title="Deconectare"
                >
                    <LogOut size={18} />
                    <span className="hidden md:inline">Deconectare</span>
                </button>
            </div>
        </header>
    );
}