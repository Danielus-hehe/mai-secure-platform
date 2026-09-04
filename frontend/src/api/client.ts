import axios from 'axios';

export const api = axios.create({
    baseURL: import.meta.env.VITE_API_URL ?? 'http://localhost:5000/api',
    timeout: 30_000,
});

// Atașare automată token JWT
api.interceptors.request.use((config) => {
    const token = localStorage.getItem('mai_access_token');
    if (token) config.headers.Authorization = `Bearer ${token}`;
    return config;
});

// Expirare sesiune → redirect la login
api.interceptors.response.use(
    (res) => res,
    (err) => {
        if (err.response?.status === 401) {
            localStorage.removeItem('mai_access_token');
            window.location.href = '/login';
        }
        return Promise.reject(err);
    }
);