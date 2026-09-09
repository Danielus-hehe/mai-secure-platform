import { lazy } from 'react';
import { Routes, Route, Navigate } from 'react-router-dom';
import { ProtectedRoute } from '../components/ProtectedRoute';
import { AppLayout } from '../components/layout/AppLayout';
import { RouteBoundary } from '../components/RouteBoundary';
import KeysGate from '../components/keys/KeysGate';

/*
 * Încărcare leneșă pe rute.
 *
 * Înainte, toate paginile intrau în bundle-ul inițial. Consecința practică: cine
 * deschidea /login descărca și AdminDashboardPage cu tot Recharts-ul după el —
 * cel mai mare chunk din aplicație, folosit de un singur rol, pe o singură rută.
 * Pe o rețea de intranet asta se vede mai puțin, dar tot se plătește la fiecare
 * deploy, când cache-ul e invalidat.
 *
 * LoginPage NU e lazy: e prima pagină pe care o vede oricine, iar un chunk
 * separat pentru ea ar adăuga un dus-întors de rețea exact acolo unde se măsoară
 * timpul până la primul ecran util.
 */
import LoginPage from '../pages/auth/LoginPage';

const DashboardPage      = lazy(() => import('../pages/dashboard/DashboardPage'));
const TransfersPage      = lazy(() => import('../pages/transfers/TransfersPage'));
const DocumentsPage      = lazy(() => import('../pages/documents/DocumentsPage'));
const ProfilePage        = lazy(() => import('../pages/profile/ProfilePage'));
const AuditPage          = lazy(() => import('../pages/audit/AuditPage'));
const UsersPage          = lazy(() => import('../pages/users/UsersPage'));
const AdminDashboardPage = lazy(() => import('../pages/admin/AdminDashboardPage'));

// UserRole enum backend: Utilizator=1, SefDirectie=2, Administrator=3

export function AppRoutes() {
    return (
        <Routes>
            <Route path="/"      element={<Navigate to="/login" replace />} />
            <Route path="/login" element={<LoginPage />} />

            {/* Toate rutele protejate învelite în AppLayout (sidebar + topbar) */}
            <Route element={<ProtectedRoute />}>
                <Route element={<AppLayout />}>

                    {/*
                      Poarta criptografică: nicio pagină nu se randează până când
                      cheile private nu sunt descuiate în fila curentă. Este pusă
                      INTERIOR față de AppLayout ca utilizatorul să vadă în
                      continuare meniul și butonul de deconectare.
                    */}
                    <Route element={<KeysGate />}>

                        {/*
                          RouteBoundary dă fiecărei pagini propria barieră de erori
                          și propriul fallback de încărcare. Un crash într-o pagină
                          nu mai înlocuiește tot ecranul: sidebar-ul rămâne, deci
                          utilizatorul poate naviga în altă parte fără F5.
                        */}
                        <Route element={<RouteBoundary />}>

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
                </Route>
            </Route>

            <Route path="*" element={<Navigate to="/login" replace />} />
        </Routes>
    );
}
