import {
    createContext,
    useCallback,
    useContext,
    useEffect,
    useMemo,
    useRef,
    useState,
    type ReactNode,
} from 'react';
import { CheckCircle2, XCircle, Info, AlertTriangle, X } from 'lucide-react';

export type ToastVariant = 'success' | 'error' | 'info' | 'warning';

interface ToastData { id: string; message: string; variant: ToastVariant; count: number; }

interface ToastContextValue {
    success: (msg: string) => void;
    error:   (msg: string) => void;
    info:    (msg: string) => void;
    warning: (msg: string) => void;
}

const ToastContext = createContext<ToastContextValue | null>(null);

/** Peste atatea toast-uri simultane, ecranul devine inutilizabil. */
const MAX_TOASTS = 4;
const AUTO_DISMISS_MS = 4500;

const CFG: Record<ToastVariant, {
    icon: typeof CheckCircle2; bar: string;
    iconBg: string; iconColor: string; border: string;
}> = {
    success: { icon: CheckCircle2,  bar: 'bg-green-500', iconBg: 'bg-green-50',  iconColor: 'text-green-600', border: 'border-green-100' },
    error:   { icon: XCircle,       bar: 'bg-red-500',   iconBg: 'bg-red-50',    iconColor: 'text-red-600',   border: 'border-red-100'   },
    info:    { icon: Info,          bar: 'bg-mai-600',   iconBg: 'bg-mai-50',    iconColor: 'text-mai-600',   border: 'border-mai-100'   },
    warning: { icon: AlertTriangle, bar: 'bg-amber-400', iconBg: 'bg-amber-50',  iconColor: 'text-amber-600', border: 'border-amber-100' },
};

function ToastBubble({ id, message, variant, count, onDismiss }: ToastData & { onDismiss: (id: string) => void }) {
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
            <p className="flex-1 text-sm font-medium text-mai-900 leading-snug pt-0.5">
                {message}
                {count > 1 && (
                    <span className="ml-2 rounded-full bg-mai-100 px-1.5 py-0.5 text-xs font-semibold text-mai-500">
                        ×{count}
                    </span>
                )}
            </p>
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

    // Tinem timerele intr-un ref ca sa le putem curata la unmount si sa le
    // resetam cand un mesaj duplicat prelungeste viata unui toast existent.
    const timers = useRef(new Map<string, ReturnType<typeof setTimeout>>());

    const dismiss = useCallback((id: string) => {
        const timer = timers.current.get(id);
        if (timer) {
            clearTimeout(timer);
            timers.current.delete(id);
        }
        setToasts((p) => p.filter((t) => t.id !== id));
    }, []);

    const scheduleDismiss = useCallback((id: string) => {
        const existing = timers.current.get(id);
        if (existing) clearTimeout(existing);
        timers.current.set(id, setTimeout(() => dismiss(id), AUTO_DISMISS_MS));
    }, [dismiss]);

    const push = useCallback((message: string, variant: ToastVariant) => {
        setToasts((prev) => {
            // Deduplicare: acelasi mesaj cu aceeasi varianta incrementeaza un contor
            // in loc sa adauge o bula noua. Plasa de siguranta impotriva buclelor
            // de re-fetch care altfel umplu ecranul cu acelasi text.
            const duplicate = prev.find((t) => t.message === message && t.variant === variant);
            if (duplicate) {
                scheduleDismiss(duplicate.id);
                return prev.map((t) =>
                    t.id === duplicate.id ? { ...t, count: t.count + 1 } : t
                );
            }

            const id = `t-${Date.now()}-${Math.random().toString(36).slice(2)}`;
            scheduleDismiss(id);

            const next = [...prev, { id, message, variant, count: 1 }];

            // Peste plafon, scoatem cele mai vechi.
            if (next.length > MAX_TOASTS) {
                const dropped = next.slice(0, next.length - MAX_TOASTS);
                dropped.forEach((t) => {
                    const timer = timers.current.get(t.id);
                    if (timer) clearTimeout(timer);
                    timers.current.delete(t.id);
                });
                return next.slice(-MAX_TOASTS);
            }

            return next;
        });
    }, [scheduleDismiss]);

    // Curatare la unmount — altfel timerele apeleaza setState pe o componenta moarta.
    useEffect(() => {
        const map = timers.current;
        return () => {
            map.forEach((t) => clearTimeout(t));
            map.clear();
        };
    }, []);

    /**
     * CRITIC: valoarea contextului trebuie memoizata.
     *
     * Inainte era un obiect literal, deci o referinta noua la fiecare render al
     * provider-ului. Orice pagina care pune `toast` in dependentele unui useCallback
     * sau useEffect — si toate o fac — isi re-crea callback-ul la fiecare render.
     * Rezultatul: fetch esueaza -> toast -> provider-ul se re-randeaza -> value nou
     * -> useEffect se re-declanseaza -> fetch esueaza... bucla infinita, cu ecranul
     * plin de acelasi mesaj.
     *
     * `push` si `dismiss` sunt deja stabile (useCallback), deci useMemo cu [push]
     * face ca referinta contextului sa nu se mai schimbe niciodata.
     */
    const value = useMemo<ToastContextValue>(
        () => ({
            success: (msg: string) => push(msg, 'success'),
            error:   (msg: string) => push(msg, 'error'),
            info:    (msg: string) => push(msg, 'info'),
            warning: (msg: string) => push(msg, 'warning'),
        }),
        [push]
    );

    return (
        <ToastContext.Provider value={value}>
            {children}
            <div className="fixed top-5 right-5 z-[200] flex flex-col gap-2 w-80 pointer-events-none">
                {toasts.map((t) => (
                    <ToastBubble key={t.id} {...t} onDismiss={dismiss} />
                ))}
            </div>
        </ToastContext.Provider>
    );
}

export function useToast(): ToastContextValue {
    const ctx = useContext(ToastContext);
    if (!ctx) throw new Error('useToast trebuie folosit înăuntrul ToastProvider');
    return ctx;
}