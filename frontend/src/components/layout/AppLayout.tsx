import { useState } from 'react';
import { Outlet }   from 'react-router-dom';
import Sidebar       from './Sidebar';
import TopBar        from './TopBar';
import Footer        from './Footer';
import ErrorBoundary from '../ErrorBoundary';
import MfaRequiredBanner from '../security/MfaRequiredBanner';

export function AppLayout() {
    const [sidebarOpen, setSidebarOpen] = useState(false);

    return (
        <div className="min-h-screen bg-mai-50 dark:bg-mai-950">
            <Sidebar open={sidebarOpen} onClose={() => setSidebarOpen(false)} />

            <div className="lg:pl-64 flex flex-col min-h-screen">
                <TopBar onMenuClick={() => setSidebarOpen(true)} />
                {/* p-4 pe mobil, p-6 pe desktop — câștig de ~32px pe ecran mic */}
                <main className="flex-1 p-4 sm:p-6">
                    <MfaRequiredBanner />
                    <ErrorBoundary>
                        <Outlet />
                    </ErrorBoundary>
                </main>
                <Footer />
            </div>
        </div>
    );
}