import { createContext, useContext, useState, type ReactNode } from 'react';
import type { Role } from '../types';
import { ROLE_HIERARCHY } from '../utils/constants';

export interface User {
    id: string | number;
    username: string;
    fullName: string;
    role: Role;
    token?: string;
    department?: string;
}

interface AuthContextType {
    user: User | null;
    isAuthenticated: boolean;
    login: (username: string, password: string) => Promise<User>;
    logout: () => void;
    /** Returnează true dacă utilizatorul curent are cel puțin rolul cerut (ierarhic) */
    hasRole: (minRole: Role) => boolean;
}

// UserRole enum din backend: Utilizator=1, SefDirectie=2, Administrator=3
const ROLE_NUM_TO_STRING: Record<number, Role> = {
    1: 'UTILIZATOR',
    2: 'SEF_DIRECTIE',
    3: 'ADMINISTRATOR',
};

const AuthContext = createContext<AuthContextType | undefined>(undefined);

export function AuthProvider({ children }: { children: ReactNode }) {
    const [user, setUser] = useState<User | null>(() => {
        const saved = localStorage.getItem('user');
        return saved ? JSON.parse(saved) : null;
    });
    const [isAuthenticated, setIsAuthenticated] = useState<boolean>(!!user);

    const login = async (username: string, password: string): Promise<User> => {
        const response = await fetch('http://localhost:5000/api/auth/login', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ username, password }),
        });

        if (!response.ok) throw new Error('Autentificare eșuată');

        const data = await response.json();
        const raw  = data.user || data;

        // Backend returnează role ca număr (1/2/3) — normalizăm la string enum
        const roleNum       = Number(raw.role);
        const normalizedRole: Role = ROLE_NUM_TO_STRING[roleNum] ?? 'UTILIZATOR';

        const loggedUser: User = {
            id:         raw.id,
            username:   raw.username,
            fullName:   raw.fullName || raw.username,
            role:       normalizedRole,
            token:      raw.token,
            department: raw.department || '',
        };

        setUser(loggedUser);
        setIsAuthenticated(true);
        localStorage.setItem('user', JSON.stringify(loggedUser));
        return loggedUser;
    };

    const logout = () => {
        setUser(null);
        setIsAuthenticated(false);
        localStorage.removeItem('user');
    };

    const hasRole = (minRole: Role): boolean => {
        if (!user) return false;
        const userLevel = ROLE_HIERARCHY[user.role as Role] ?? 0;
        const minLevel  = ROLE_HIERARCHY[minRole]           ?? 0;
        return userLevel >= minLevel;
    };

    return (
        <AuthContext.Provider value={{ user, isAuthenticated, login, logout, hasRole }}>
            {children}
        </AuthContext.Provider>
    );
}

export function useAuth() {
    const context = useContext(AuthContext);
    if (!context) throw new Error('useAuth trebuie folosit în interiorul AuthProvider');
    return context;
}