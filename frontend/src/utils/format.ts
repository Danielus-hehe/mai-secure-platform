export function formatFileSize(bytes: number): string {
    const units = ['B', 'KB', 'MB', 'GB'];
    let i = 0, v = bytes;
    while (v >= 1024 && i < units.length - 1) { v /= 1024; i++; }
    return `${v.toFixed(i === 0 ? 0 : 1)} ${units[i]}`;
}

export function formatDateTime(iso: string): string {
    return new Date(iso).toLocaleString('ro-RO', {
        dateStyle: 'short', timeStyle: 'short',
    });
}

export function truncateSha(sha: string): string {
    return `${sha.slice(0, 8)}…${sha.slice(-6)}`;
}