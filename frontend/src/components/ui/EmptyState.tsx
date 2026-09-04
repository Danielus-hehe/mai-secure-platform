import type { LucideIcon } from 'lucide-react';

interface Props {
    icon: LucideIcon;
    title: string;
    description?: string;
}

export default function EmptyState({ icon: Icon, title, description }: Props) {
    return (
        <div className="rounded-xl border-2 border-dashed border-mai-200 bg-white
      px-8 py-14 text-center">
            <div className="mx-auto w-12 h-12 rounded-full bg-mai-50 text-mai-400
        flex items-center justify-center mb-4">
                <Icon size={24} />
            </div>
            <p className="font-semibold text-mai-800">{title}</p>
            {description && <p className="mt-1 text-sm text-mai-400">{description}</p>}
        </div>
    );
}