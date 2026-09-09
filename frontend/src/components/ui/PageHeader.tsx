interface Props {
    title: string;
    subtitle?: string;
    actions?: React.ReactNode;
}

export default function PageHeader({ title, subtitle, actions }: Props) {
    return (
        <div className="flex flex-col sm:flex-row sm:flex-wrap sm:items-end justify-between gap-3 sm:gap-4
            border-b border-mai-100 dark:border-mai-700 pb-4">
            <div className="min-w-0">
                <h1 className="text-xl sm:text-2xl font-bold text-mai-900 dark:text-white truncate">{title}</h1>
                {subtitle && <p className="mt-1 text-sm text-mai-400 dark:text-mai-400">{subtitle}</p>}
            </div>
            {actions && <div className="flex gap-2 shrink-0">{actions}</div>}
        </div>
    );
}