import { expect, test, type APIRequestContext } from '@playwright/test';
import { iniciarSesion, usuarios, unico } from './ayuda';
import fs from 'node:fs/promises';
import { execFileSync } from 'node:child_process';

// Opt-in: these business documents are immutable and belong ONLY to the isolated Part 2 lab.
test.skip(process.env.E2E_PARTE2 !== '1', 'Requires the dedicated Part 2 lab and explicit opt-in.');

test('Parte 2: vendedor, venta, inventario, CxC, rollback, cuatro reportes y exportación', async ({ page, playwright }, testInfo) => {
  test.setTimeout(180_000);
  const apiUrl = process.env.E2E_API_URL ?? 'http://localhost:18081';
  const password = process.env.E2E_PASSWORD;
  if (!password) throw new Error('Set private E2E_PASSWORD.');
  const anonymous = await playwright.request.newContext({ baseURL: apiUrl });
  const incorrecto = await anonymous.post('/api/auth/login', { data: { userNameOrEmail: usuarios.admin, password: 'intentionally-incorrect' } });
  expect(incorrecto.ok()).toBeFalsy();
  const auth = async (usuario: string) => {
    const response = await anonymous.post('/api/auth/login', { data: { userNameOrEmail: usuario, password } });
    expect(response.ok(), `Login ${usuario}: ${response.status()}`).toBeTruthy();
    return playwright.request.newContext({ baseURL: apiUrl, extraHTTPHeaders: { Authorization: `Bearer ${(await response.json()).accessToken}` } });
  };
  const admin = await auth(usuarios.admin);
  const vendedor = await auth('vendedor@opensource1.local');
  const post = async (api: APIRequestContext, url: string, data?: unknown) => {
    const response = await api.post(url, { data });
    expect(response.ok(), `${url} (${response.status()}): ${await response.text()}`).toBeTruthy();
    return response.json();
  };
  const marca = unico('P2');
  const fecha = new Date().toISOString().slice(0, 10);
  const vencimiento = new Date(Date.now() + 30 * 86400000).toISOString().slice(0, 10);
  try {
    expect((await anonymous.get('/api/reportes/ventas')).status()).toBe(401);
    expect((await vendedor.get('/api/users')).status()).toBe(403);
    expect((await admin.post('/api/productos', { data: { codigo: '', nombre: '', precioVenta: -1 } })).status()).toBe(400);
    const cliente = await post(admin, '/api/socios-negocio', { nombreComercial: `Cliente ${marca}`, email: `${marca.toLowerCase()}@example.test` });
    const producto = await post(admin, '/api/productos', { codigo: marca, nombre: `Producto ${marca}`, precioVenta: 100 });
    const almacenes = await (await admin.get('/api/almacenes')).json();
    const almacen = almacenes.items[0];
    const plantillas = await (await admin.get('/api/diarios-inventario/plantillas')).json();
    const plantilla = plantillas.find((p: { codigo: string }) => p.codigo === 'ARTICULO');
    const lote = await post(admin, '/api/diarios-inventario/lotes', { plantillaDiarioId: plantilla.id, codigo: marca, nombre: marca, bloqueado: false });
    await post(admin, `/api/diarios-inventario/lotes/${lote.id}/lineas`, {
      fechaRegistro: fecha, fechaDocumento: fecha, tipoMovimiento: 3, productoId: producto.id,
      almacenId: almacen.id, unidadMedidaId: producto.unidadMedidaBaseId, cantidad: 10, costoUnitario: 50,
    });
    await post(admin, `/api/diarios-inventario/lotes/${lote.id}/registrar`);
    const borrador = async (cantidad: number) => {
      const b = await post(vendedor, '/api/facturas-venta/borradores', {
        socioNegocioId: cliente.id, fechaRegistro: fecha, fechaDocumento: fecha, fechaVencimiento: vencimiento, almacenId: almacen.id,
      });
      await post(vendedor, `/api/facturas-venta/borradores/${b.id}/lineas`, { tipo: 1, productoId: producto.id, cantidad, precioUnitario: 100, almacenId: almacen.id });
      return b;
    };
    const b = await borrador(2);
    const venta = await post(vendedor, `/api/facturas-venta/borradores/${b.id}/postear`);
    expect(venta.importeTotal).toBeGreaterThanOrEqual(200);
    const factura = await (await vendedor.get(`/api/facturas-venta/${encodeURIComponent(venta.numero)}`)).json();
    expect(factura.cabecera.fechaVencimiento).toBe(vencimiento);
    expect(factura.cabecera.socioNegocioFacturarAId).toBe(cliente.id);
    expect(factura.cabecera.importeTotal).toBe(venta.importeTotal);
    expect(factura.lineas).toHaveLength(1);
    expect(factura.lineas[0].cantidad).toBe(2);
    const existencias = async () => (await (await admin.get(`/api/reportes/inventario?productoId=${producto.id}&hasta=${fecha}`)).json()).pagina.items;
    expect((await existencias())[0].existencia).toBe(8);
    const saldo = await (await admin.get(`/api/clientes/${cliente.id}/saldo`)).json();
    expect(saldo.saldo).toBe(venta.importeTotal);
    const movimientos = await (await admin.get(`/api/clientes/${cliente.id}/movimientos`)).json();
    const cuentaPorCobrar = movimientos.items.find((m: { numeroDocumento: string }) => m.numeroDocumento === venta.numero);
    expect(cuentaPorCobrar).toBeDefined();
    expect(cuentaPorCobrar.importeOriginal).toBe(venta.importeTotal);
    expect(cuentaPorCobrar.fechaVencimiento).toBe(vencimiento);
    expect(cuentaPorCobrar.socioNegocioId).toBe(cliente.id);
    expect(cuentaPorCobrar.importeRestante).toBe(venta.importeTotal);
    expect(cuentaPorCobrar.abierta).toBe(true);
    const excesivo = await borrador(99);
    const rechazo = await vendedor.post(`/api/facturas-venta/borradores/${excesivo.id}/postear`);
    expect(rechazo.status()).toBe(400);
    expect((await existencias())[0].existencia).toBe(8);
    expect((await (await admin.get(`/api/clientes/${cliente.id}/saldo`)).json()).saldo).toBe(saldo.saldo);
    await post(admin, '/api/contabilidad/postear-costo-inventario');
    const segundoBatch = await post(admin, '/api/contabilidad/postear-costo-inventario');
    expect(segundoBatch.movimientosValorContabilizados).toBe(0);

    await iniciarSesion(page, 'vendedor');
    await page.goto(`/facturas-venta/${encodeURIComponent(venta.numero)}`);
    await expect(page.getByText(venta.numero).first()).toBeVisible();
    await page.locator('h1').evaluateAll(elements => elements.forEach(element => (element as HTMLElement).blur()));
    await page.screenshot({ path: testInfo.outputPath('factura.png'), fullPage: true });
    await testInfo.attach('Factura registrada', { path: testInfo.outputPath('factura.png'), contentType: 'image/png' });
    const reportes = ['ventas', 'inventario', 'top-productos', 'cuentas-por-cobrar'];
    for (const tipo of reportes) {
      const filtro = tipo === 'inventario' ? `productoId=${producto.id}` : `clienteId=${cliente.id}`;
      await page.goto(`/reporteria/empresarial?tipo=${tipo}&hasta=${fecha}&${filtro}`);
      await expect(page.getByRole('heading', { name: /Ventas por período|Inventario disponible|Productos más vendidos|Cuentas por cobrar/ })).toBeVisible();
      await expect(page.getByRole('alert')).toHaveCount(0);
      await expect(page.locator('tbody tr')).not.toHaveCount(0);
      await page.locator('h1').evaluateAll(elements => elements.forEach(element => (element as HTMLElement).blur()));
      await page.screenshot({ path: testInfo.outputPath(`${tipo}.png`), fullPage: true });
      await testInfo.attach(`Reporte ${tipo}`, { path: testInfo.outputPath(`${tipo}.png`), contentType: 'image/png' });
      for (const formato of ['PDF', 'Excel']) {
        const downloadPromise = page.waitForEvent('download');
        await page.getByRole('link', { name: formato === 'PDF' ? 'Descargar PDF' : 'Exportar Excel' }).click();
        const download = await downloadPromise;
        const destino = testInfo.outputPath(download.suggestedFilename());
        await download.saveAs(destino);
        expect((await fs.stat(destino)).size).toBeGreaterThan(100);
        if (formato === 'PDF') expect((await fs.readFile(destino)).subarray(0, 5).toString()).toBe('%PDF-');
        else execFileSync('python3', ['../../scripts/parte2-verificar-export.py', tipo, destino, String(venta.importeTotal)], { encoding: 'utf8' });
        await testInfo.attach(`${tipo}-${formato}`, { path: destino });
      }
    }
    await page.goto('/reporteria/empresarial?tipo=ventas&desde=2026-12-31&hasta=2026-01-01');
    await expect(page.getByRole('alert')).toContainText('filtros');
    await page.goto('/admin/users');
    await expect(page.getByText(/acceso denegado|sin permisos|no tiene permisos/i).first()).toBeVisible();
  } finally {
    await admin.dispose(); await vendedor.dispose(); await anonymous.dispose();
  }
});
