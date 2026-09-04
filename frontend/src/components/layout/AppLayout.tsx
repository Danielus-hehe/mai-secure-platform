import { useState } from 'react';
import { Outlet } from 'react-router-dom';
import Sidebar from './Sidebar';
import TopBar from './TopBar';
import Footer from './Footer';

export function AppLayout() {
    const [sidebarOpen, setSidebarOpen] = useState(false);

    return (
        <div className="min-h-screen bg-mai-50">
            <Sidebar open={sidebarOpen} onClose={() => setSidebarOpen(false)} />

            <div className="lg:pl-64 flex flex-col min-h-screen">
                <TopBar onMenuClick={() => setSidebarOpen(true)} />
                <main className="flex-1 p-6">
                    <Outlet />
                </main>
                <Footer />
            </div>
        </div>
    );
}