import axios from 'axios';

// Schimbă URL-ul dacă backend-ul rulează pe alt port (ex: 5000 / 7071)
const API_URL = 'https://localhost:7123/api/Auth';

export interface LoginPayload {
    username: string;
    password: string;
    role: number; // 0 = User, 1 = Sef, 2 = Admin
}

export interface AuthResponse {
    token: string;
    role: number;
}

export const login = async (credentials: LoginPayload): Promise<AuthResponse> => {
    const response = await axios.post<AuthResponse>(`${API_URL}/login`, credentials);

    if (response.data.token) {
        localStorage.setItem('token', response.data.token);
        localStorage.setItem('userRole', response.data.role.toString());
    }

    return response.data;
};

export const logout = () => {
    localStorage.removeItem('token');
    localStorage.removeItem('userRole');
};

export const isAuthenticated = (): boolean => {
    return !!localStorage.getItem('token');
};

export const getUserRole = (): number | null => {
    const role = localStorage.getItem('userRole');
    return role !== null ? parseInt(role, 10) : null;
};