import { Routes, Route, Navigate } from 'react-router-dom';
import LoginPage          from '../pages/auth/LoginPage';
import DashboardPage      from '../pages/dashboard/DashboardPage';
import TransfersPage      from '../pages/transfers/TransfersPage';
import DocumentsPage      from '../pages/documents/DocumentsPage';
import ProfilePage        from '../pages/profile/ProfilePage';
import AuditPage          from '../pages/audit/AuditPage';
import UsersPage          from '../pages/users/UsersPage';
import AdminDashboardPage from '../pages/admin/AdminDashboardPage';
import { ProtectedRoute } from '../components/ProtectedRoute';
import { AppLayout }      from '../components/layout/AppLayout';

// UserRole enum backend: Utilizator=1, SefDirectie=2, Administrator=3

export function AppRoutes() {
    return (
        <Routes>
            <Route path="/"      element={<Navigate to="/login" replace />} />
            <Route path="/login" element={<LoginPage />} />

            {/* Toate rutele protejate învelite în AppLayout (sidebar + topbar) */}
            <Route element={<ProtectedRoute />}>
                <Route element={<AppLayout />}>

                    {/* Rute accesibile oricărui utilizator autentificat */}
                    <Route path="/dashboard" element={<DashboardPage />} />
                    <Route path="/transfers" element={<TransfersPage />} />
                    <Route path="/documents" element={<DocumentsPage />} />
                    <Route path="/profile"   element={<ProfilePage />} />

                    {/* Supervizare: SefDirectie(2) + Administrator(3) */}
                    <Route element={<ProtectedRoute allowedRoles={[2, 3]} />}>
                        <Route path="/audit" element={<AuditPage />} />
                    </Route>

                    {/* Administrare: exclusiv Administrator(3) */}
                    <Route element={<ProtectedRoute allowedRoles={[3]} />}>
                        <Route path="/users" element={<UsersPage />} />
                        <Route path="/admin" element={<AdminDashboardPage />} />
                    </Route>

                </Route>
            </Route>

            <Route path="*" element={<Navigate to="/login" replace />} />
        </Routes>
    );
}