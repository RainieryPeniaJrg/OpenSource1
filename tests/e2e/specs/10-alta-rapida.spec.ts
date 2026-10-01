import { expect, test, type Page } from '@playwright/test';
import { iniciarSesion, unico, usuarios } from './ayuda';

// Spec alta rápida: criterio de éxito. Una factura de 2 líneas se prepara y se registra en pocas cargas de página y su nota de crédito
// total sale desde la factura en 2 clics más la confirmación. Las líneas son de cuenta contable (4101 Ventas, ITBIS18): no mueven
// inventario y no necesitan existencias. ESTE SPEC REGISTRA DOCUMENTOS: la factura y la nota quedan en la base de desarrollo (cliente
// "E2E Alta …"); no se pueden borrar.

/**
 * Cuenta las cargas de página del recorrido: cada acción del usuario que pide una página al servidor (enlace, formulario GET o POST
 * con redirección) se ejecuta con cargar(); se espera a la URL de destino si se indica. Contar acciones (no peticiones HTTP) evita
 * depender de cómo la navegación mejorada de Blazor encadena el POST y el GET de la redirección.
 */
function contadorDeCargas(page: Page) {
  let cargas = 0;
  return {
    get total(): number {
      return cargas;
    },
    async cargar(accion: () => Promise<unknown>, url?: RegExp): Promise<void> {
      await accion();
      if (url) {
        await expect(page).toHaveURL(url);
      }
      await page.waitForLoadState('domcontentloaded');
      cargas += 1;
    },
  };
}

async function rellenarLineaDeCuenta(page: Page, importe: string): Promise<void> {
  await page.locator('[name$=".CuentaContableId"]').selectOption({ label: '4101 — Ventas' });
  await page.locator('[name$=".GrupoIvaProductoId"]').selectOption({ label: 'ITBIS18 — Gravado con ITBIS 18%' });
  await page.locator('[name$=".CantidadTexto"]').fill('1');
  await page.locator('[name$=".PrecioUnitarioTexto"]').fill(importe);
}

async function crearCliente(page: Page, nombre: string, codigo: string): Promise<void> {
  await page.goto('/clientes/nuevo');
  await page.locator('[name="Input.NombreComercial"]').fill(nombre);
  await page.locator('[name="Input.Email"]').fill(`${codigo.toLowerCase()}@e2e.local`);
  await page.getByTestId('guardar').click();
  await expect(page).toHaveURL(/\/clientes\?.*ok=created/);
}

test('alta rápida: factura de 2 líneas registrada y nota de crédito total desde la factura', async ({ page }) => {
  await iniciarSesion(page, usuarios.admin);

  const codigo = unico('E2E');
  const nombreCliente = `E2E Alta ${codigo}`;
  await crearCliente(page, nombreCliente, codigo);

  // ---- Factura de 2 líneas: desde la lista hasta "Factura … registrada." ----
  await page.goto('/facturas-venta');
  const factura = contadorDeCargas(page);
  await factura.cargar(() => page.getByRole('link', { name: '+ Nueva factura' }).click(), /\/facturas-venta\/nueva/);
  await page.locator('[name="socioQuery"]').fill(nombreCliente);
  await factura.cargar(() => page.getByRole('button', { name: 'Buscar' }).click(), /socioQuery=/);
  await factura.cargar(() => page.getByRole('link', { name: `Usar ${nombreCliente}` }).click(), /socioId=/);
  await expect(page.getByTestId('datos-derivados')).toBeVisible();
  await factura.cargar(() => page.getByTestId('tipos-linea').getByRole('link', { name: 'Cuenta contable' }).click(), /tipo=2/);
  await rellenarLineaDeCuenta(page, '100');
  await factura.cargar(() => page.getByTestId('agregar-linea').click(), /\/facturas-venta\/en-preparacion\/[0-9a-f-]+\?tipo=2&ok=linea_creada/);
  await rellenarLineaDeCuenta(page, '50');
  await factura.cargar(() => page.getByTestId('fila-nueva').getByRole('button', { name: 'Agregar línea' }).click());
  await expect(page.getByTestId('tabla-lineas').locator('tbody tr[id^="linea-"]')).toHaveCount(2);
  await expect(page.getByTestId('total-sin-iva')).toHaveText('150.00');
  await factura.cargar(() => page.getByTestId('registrar').click(), /registrar=true/);
  await expect(page.getByTestId('resumen-registro')).toContainText('Total');
  await factura.cargar(() => page.getByTestId('si-registrar').click(), /\/facturas-venta\/[^/?]+\?ok=registrada/);
  await expect(page.getByText(/Factura .+ registrada\./)).toBeVisible();
  console.log(`Cargas de página de la factura de 2 líneas: ${factura.total}`);
  // Criterio del spec: de ~12 a ~7 (aquí 8: incluye buscar el cliente y elegir el tipo de línea "Cuenta contable").
  expect(factura.total).toBeLessThanOrEqual(8);
  const urlFactura = new URL(page.url()).pathname;
  console.log(`${await page.getByText(/Factura .+ registrada\./).innerText()} (cliente ${nombreCliente})`);

  // ---- Nota de crédito total: 2 clics más la confirmación ----
  const nota = contadorDeCargas(page);
  await nota.cargar(() => page.getByTestId('crear-nota').click(), /\/notas-credito-venta\/en-preparacion\/[0-9a-f-]+\?ok=creado/);
  await expect(page.getByTestId('tabla-lineas').locator('tbody tr[id^="linea-"]')).toHaveCount(2);
  await nota.cargar(() => page.getByTestId('registrar').click(), /registrar=true/);
  await nota.cargar(() => page.getByTestId('si-registrar').click(), /\/notas-credito-venta\/[^/?]+\?ok=registrada/);
  console.log(`Cargas de página de la nota total: ${nota.total}`);
  console.log(`Nota de crédito registrada: ${await page.getByRole('heading', { level: 1 }).first().innerText()}`);
  expect(nota.total).toBeLessThanOrEqual(3);

  // La factura ya no tiene pendiente: sin "Crear nota de crédito".
  await page.goto(urlFactura);
  await expect(page.getByTestId('crear-nota')).toHaveCount(0);
});

