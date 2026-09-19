import {
    createContext,
    useCallback,
    useContext,
    useEffect,
    useMemo,
    useState,
    type ReactNode,
} from 'react';

type Theme = 'light' | 'dark';

interface ThemeContextValue {
    theme: Theme;
    toggle: () => void;
    setTheme: (t: Theme) => void;
}

const ThemeContext = createContext<ThemeContextValue | null>(null);

const STORAGE_KEY = 'sgdm-theme';

/**
 * Determină tema inițială:
 *   1. Ce a ales utilizatorul data trecută (localStorage)
 *   2. Preferința sistemului de operare (prefers-color-scheme)
 *   3. Fallback pe „light"
 */
function resolveInitial(): Theme {
    try {
        const stored = localStorage.getItem(STORAGE_KEY);
        if (stored === 'dark' || stored === 'light') return stored;
    } catch {
        /* localStorage inaccesibil - continuăm cu fallback */
    }

    if (window.matchMedia?.('(prefers-color-scheme: dark)').matches) return 'dark';
    return 'light';
}

function applyToDocument(theme: Theme) {
    const root = document.documentElement;
    if (theme === 'dark') {
        root.classList.add('dark');
    } else {
        root.classList.remove('dark');
    }
}

export function ThemeProvider({ children }: { children: ReactNode }) {
    const [theme, setThemeState] = useState<Theme>(resolveInitial);

    // Aplicăm clasa pe <html> la fiecare schimbare
    useEffect(() => {
        applyToDocument(theme);
        try { localStorage.setItem(STORAGE_KEY, theme); } catch { /* ignore */ }
    }, [theme]);

    // Reacționăm la schimbarea preferinței OS, dacă utilizatorul n-a ales manual
    useEffect(() => {
        const mq = window.matchMedia('(prefers-color-scheme: dark)');
        const handler = (e: MediaQueryListEvent) => {
            // Respectăm preferința OS doar dacă nu există o alegere explicită
            const stored = localStorage.getItem(STORAGE_KEY);
            if (!stored) {
                setThemeState(e.matches ? 'dark' : 'light');
            }
        };
        mq.addEventListener('change', handler);
        return () => mq.removeEventListener('change', handler);
    }, []);

    const setTheme = useCallback((t: Theme) => setThemeState(t), []);
    const toggle = useCallback(() => setThemeState(prev => prev === 'dark' ? 'light' : 'dark'), []);

    const value = useMemo(() => ({ theme, toggle, setTheme }), [theme, toggle, setTheme]);

    return (
        <ThemeContext.Provider value={value}>
            {children}
        </ThemeContext.Provider>
    );
}

export function useTheme(): ThemeContextValue {
    const ctx = useContext(ThemeContext);
    if (!ctx) throw new Error('useTheme trebuie folosit înăuntrul ThemeProvider');
    return ctx;
}