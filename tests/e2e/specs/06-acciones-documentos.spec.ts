import { expect, test } from '@playwright/test';
import { captura, iniciarSesion, unico, usuarios } from './ayuda';

test('desde un cliente: Crear ▾ Factura abre la nueva factura con el cliente precargado', async ({ page }) => {
  await iniciarSesion(page, usuarios.admin);
  await page.goto('/clientes?view=list');
  await page.getByTestId('seleccionar-fila').first().click();
  await expect(page).toHaveURL(/sel=/);
  await page.getByTestId('menu-crear').locator('summary').click();
  await page.getByTestId('menu-crear').getByRole('link', { name: 'Factura', exact: true }).click();
  await expect(page).toHaveURL(/\/facturas-venta\/nueva\?.*socioId=/);
  // Alta rápida: el cliente elegido se muestra con sus datos derivados en solo lectura.
  await expect(page.getByTestId('cliente-elegido')).toBeVisible();
  await expect(page.getByTestId('datos-derivados')).toBeVisible();
  await captura(page, '26-nueva-factura-precargada', /nueva factura/i);
});

test('facturas filtradas por cliente y ficha de producto con acciones', async ({ page }) => {
  await iniciarSesion(page, usuarios.admin);
  // Producto E2E propio (el catálogo puede estar vacío); se elimina al final.
  // Sin recortar: unico() da 16 caracteres (con su sufijo aleatorio) y el código de producto admite 50.
  const codigo = unico('E2E');
  await page.goto('/productos/nuevo');
  await page.locator('[name="Input.Codigo"]').fill(codigo);
  await page.locator('[name="Input.Nombre"]').fill(`E2E Producto ${codigo}`);
  await page.locator('[name="Input.PrecioVentaTexto"]').fill('80.00');
  await page.getByTestId('guardar').click();
  await expect(page).toHaveURL(/ok=created/);
  await page.goto(`/productos?view=list&filters=codigo&codigo=${codigo}`);
  await page.getByTestId('seleccionar-fila').first().click();
  await expect(page).toHaveURL(/sel=/);
  await page.getByTestId('menu-ver').locator('summary').click();
  await expect(page.getByTestId('menu-ver').getByRole('link', { name: 'Existencias' })).toBeVisible();
  await captura(page, '27-producto-menu-ver', page.getByTestId('menu-ver').getByRole('link', { name: 'Movimientos de producto' }));
  await page.getByTestId('menu-ver').locator('summary').click();
  await page.getByTestId('accion-eliminar').click();
  await page.getByRole('button', { name: 'Sí, eliminar' }).click();
  await expect(page).toHaveURL(/ok=deleted/);

  // Cliente con alguna factura posteada: se toma de la cabecera de la primera factura ("Vender a"). Los documentos
  // posteados no se pueden borrar, así que la suite no los crea: sin facturas la prueba se omite con su motivo.
  await page.goto('/facturas-venta');
  const numeros = page.getByTestId('tabla-facturas').locator('a[href^="/facturas-venta/"]');
  test.skip((await numeros.count()) === 0, 'No hay facturas posteadas en la base de datos (la suite no postea documentos).');
  const numeroFactura = (await numeros.first().innerText()).trim();
  await numeros.first().click();
  const venderA = page.getByTestId('cabecera-factura').locator('div').filter({ has: page.getByText('Vender a', { exact: true }) }).last();
  const codigoCliente = (await venderA.getByText(/ — /).first().innerText()).split(' — ')[0].trim();
  expect(codigoCliente).not.toBe('');
  await page.goto(`/clientes?view=list&filters=codigo&codigo=${encodeURIComponent(codigoCliente)}`);
  await page.getByTestId('seleccionar-fila').first().click();
  await expect(page).toHaveURL(/sel=/);
  await page.getByTestId('menu-ver').locator('summary').click();
  await page.getByTestId('menu-ver').getByRole('link', { name: 'Facturas del cliente' }).click();
  await expect(page).toHaveURL(/\/facturas-venta\?.*socioId=/);
  await expect(page.getByTestId('filtro-cliente')).toBeVisible();
  // El filtro incluye las facturas donde el cliente vende o factura: la factura de origen está en la lista.
  await expect(page.getByTestId('tabla-facturas').getByRole('link', { name: numeroFactura }).first()).toBeVisible();
  await captura(page, '28-facturas', page.getByTestId('filtro-cliente'));
});
