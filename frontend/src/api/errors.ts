/**
 * Extragerea mesajului de eroare dintr-un răspuns al API-ului.
 *
 * Backendul întoarce erorile ca `{ message: "..." }`. Axios ambalează asta în
 * `error.response.data`, deci `error.message` conține doar textul generic
 * „Request failed with status code 400" - inutil pentru utilizator.
 *
 * Codul vechi făcea peste tot `res.json().catch(...)` manual. Aici se face o
 * singură dată, corect, inclusiv pentru cazurile în care serverul nu a răspuns
 * deloc (rețea căzută, API oprit).
 */

import axios from 'axios';

export function apiErrorMessage(error: unknown, fallback = 'Operația a eșuat.'): string {
    if (axios.isAxiosError(error)) {
        // Serverul nu a răspuns: API oprit, rețea căzută, CORS blocat.
        if (!error.response) {
            return 'Serverul nu răspunde. Verificați conexiunea sau dacă API-ul rulează.';
        }

        const data = error.response.data as unknown;

        if (typeof data === 'string' && data.trim()) return data;

        if (data && typeof data === 'object') {
            const message = (data as { message?: unknown }).message;
            if (typeof message === 'string' && message.trim()) return message;

            // Endpointurile de invitație răspund cu `{ error }`, nu cu `{ message }`.
            const errorText = (data as { error?: unknown }).error;
            if (typeof errorText === 'string' && errorText.trim()) return errorText;

            // ProblemDetails din ASP.NET, când validarea modelului eșuează.
            const title = (data as { title?: unknown }).title;
            if (typeof title === 'string' && title.trim()) return title;
        }

        if (error.response.status === 403) return 'Nu aveți permisiunea necesară.';
        if (error.response.status === 429) return 'Prea multe încercări. Reîncercați peste câteva minute.';

        return `${fallback} (HTTP ${error.response.status})`;
    }

    if (error instanceof Error && error.message) return error.message;

    return fallback;
}