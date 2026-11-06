#!/usr/bin/env python3
"""Build the consolidated Spanish Part 2 submission from verified evidence."""
from pathlib import Path
from docx import Document
from docx.shared import Inches, Pt, RGBColor
from docx.oxml import OxmlElement
from docx.oxml.ns import qn
from docx.enum.text import WD_ALIGN_PARAGRAPH
from docx.enum.table import WD_TABLE_ALIGNMENT, WD_CELL_VERTICAL_ALIGNMENT
from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'docs/entregable-parte-2'
d = Document()
s = d.sections[0]
s.page_width, s.page_height = Inches(8.5), Inches(11)
s.top_margin = s.bottom_margin = Inches(.65)
s.left_margin = s.right_margin = Inches(.8)
for name in ['Normal', 'Title', 'Subtitle', 'Heading 1', 'Heading 2', 'Caption']:
    st = d.styles[name]
    st.font.name = 'Calibri'
    st.font.color.rgb = RGBColor(0, 0, 0)
d.styles['Normal'].font.size = Pt(10.5)
d.styles['Normal'].paragraph_format.space_after = Pt(5)
d.styles['Normal'].paragraph_format.line_spacing = 1.02
d.styles['Title'].font.size = Pt(27)
d.styles['Heading 1'].font.size = Pt(19)
d.styles['Heading 2'].font.size = Pt(13)
d.styles['Caption'].font.size = Pt(9)
d.styles['Caption'].font.italic = False
d.styles['Caption'].font.bold = False
for name in ['Heading 1', 'Heading 2']:
    d.styles[name].paragraph_format.space_before = Pt(9)
    d.styles[name].paragraph_format.space_after = Pt(6)
for style in d.styles:
    for border in style.element.xpath('.//w:pBdr'):
        border.getparent().remove(border)
footer = s.footer.paragraphs[0]
footer.alignment = WD_ALIGN_PARAGRAPH.RIGHT
footer.add_run('AxionERP · Parte 2 · ')
field = OxmlElement('w:fldSimple'); field.set(qn('w:instr'), 'PAGE')
footer._p.append(field)
for r in footer.runs: r.font.size = Pt(9)
d.core_properties.title = 'AxionERP Parte 2 Seguridad reportería y procesos'
d.core_properties.subject = 'Informe técnico y evidencias de la segunda entrega'
d.core_properties.author = 'Proyecto AxionERP'
d.core_properties.keywords = 'ISO-615, seguridad, reportes, ventas, inventario, cuentas por cobrar'

def p(text, style=None):
    return d.add_paragraph(text, style)

def h(text):
    d.add_heading(text, 2)

def page(title):
    heading = d.add_heading(title, 1)
    heading.paragraph_format.page_break_before = True

def table(headers, rows, widths=None, size=9):
    t = d.add_table(rows=1, cols=len(headers))
    t.alignment = WD_TABLE_ALIGNMENT.CENTER
    t.autofit = False
    if widths:
        for col, width in zip(t.columns, widths): col.width = Inches(width)
    for c, text in zip(t.rows[0].cells, headers): c.text = text
    repeat = OxmlElement('w:tblHeader'); t.rows[0]._tr.get_or_add_trPr().append(repeat)
    for row in rows:
        for c, text in zip(t.add_row().cells, row): c.text = text
    for i, row in enumerate(t.rows):
        trpr = row._tr.get_or_add_trPr()
        trpr.append(OxmlElement('w:cantSplit'))
        for j, c in enumerate(row.cells):
            if widths: c.width = Inches(widths[j])
            c.vertical_alignment = WD_CELL_VERTICAL_ALIGNMENT.CENTER
            pr = c._tc.get_or_add_tcPr()
            sh = OxmlElement('w:shd'); sh.set(qn('w:fill'), 'E5E7EB' if i == 0 else ('F5F6F8' if i % 2 == 0 else 'FFFFFF')); pr.append(sh)
            borders = OxmlElement('w:tcBorders')
            for edge in ['top', 'left', 'bottom', 'right']:
                el = OxmlElement('w:'+edge); el.set(qn('w:val'), 'single'); el.set(qn('w:sz'), '4'); el.set(qn('w:color'), 'D9D9D9'); borders.append(el)
            pr.append(borders)
            margins = OxmlElement('w:tcMar')
            for edge in ['top', 'left', 'bottom', 'right']:
                el = OxmlElement('w:'+edge); el.set(qn('w:w'), '80'); el.set(qn('w:type'), 'dxa'); margins.append(el)
            pr.append(margins)
            for para in c.paragraphs:
                if j > 0 and len(c.text) <= 12:
                    para.alignment = WD_ALIGN_PARAGRAPH.CENTER
                para.paragraph_format.space_after = Pt(3)
                para.paragraph_format.space_before = Pt(3)
                para.paragraph_format.line_spacing = 1
                for r in para.runs: r.font.size = Pt(size); r.bold = i == 0
    spacer = p(''); spacer.paragraph_format.space_after = Pt(0)
    spacer.paragraph_format.line_spacing = Pt(3)
    spacer.paragraph_format.space_before = Pt(0)
    return t

