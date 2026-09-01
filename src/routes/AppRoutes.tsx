import { Routes, Route, Navigate  } from 'react-router-dom';
import { ProtectedRoute } from '../components/auth/ProtectedRoute';
import { AppLayout } from '../components/layout/AppLayout';
import LoginPage from '../pages/auth/LoginPage';
import DashboardPage from '../pages/dashboard/DashboardPage';
import TransfersPage from '../pages/transfers/TransfersPage';
import DocumentsPage from '../pages/documents/DocumentsPage';
import UsersPage from '../pages/users/UsersPage';
import AuditPage from '../pages/audit/AuditPage';
import AdminDashboardPage from '../pages/admin/AdminDashboardPage';

export function AppRoutes() {
    
    return (
        <Routes>
            <Route path="/login" element={<LoginPage />} />

            <Route element={<ProtectedRoute />}>
                <Route element={<AppLayout />}>
                    <Route index element={<DashboardPage />} />
                    <Route path="transfers" element={<TransfersPage />} />
                    <Route path="documents" element={<DocumentsPage />} />

                    {/* Șef de Direcție: vizualizare transferuri departament */}
                    <Route element={<ProtectedRoute minRole="SEF_DIRECTIE" />}>
                        <Route path="audit" element={<AuditPage />} />
                    </Route>

                    {/* Administrator: gestiune conturi + panou admin */}
                    <Route element={<ProtectedRoute minRole="ADMINISTRATOR" />}>
                        <Route path="users" element={<UsersPage />} />
                        <Route path="admin" element={<AdminDashboardPage />} />
                    </Route>
                </Route>
            </Route>

            <Route path="*" element={<Navigate to="/" replace />} />
        </Routes>
    );
}