import { Component, type ReactNode, type ErrorInfo } from 'react';
import { AlertTriangle, RefreshCw } from 'lucide-react';

interface Props  { children: ReactNode; }
interface State  { hasError: boolean; error?: Error; }

/**
 * ErrorBoundary - prinde erorile din componentele copil și afișează
 * un UI de fallback în loc de ecranul alb.
 *
 * Utilizare în AppLayout.tsx:
 *   import ErrorBoundary from '../ErrorBoundary';
 *   ...
 *   <main className="flex-1 p-6">
 *     <ErrorBoundary>
 *       <Outlet />
 *     </ErrorBoundary>
 *   </main>
 */
export default class ErrorBoundary extends Component<Props, State> {
    state: State = { hasError: false };

    static getDerivedStateFromError(error: Error): State {
        return { hasError: true, error };
    }

    componentDidCatch(error: Error, info: ErrorInfo) {
        console.error('[ErrorBoundary]', error, info.componentStack);
    }

    handleRetry = () => {
        this.setState({ hasError: false, error: undefined });
    };

    render() {
        if (!this.state.hasError) return this.props.children;

        return (
            <div className="flex flex-col items-center justify-center min-h-[60vh] gap-5 p-8 text-center">
                <div className="w-16 h-16 rounded-full bg-red-50 dark:bg-red-900/40 flex items-center justify-center">
                    <AlertTriangle size={28} className="text-red-500 dark:text-red-400" />
                </div>

                <div>
                    <h2 className="text-lg font-bold text-mai-900 dark:text-white">Eroare neașteptată</h2>
                    <p className="text-sm text-mai-500 dark:text-mai-300 dark:text-mai-500 mt-1 max-w-sm">
                        {this.state.error?.message ?? 'A apărut o eroare la randarea acestei pagini.'}
                    </p>
                </div>

                <button
                    onClick={this.handleRetry}
                    className="flex items-center gap-2 px-5 py-2.5 bg-mai-700 dark:bg-mai-600 text-white
                        rounded-xl text-sm font-medium hover:bg-mai-800 dark:hover:bg-mai-500 transition-colors"
                >
                    <RefreshCw size={15} />
                    Reîncercați
                </button>

                <p className="text-xs text-mai-300 dark:text-mai-500">
                    Dacă problema persistă, reîncărcați pagina complet (F5).
                </p>
            </div>
        );
    }
}