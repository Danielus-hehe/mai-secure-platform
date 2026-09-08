import { AuthProvider } from './context/AuthContext';
import { ToastProvider } from './context/ToastContext';
import { KeysProvider } from './context/KeysContext';
import { AppRoutes } from './routes/AppRoutes';

export default function App() {
    return (
        // KeysProvider trebuie sa fie INTERIOR fata de AuthProvider: are nevoie
        // de useAuth ca sa stie cand s-a schimbat contul si sa uite cheile.
        <AuthProvider>
            <ToastProvider>
                <KeysProvider>
                    <AppRoutes />
                </KeysProvider>
            </ToastProvider>
        </AuthProvider>
    );
}