def figure(file, caption):
    para = p(''); para.paragraph_format.space_after = Pt(3)
    path = OUT / 'evidencias/capturas' / file
    with Image.open(path) as img: width, height = img.size
    run = para.add_run(); run.add_picture(str(path), width=Inches(min(6.9, 5.6 * width / height)))
    props = run._r.xpath('.//wp:docPr')
    if props: props[0].set('descr', caption)
    p(caption, 'Caption')

p('AxionERP Parte 2', 'Title')
p('Seguridad reportería y procesos', 'Subtitle')
p('Informe técnico y evidencias de la segunda entrega')
p('Asignatura: Desarrollo de Software con Tecnologías Propietarias y Open Source I (ISO-615)\nUnidades relacionadas: IV, V y VI\nDocente: Ing. Arnaldo Abreu\nFecha del informe: 5 de noviembre de 2026')
p('Proyecto: AxionERP · Repositorio: OpenSource1\nRama: parte2_opensource_2 · Versión de aplicación: 42aff84\nBase de la fase: 45bc4bc · Evidencias ejecutadas: 4 de noviembre de 2026')
h('Resumen ejecutivo')
p('AxionERP integra clientes, productos, inventario, facturación, cuentas por cobrar y contabilidad para una empresa comercial. Esta entrega evoluciona la aplicación de la Parte 1 con el rol Vendedor, cuatro reportes empresariales y exportación a PDF y Excel. Conserva la autenticación, las validaciones y el proceso transaccional existentes, y corrige incidencias confirmadas en aplicaciones de cobros, valoración de inventario y consultas de notas de crédito.')
p('El flujo demostrado permite autenticar al vendedor, registrar una venta, descontar existencias, emitir la factura y consultar el saldo del cliente. Los permisos se aplican en el servidor. Cuando una venta supera la existencia disponible, la operación falla sin dejar registros parciales.')
p('La evidencia de calidad combina 2093 casos .NET únicos verificados mediante una corrida completa y un retest de fixture, más 17 escenarios únicos de navegador. El detalle de esta conciliación se presenta en la sección de pruebas para conservar los resultados originales sin atribuir una segunda corrida completa que no se realizó.')
h('Organización del informe')
p('Las secciones 1 a 6 explican la cobertura, la arquitectura, la seguridad, los roles, los procesos y los reportes. Las secciones 7 y 8 documentan las pruebas. Las secciones 9 a 13 presentan capturas reales. Las secciones 14 y 15 describen la reproducción, la demostración y los archivos de entrega.')

page('1 Cobertura de los requisitos')
p('La implementación se concentró en los faltantes. La base ya disponía de login, controles de acceso, validadores, facturas, libros de inventario y clientes, y transacciones. La siguiente matriz relaciona los 14 pasos de la consigna con la solución entregada.')
table(['Nº', 'Requisito', 'Cobertura y evidencia'], [
('1', 'Autenticación', 'Login con usuario o correo y contraseña; escenarios de ingreso correcto e incorrecto.'),
('2', 'Roles', 'Administrador, Supervisor y nuevo Vendedor; se conserva Ejecutor. Matriz en sección 4.'),
('3', 'Autorización', 'Políticas en API y SSR; operaciones forzadas sin permiso se rechazan.'),
('4', 'Validaciones', 'Controles de formulario y servidor; conversión exacta de unidades y fechas permitidas personalizadas.'),
('5', 'Seguridad', 'Identity, JWT, sesiones de servidor, antiforgery, errores controlados y referencia TLS.'),
('6', 'Cuatro reportes', 'Ventas, inventario, productos más vendidos y cuentas por cobrar.'),
('7', 'Filtros', 'Fechas, cliente, producto, almacén, estado y moneda según la naturaleza del reporte.'),
('8', 'Exportación', 'Los cuatro reportes exportan PDF y XLSX completos con filtros y totales.'),
('9', 'Venta y factura', 'Selección de cliente y productos, cantidades, impuestos, total y registro de factura.'),
('10', 'Inventario', 'Descuento al postear y rechazo por insuficiencia; evidencia de 10 a 8 unidades.'),
('11', 'Cuentas por cobrar', 'Movimiento del cliente con factura, importe, fechas, vencimiento y saldo derivado.'),
('12', 'Transacciones', 'Commit o rollback del documento y sus libros; reclasificación de CxC atómica.'),
('13', 'Pruebas', 'Resultados, casos identificables y evidencias de navegador en secciones 7 y 8.'),
('14', 'Segunda entrega', 'Código integrado, este informe, presentación, demostración y anexos reales.')
], [.4, 1.35, 5.15], 9)
p('La configuración de producción se entrega como referencia validada. La demostración corresponde a un laboratorio local; no acredita un despliegue público ni la emisión de un certificado TLS real.')

