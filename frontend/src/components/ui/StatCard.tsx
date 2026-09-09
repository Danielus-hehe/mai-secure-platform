import type { LucideIcon } from 'lucide-react';

interface Props {
    label: string;
    value: number | string;
    icon: LucideIcon;
    tone?: 'blue' | 'gold' | 'red' | 'green';
}

const toneStyles = {
    blue: 'bg-mai-50 text-mai-700 dark:bg-mai-700/40 dark:text-mai-300',
    gold: 'bg-gold-500/10 text-gold-600 dark:bg-gold-500/20 dark:text-gold-400',
    red: 'bg-red-50 text-red-600 dark:bg-red-900/40 dark:text-red-400',
    green: 'bg-green-50 text-green-600 dark:bg-green-900/40 dark:text-green-400',
};

export default function StatCard({ label, value, icon: Icon, tone = 'blue' }: Props) {
    return (
        <div className="bg-white dark:bg-mai-800 rounded-xl shadow-card dark:shadow-none p-5 flex items-center gap-4
      border border-mai-100/50 dark:border-mai-700">
            <div className={`w-12 h-12 rounded-lg flex items-center justify-center
        ${toneStyles[tone]}`}>
                <Icon size={24} />
            </div>
            <div>
                <p className="text-2xl font-bold text-mai-900 dark:text-white leading-none">{value}</p>
                <p className="mt-1.5 text-xs text-mai-400 dark:text-mai-400">{label}</p>
            </div>
        </div>
    );
}