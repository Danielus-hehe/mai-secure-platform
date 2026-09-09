import { AuthProvider } from './context/AuthContext';
import { ToastProvider } from './context/ToastContext';
import { KeysProvider } from './context/KeysContext';
import { ThemeProvider } from './context/ThemeContext';
import { AppRoutes } from './routes/AppRoutes';

export default function App() {
    return (
        // ThemeProvider e EXTERIOR față de restul: tema trebuie disponibilă
        // înainte de orice componentă vizuală, inclusiv toast-urile.
        <ThemeProvider>
            <AuthProvider>
                <ToastProvider>
                    <KeysProvider>
                        <AppRoutes />
                    </KeysProvider>
                </ToastProvider>
            </AuthProvider>
        </ThemeProvider>
    );
}