page('2 Arquitectura y flujo de datos')
p('La aplicación conserva una arquitectura Onion de cinco proyectos sobre .NET 10. El dominio y los casos de uso permanecen separados de la persistencia, los servicios HTTP y la presentación. Blazor utiliza renderizado estático en el servidor (Static SSR); esta fase no introduce un runtime interactivo.')
table(['Proyecto', 'Responsabilidad'], [
('OpenSource1.Core', 'Entidades, reglas y contratos de dominio.'),
('OpenSource1.Application', 'Casos de uso, validaciones y coordinación de operaciones.'),
('OpenSource1.Infrastructure', 'Persistencia PostgreSQL, Identity e implementaciones de infraestructura.'),
('OpenSource1.Api', 'Endpoints HTTP, políticas de autorización y respuestas controladas.'),
('OpenSource1.Blazor', 'Interfaz SSR, formularios, sesión y clientes HTTP tipados.')
], [2.25, 4.65], 10)
h('Recorrido de una operación')
p('El navegador envía el formulario a Blazor. La sesión del servidor conserva el token; el cliente HTTP tipado consulta la API. La API verifica identidad, permisos y entradas antes de ejecutar el caso de uso. El proceso registra los documentos y los movimientos en PostgreSQL dentro de la transacción correspondiente. La respuesta vuelve a la interfaz con el resultado o un error controlado.')
h('Fuente de verdad de los saldos')
p('Las existencias se calculan desde los libros de inventario y sus valores acumulados. La deuda se deriva del movimiento de la factura, los cobros, las notas de crédito y los detalles de aplicación. Los reportes consultan esos registros; no mantienen saldos paralelos editables que puedan divergir del proceso de venta.')
h('Compatibilidad de la ampliación')
p('Se conservan las rutas, los clientes tipados y los roles previos. Vendedor se agrega mediante una migración de datos de Identity, incluso cuando la semilla de usuarios de demostración está desactivada. La migración respeta los identificadores, usuarios y asignaciones existentes. Su reversión no elimina membresías. Donde se sustituyó comportamiento se retuvo el código anterior como comentario, conforme a la instrucción de esta fase.')
h('Consulta y exportación coherentes')
p('Las exportaciones usan una instantánea de lectura RepeatableRead y ReadOnly en el servidor. Todas las páginas de resultados pertenecen al mismo estado de datos, aunque otra sesión registre una factura durante la descarga. Los totales corresponden al conjunto filtrado completo.')

