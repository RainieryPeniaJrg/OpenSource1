# Parte 2 — Seguridad, reportería y procesos

Unidades relacionadas: IV, V y VI.

Objetivo: evolucionar la aplicación de la Parte 1 incorporando autenticación, autorización, validaciones, reportes y procesos empresariales con consistencia transaccional.

1. Implementar autenticación. Crear pantalla de Login utilizando usuario o correo y contraseña.
2. Implementar roles. Como mínimo: Administrador, Supervisor y Vendedor. Definir claramente los permisos de cada rol.
3. Implementar autorización. Proteger controladores, acciones, rutas y operaciones sensibles. Ocultar opciones del menú no será considerado suficiente como único control.
4. Implementar validaciones. Aplicar controles del lado cliente y servidor: campos obligatorios, rangos, formatos, cantidades, precios, correo e identificadores. Incorporar al menos una validación personalizada.
5. Fortalecer la seguridad. Gestionar adecuadamente contraseñas, sesiones, errores, entradas del usuario y HTTPS/TLS cuando corresponda.
6. Crear el módulo de reportería. Desarrollar al menos cuatro reportes: ventas por período, inventario disponible, productos más vendidos y cuentas por cobrar.
7. Agregar filtros a los reportes. Permitir criterios como fecha inicial, fecha final, cliente, producto y estado.
8. Exportar información. Al menos un reporte debe exportarse a PDF o Excel; se valorará positivamente soportar ambos formatos.
9. Implementar el proceso de venta. Flujo mínimo: seleccionar cliente → seleccionar productos → indicar cantidades → calcular total → registrar venta → generar factura.
10. Actualizar inventario. Al completar una venta, descontar la cantidad vendida y evitar ventas superiores a la existencia disponible.
11. Crear cuentas por cobrar. Cuando una venta sea a crédito, generar la cuenta por cobrar con cliente, factura, monto, fecha, vencimiento y estado.
12. Aplicar transacciones. Evitar estados parciales. Si una operación crítica falla, el sistema deberá mantener consistencia mediante commit/rollback o mecanismo equivalente.
13. Documentar las pruebas. Incluir login correcto e incorrecto, usuario sin autorización, validaciones, venta, inventario, facturación, reportes y exportación.
14. Preparar la segunda entrega. Integrar todos los cambios sobre la aplicación de la Parte 1 y presentar una demostración funcional.

Fuente: enunciado del Ing. Arnaldo Abreu, proporcionado por el usuario el 2026-11-03.

## Entregables

- Aplicación actualizada y código fuente.
- Informe técnico de seguridad, reportería y procesos.
- Matriz o descripción de roles y permisos.
- Evidencias de los cuatro reportes y sus filtros.
- Evidencia de exportación.
- Pruebas del proceso de venta, inventario, facturación y cuenta por cobrar.
- Presentación y demostración.
