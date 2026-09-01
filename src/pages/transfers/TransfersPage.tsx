export default function TransfersPage() {
    return (
        <div className="space-y-6">
            <div>
                <h1 className="text-2xl font-bold text-mai-900">Transferuri securizate</h1>
                <p className="text-sm text-mai-400">
                    Trimite și primește fișiere criptate (AES-256-GCM) între utilizatori
                </p>
            </div>
            <div className="rounded-lg border-2 border-dashed border-mai-200 bg-white p-10 text-center text-mai-400">
                Modulul de transfer va fi implementat aici (upload, criptare, confirmare primire)
            </div>
        </div>
    );
}