page('3 Autenticación seguridad y validaciones')
h('Identidad y sesión')
p('El login acepta usuario o correo y contraseña. ASP.NET Core Identity almacena contraseñas mediante hash, exige composición de contraseña y correos únicos. Tras tres intentos fallidos aplica bloqueo por cinco minutos. La API valida emisor, audiencia, firma y vigencia del JWT. Blazor conserva el token en la sesión del servidor, fuera del almacenamiento del navegador.')
p('Las cookies usan HttpOnly y SameSite; los formularios incorporan antiforgery. En la referencia de producción las cookies de autenticación, sesión y antiforgery requieren HTTPS. Caddy termina TLS y las cabeceras reenviadas solo se aceptan desde el proxy conocido y un salto. La configuración no confía indiscriminadamente en direcciones externas.')
h('Autorización efectiva')
p('La API comprueba políticas en cada operación sensible. Las rutas y formularios SSR comprueban los permisos equivalentes. Ocultar un botón mejora la navegación, pero no concede seguridad por sí mismo: una petición forzada sigue sometida a la autorización del servidor. Vendedor usa CanCapturarFactura y CanEmitirFactura, sin recibir CanAdd o CanModify globales.')
h('Entradas y reglas de negocio')
table(['Control', 'Aplicación'], [
('Obligatorios y formatos', 'Formularios y servidor verifican campos, correo e identificadores fiscales.'),
('Importes y cantidades', 'Rangos, precisión decimal, cantidades, precios y disponibilidad de inventario.'),
('Fechas y concurrencia', 'Fechas de registro autorizadas, coherencia temporal y tokens de concurrencia.'),
('Validación personalizada', 'Conversión exacta de unidades y fechas de registro permitidas por usuario.'),
('Reportes', 'Rangos no invertidos, estados válidos, moneda y combinaciones de filtros admitidas.'),
('Aplicación de cobros', 'No admite una fecha anterior al registro de la factura o del pago.')
], [1.7, 5.2], 10)
h('Errores y archivos exportados')
p('Los errores siguen las convenciones Result y ProblemDetails, con mensajes controlados. Excel conserva importes como celdas numéricas y trata como texto las cadenas que parezcan fórmulas. PDF y Excel conservan filtros, columnas y totales, sin truncar silenciosamente los resultados.')
p('Las credenciales del laboratorio se generan en un archivo privado excluido de Git y del contexto Docker. No se incluyen contraseñas ni tokens en el informe o las evidencias.')

page('4 Matriz de roles y permisos')
p('La matriz representa permisos efectivos de las operaciones. El rol Ejecutor se conserva por compatibilidad con la Parte 1. La concesión específica de facturación permite al vendedor completar el proceso comercial sin habilitar la escritura general de datos maestros.')
table(['Operación', 'Admin', 'Supervisor', 'Vendedor', 'Ejecutor'], [
('Consultar registros y reportes; exportar', 'Sí', 'Sí', 'Sí', 'Sí'),
('Crear clientes, productos y maestros', 'Sí', 'No', 'No', 'Sí'),
('Modificar datos maestros', 'Sí', 'Sí', 'No', 'No'),
('Capturar borradores y líneas de factura', 'Sí', 'No', 'Sí', 'Sí'),
('Modificar, liberar y postear facturas', 'Sí', 'Sí', 'Sí¹', 'No'),
('Eliminar registros permitidos', 'Sí', 'No', 'No', 'No'),
('Administrar usuarios y configuración', 'Sí', 'No', 'No', 'No'),
('Registrar cobros de clientes', 'Sí', 'Sí', 'No', 'No'),
('Aplicar pagos y postear inventario y costo', 'Sí', 'Sí', 'No', 'No')
], [3.0, .7, 1.1, 1.05, 1.05], 9)
p('¹ Mediante políticas específicas de factura. No equivale a modificar cualquier entidad. Los documentos posteados y los libros conservan su inmutabilidad; la autorización no habilita su edición directa.', 'Caption')
h('Responsabilidades operativas')
p('El Administrador prepara usuarios, maestros, configuración y permisos. El Supervisor revisa datos existentes y ejecuta operaciones de cobro, aplicación y contabilización autorizadas. El Vendedor consulta los catálogos disponibles y captura y emite facturas. El Ejecutor mantiene su capacidad previa de alta y consulta, sin convertirla en permiso para emitir facturas.')
h('Controles comprobados')
p('Las pruebas de Vendedor verifican que puede capturar y emitir una factura y que no puede escribir globalmente. Las pruebas de compatibilidad comprueban los roles anteriores. La migración se prueba sin semilla y con asignaciones ya existentes para evitar que una actualización reemplace identidades o pierda membresías.')
h('Alcance compartido de documentos')
p('La colaboración documental sigue el modelo compartido de la aplicación. Esta fase no incorpora una restricción de propiedad individual por vendedor. Las capacidades dependen del rol y de las políticas de operación descritas en esta matriz.')

