import type { LucideIcon } from 'lucide-react';

interface Props {
    label: string;
    value: number | string;
    icon: LucideIcon;
    tone?: 'blue' | 'gold' | 'red' | 'green';
}

const toneStyles = {
    blue: 'bg-mai-50 text-mai-700',
    gold: 'bg-gold-500/10 text-gold-600',
    red: 'bg-red-50 text-red-600',
    green: 'bg-green-50 text-green-600',
};

export default function StatCard({ label, value, icon: Icon, tone = 'blue' }: Props) {
    return (
        <div className="bg-white rounded-xl shadow-card p-5 flex items-center gap-4
      border border-mai-100/50">
            <div className={`w-12 h-12 rounded-lg flex items-center justify-center
        ${toneStyles[tone]}`}>
                <Icon size={24} />
            </div>
            <div>
                <p className="text-2xl font-bold text-mai-900 leading-none">{value}</p>
                <p className="mt-1.5 text-xs text-mai-400">{label}</p>
            </div>
        </div>
    );
}