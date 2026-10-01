import { expect, test } from '@playwright/test';
import { iniciarSesion, unico, usuarios } from './ayuda';

// Spec alta rápida: la factura en preparación es UNA página (datos derivados, numeración, líneas con fila de alta y edición en la
// fila, totales al pie, barra del documento); la suite no registra documentos en este spec: crea un cliente, un producto y una factura
// en preparación (todos con prefijo E2E) y los descarta/elimina al final.
test('factura en preparación en una sola página: alta rápida, líneas, totales y rutas antiguas', async ({ page }) => {
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

    // Alta rápida: cliente por GET + "Usar", datos derivados en solo lectura y la primera línea (producto) en un solo envío.
    await page.goto(`/facturas-venta/nueva?socioQuery=${encodeURIComponent(nombreCliente)}`);
    await page.getByTestId('clientes-encontrados').locator('li', { hasText: nombreCliente }).getByRole('link', { name: /Usar/ }).click();
    await expect(page.getByTestId('cliente-elegido')).toBeVisible();
    await expect(page.getByTestId('datos-derivados')).toContainText('Moneda');
    await expect(page.getByTestId('serie-registro')).toContainText('Se numerará con la serie');
    await page.locator('[name="addProductoQuery"]').fill(codigo);
    await page.getByRole('button', { name: 'Buscar' }).click();
    await expect(page).toHaveURL(/addProductoQuery=/);
    await page.locator('li', { hasText: codigo }).getByRole('link', { name: 'Usar' }).click();
    await expect(page).toHaveURL(/addProductoId=/);
    await page.locator('[name="AltaInput.CantidadTexto"]').fill('2');
    await page.getByTestId('agregar-linea').click();
    await expect(page).toHaveURL(/\/facturas-venta\/en-preparacion\/[0-9a-f-]+.*ok=linea_creada/);
    borradorUrl = new URL(page.url()).pathname;

    // Una sola página: datos derivados, numeración, tabla con la línea y fila nueva, totales; sin número de borrador.
    await expect(page.getByTestId('cabecera-editable')).toBeVisible();
    await expect(page.getByTestId('tabla-lineas')).toContainText(codigo);
    await expect(page.getByTestId('fila-nueva')).toBeVisible();
    // 2 × 100 sin descuento (el ITBIS depende del grupo de IVA por defecto del producto: no se asevera el total).
    await expect(page.getByTestId('total-sin-iva')).toHaveText('200.00');

    // Barra del documento: Ver ▾ lleva al cliente.
    await page.getByTestId('menu-ver').locator('summary').click();
    await expect(page.getByTestId('menu-ver').getByRole('link', { name: 'Cliente', exact: true })).toBeVisible();
    await page.getByTestId('menu-ver').locator('summary').click();

    // Guardar cambios vuelve a la misma página.
    await page.locator('[name="UpdateInput.Descripcion"]').fill(`E2E ${codigo}`);
    await page.getByTestId('guardar-cabecera').click();
    await expect(page).toHaveURL(new RegExp(`${borradorUrl}\\?ok=modificado`));
    await expect(page.locator('[name="UpdateInput.Descripcion"]')).toHaveValue(`E2E ${codigo}`);

    // Editar la línea en su fila (nombre accesible "Modificar línea 1").
    await page.getByRole('link', { name: 'Modificar línea 1' }).click();
    await expect(page.getByTestId('fila-edicion')).toBeVisible();
    await page.getByTestId('fila-edicion').locator('[name="UpdateInputLinea.CantidadTexto"]').fill('3');
    await page.getByTestId('fila-edicion').getByRole('button', { name: 'Guardar cambios' }).click();
    await expect(page).toHaveURL(/ok=linea_modificada/);
    await expect(page.getByTestId('total-sin-iva')).toHaveText('300.00');

    // Las rutas antiguas redirigen a la factura en preparación.
    const id = borradorUrl.split('/').pop();
    await page.goto(`/facturas-venta/borradores/${id}`);
    await expect(page).toHaveURL(new RegExp(`${borradorUrl}$`));
    await page.goto(`/facturas-venta/borradores/${id}/editar`);
    await expect(page).toHaveURL(new RegExp(`${borradorUrl}$`));
  } catch (error) {
    fallo = error;
    throw error;
  } finally {
    // Limpieza en orden inverso (la factura en preparación antes que el cliente y el producto que referencia). Cada borrado se intenta aunque
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
      await intentar('factura en preparación', async () => {
        await page.goto(`${borradorUrl}?eliminar=true`);
        await page.getByRole('button', { name: 'Sí, descartar' }).click();
        await expect(page).toHaveURL(/ok=descartada/);
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
