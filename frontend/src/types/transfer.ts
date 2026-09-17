/**
 * Re-exporturi din locul canonical (api/transfers.ts).
 *
 * Nu se mai definesc tipuri sau constante aici: erasableSyntaxOnly interzice
 * enum-urile care emit JavaScript, iar duplicarea ar crea două surse de adevăr.
 * Importați direct din '../../api/transfers' sau din aici — ambele merg.
 */
export { TransferCategory, CATEGORY_LABELS } from '../api/transfers';
export type { TransferCategory as TransferCategoryType } from '../api/transfers';