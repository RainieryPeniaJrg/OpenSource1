// Reproducible local presentation, following the existing delivery's pptxgenjs tooling.
const pptxgen = require('pptxgenjs');
const path = require('node:path');
const fs = require('node:fs');
const output = path.resolve(__dirname, '../../../docs/entregable-parte-2');
const pres = new pptxgen();
pres.layout = 'LAYOUT_WIDE';
pres.lang = 'es-DO';
pres.title = 'AxionERP — Parte 2: seguridad, reportería y procesos';
pres.author = 'AxionERP';
const slides = [
  ['AxionERP', 'PARTE 2 · SEGURIDAD, REPORTERÍA Y PROCESOS',
    ['Una venta conecta inventario, factura y cuenta por cobrar.', 'Ampliación compatible de la aplicación de la Parte 1.'],
    'Presentar el objetivo de las unidades IV, V y VI y abrir la aplicación del laboratorio en18080.'],
  ['Ampliar lo que falta', 'COBERTURA REAL, SIN RECONSTRUIR LA PARTE 1',
    ['Reutilizamos Identity, validación, facturas y libros transaccionales.', 'Añadimos Vendedor, cuatro reportes empresariales y PDF/Excel.', 'Corregimos reclasificación CxC, fechas de aplicación y notas truncadas.'],
    'Mostrar requisitos.md y la matriz del informe. Explicar qué existía y qué se añadió.'],
  ['El permiso decide en el servidor', 'ADMINISTRADOR · SUPERVISOR · VENDEDOR · EJECUTOR',
    ['Vendedor consulta y completa facturas; no administra ni borra.', 'Administrador conserva gestión total; Supervisor conserva su alcance.', 'Ejecutor permanece para mantener compatibilidad con la Parte 1.'],
    'Iniciar sesión como vendedor y forzar acceso a usuarios. Mostrar403 y política API.'],
  ['Una operación, un resultado', 'VENTA → INVENTARIO → FACTURA → CUENTA POR COBRAR',
    ['Seleccionar cliente y producto; capturar cantidad y fechas.', 'Calcular impuestos y registrar bajo bloqueos y transacción.', 'Si falta existencia o configuración, rollback completo.'],
    'Demostrar entrada10, venta2, existencia8 y cuenta por cobrar. Intentar vender99.'],
  ['Cuatro decisiones con datos', 'LOS REPORTES CONSULTAN DOCUMENTOS Y LIBROS REALES',
    ['Ventas por período: ventas, devoluciones y neto.', 'Inventario disponible: cantidad y valor a una fecha de corte.', 'Productos más vendidos: unidades base y valor neto sin IVA.', 'Cuentas por cobrar: saldo y antigüedad de vencimiento.'],
    'Abrir cada reporte con los datos de la venta. Los filtros cambian según el significado del reporte.'],
  ['La descarga conserva el significado', 'FILTROS · FECHA DE CORTE · MONEDA · TOTALES',
    ['Fechas inclusivas y filtros por cliente, producto, almacén y estado.', 'PDF y Excel para los cuatro reportes.', 'Lectura estable para evitar mezclar páginas durante ventas concurrentes.', 'Excel conserva números y evita ejecutar texto como fórmulas.'],
    'Descargar un PDF y Excel y comparar filtros y totales con pantalla.'],
  ['Seguridad de extremo a extremo', 'IDENTIDAD · VALIDACIÓN · SESIÓN · TLS',
    ['Contraseñas hash, bloqueo de intentos y JWT validado.', 'Cookie HttpOnly; token en servidor; formularios con antiforgery.', 'Validaciones servidor y formulario; mensajes de error controlados.', 'Referencia de producción con TLS y proxy explícitamente confiable.'],
    'Mostrar validación de fechas, acceso denegado y configuración de producción sin exponer secretos.'],
  ['Evidencia reproducible', 'LABORATORIO AISLADO · XUNIT · PLAYWRIGHT',
    ['Pruebas de permisos, filtros, cálculo, fechas y rollback.', 'Escenario real de venta, inventario, CxC y ocho descargas.', 'Capturas y archivos exportados disponibles en el paquete de entrega.', 'Resultados exactos y límites en evidencias/validacion.md.'],
    'No citar conteos históricos como ejecución actual. Abrir el registro final de validación.'],
  ['Demostración y operación', 'UN SOLO REPOSITORIO; UNA ENTREGA TRAZABLE',
    ['Rama parte2_opensource_2 y plan con tareas verificables.', 'Laboratorio propio: web18080, API18081, PostgreSQL65433.', 'Costo de inventario: batch explícito e idempotente.', 'Producción requiere secretos, DNS y aceptación del operador.'],
    'Cerrar con el escenario completo. Aclarar que el laboratorio no prueba una publicación de producción.'],
];
slides.forEach(([title, subtitle, points, notes], index) => {
  const slide = pres.addSlide();
  slide.background = { color: '101E33' };
  slide.addShape(pres.ShapeType.rect, { x: 0, y: 0, w: 0.18, h: 7.5, fill: { color: '31D2B3' }, line: { color: '31D2B3' } });
  slide.addText(subtitle, { x: 0.7, y: 0.55, w: 11.7, h: 0.4, fontFace: 'Aptos', fontSize: 13, color: '31D2B3', bold: true, margin: 0, isTextBox: true });
  slide.addText(title, { x: 0.7, y: 1.2, w: 11.7, h: 1.1, fontFace: 'Aptos Display', fontSize: index === 0 ? 60 : 38, color: 'FFFFFF', bold: true, margin: 0, isTextBox: true });
  points.forEach((text, p) => {
    slide.addText(String(p + 1).padStart(2, '0'), { x: 0.75, y: 2.8 + p * 0.79, w: 0.5, h: 0.4, fontSize: 16, color: '31D2B3', margin: 0, isTextBox: true });
    slide.addText(text, { x: 1.5, y: 2.75 + p * 0.79, w: 10.7, h: 0.65, fontFace: 'Aptos', fontSize: 21, color: 'DFE8F3', margin: 0, breakLine: false, isTextBox: true });
  });
  slide.addText(`AXIONERP  /  PARTE 2                                      ${String(index + 1).padStart(2, '0')} / ${slides.length}`, { x: 0.7, y: 6.95, w: 11.9, h: 0.25, fontFace: 'Aptos', fontSize: 10, color: '8CA1B9', margin: 0, isTextBox: true });
  slide.addNotes(notes);
});
fs.mkdirSync(output, { recursive: true });
pres.writeFile({ fileName: path.join(output, 'presentacion.pptx') });
