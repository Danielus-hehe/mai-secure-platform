interface Props {
    title: string;
    subtitle?: string;
    actions?: React.ReactNode;
}

export default function PageHeader({ title, subtitle, actions }: Props) {
    return (
        <div className="flex flex-wrap items-end justify-between gap-4 border-b border-mai-100 pb-4">
            <div>
                <h1 className="text-2xl font-bold text-mai-900">{title}</h1>
                {subtitle && <p className="mt-1 text-sm text-mai-400">{subtitle}</p>}
            </div>
            {actions && <div className="flex gap-2">{actions}</div>}
        </div>
    );
}