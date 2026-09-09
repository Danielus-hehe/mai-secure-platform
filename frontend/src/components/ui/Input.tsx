import { forwardRef, type InputHTMLAttributes } from 'react';

interface Props extends InputHTMLAttributes<HTMLInputElement> {
    label: string;
    error?: string;
}

const Input = forwardRef<HTMLInputElement, Props>(function Input(
    { label, error, id, className = '', ...rest },
    ref
) {
    return (
        <div>
            <label htmlFor={id} className="block text-sm font-medium text-mai-800 dark:text-mai-200 mb-1.5">
                {label}
            </label>
            <input
                ref={ref}
                id={id}
                {...rest}
                className={`w-full rounded-lg border px-3.5 py-2.5 text-sm bg-white dark:bg-mai-800
          dark:text-mai-100 transition
          placeholder:text-mai-300 dark:placeholder:text-mai-500
          focus:outline-none focus:ring-2 focus:ring-mai-500 focus:border-mai-500
          dark:focus:ring-mai-400 dark:focus:border-mai-400
          ${error ? 'border-red-400 dark:border-red-500' : 'border-mai-200 dark:border-mai-600'} ${className}`}
            />
            {error && <p className="mt-1 text-xs text-red-600 dark:text-red-400">{error}</p>}
        </div>
    );
});

export default Input;