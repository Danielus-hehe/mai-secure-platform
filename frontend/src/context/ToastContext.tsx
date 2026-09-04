import { createContext, useCallback, useContext, useState, type ReactNode } from 'react';
import { CheckCircle2, XCircle, Info, AlertTriangle, X } from 'lucide-react';

export type ToastVariant = 'success' | 'error' | 'info' | 'warning';

interface ToastData { id: string; message: string; variant: ToastVariant; }

interface ToastContextValue {
    success: (msg: string) => void;
    error:   (msg: string) => void;
    info:    (msg: string) => void;
    warning: (msg: string) => void;
}

const ToastContext = createContext<ToastContextValue | null>(null);

const CFG: Record<ToastVariant, {
    icon: typeof CheckCircle2; bar: string;
    iconBg: string; iconColor: string; border: string;
}> = {
    success: { icon: CheckCircle2,  bar: 'bg-green-500', iconBg: 'bg-green-50',  iconColor: 'text-green-600', border: 'border-green-100' },
    error:   { icon: XCircle,       bar: 'bg-red-500',   iconBg: 'bg-red-50',    iconColor: 'text-red-600',   border: 'border-red-100'   },
    info:    { icon: Info,          bar: 'bg-mai-600',   iconBg: 'bg-mai-50',    iconColor: 'text-mai-600',   border: 'border-mai-100'   },
    warning: { icon: AlertTriangle, bar: 'bg-amber-400', iconBg: 'bg-amber-50',  iconColor: 'text-amber-600', border: 'border-amber-100' },
};

function ToastBubble({ id, message, variant, onDismiss }: ToastData & { onDismiss: (id: string) => void }) {
    const { icon: Icon, bar, iconBg, iconColor, border } = CFG[variant];
    return (
        <div
            className={`toast-enter pointer-events-auto relative overflow-hidden rounded-xl
                border bg-white shadow-xl flex items-start gap-3 px-4 py-3.5 ${border}`}
            role="alert"
        >
            <div className={`absolute inset-y-0 left-0 w-1 ${bar}`} />
            <div className={`shrink-0 w-8 h-8 rounded-lg flex items-center justify-center ${iconBg}`}>
                <Icon size={16} className={iconColor} />
            </div>
            <p className="flex-1 text-sm font-medium text-mai-900 leading-snug pt-0.5">{message}</p>
            <button
                onClick={() => onDismiss(id)}
                className="shrink-0 text-mai-300 hover:text-mai-600 mt-0.5 transition-colors"
            >
                <X size={15} />
            </button>
        </div>
    );
}

export function ToastProvider({ children }: { children: ReactNode }) {
    const [toasts, setToasts] = useState<ToastData[]>([]);

    const dismiss = useCallback((id: string) =>
        setToasts(p => p.filter(t => t.id !== id)), []);

    const push = useCallback((message: string, variant: ToastVariant) => {
        const id = `t-${Date.now()}-${Math.random().toString(36).slice(2)}`;
        setToasts(p => [...p, { id, message, variant }]);
        setTimeout(() => dismiss(id), 4500);
    }, [dismiss]);

    return (
        <ToastContext.Provider value={{
            success: (msg) => push(msg, 'success'),
            error:   (msg) => push(msg, 'error'),
            info:    (msg) => push(msg, 'info'),
            warning: (msg) => push(msg, 'warning'),
        }}>
            {children}
            {/* Portal-like fixed container */}
            <div className="fixed top-5 right-5 z-[200] flex flex-col gap-2 w-80 pointer-events-none">
                {toasts.map(t => <ToastBubble key={t.id} {...t} onDismiss={dismiss} />)}
            </div>
        </ToastContext.Provider>
    );
}

export function useToast(): ToastContextValue {
    const ctx = useContext(ToastContext);
    if (!ctx) throw new Error('useToast trebuie folosit înăuntrul ToastProvider');
    return ctx;
}
