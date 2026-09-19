// Import din api/transfers - nu din @/types/transfer (alias @/ nu e configurat)
import { TransferCategory, CATEGORY_LABELS } from '../../api/transfers';

interface Props {
    category: TransferCategory;
}

const STYLES: Record<TransferCategory, string> = {
    [TransferCategory.Critical]:  'bg-red-100   text-red-800   border-red-200   dark:bg-red-900/30   dark:text-red-300   dark:border-red-700',
    [TransferCategory.Important]: 'bg-amber-100 text-amber-800 border-amber-200 dark:bg-amber-900/30 dark:text-amber-300 dark:border-amber-700',
    [TransferCategory.General]:   'bg-teal-100  text-teal-800  border-teal-200  dark:bg-teal-900/30  dark:text-teal-300  dark:border-teal-700',
    [TransferCategory.Normal]:    'bg-gray-100  text-gray-600  border-gray-200  dark:bg-gray-700/40  dark:text-gray-400  dark:border-gray-600',
};

export function CategoryBadge({ category }: Props) {
    return (
        <span
            className={`inline-flex items-center rounded-full border px-2 py-0.5 text-xs font-medium ${STYLES[category]}`}
        >
            {CATEGORY_LABELS[category]}
        </span>
    );
}