test('las listas muestran el contador "En preparación (n)" y cargan', async ({ page }) => {
  await iniciarSesion(page, usuarios.admin);

  for (const ruta of ['/facturas-venta', '/notas-credito-venta']) {
    await page.goto(ruta);
    await expect(page.getByTestId('pestana-preparacion')).toHaveText(/^En preparación( \(\d+\))?$/);
    await page.getByTestId('pestana-preparacion').click();
    await expect(page).toHaveURL(/estado=preparacion/);
    await expect(page.getByTestId('tabla-en-preparacion')).toBeVisible();
  }
});

// Recorrido de la puerta G2: menú, paleta, rutas antiguas, "Más opciones" solo para el Administrador y foco en el resumen de errores.
// Crea un cliente y una factura en preparación propios y los descarta/elimina al final (no registra nada).
test('menú de Facturación, paleta, rutas antiguas, serie solo para el administrador y precio obligatorio', async ({ page }) => {
  await iniciarSesion(page, usuarios.admin);

  // El grupo Facturación tiene exactamente 2 entradas (además de "Ver grupo").
  await page.goto('/facturas-venta');
  const grupo = page.getByTestId('nav-menu').locator('details[data-grupo="facturacion"]');
  await expect(grupo.locator('a:not([href^="/modulos/"])')).toHaveText(['Facturas', 'Notas de crédito']);

  // La paleta Ctrl+K encuentra "en preparación".
  await page.keyboard.press('Control+k');
  await expect(page.locator('#busqueda-paleta')).toBeVisible();
  await page.keyboard.type('en preparación');
  await expect(page.locator('#busqueda-paleta-lista')).not.toContainText('Buscando');
  const opciones = page.locator('#busqueda-paleta-lista [role="option"]');
  await expect(opciones.filter({ hasText: 'Facturas' }).first()).toBeVisible();
  await expect(opciones.filter({ hasText: 'Notas de crédito' }).first()).toBeVisible();
  await page.keyboard.press('Escape');

  // La lista antigua de borradores redirige a la pestaña "En preparación".
  await page.goto('/facturas-venta/borradores');
  await expect(page).toHaveURL(/\/facturas-venta\?estado=preparacion$/);
  await expect(page.getByTestId('tabla-en-preparacion')).toBeVisible();

  const codigo = unico('E2E');
  const nombreCliente = `E2E Serie ${codigo}`;
  let clienteCreado = false;
  let preparacionUrl = '';
  let fallo: unknown;
  try {
    await crearCliente(page, nombreCliente, codigo);
    clienteCreado = true;

    await page.goto(`/facturas-venta/nueva?socioQuery=${encodeURIComponent(nombreCliente)}`);
    await page.getByRole('link', { name: `Usar ${nombreCliente}` }).click();
    await expect(page).toHaveURL(/socioId=/);

    // Administrador en el alta: "Más opciones → Cambiar serie de registro" (dentro del formulario de la primera línea, que con el
    // tipo Producto aparece al elegir el producto; aquí, con el tipo "Cuenta contable").
    await expect(page.getByTestId('serie-registro')).toContainText(/Se numerará con la serie \S+ · próximo \S+/);
    await page.getByTestId('tipos-linea').getByRole('link', { name: 'Cuenta contable' }).click();
    await expect(page).toHaveURL(/tipo=2/);
    await page.getByTestId('mas-opciones').locator('summary').click();
    await expect(page.getByTestId('mas-opciones').getByLabel('Cambiar serie de registro')).toBeVisible();

    // Error de validación (precio vacío): no se crea nada y el mensaje queda junto al campo (el foco: test aparte, más abajo).
    await rellenarLineaDeCuenta(page, '');
    await page.getByTestId('agregar-linea').click();
    await expect(page.getByText(/El precio unitario es obligatorio/)).toBeVisible();
    await expect(page).toHaveURL(/\/facturas-venta\/nueva/);

    // Con precio se crea la factura en preparación (se descarta al final).
    await page.locator('[name$=".PrecioUnitarioTexto"]').fill('10');
    await page.getByTestId('agregar-linea').click();
    await expect(page).toHaveURL(/\/facturas-venta\/en-preparacion\/[0-9a-f-]+\?tipo=2&ok=linea_creada/);
    preparacionUrl = new URL(page.url()).pathname;
    await page.getByTestId('mas-opciones').locator('summary').click();
    await expect(page.getByTestId('mas-opciones').getByLabel('Cambiar serie de registro')).toBeVisible();

    // Supervisor (consultar + modificar, sin agregar): la factura en preparación editable, sin "Más opciones".
    await page.context().clearCookies();
    await iniciarSesion(page, usuarios.supervisor);
    await page.goto(preparacionUrl);
    await expect(page.getByTestId('guardar-cabecera')).toBeVisible();
    await expect(page.getByTestId('serie-registro')).toContainText('Se numerará con la serie');
    await expect(page.getByTestId('mas-opciones')).toHaveCount(0);
    await expect(page.getByText('Cambiar serie de registro')).toHaveCount(0);
  } catch (error) {
    fallo = error;
    throw error;
  } finally {
    if (clienteCreado) {
      try {
        await page.context().clearCookies();
        await iniciarSesion(page, usuarios.admin);
        if (preparacionUrl) {
          await page.goto(`${preparacionUrl}?eliminar=true`);
          await page.getByRole('button', { name: 'Sí, descartar' }).click();
          await expect(page).toHaveURL(/ok=descartada/);
        }
        await page.goto(`/clientes?view=list&nombre=${encodeURIComponent(nombreCliente)}`);
        await expect(page.getByTestId('seleccionar-fila')).toHaveCount(1);
        await page.getByTestId('seleccionar-fila').click();
        await expect(page).toHaveURL(/sel=/);
        await page.getByTestId('accion-eliminar').click();
        await page.getByRole('button', { name: 'Sí, eliminar' }).click();
        await expect(page).toHaveURL(/ok=deleted/);
      } catch (error) {
        const mensaje = `Limpieza E2E incompleta (cliente ${nombreCliente}): ${error instanceof Error ? error.message : String(error)}`;
        if (fallo === undefined) throw new Error(mensaje);
        console.warn(mensaje);
      }
    }
  }
});

