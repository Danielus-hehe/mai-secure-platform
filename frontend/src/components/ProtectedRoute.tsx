import { Navigate, Outlet } from 'react-router-dom';
import { useAuth } from '../context/AuthContext';

// Enum backend: Utilizator=1, SefDirectie=2, Administrator=3
const ROLE_TO_NUM: Record<string, number> = {
    'UTILIZATOR':    1,
    'SEF_DIRECTIE':  2,
    'ADMINISTRATOR': 3,
};

interface ProtectedRouteProps {
    allowedRoles?: number[];
}

export function ProtectedRoute({ allowedRoles }: ProtectedRouteProps) {
    const { user, isAuthenticated, isInitializing } = useAuth();

    // La refresh de pagina, AuthContext incearca sa reia sesiunea din refresh token.
    // Fara acest guard, redirectionam la /login inainte ca incercarea sa se termine
    // si utilizatorul e delogat de fiecare data cand da F5.
    if (isInitializing) {
        return (
            <div className="flex h-screen items-center justify-center">
                <div className="flex flex-col items-center gap-3">
                    <div className="h-8 w-8 animate-spin rounded-full border-2 border-slate-300 border-t-slate-700" />
                    <p className="text-sm text-slate-500">Se verifică sesiunea…</p>
                </div>
            </div>
        );
    }

    if (!isAuthenticated || !user) {
        return <Navigate to="/login" replace />;
    }

    if (allowedRoles) {
        const userRoleNum =
            typeof user.role === 'number'
                ? user.role
                : (ROLE_TO_NUM[user.role] ?? -1);

        const hasAccess = allowedRoles.includes(userRoleNum);
        if (!hasAccess) return <Navigate to="/dashboard" replace />;
    }

    return <Outlet />;
}