page('5 Venta inventario facturación y crédito')
h('Secuencia funcional')
for text in [
'1. Preparar cliente, producto, almacén, configuración contable y existencia mediante un usuario autorizado.',
'2. Crear un borrador de factura; seleccionar cliente y productos e indicar cantidades, almacén y fechas de registro y vencimiento.',
'3. Consultar la previsualización de impuestos y total calculada por el motor existente.',
'4. Postear la factura. El servidor verifica permisos, configuración, fechas y existencia bajo bloqueos.',
'5. Asignar el número y registrar el documento y los movimientos asociados de inventario, clientes y contabilidad dentro de la transacción.',
'6. Consultar la factura emitida, la nueva existencia y el saldo pendiente. Los libros y el documento posteado permanecen inmutables.'
]: p(text)
h('Resultado del caso demostrado')
p('El laboratorio inicia con 10 unidades. La venta de 2 unidades a DOP 100 produce un importe neto de DOP 200, impuesto de DOP 36 y total de DOP 236. El inventario queda en 8 unidades y la factura genera un saldo pendiente de DOP 236. Los reportes de ventas y cuentas por cobrar incluyen el total con impuesto; el ranking de productos usa DOP 200 sin impuesto.')
h('Rollback y disponibilidad')
p('Una venta de 99 unidades se rechaza por existencia insuficiente. Si falla una operación crítica, se revierten sus registros, incluidos los números de serie transaccionales consumidos. La prueba de insuficiencia en una línea comprueba que no queda ninguna fila parcial del proceso.')
h('Crédito cobros y consistencia contable')
p('La cuenta por cobrar conserva cliente, factura, importe original, fecha de documento, fecha de registro y vencimiento. El estado abierto o liquidado y el saldo se derivan de los detalles. Las notas de crédito y las aplicaciones reducen el saldo. Si cambia el grupo contable del cliente entre factura y pago, la aplicación registra una reclasificación balanceada desde la cuenta CxC congelada del pago a la congelada de la factura, por el importe aplicado y en la misma transacción.')
p('La contabilización del costo de inventario sigue siendo un lote manual autorizado e idempotente. Ejecutarlo por segunda vez no duplica asientos. El resumen de notas de crédito consulta coincidencias exactas y agregados en una instantánea, sin el antiguo límite de 200 registros ni peticiones HTTP por cada nota.')

page('6 Reportes filtros y exportaciones')
table(['Reporte', 'Criterios admitidos', 'Interpretación'], [
('Ventas por período', 'Desde y hasta inclusivos; cliente, producto, almacén, estado y moneda.', 'Facturas y notas de crédito posteadas. Ventas, devoluciones y neto con impuesto.'),
('Inventario disponible', 'Fecha de corte; producto, almacén y estado con o sin cantidad.', 'Cantidad y valor acumulados por producto y almacén. Moneda base DOP.'),
('Productos más vendidos', 'Desde y hasta inclusivos; cliente, producto, almacén, estado y moneda.', 'Líneas de productos; cantidades en unidad base y valor neto sin impuesto. Orden por valor neto.'),
('Cuentas por cobrar', 'Fecha de corte; cliente y estado todos o con saldo.', 'Saldos históricos y antigüedad. Vigente, 1–30, 31–60, 61–90, más de 90 días y no aplicado.')
], [1.4, 2.55, 2.95], 10)
h('Reglas de interpretación')
p('Los borradores no se cuentan como ventas. En ventas, el filtro por producto o almacén selecciona documentos completos que contienen coincidencias; en el ranking selecciona las líneas coincidentes. Las devoluciones se muestran de forma explícita y también se reflejan en el neto. No se suman cantidades de productos con unidades incompatibles.')
p('Inventario y cuentas por cobrar son vistas acumuladas: aceptan corte y rechazan una fecha inicial. Inventario rechaza cliente; cuentas por cobrar rechaza producto y almacén. Ventas y ranking utilizan una sola moneda por consulta, DOP por defecto. Esta fase no agrega contabilidad de diferencias de cambio.')
h('Existencia física y valoración residual')
p('En el reporte empresarial, los estados con o sin existencia se refieren a cantidad física. Para revisar valoraciones con cantidad cero se consulta Todos. La vista histórica previa SoloConExistencia conserva filas con cantidad o valor residual distintos de cero, lo que permite conciliar el libro de valor.')
h('PDF y Excel')
p('Los cuatro reportes ofrecen ambos formatos. La pantalla puede paginar hasta 200 filas, pero los totales representan todo el conjunto filtrado y las descargas incluyen todas las filas. El servidor mantiene una instantánea estable durante la exportación y rechaza una instantánea incompleta en lugar de producir un archivo truncado.')
p('Los archivos reales se adjuntan en evidencias/exportaciones. La verificación incluye números de Excel, ausencia de fórmulas inyectadas, más de una página de datos, reporte vacío y concurrencia durante la descarga.')

