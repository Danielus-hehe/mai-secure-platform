import type { InputHTMLAttributes } from 'react';

interface Props extends InputHTMLAttributes<HTMLInputElement> {
    label: string;
    error?: string;
}

export default function Input({ label, error, id, ...rest }: Props) {
    return (
        <div>
            <label htmlFor={id} className="block text-sm font-medium text-mai-800 mb-1.5">
                {label}
            </label>
            <input
                id={id}
                {...rest}
                className={`w-full rounded-lg border px-3.5 py-2.5 text-sm bg-white transition
          placeholder:text-mai-300
          focus:outline-none focus:ring-2 focus:ring-mai-500 focus:border-mai-500
          ${error ? 'border-red-400' : 'border-mai-200'}`}
            />
            {error && <p className="mt-1 text-xs text-red-600">{error}</p>}
        </div>
    );
}