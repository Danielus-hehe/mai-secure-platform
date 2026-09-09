import type { ReactNode } from 'react';

type Tone = 'blue' | 'gold' | 'green' | 'red' | 'gray';

const tones: Record<Tone, string> = {
    blue: 'bg-mai-100 text-mai-700 dark:bg-mai-700/40 dark:text-mai-200',
    gold: 'bg-gold-500/15 text-gold-600 dark:bg-gold-500/20 dark:text-gold-400',
    green: 'bg-green-100 text-green-700 dark:bg-green-900/40 dark:text-green-400',
    red: 'bg-red-100 text-red-700 dark:bg-red-900/40 dark:text-red-400',
    gray: 'bg-mai-50 text-mai-400 dark:bg-mai-700/30 dark:text-mai-400',
};

export default function Badge({ tone = 'gray', children }:
                              { tone?: Tone; children: ReactNode }) {
    return (
        <span className={`inline-flex items-center rounded-full px-2.5 py-0.5
      text-[11px] font-semibold ${tones[tone]}`}>
      {children}
    </span>
    );
}