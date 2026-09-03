import { useState, type FormEvent } from 'react';
import { useNavigate } from 'react-router-dom';
import { ShieldCheck, Lock, User, EyeOff, Eye } from 'lucide-react';
import { useAuth } from '../../context/AuthContext';
import { useToast } from '../../context/ToastContext';
import Button from '../../components/ui/Button';
import Input from '../../components/ui/Input';

export default function LoginPage() {
    const { login } = useAuth();
    const navigate  = useNavigate();
    const toast     = useToast();

    const [username, setUsername]       = useState('');
    const [password, setPassword]       = useState('');
    const [showPassword, setShowPassword] = useState(false);
    const [loading, setLoading]         = useState(false);

    const handleSubmit = async (e: FormEvent) => {
        e.preventDefault();
        setLoading(true);
        try {
            await login(username, password);
            navigate('/', { replace: true });
        } catch {
            toast.error('Nume de utilizator sau parolă incorectă. Încercați din nou.');
        } finally {
            setLoading(false);
        }
    };

    return (
        <div className="min-h-screen flex bg-mai-950">
            {/* Panoul instituțional (stânga, doar desktop) */}
            <div className="hidden lg:flex flex-col justify-between w-[45%] p-12
                bg-gradient-to-br from-mai-900 via-mai-800 to-mai-950 relative overflow-hidden">
                <div className="absolute -bottom-32 -left-32 w-96 h-96 rounded-full border-[40px] border-white/5" />
                <div className="absolute -top-24 -right-24 w-72 h-72 rounded-full border-[30px] border-gold-500/10" />

                <div className="flex items-center gap-3">
                    <div className="w-12 h-12 rounded-full bg-gold-500 flex items-center justify-center text-mai-900 font-bold">
                        MAI
                    </div>
                    <div className="leading-tight">
                        <p className="text-white font-bold">Ministerul Afacerilor Interne</p>
                        <p className="text-mai-300 text-xs">Republica Moldova</p>
                    </div>
                </div>

                <div>
                    <h2 className="text-3xl font-bold text-white leading-snug">
                        Sistem de Gestiune Documente<br />și Transferuri Securizate
                    </h2>
                    <p className="mt-4 text-mai-200 max-w-md leading-relaxed">
                        Transfer criptat de fișiere, gestiune documente normative și trasabilitate
                        completă — în conformitate cu politicile de securitate ale ministerului.
                    </p>
                    <div className="mt-8 space-y-3">
                        {[
                            ['Criptare AES-256-GCM', 'per fișier, cu cheie derivată per utilizator'],
                            ['Integritate SHA-256',  'verificare automată la fiecare descărcare'],
                            ['Jurnal de audit complet', 'toate acțiunile înregistrate și trasabile'],
                        ].map(([t, d]) => (
                            <div key={t} className="flex items-start gap-3">
                                <ShieldCheck size={18} className="text-gold-400 mt-0.5 shrink-0" />
                                <p className="text-sm text-mai-100">
                                    <span className="font-semibold text-white">{t}</span> — {d}
                                </p>
                            </div>
                        ))}
                    </div>
                </div>

                <p className="text-mai-400 text-xs">
                    © 2026 Ministerul Afacerilor Interne · Acces exclusiv în rețeaua intranet
                </p>
            </div>

            {/* Formularul (dreapta) */}
            <div className="flex-1 flex items-center justify-center p-6 bg-mai-50">
                <div className="w-full max-w-md">
                    <div className="lg:hidden flex items-center justify-center gap-3 mb-8">
                        <div className="w-11 h-11 rounded-full bg-gold-500 flex items-center justify-center text-mai-900 font-bold text-sm">
                            MAI
                        </div>
                        <p className="text-mai-900 font-bold">Ministerul Afacerilor Interne</p>
                    </div>

                    <div className="bg-white rounded-2xl shadow-card p-8">
                        <h1 className="text-xl font-bold text-mai-900">Autentificare</h1>
                        <p className="text-sm text-mai-400 mt-1 mb-6">
                            Introduceți datele de acces primite de la administratorul de sistem.
                        </p>

                        <form onSubmit={handleSubmit} className="space-y-4">
                            <div className="relative">
                                <User size={16} className="absolute left-3.5 top-[42px] text-mai-300 z-10" />
                                <div className="pl-9">
                                    <Input id="username" label="Nume de utilizator"
                                        value={username} onChange={e => setUsername(e.target.value)}
                                        placeholder="ex: nume.prenume" required autoComplete="username" />
                                </div>
                            </div>

                            <div className="relative">
                                <Lock size={16} className="absolute left-3.5 top-[42px] text-mai-300 z-10" />
                                <button type="button" onClick={() => setShowPassword(v => !v)}
                                    className="absolute right-3.5 top-[42px] text-mai-300 hover:text-mai-500 z-10">
                                    {showPassword ? <EyeOff size={16} /> : <Eye size={16} />}
                                </button>
                                <div className="pl-9 pr-10">
                                    <Input id="password" label="Parolă"
                                        type={showPassword ? 'text' : 'password'}
                                        value={password} onChange={e => setPassword(e.target.value)}
                                        placeholder="••••••••" required autoComplete="current-password" />
                                </div>
                            </div>

                            <Button type="submit" disabled={loading} className="w-full mt-2">
                                {loading ? 'Se autentifică…' : 'Autentificare'}
                            </Button>
                        </form>
                    </div>

                    <p className="text-center text-xs text-mai-400 mt-6">
                        Conturile sunt create exclusiv de administratorul de sistem.<br />
                        Utilizarea neautorizată este interzisă și se sancționează conform legislației.
                    </p>
                </div>
            </div>
        </div>
    );
}
