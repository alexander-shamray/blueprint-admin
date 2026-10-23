/**
 * The second fake host, with the assistant switched off (AI assistant design §9.1): playwright.config.ts starts it.
 * Its port is one neither the host's own (`AdminOptions.Port`) nor the SPA dev server's (`angular.json`) is, so a
 * developer's running console is never taken for it, and `npm start` still binds while the smoke runs.
 */
export const OFF_PORT = 5310;
export const OFF_URL = `http://127.0.0.1:${OFF_PORT}`;