// Puerta G2 (teclado): tras un error de validación (precio vacío) el foco debería quedar en el resumen de errores. HOY FALLA: los
// errores de DataAnnotations/IValidatableObject del formulario de línea solo se pintan junto al campo (ValidationMessage) y
// ResumenErrores (data-resumen-errores, que app.errores.js enfoca) solo aparece con errores de la API. test.fail() lo deja registrado:
// cuando la página muestre el resumen, este test empezará a "pasar inesperadamente" y habrá que quitar la marca.
test('foco en el resumen de errores tras un precio vacío en la primera línea', async ({ page }) => {
  test.fail(true, 'Brecha conocida (R9): sin resumen de errores para la validación del formulario de la línea.');
  await iniciarSesion(page, usuarios.admin);
  // Cualquier cliente sirve: el envío con error no crea nada.
  await page.goto('/facturas-venta/nueva');
  await page.getByTestId('clientes-encontrados').getByRole('link', { name: /^Usar / }).first().click();
  await expect(page).toHaveURL(/socioId=/);
  await page.getByTestId('tipos-linea').getByRole('link', { name: 'Cuenta contable' }).click();
  await expect(page).toHaveURL(/tipo=2/);
  await rellenarLineaDeCuenta(page, '');
  await page.getByTestId('agregar-linea').click();
  await expect(page.getByText(/El precio unitario es obligatorio/)).toBeVisible();
  await expect(page.getByTestId('resumen-errores')).toBeFocused({ timeout: 3_000 });
});
