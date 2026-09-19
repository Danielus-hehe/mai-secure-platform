interface Props {
    expiresAt: string | null;
}

/** „Expiră în 3z”, „Expiră în 5h” sau „Expirat”, sub numele fișierului. */
export function ExpiryBadge({ expiresAt }: Props) {
    if (!expiresAt) return null;

    const expiry = new Date(expiresAt);
    const now    = new Date();

    if (expiry <= now) {
        return (
            <span className="inline-flex items-center gap-1 text-[11px] font-medium text-red-600 dark:text-red-400">
                <span aria-hidden="true">⊘</span> Expirat
            </span>
        );
    }

    const diffMs    = expiry.getTime() - now.getTime();
    const diffHours = Math.ceil(diffMs / 3_600_000);
    const diffDays  = Math.ceil(diffMs / 86_400_000);

    if (diffHours <= 24) {
        return (
            <span className="text-[11px] font-medium text-amber-600 dark:text-amber-400">
                Expiră în {diffHours}h
            </span>
        );
    }

    return (
        <span className="text-[11px] text-mai-400 dark:text-mai-500" title={expiry.toLocaleString('ro-RO')}>
            Expiră în {diffDays}z
        </span>
    );
}
