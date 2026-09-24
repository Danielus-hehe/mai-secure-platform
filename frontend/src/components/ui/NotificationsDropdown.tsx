import { useEffect, useRef, useState, useCallback } from 'react';
import { Bell, FileDown, Check } from 'lucide-react';
import { useAuth } from '../../context/AuthContext';
import { formatDateTime, formatFileSize } from '../../utils/format';
import api from '../../api/client';

interface Notif {
    id:         string;
    fileName:   string;
    fileSize:   number;
    senderName: string;
    createdAt:  string;
    status:     string;
}

interface PagedTransfers {
    items: (Notif & { isMine: boolean })[];
    totalCount: number;
}

export default function NotificationsDropdown() {
    const { isAuthenticated, user } = useAuth();

    // Cu parola temporară, lista de transferuri e refuzată de server până la
    // schimbarea parolei (PasswordChangeRequiredFilter).
    const canFetch = isAuthenticated && !user?.mustChangePassword;
    const [open,     setOpen]     = useState(false);
    const [notifs,   setNotifs]   = useState<Notif[]>([]);
    const [readIds,  setReadIds]  = useState<Set<string>>(new Set());
    const ref = useRef<HTMLDivElement>(null);

    const fetchPending = useCallback(async () => {
        if (!canFetch) return;
        try {
            const { data } = await api.get<PagedTransfers>('/Transfers', {
                params: {
                    direction: 'received',
                    status: 'Pending',
                    page: 1,
                    pageSize: 10,
                },
            });
            setNotifs(data.items ?? []);
        } catch {
            // Esec silentios: o notificare lipsa nu merita un toast de eroare.
        }
    }, [canFetch]);

    useEffect(() => { void fetchPending(); }, [fetchPending]);

    // Închide dropdown-ul când se dă click în afara lui
    useEffect(() => {
        const handler = (e: MouseEvent) => {
            if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false);
        };
        document.addEventListener('mousedown', handler);
        return () => document.removeEventListener('mousedown', handler);
    }, []);

    const unreadCount = notifs.filter(t => !readIds.has(t.id)).length;
    const markAllRead = () => setReadIds(new Set(notifs.map(t => t.id)));

    return (
        <div className="relative" ref={ref}>
            {/* Bell button */}
            <button
                onClick={() => setOpen(v => !v)}
                className="relative text-mai-700 dark:text-mai-200 hover:text-mai-900 dark:hover:text-white transition-colors"
                aria-label="Notificări"
            >
                <Bell size={20} />
                {unreadCount > 0 && (
                    <span className="absolute -top-1.5 -right-1.5 min-w-[18px] h-[18px]
                        rounded-full bg-gold-500 text-[10px] text-white font-bold
                        flex items-center justify-center px-0.5 shadow-sm">
                        {unreadCount > 9 ? '9+' : unreadCount}
                    </span>
                )}
            </button>

            {/* Dropdown */}
            {open && (
                <div className="absolute right-0 top-9 w-[calc(100vw-2rem)] sm:w-80 max-w-80 bg-white dark:bg-mai-800 rounded-xl shadow-xl
                    border border-mai-100 dark:border-mai-700 z-50 overflow-hidden">

                    {/* Header */}
                    <div className="flex items-center justify-between px-4 py-3
                        border-b border-mai-100 dark:border-mai-700 bg-mai-50/50 dark:bg-mai-900/50">
                        <div className="flex items-center gap-2">
                            <Bell size={14} className="text-mai-500 dark:text-mai-400" />
                            <h3 className="font-semibold text-mai-900 dark:text-white text-sm">Notificări</h3>
                            {unreadCount > 0 && (
                                <span className="bg-gold-500 text-white text-[10px] font-bold px-1.5 py-0.5 rounded-full">
                                    {unreadCount}
                                </span>
                            )}
                        </div>
                        {unreadCount > 0 && (
                            <button onClick={markAllRead}
                                    className="text-xs text-mai-400 hover:text-mai-700 dark:hover:text-mai-200
                                        flex items-center gap-1 transition-colors">
                                <Check size={11} /> Toate citite
                            </button>
                        )}
                    </div>

                    {/* Items */}
                    <div className="max-h-72 overflow-y-auto divide-y divide-mai-50 dark:divide-mai-700">
                        {notifs.length === 0 ? (
                            <div className="py-10 text-center">
                                <Bell size={28} className="mx-auto text-mai-200 dark:text-mai-600 mb-2" />
                                <p className="text-sm text-mai-400">Niciun fișier în așteptare</p>
                            </div>
                        ) : notifs.map(t => {
                            const isUnread = !readIds.has(t.id);
                            return (
                                <div key={t.id}
                                     className={`px-4 py-3 flex items-start gap-3 transition-colors
                                        ${isUnread
                                         ? 'bg-mai-50/60 dark:bg-mai-700/40'
                                         : 'hover:bg-mai-50/30 dark:hover:bg-mai-700/20'}`}>
                                    <div className={`w-9 h-9 rounded-lg flex items-center justify-center shrink-0
                                        ${isUnread
                                        ? 'bg-mai-700 text-white dark:bg-mai-500'
                                        : 'bg-mai-100 text-mai-500 dark:bg-mai-700 dark:text-mai-300'}`}>
                                        <FileDown size={15} />
                                    </div>
                                    <div className="flex-1 min-w-0">
                                        <p className={`text-sm truncate
                                            ${isUnread
                                            ? 'font-semibold text-mai-900 dark:text-white'
                                            : 'text-mai-600 dark:text-mai-300'}`}>
                                            {t.fileName}
                                        </p>
                                        <p className="text-xs text-mai-400 mt-0.5">
                                            De la <span className="font-medium text-mai-600 dark:text-mai-300">{t.senderName}</span>
                                            {' · '}{formatFileSize(t.fileSize)}
                                        </p>
                                        <p className="text-[11px] text-mai-300 dark:text-mai-500 mt-0.5">
                                            {formatDateTime(t.createdAt)}
                                        </p>
                                    </div>
                                    {isUnread && (
                                        <div className="w-2 h-2 rounded-full bg-gold-500 mt-1.5 shrink-0" />
                                    )}
                                </div>
                            );
                        })}
                    </div>

                    {/* Footer */}
                    <div className="px-4 py-2.5 border-t border-mai-100 dark:border-mai-700 bg-mai-50/30 dark:bg-mai-900/30 text-center">
                        <p className="text-xs text-mai-400">
                            {unreadCount > 0
                                ? `${unreadCount} ${unreadCount === 1 ? 'fișier în așteptare' : 'fișiere în așteptare'}`
                                : 'Toate fișierele au fost procesate'}
                        </p>
                    </div>
                </div>
            )}
        </div>
    );
}