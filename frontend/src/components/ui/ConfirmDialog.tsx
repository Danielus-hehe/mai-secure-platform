import { AlertTriangle } from 'lucide-react';
import Modal from './Modal';
import Button from './Button';

interface Props {
    open: boolean;
    title: string;
    message: string;
    confirmLabel?: string;
    variant?: 'danger' | 'warning';
    onConfirm: () => void;
    onCancel: () => void;
}

export default function ConfirmDialog({
                                          open, title, message,
                                          confirmLabel = 'Confirmă',
                                          variant = 'danger',
                                          onConfirm, onCancel,
                                      }: Props) {
    return (
        <Modal open={open} title="" onClose={onCancel}>
            <div className="flex flex-col items-center text-center gap-5 py-2">
                <div className={`w-14 h-14 rounded-full flex items-center justify-center
                    ${variant === 'danger' ? 'bg-red-100 dark:bg-red-900/40' : 'bg-amber-100 dark:bg-amber-900/40'}`}>
                    <AlertTriangle
                        size={28}
                        className={variant === 'danger' ? 'text-red-600 dark:text-red-400' : 'text-amber-600 dark:text-amber-400'}
                    />
                </div>
                <div>
                    <h3 className="font-bold text-mai-900 dark:text-white text-lg">{title}</h3>
                    <p className="text-mai-500 dark:text-mai-300 text-sm mt-1.5 leading-relaxed max-w-xs mx-auto">
                        {message}
                    </p>
                </div>
                <div className="flex gap-3 w-full pt-1">
                    <Button variant="secondary" className="flex-1" onClick={onCancel}>
                        Anulează
                    </Button>
                    <Button
                        variant={variant === 'danger' ? 'danger' : 'primary'}
                        className="flex-1"
                        onClick={onConfirm}
                    >
                        {confirmLabel}
                    </Button>
                </div>
            </div>
        </Modal>
    );
}