import type { AuthSession } from '../types';
import { MOCK_USERS } from '../utils/mockData';

/**
 * Autentificare simulată — va fi înlocuită de API-ul real (JWT + bcrypt).
 * Căutarea contului: `MOCK_USERS.find(u => u.username === ...)`.
 */
export function mockLogin(username: string, password: string): Promise<AuthSession> {
    return new Promise((resolve, reject) => {
        setTimeout(() => {
            const user = MOCK_USERS.find(
                (u) => u.username === username.trim().toLowerCase() && u.password === password
            );
            if (!user || !user.isActive) {
                reject(new Error('INVALID_CREDENTIALS'));
                return;
            }
            const { password: _pw, ...safeUser } = user;
            resolve({
                accessToken: `mock-jwt.${btoa(user.id)}.${Date.now() + 3600_000}`,
                expiresAt: Date.now() + 3600_000, // 1 oră
                user: safeUser,
            });
        }, 600); // latență simulată
    });
}