import type { ButtonHTMLAttributes } from 'react';

type Variant = 'primary' | 'secondary' | 'danger' | 'ghost';

const styles: Record<Variant, string> = {
    primary: 'bg-mai-700 hover:bg-mai-600 text-white shadow-sm',
    secondary: 'bg-white hover:bg-mai-50 text-mai-700 border border-mai-200',
    danger: 'bg-red-600 hover:bg-red-500 text-white',
    ghost: 'text-mai-600 hover:bg-mai-50',
};

interface Props extends ButtonHTMLAttributes<HTMLButtonElement> {
    variant?: Variant;
}

export default function Button({ variant = 'primary', className = '', ...rest }: Props) {
    return (
        <button
            {...rest}
            className={`inline-flex items-center justify-center gap-2 rounded-lg px-4 py-2.5
        text-sm font-semibold transition-colors disabled:opacity-60 disabled:cursor-not-allowed
        ${styles[variant]} ${className}`}
        />
    );
}