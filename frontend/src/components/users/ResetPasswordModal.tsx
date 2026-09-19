import { useState } from 'react';
import { Mail, KeyRound, Loader2, AlertTriangle, ChevronDown, ChevronRight } from 'lucide-react';
import Modal from '../ui/Modal';
import Button from '../ui/Button';
import Input from '../ui/Input';
import { useToast } from '../../context/ToastContext';
import { apiErrorMessage } from '../../api/errors';
import { sendPasswordResetLink, resetPasswordTemporary } from '../../api/users';

export interface ResetTarget {
    id: string;
    username: string;
    fullName: string;
    email: string;
    isActive: boolean;
}

interface Props {
    target: ResetTarget;
    onClose: () => void;
    onDone: () => void;
}

/**
 * Resetarea parolei unui cont, în două variante:
 *
 *   1. Link pe email (recomandat): titularul primește un link, își alege singur
 *      parola, iar administratorul nu o află niciodată. Parola actuală rămâne
 *      valabilă până la folosirea linkului.
 *   2. Parolă temporară (fără email / SMTP): administratorul stabilește o
 *      parolă, iar titularul e obligat s-o schimbe la prima autentificare.
 *
 * În ambele cazuri cheile E2EE ale contului se regenerează: erau încuiate cu
 * parola veche. Avertismentul e afișat înainte, nu descoperit după.
 */
export default function ResetPasswordModal({ target, onClose, onDone }: Props) {
    const toast = useToast();
    const hasEmail = target.email.trim().length > 0;

    const [sending, setSending] = useState(false);
    const [showTemp, setShowTemp] = useState(!hasEmail);
    const [tempPassword, setTempPassword] = useState('');
    const [saving, setSaving] = useState(false);

    const busy = sending || saving;

    const handleSendLink = async () => {
        setSending(true);
        try {
            const result = await sendPasswordResetLink(target.id);
            toast.success(result.message);
            onDone();
        } catch (e) {
            toast.error(apiErrorMessage(e, 'Linkul de resetare nu a putut fi trimis.'));
        } finally {
            setSending(false);
        }
    };

    const handleTemporary = async () => {
        if (!tempPassword) return;
        setSaving(true);
        try {
            const result = await resetPasswordTemporary(target.id, tempPassword);
            toast.success(result.message);
            onDone();
        } catch (e) {
            toast.error(apiErrorMessage(e, 'Parola nu a putut fi resetată.'));
        } finally {
            setSaving(false);
        }
    };

    return (
        <Modal open title={`Resetare parolă - ${target.fullName || '@' + target.username}`} onClose={() => { if (!busy) onClose(); }}>
            <div className="space-y-5">
                {!target.isActive && (
                    <p className="rounded-lg border border-amber-200 bg-amber-50 px-3.5 py-2.5 text-xs text-amber-800
                        dark:border-amber-700/50 dark:bg-amber-900/20 dark:text-amber-300">
                        Contul este dezactivat. Activați-l înainte de a trimite linkul de resetare.
                    </p>
                )}

                {/* ── Varianta recomandată: link pe email ──────────────────── */}
                <section className="space-y-3">
                    <div className="flex items-start gap-3">
                        <div className="rounded-lg bg-mai-100 p-2 text-mai-700 dark:bg-mai-700 dark:text-mai-200">
                            <Mail size={18} />
                        </div>
                        <div className="text-sm">
                            <p className="font-medium text-mai-900 dark:text-white">Trimite link de resetare pe email</p>
                            <p className="mt-0.5 text-xs leading-relaxed text-mai-500 dark:text-mai-400">
                                {hasEmail ? (
                                    <>
                                        Utilizatorul primește la <strong className="text-mai-700 dark:text-mai-200">{target.email}</strong> un
                                        link de unică folosință, cu care își stabilește singur parola nouă. Parola actuală
                                        rămâne valabilă până atunci. După schimbare primește și un email de confirmare.
                                    </>
                                ) : (
                                    'Contul nu are adresă de email - folosiți parola temporară de mai jos.'
                                )}
                            </p>
                        </div>
                    </div>

                    <Button
                        className="w-full justify-center"
                        disabled={!hasEmail || !target.isActive || busy}
                        onClick={() => void handleSendLink()}
                    >
                        {sending ? <Loader2 size={15} className="animate-spin" /> : <Mail size={15} />}
                        Trimite linkul
                    </Button>
                </section>

                <div className="flex items-start gap-2.5 rounded-lg border border-amber-200 bg-amber-50 px-3.5 py-2.5
                    dark:border-amber-700/50 dark:bg-amber-900/20">
                    <AlertTriangle size={15} className="mt-0.5 shrink-0 text-amber-600 dark:text-amber-400" />
                    <p className="text-xs leading-relaxed text-amber-800 dark:text-amber-300">
                        Cheile de criptare ale contului sunt protejate cu parola actuală. După resetare se
                        generează chei noi, iar fișierele criptate primite anterior nu vor mai putea fi
                        deschise de acest cont - expeditorii le pot retrimite. Toate sesiunile se închid.
                    </p>
                </div>

                {/* ── Varianta de rezervă: parolă temporară ────────────────── */}
                <section className="border-t border-mai-100 pt-4 dark:border-mai-700">
                    <button
                        type="button"
                        onClick={() => setShowTemp((v) => !v)}
                        className="flex w-full items-center gap-2 text-left text-sm font-medium text-mai-700 dark:text-mai-200"
                    >
                        {showTemp ? <ChevronDown size={15} /> : <ChevronRight size={15} />}
                        Stabilesc eu o parolă temporară
                    </button>

                    {showTemp && (
                        <div className="mt-3 space-y-3">
                            <p className="text-xs leading-relaxed text-mai-500 dark:text-mai-400">
                                Pentru conturi fără email sau când serverul SMTP nu este disponibil. Comunicați
                                parola pe un canal sigur; utilizatorul va fi obligat s-o schimbe la prima autentificare.
                            </p>
                            <Input
                                id="temp-password"
                                label="Parolă temporară"
                                type="password"
                                autoComplete="new-password"
                                value={tempPassword}
                                onChange={(e) => setTempPassword(e.target.value)}
                                placeholder="Minim 12 caractere, cu majusculă, cifră și simbol"
                            />
                            <Button
                                variant="secondary"
                                className="w-full justify-center"
                                disabled={!tempPassword || busy}
                                onClick={() => void handleTemporary()}
                            >
                                {saving ? <Loader2 size={15} className="animate-spin" /> : <KeyRound size={15} />}
                                Setează parola temporară
                            </Button>
                        </div>
                    )}
                </section>
            </div>
        </Modal>
    );
}
