import { defineConfig, devices } from '@playwright/test';

// E2E contra el stack de Docker Compose (Blazor en :8080). Credenciales solo por variables de entorno (ver specs/ayuda.ts).
// Los artefactos pesados (trazas, vídeos, capturas de fallo) quedan en tests/e2e/test-results (ignorado por git); el
// reporte HTML versionado solo lleva adjuntos cuando algo falla, por eso las trazas se activan con E2E_TRACE=1.
export default defineConfig({
  testDir: './specs',
  timeout: 60_000,
  expect: { timeout: 10_000 },
  fullyParallel: false,
  workers: 1,
  // El caso @api-caida se ejecuta aparte (API detenida): E2E_REPORTE=reporte-e2e-api-caida para no pisar el reporte principal.
  reporter: [
    ['list'],
    // Part 1: ['html', { outputFolder: `../../docs/entregable-parte-1/evidencias/${process.env.E2E_REPORTE ?? 'reporte-e2e'}`, open: 'never' }],
    ['html', { outputFolder: process.env.E2E_REPORT_DIR ?? `../../docs/entregable-parte-1/evidencias/${process.env.E2E_REPORTE ?? 'reporte-e2e'}`, open: 'never' }],
  ],
  use: {
    baseURL: process.env.E2E_BASE_URL ?? 'http://localhost:8080',
    locale: 'es-DO',
    viewport: { width: 1440, height: 900 },
    screenshot: 'only-on-failure',
    trace: process.env.E2E_TRACE === '1' ? 'retain-on-failure' : 'off',
  },
  // Controles nativos en español (selector de archivo, formato de fecha): locale del contexto + idioma del navegador.
  projects: [
    {
      name: 'chromium',
      use: {
        ...devices['Desktop Chrome'],
        // Chromium completo (nuevo headless) en vez de headless_shell: respeta --lang en los controles nativos.
        channel: 'chromium',
        viewport: { width: 1440, height: 900 },
        locale: 'es-DO',
        launchOptions: { args: ['--lang=es-DO'], env: { ...process.env, LANG: 'es_DO.UTF-8', LANGUAGE: 'es_DO:es' } },
      },
    },
  ],
});