page('7 Resultados de las pruebas')
p('Las pruebas corresponden al cierre de validación del 4 de noviembre de 2026. El recibo resultados.json conserva contadores originales, hashes de TRX y la identidad de los binarios. Los resultados dirigidos son complementarios y no se suman al total único.')
table(['Ejecución', 'Resultado', 'Duración'], [
('Suite .NET completa en Release', '2093 ejecutadas; 2092 correctas y 1 fallida', '35 min 38 s'),
('Retest completo de ClientesVistasApiTests', '20 correctas; 0 fallidas', '42 s'),
('Casos .NET únicos verificados', '2093 por conciliación de ambas ejecuciones', 'No acumulativa'),
('Regresión Chromium', '16 correctos; 1 omitido condicional', '2,8 min'),
('Caída controlada de API por separado', '1 correcto; API recuperada', '14,8 s'),
('Escenarios únicos de navegador', '17; sin duplicar el omitido', 'No acumulativa')
], [2.55, 3.2, 1.15], 10)
h('Explicación del único fallo de la corrida completa')
p('El caso AplicacionFechadaAntesDelPago_NoReduceLaFacturaHastaQueElPagoExiste intentaba preparar un dato histórico mediante una escritura API anterior a la fecha del pago. La nueva validación rechaza correctamente esa operación. Se corrigió exclusivamente el fixture para insertar sus dos filas históricas directamente en la base dedicada de pruebas y se mantuvieron las aserciones de saldos históricos.')
p('Después de la corrección pasaron los 20 casos de la clase. Los cinco ensamblados de producción y sus cinco copias en el ejecutor conservaron exactamente sus hashes SHA-256. Por tanto, la conciliación verifica 2093 casos únicos con los mismos binarios de producto. No se afirma una segunda ejecución completa con todos los casos en verde.')
h('Verificaciones complementarias')
p('Los grupos dirigidos registraron 23 casos correctos de reportes, exportación y proxy; 69 de cobros, reclasificación y autorización; 68 de módulos y acciones de factura; y 3 de migración y resumen de notas. Las revisiones independientes no reportaron hallazgos materiales. La configuración Compose y Caddy se validó localmente. La presentación de 9 diapositivas se renderizó y revisó.')
p('Se entrega un workflow de GitHub Actions y se ejecutó su secuencia equivalente local. Este informe no atribuye éxito a una ejecución remota de CI sin su resultado real.')

page('8 Casos y criterios de aceptación')
table(['Caso', 'Acción y resultado esperado', 'Evidencia'], [
('Login válido e inválido', 'Ingreso correcto con identidad válida; rechazo de clave incorrecta.', 'Regresión Chromium; informe HTML.'),
('Sin autorización', 'Vendedor no accede a administración ni a escrituras globales.', 'Api_Vendedor_PermiteSoloOperacionesDeVenta.'),
('Validación personalizada', 'Conversión exacta y fechas permitidas se validan en servidor.', 'Suite existente y validadores conservados.'),
('Rango inválido', 'Fecha inicial mayor que final produce error.', 'RangoInvertido_DevuelveErrorDeDesde.'),
('Venta y factura', 'Registrar 2 unidades; total 236; documento numerado.', 'Caso E2E Parte 2; factura.png.'),
('Stock y rollback', 'Venta superior al stock falla sin filas parciales.', 'ReviewFocus5_ExistenciaInsuficienteEnUnaLinea_FallaConSuNumero_YNoDejaNingunaFila.'),
('Inventario', 'Existencia histórica y valor respetan corte y filtros.', 'Inventario_DerivaCantidadYValorHistoricos_ConFiltros.'),
('CxC', 'Saldo histórico, corte y cliente correctos.', 'CuentasPorCobrar_ConservaCorteClienteYSaldosHistoricos.'),
('Reclasificación', 'Cuentas distintas generan asiento balanceado; iguales no.', 'CuentasDistintas_RevierteCreditoDePagoYAcreditaCuentaFactura; MismaCuenta_NoGeneraAsiento.'),
('Reportes', 'Ventas incluyen extremos y devoluciones; ranking normaliza unidades.', 'Ventas_IncluyeExtremosYDevoluciones_TotalesSuperanPagina; TopProductos_NormalizaUnidadYNeteaDevolucion_SinIncluirCuentas.'),
('Excel y PDF', 'Datos completos, celdas numéricas, sin fórmulas; PDF vacío válido.', 'Excel_PreservaNumerosFiltrosYTextoSinFormulas; Pdf_Vacio_EsDocumentoValido.'),
('Concurrencia', 'Factura concurrente no duplica ni omite filas de exportación.', 'Exportacion_ConFacturaConcurrenteEntrePaginas_ConservaSnapshotSinDuplicarNiOmitir.'),
('Costo idempotente', 'Segunda ejecución no agrega asientos.', 'ReviewFocus3_DosEjecucionesSeguidas_LaSegundaNoInsertaNada.')
], [.95, 2.75, 3.2], 8.5)
p('Los nombres completos, los comandos y verificaciones adicionales se conservan en evidencias/validacion.md. El reporte HTML contiene pasos, adjuntos y resultados navegables del escenario real.')

