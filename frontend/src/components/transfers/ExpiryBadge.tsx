interface Props {
    expiresAt: string | null;
}

export function ExpiryBadge({ expiresAt }: Props) {
    if (!expiresAt) return null;

    const expiry = new Date(expiresAt);
    const now    = new Date();

    if (expiry < now) {
        return (
            <span className="inline-flex items-center gap-1 text-xs font-medium text-red-600">
        <span aria-hidden="true">⊘</span> Expirat
      </span>
        );
    }

    const diffMs    = expiry.getTime() - now.getTime();
    const diffHours = Math.ceil(diffMs / 3_600_000);
    const diffDays  = Math.ceil(diffMs / 86_400_000);

    if (diffHours <= 24) {
        return (
            <span className="text-xs font-medium text-amber-600">
        Expiră în {diffHours}h
      </span>
        );
    }

    return (
        <span className="text-xs text-gray-500">
      Expiră în {diffDays}z
    </span>
    );
}