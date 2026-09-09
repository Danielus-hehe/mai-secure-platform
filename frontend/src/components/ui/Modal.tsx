import { X } from 'lucide-react';
import type { ReactNode } from 'react';

interface Props {
    open: boolean;
    title: string;
    onClose: () => void;
    children: ReactNode;
    wide?: boolean;
}

export default function Modal({ open, title, onClose, children, wide }: Props) {
    if (!open) return null;
    return (
        <div className="fixed inset-0 z-50 flex items-center justify-center p-4">
            <div className="absolute inset-0 bg-mai-950/60 backdrop-blur-sm" onClick={onClose} />
            <div className={`relative bg-white dark:bg-mai-800 rounded-xl shadow-card w-full
        ${wide ? 'max-w-3xl' : 'max-w-lg'} max-h-[90vh] overflow-y-auto`}>
                <div className="flex items-center justify-between px-6 py-4 border-b border-mai-100
          dark:border-mai-700 sticky top-0 bg-white dark:bg-mai-800 rounded-t-xl">
                    <h2 className="font-bold text-mai-900 dark:text-white">{title}</h2>
                    <button onClick={onClose} className="text-mai-400 hover:text-mai-700 dark:text-mai-400 dark:hover:text-mai-200">
                        <X size={20} />
                    </button>
                </div>
                <div className="p-6">{children}</div>
            </div>
        </div>
    );
}