page('9 Evidencia de la factura y el crédito')
p('Captura de la factura emitida durante el escenario automatizado de la Parte 2. El documento permite relacionar la venta con el cliente, las líneas, los importes y las fechas usadas por inventario y cuentas por cobrar.')
figure('factura.png', 'Figura 1. Factura real del laboratorio de la Parte 2 después de completar el registro.')
table(['Dato de control', 'Resultado del escenario'], [('Cantidad vendida', '2 unidades'), ('Base e impuesto', 'DOP 200 + DOP 36'), ('Total y saldo inicial', 'DOP 236'), ('Existencia antes y después', '10 → 8 unidades')], [3.2, 3.7], 10)
p('La factura posteada no se modifica directamente. La prueba de intento posterior por 99 unidades verifica rechazo y conservación de existencias y saldos.')

for number, title, file, text, rows in [
(10, 'Evidencia de ventas por período', 'ventas.png', 'La consulta filtra el cliente de la demostración y muestra la factura posteada. El corte visible es el 4 de noviembre de 2026. El total incluye impuesto, a diferencia del ranking de productos.', [('Ventas', 'DOP 236,00'), ('Devoluciones', 'DOP 0,00'), ('Neto', 'DOP 236,00')]),
(11, 'Evidencia de inventario disponible', 'inventario.png', 'La vista acumula movimientos hasta el corte y filtra el producto del escenario. Tras vender 2 de las 10 unidades iniciales, quedan 8. La valoración corresponde al libro de inventario.', [('Cantidad disponible', '8 unidades'), ('Valor del inventario', 'DOP 400,00'), ('Filtro temporal', 'Acumulado hasta fecha de corte')]),
(12, 'Evidencia de productos más vendidos', 'top-productos.png', 'El ranking consulta las líneas del producto vendido y convierte cantidades a unidad base. Su valor neto excluye impuesto; por ello no debe compararse como si fuera el total fiscal de la factura.', [('Cantidad neta', '2 unidades base'), ('Valor neto sin impuesto', 'DOP 200,00'), ('Orden del ranking', 'Valor neto vendido')]),
(13, 'Evidencia de cuentas por cobrar', 'cuentas-por-cobrar.png', 'El reporte presenta el saldo histórico del cliente de la demostración a la fecha de corte. El vencimiento permite distribuir la deuda entre vigente y rangos de atraso, conservando los importes no aplicados.', [('Saldo pendiente', 'DOP 236,00'), ('Origen', 'Factura de venta a crédito'), ('Criterio', 'Cliente y fecha de corte')])
]:
    page(f'{number} {title}')
    p(text)
    figure(file, f'Figura {number-8}. Captura real del reporte con sus filtros y opciones de descarga PDF y Excel.')
    table(['Control', 'Resultado'], rows, [3.2, 3.7], 10)
    stem = {'ventas.png':'ventas', 'inventario.png':'inventario', 'top-productos.png':'top-productos', 'cuentas-por-cobrar.png':'cuentas-por-cobrar'}[file]
    p(f'Exportaciones adjuntas: evidencias/exportaciones/{stem}.pdf y {stem}.xlsx. Los archivos permiten contrastar los filtros, el detalle y los totales de la captura.', 'Caption')

page('14 Instalación y demostración funcional')
h('Preparación del laboratorio')
p('Se requiere Docker con Compose y las herramientas de desarrollo indicadas en el README del repositorio. El laboratorio de esta fase usa un proyecto, volúmenes y credenciales propios. La aplicación web se consulta en http://localhost:18080, la API en http://localhost:18081 y PostgreSQL en 127.0.0.1:65433.')
for cmd in ['python3 scripts/parte2-env.py', 'docker compose --env-file .local/parte2.env -f docker-compose.parte2.yml up -d --build', 'python3 scripts/parte2-e2e.py']:
    para = p(cmd)
    for r in para.runs: r.font.name = 'Consolas'; r.font.size = Pt(9)
