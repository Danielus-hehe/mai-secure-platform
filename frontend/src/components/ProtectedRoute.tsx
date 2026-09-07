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
    const { user, isAuthenticated } = useAuth();

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