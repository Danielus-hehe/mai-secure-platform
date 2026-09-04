import { Navigate, Outlet } from 'react-router-dom';
import { useAuth } from '../../context/AuthContext';
import type { Role } from '../../types';

interface Props {
    minRole?: Role;
}

export function ProtectedRoute({ minRole }: Props) {
    const { isAuthenticated, user } = useAuth();

    if (!isAuthenticated || !user) return <Navigate to="/login" replace />;

    if (minRole) {
        const hierarchy = { UTILIZATOR: 1, SEF_DIRECTIE: 2, ADMINISTRATOR: 3 } as const;
        if (hierarchy[user.role] < hierarchy[minRole]) {
            return <Navigate to="/" replace />;
        }
    }
    return <Outlet />;
}