p('La utilidad de entorno genera .local/parte2.env con permisos 0600. Los usuarios iniciales son admin, supervisor, ejecutor y vendedor. La contraseña se consulta privadamente en ese archivo; no debe copiarse a la entrega. El escenario guarda documentos inmutables y debe ejecutarse únicamente en este laboratorio.')
h('Guion breve para la presentación')
for text in [
'1. Mostrar un login incorrecto y luego ingresar como vendedor. Intentar administración y comprobar la denegación.',
'2. Mostrar los datos y la existencia preparada por un administrador. Registrar la venta de 2 unidades, revisar total y factura.',
'3. Comprobar inventario de 8 unidades y CxC de DOP 236. Intentar vender 99 y comprobar que no altera los libros.',
'4. Ejecutar el lote de costo como usuario autorizado y repetirlo para comprobar idempotencia.',
'5. Consultar los cuatro reportes y sus filtros. Descargar y abrir al menos un PDF y un Excel; contrastar cifras.',
'6. Mostrar el rechazo de un rango invertido y cerrar con matriz de permisos, pruebas y límites operativos.'
]: p(text)
h('Condiciones de producción')
p('La referencia docker-compose.production.yml y deploy/Caddyfile usa una réplica detrás de TLS. Antes de publicar requiere DNS real, secretos independientes, migraciones controladas, administrador inicial y copias de seguridad de bases y archivos. La semilla de demostración y las migraciones automáticas están desactivadas. La sesión está en memoria: un reinicio requiere nuevo login y múltiples réplicas necesitan almacenamiento compartido. No se realizó despliegue público ni rotación de credenciales históricas.')

page('15 Archivos de entrega y cierre')
table(['Entregable académico', 'Ubicación'], [
('Aplicación y código fuente', 'Repositorio OpenSource1; rama parte2_opensource_2; versión 42aff84.'),
('Informe técnico y roles', 'Este documento; informe-tecnico.md como referencia técnica complementaria.'),
('Evidencias de cuatro reportes', 'evidencias/capturas y las secciones 10 a 13 de este informe.'),
('Exportaciones reales', 'evidencias/exportaciones: cuatro PDF y cuatro XLSX.'),
('Proceso de venta y pruebas', 'evidencias/validacion.md, resultados.json y reporte-e2e/index.html.'),
('Recuperación ante caída de API', 'evidencias/reporte-api-caida/index.html.'),
('Presentación y demostración', 'presentacion.pptx, presentacion.pdf y guion-demo.md.'),
('Análisis y ejecución', '../superpowers/plans/2026-11-03-parte2-opensource-2.md.'),
('Consigna preservada', 'requisitos.md; unidades IV, V y VI; pasos 1 a 14.')
], [2.1, 4.8], 10)
h('Conclusiones')
p('La segunda entrega reúne autenticación y autorización de servidor, validaciones, cuatro reportes filtrables con ambos formatos de exportación y un proceso de venta con inventario, factura y cuenta por cobrar coherentes. La ampliación aprovecha la cobertura previa y agrega los faltantes sin sustituir los módulos de la Parte 1.')
p('Las capturas y descargas acreditan las cifras del escenario. Las pruebas cubren acceso, validaciones, rollback, saldos históricos, exportaciones concurrentes y costo idempotente. La conciliación conserva el fallo original de fixture y su corrección.')
p('El alcance entregado es una demostración funcional local con configuración de producción de referencia. La disponibilidad pública, la operación con varias réplicas, la contabilidad de diferencias de cambio y las obligaciones de facturación electrónica no se acreditan como funcionalidades incorporadas por esta fase.')
h('Referencias verificables')
p('Código y revisión de integración: https://github.com/RainieryPeniaJrg/OpenSource1/pull/25')
p('Versión de aplicación: https://github.com/RainieryPeniaJrg/OpenSource1/tree/42aff84')
p('Rutas relativas a docs/entregable-parte-2. El recibo resultados.json publica hashes y contadores de los TRX privados.')

d.save(OUT / 'documento-entregable-parte-2.docx')
print(OUT / 'documento-entregable-parte-2.docx')
