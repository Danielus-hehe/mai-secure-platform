import type { ButtonHTMLAttributes } from 'react';

type Variant = 'primary' | 'secondary' | 'danger' | 'ghost';

const styles: Record<Variant, string> = {
    primary:   'bg-mai-700 hover:bg-mai-600 active:bg-mai-800 text-white shadow-sm',
    secondary: 'bg-white hover:bg-mai-100 active:bg-mai-200 text-mai-700 border border-mai-200 hover:border-mai-300',
    danger:    'bg-red-600 hover:bg-red-500 active:bg-red-700 text-white',
    ghost:     'text-mai-600 hover:bg-mai-100 hover:text-mai-900 active:bg-mai-200',
};

interface Props extends ButtonHTMLAttributes<HTMLButtonElement> {
    variant?: Variant;
}

export default function Button({ variant = 'primary', className = '', ...rest }: Props) {
    return (
        <button
            {...rest}
            className={`inline-flex items-center justify-center gap-2 rounded-lg px-4 py-2.5
                text-sm font-semibold transition-colors
                disabled:opacity-50 disabled:cursor-not-allowed
                ${styles[variant]} ${className}`}
        />
    );
}
