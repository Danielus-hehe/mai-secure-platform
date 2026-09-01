/** @type {import('tailwindcss').Config} */
export default {
    content: ['./index.html', './src/**/*.{ts,tsx}'],
    theme: {
        extend: {
            colors: {
                mai: {
                    50: '#eef4fb',
                    100: '#d9e6f5',
                    200: '#b3cde9',
                    300: '#7fa8d6',
                    400: '#4d7fbd',
                    500: '#2a5a99',
                    600: '#1b4377',
                    700: '#143461', 
                    800: '#0e2649',
                    900: '#0a1c38',   // sidebar / header
                    950: '#061225',
                },
                gold: {
                    400: '#e8c15a',
                    500: '#d4a935',
                    600: '#b3891f',
                },
            },
            fontFamily: {
                sans: ['"Segoe UI"', 'Roboto', 'system-ui', 'sans-serif'],
            },
            boxShadow: {
                card: '0 1px 3px rgba(10,28,56,0.12), 0 1px 2px rgba(10,28,56,0.08)',
            },
        },
    },
    plugins: [],
};