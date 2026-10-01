import { expect, test } from '@playwright/test';
import { iniciarSesion, unico, usuarios } from './ayuda';

// Spec no-series (Parte 4): el borrador de factura es UNA página (cabecera editable en el sitio, líneas con fila de alta y edición
// en la fila, totales al pie, barra del documento). La suite no postea documentos: crea un cliente, un producto y un borrador
// (todos con prefijo E2E) y los elimina al final.
test('borrador de factura en una sola página: cabecera, líneas, totales y barra', async ({ page }) => {
  await iniciarSesion(page, usuarios.admin);

  const codigo = unico('E2E');
  const nombreCliente = `E2E Cliente ${codigo}`;
  let clienteCreado = false;
  let productoCreado = false;
  let borradorUrl = '';
  let fallo: unknown;
  try {
    // Datos propios: la base de desarrollo puede no tener clientes ni productos activos.
    await page.goto('/clientes/nuevo');
    await page.locator('[name="Input.NombreComercial"]').fill(nombreCliente);
    await page.locator('[name="Input.Email"]').fill(`${codigo.toLowerCase()}@e2e.local`);
    await page.getByTestId('guardar').click();
    await expect(page).toHaveURL(/\/clientes\?.*ok=created/);
    clienteCreado = true;

    await page.goto('/productos/nuevo');
    await page.locator('[name="Input.Codigo"]').fill(codigo);
    await page.locator('[name="Input.Nombre"]').fill(`E2E Producto ${codigo}`);
    await page.locator('[name="Input.PrecioVentaTexto"]').fill('100.00');
    await page.getByTestId('guardar').click();
    await expect(page).toHaveURL(/ok=created/);
    productoCreado = true;

    // Alta con los selectores de serie (la configurada por defecto).
    await page.goto(`/facturas-venta/nueva?socioQuery=${encodeURIComponent(nombreCliente)}`);
    await expect(page.locator('[name="AddInput.SerieRegistroId"]')).toBeVisible();
    const cliente = page.locator('[name="AddInput.SocioNegocioId"] option', { hasText: nombreCliente });
    await page.locator('[name="AddInput.SocioNegocioId"]').selectOption((await cliente.first().getAttribute('value')) ?? '');
    await page.getByTestId('guardar').click();
    await expect(page).toHaveURL(/\/facturas-venta\/borradores\/[0-9a-f-]+\?ok=creado/);
    borradorUrl = new URL(page.url()).pathname;

    // Una sola página: cabecera editable, serie de registro con próximo número, tabla con fila nueva y totales.
    await expect(page.getByTestId('cabecera-editable')).toBeVisible();
    await expect(page.getByTestId('serie-registro')).toContainText('Se numerará');
    await expect(page.getByTestId('tabla-lineas')).toBeVisible();
    await expect(page.getByTestId('fila-nueva')).toBeVisible();
    await expect(page.getByTestId('totales')).toBeVisible();

    // Barra del documento: Ver ▾ lleva al cliente del borrador.
    await page.getByTestId('menu-ver').locator('summary').click();
    await expect(page.getByTestId('menu-ver').getByRole('link', { name: 'Cliente', exact: true })).toBeVisible();
    await page.getByTestId('menu-ver').locator('summary').click();

    // Guardar la cabecera vuelve a la misma página.
    await page.locator('[name="UpdateInput.Descripcion"]').fill(`E2E ${codigo}`);
    await page.getByTestId('guardar-cabecera').click();
    await expect(page).toHaveURL(new RegExp(`${borradorUrl}\\?ok=modificado`));
    await expect(page.locator('[name="UpdateInput.Descripcion"]')).toHaveValue(`E2E ${codigo}`);

    // Agregar una línea desde la última fila (buscador de producto por nombre en la fila; el enlace de cada resultado es "Usar").
    const filaNueva = page.getByTestId('fila-nueva');
    await filaNueva.locator('[name="addProductoQuery"]').fill(codigo);
    await filaNueva.getByRole('button', { name: 'Buscar' }).click();
    await expect(page).toHaveURL(/addProductoQuery=/);
    await page.getByTestId('fila-nueva').locator('li', { hasText: codigo }).getByRole('link', { name: 'Usar' }).click();
    await expect(page).toHaveURL(/addProductoId=/);
    await page.getByTestId('fila-nueva').locator('[name="AddInput.CantidadTexto"]').fill('2');
    await page.getByTestId('fila-nueva').getByRole('button', { name: 'Agregar línea' }).click();
    await expect(page).toHaveURL(/ok=linea_creada/);
    await expect(page.getByTestId('tabla-lineas')).toContainText(codigo);
    // 2 × 100 sin descuento (el ITBIS depende del grupo de IVA por defecto del producto: no se asevera el total).
    await expect(page.getByTestId('total-sin-iva')).toHaveText('200.00');

    // Editar la línea en su fila.
    await page.getByTestId('tabla-lineas').getByRole('link', { name: 'Modificar' }).first().click();
    await expect(page.getByTestId('fila-edicion')).toBeVisible();
    await page.getByTestId('fila-edicion').locator('[name="UpdateInputLinea.CantidadTexto"]').fill('3');
    await page.getByTestId('fila-edicion').getByRole('button', { name: 'Guardar cambios' }).click();
    await expect(page).toHaveURL(/ok=linea_modificada/);
    await expect(page.getByTestId('total-sin-iva')).toHaveText('300.00');

    // La ruta antigua de la cabecera redirige a la página única.
    await page.goto(`${borradorUrl}/editar`);
    await expect(page).toHaveURL(new RegExp(`${borradorUrl}$`));
    await expect(page.getByTestId('cabecera-editable')).toBeVisible();
  } catch (error) {
    fallo = error;
    throw error;
  } finally {
    // Limpieza en orden inverso (el borrador antes que el cliente y el producto que referencia). Cada borrado se intenta aunque
    // falle el anterior; si la prueba ya falló, los fallos de limpieza se informan sin tapar el error original.
    const errores: string[] = [];
    const intentar = async (nombre: string, borrar: () => Promise<void>): Promise<void> => {
      try {
        await borrar();
      } catch (error) {
        errores.push(`${nombre}: ${error instanceof Error ? error.message : String(error)}`);
      }
    };
    if (borradorUrl) {
      await intentar('borrador', async () => {
        await page.goto(`${borradorUrl}?eliminar=true`);
        await page.getByRole('button', { name: 'Sí, eliminar' }).click();
        await expect(page).toHaveURL(/ok=eliminado/);
      });
    }
    if (productoCreado) {
      await intentar('producto', async () => {
        await page.goto(`/productos?view=list&filters=codigo&codigo=${encodeURIComponent(codigo)}`);
        await expect(page.getByTestId('seleccionar-fila')).toHaveCount(1);
        await page.getByTestId('seleccionar-fila').click();
        await expect(page).toHaveURL(/sel=/);
        await page.getByTestId('accion-eliminar').click();
        await page.getByRole('button', { name: 'Sí, eliminar' }).click();
        await expect(page).toHaveURL(/ok=deleted/);
      });
    }
    if (clienteCreado) {
      await intentar('cliente', async () => {
        await page.goto(`/clientes?view=list&nombre=${encodeURIComponent(nombreCliente)}`);
        await expect(page.getByTestId('seleccionar-fila')).toHaveCount(1);
        await page.getByTestId('seleccionar-fila').click();
        await expect(page).toHaveURL(/sel=/);
        await page.getByTestId('accion-eliminar').click();
        await page.getByRole('button', { name: 'Sí, eliminar' }).click();
        await expect(page).toHaveURL(/ok=deleted/);
      });
    }
    if (errores.length > 0) {
      const mensaje = `Limpieza E2E incompleta (datos con prefijo ${codigo}): ${errores.join(' | ')}`;
      if (fallo === undefined) {
        throw new Error(mensaje);
      }
      console.warn(mensaje);
    }
  }
});
