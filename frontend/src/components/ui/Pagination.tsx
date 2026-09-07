import { ChevronLeft, ChevronRight } from 'lucide-react';

export interface PagedResult<T> {
    items: T[];
    totalCount: number;
    page: number;
    pageSize: number;
    totalPages: number;
    hasPrevious: boolean;
    hasNext: boolean;
}

interface Props {
    page: number;
    pageSize: number;
    totalCount: number;
    totalPages: number;
    onPageChange: (page: number) => void;
    onPageSizeChange?: (size: number) => void;
    /** Cuvantul pentru unitatea numarata: "inregistrari", "utilizatori", "transferuri". */
    itemLabel?: string;
}

const PAGE_SIZES = [10, 25, 50, 100];

/**
 * Genereaza numerele de pagina afisate, cu elipse pentru seturile mari.
 * Ex. pagina 7 din 20 -> [1, '…', 6, 7, 8, '…', 20]
 */
function pageNumbers(current: number, total: number): (number | '…')[] {
    if (total <= 7) return Array.from({ length: total }, (_, i) => i + 1);

    const pages: (number | '…')[] = [1];

    if (current > 3) pages.push('…');

    const start = Math.max(2, current - 1);
    const end = Math.min(total - 1, current + 1);
    for (let i = start; i <= end; i++) pages.push(i);

    if (current < total - 2) pages.push('…');

    pages.push(total);
    return pages;
}

export default function Pagination({
                                       page,
                                       pageSize,
                                       totalCount,
                                       totalPages,
                                       onPageChange,
                                       onPageSizeChange,
                                       itemLabel = 'înregistrări',
                                   }: Props) {
    if (totalCount === 0) return null;

    const first = (page - 1) * pageSize + 1;
    const last = Math.min(page * pageSize, totalCount);

    const btn = `inline-flex h-8 min-w-8 items-center justify-center rounded-md border
        border-mai-200 px-2 text-sm transition-colors
        hover:bg-mai-100 disabled:cursor-not-allowed disabled:opacity-40`;

    return (
        <div className="flex flex-wrap items-center justify-between gap-3 border-t border-mai-100 px-5 py-3">
            <p className="text-sm text-mai-400">
                {first}–{last} din {totalCount.toLocaleString('ro-RO')} {itemLabel}
            </p>

            <div className="flex items-center gap-1">
                <button
                    className={btn}
                    onClick={() => onPageChange(page - 1)}
                    disabled={page <= 1}
                    aria-label="Pagina anterioară"
                >
                    <ChevronLeft size={15} />
                </button>

                {pageNumbers(page, totalPages).map((p, i) =>
                    p === '…' ? (
                        <span key={`gap-${i}`} className="px-1 text-sm text-mai-300">
                            …
                        </span>
                    ) : (
                        <button
                            key={p}
                            className={`${btn} ${
                                p === page
                                    ? 'border-mai-500 bg-mai-500 font-medium text-white hover:bg-mai-500'
                                    : ''
                            }`}
                            onClick={() => onPageChange(p)}
                            aria-current={p === page ? 'page' : undefined}
                        >
                            {p}
                        </button>
                    )
                )}

                <button
                    className={btn}
                    onClick={() => onPageChange(page + 1)}
                    disabled={page >= totalPages}
                    aria-label="Pagina următoare"
                >
                    <ChevronRight size={15} />
                </button>
            </div>

            {onPageSizeChange && (
                <select
                    value={pageSize}
                    onChange={(e) => onPageSizeChange(Number(e.target.value))}
                    className="cursor-pointer rounded-md border border-mai-200 bg-white px-2 py-1 text-sm
                               hover:border-mai-400 focus:outline-none focus:ring-2 focus:ring-mai-500"
                    aria-label="Înregistrări pe pagină"
                >
                    {PAGE_SIZES.map((s) => (
                        <option key={s} value={s}>
                            {s} / pagină
                        </option>
                    ))}
                </select>
            )}
        </div>
    );
}