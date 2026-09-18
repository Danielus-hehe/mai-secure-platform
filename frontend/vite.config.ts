import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite'

export default defineConfig({
  plugins: [react(), tailwindcss()],
  // .env-ul comun stă în rădăcina repository-ului, lângă docker-compose.yml.
  // Vite expune în cod DOAR variabilele cu prefixul VITE_, deci secretele
  // backend-ului din același fișier (MAI_JWT_KEY, parole) nu ajung în bundle.
  envDir: '..',
  server: {
    proxy: {
      '/api': {
        target: 'http://localhost:5000',
        changeOrigin: true,
      },
    },
  },
});