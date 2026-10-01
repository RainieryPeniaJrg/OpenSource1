# AxionERP / OpenSource1

Proyecto final para la materia **DESARROLLO DE SOFTWARE CON TECNOLOGÍAS PROPIETARIAS Y OPEN SOURCE I / ISO-615**.

**Nombre del proyecto:** Sistema de Gestión Empresarial con Control de Acceso por Roles.

AxionERP es una aplicación cliente-servidor construida con **.NET 10**, **ASP.NET Core Web API**, **Blazor Web App Static SSR**, **PostgreSQL 17**, **ASP.NET Core Identity**, **JWT**, **Entity Framework Core**, **Dapper**, **MediatR**, **Tailwind CSS** y arquitectura Onion.

## Resumen del proyecto

El sistema cubre la gestión empresarial de una empresa comercial pequeña:

- Autenticación de usuarios, roles y permisos validados en la API.
- Maestros de clientes, productos, almacenes y catálogos auxiliares.
- Inventario con diarios de ajuste y transferencia, existencias y libros de cantidad y de costo.
- Facturación de ventas con borradores, facturas posteadas, notas de crédito y cobros.
- Cuentas por cobrar (libro de clientes y estado de cuenta).
- Contabilidad: plan de cuentas, setups contables, movimientos, balance de comprobación y costo de inventario.
- Series de numeración configurables por tipo de documento, sin huecos.
- Búsqueda global, dashboards por grupo, reportería PDF/Excel y bitácora.
- Interfaz web en español, sin JavaScript obligatorio (Static SSR).

## Tecnologías usadas

| Área | Tecnología |
| --- | --- |
| Lenguaje | C# (.NET 10) |
| Backend | ASP.NET Core Web API, MediatR, patrón `Result`/`Error` |
| Frontend | Blazor Web App Static SSR |
| Base de datos | PostgreSQL 17 |
| ORM / datos | Entity Framework Core (escrituras y migraciones) y Dapper (lecturas) |
| Seguridad | ASP.NET Core Identity, JWT, cookies HttpOnly, antiforgery |
| Contenedores | Docker y Docker Compose |
| Estilos | Tailwind CSS |
| Pruebas | xUnit + Moq, PostgreSQL real en contenedor, Playwright (E2E) |
| Control de versiones | Git |

## Arquitectura

La solución (`test.slnx`) está organizada con arquitectura Onion:

| Proyecto | Responsabilidad |
| --- | --- |
| `OpenSource1.Core` | Entidades de dominio, enums y reglas puras (por ejemplo, el cálculo de números de serie). |
| `OpenSource1.Application` | Casos de uso (`Features/<Entidad>`), comandos y consultas MediatR, DTOs, validaciones y contratos. |
| `OpenSource1.Infrastructure` | EF Core, Identity, repositorios Dapper, Unit of Work, migraciones y generador de números. |
| `OpenSource1.Api` | Controladores REST, autenticación JWT y autorización por políticas. |
| `OpenSource1.Blazor` | Interfaz Static SSR, registro de módulos, clientes HTTP tipados hacia la API. |
| `tests/OpenSource1.SmokeTests` | Pruebas unitarias, de integración (PostgreSQL real) y de páginas SSR. |
| `tests/e2e` | Pruebas E2E Playwright (Node), fuera de `test.slnx`. |

Reglas generales:

- Las escrituras usan EF Core dentro de una transacción; las lecturas y listados usan Dapper.
- La numeración de documentos es sin huecos: la serie y su línea se bloquean dentro de la transacción del documento.
- Los documentos posteados (facturas, notas de crédito, asientos) son inmutables.
- Blazor nunca expone el JWT al navegador: usa una cookie HttpOnly y una sesión del lado servidor.

## Módulos

El menú lateral y el inicio se generan desde un registro único de módulos (`src/OpenSource1.Blazor/Navigation/CatalogoModulos.cs`); cada módulo se muestra según el permiso del usuario. Cada grupo tiene su página `/modulos/{grupo}` con enlaces e indicadores.

| Grupo | Módulos |
| --- | --- |
| Clientes | Clientes (`/clientes`), panel de clientes |
| Productos | Productos (`/productos`), categorías de producto, panel de productos |
| Inventario | Existencias, almacenes, diarios de inventario, movimientos de producto y de valor |
| Ventas | Cobros, movimientos de cliente, estado de cuenta (CxC) |
| Facturación | Borradores de factura, facturas, borradores de nota de crédito, notas de crédito |
| Contabilidad | Plan de cuentas, movimientos contables, balance de comprobación, costo de inventario |
| Reportes | Reportería PDF/Excel, bitácora |
| Configuración | Términos de pago, unidades de medida, grupos contables, grupos de cliente contable, setups contables, fechas de registro, series de numeración, configuración de numeración |
| Administración | Usuarios (`/admin/users`) |

Funciones transversales:

- **Búsqueda global** de módulos y registros (clientes, productos y documentos): barra superior con resultados en `/buscar?q=` y paleta **Ctrl+K** (también `/`) como mejora progresiva.
- **Patrón de página común** en los mantenimientos: barra de acciones (`+ Nuevo`, `Crear ▾`, `Ver ▾`, `Editar`, `Eliminar`) según permisos, selección de fila con `?sel=`, alta y edición en página propia (`/x/nuevo`, `/x/{id}/editar`) con vuelta segura al listado (`returnUrl` local validado).
- **Acciones contextuales**: nueva factura o nota de crédito desde un cliente, facturas filtradas por cliente, cobro en página propia y ajuste de inventario precargado desde un producto.
- **Series de numeración** (`/series`): tipo de documento, estado activo y líneas por fecha con prefijo, ancho, rango, incremento, número de aviso y bloqueo. La serie usada no se borra y la serie asignada a un tipo no se borra ni se desactiva. La serie de cada tipo de documento se elige en `/configuracion/numeracion`.
- **Documento en una sola página**: los borradores de factura y de nota de crédito muestran cabecera editable, líneas y totales en la misma página, con serie de borrador, serie de registro y próximo número. Al postear se numera con la serie de registro y el borrador queda **Posteada** en solo lectura, enlazado a su documento. Una factura posteada se puede copiar a un borrador nuevo.
- **Fechas de registro permitidas**: los documentos solo se registran dentro del rango configurado por el Administrador.

## Roles y permisos

| Rol | Consultar | Agregar | Modificar | Eliminar | Administrar |
| --- | --- | --- | --- | --- | --- |
| `Administrador` | Sí | Sí | Sí | Sí | Sí |
| `Supervisor` | Sí | No | Sí | No | No |
| `Ejecutor` | Sí | Sí | No | No | No |

Políticas usadas por la API:

- `CanConsult`: Administrador, Supervisor, Ejecutor.
- `CanAdd`: Administrador, Ejecutor.
- `CanModify`: Administrador, Supervisor.
- `CanDelete`: Administrador.
- `CanAdministrar`: Administrador (fechas de registro, series y configuración de numeración).

Además, la gestión de usuarios es exclusiva del Administrador y la bitácora es visible para Administrador y Supervisor. La interfaz oculta las acciones según el permiso, pero la seguridad se valida siempre en la API.

## Usuarios iniciales

Cuando la carga inicial está habilitada (`UserSeed__Enabled=true`, activa en Docker Compose), se crean:

| Usuario | Rol |
| --- | --- |
| `admin` | `Administrador` |
| `supervisor` | `Supervisor` |
| `ejecutor` | `Ejecutor` |

Su contraseña es la que se define en `AUTH_SEED_DEFAULT_PASSWORD` del archivo `.env`. Use un valor propio y no lo publique; la carga inicial es solo para desarrollo y demostración.

## Autenticación y seguridad

Flujo:

1. Blazor envía las credenciales a `POST /api/auth/login`.
2. La API valida el usuario con ASP.NET Core Identity.
3. La API devuelve un JWT con roles y permisos.
4. Blazor crea una sesión web con cookie segura HttpOnly.
5. El JWT se guarda del lado servidor para llamar a la API.

El JWT no se guarda en `localStorage`, `sessionStorage` ni en cookies accesibles desde JavaScript. Los formularios usan tokens antiforgery y las escrituras concurrentes se controlan con la versión de fila de PostgreSQL (`xmin`, 409 si está obsoleta).

Endpoints de autenticación:

| Endpoint | Descripción |
| --- | --- |
| `POST /api/auth/register` | Registro de usuario. |
| `POST /api/auth/login` | Inicio de sesión y emisión de JWT. |
| `GET /api/auth/me` | Usuario actual, roles y permisos. |
| `POST /api/auth/forgot-password` | Solicitud de recuperación de contraseña. |
| `POST /api/auth/reset-password` | Restablecimiento de contraseña. |
| `POST /api/auth/change-password` | Cambio de contraseña autenticado. |

El resto de endpoints se puede explorar en la documentación interactiva (`/scalar/v1`).

> Nota: el token de recuperación no debe mostrarse en pantalla. En producción debe enviarse por correo o por otro canal seguro.

## Estructura del repositorio

```txt
.
├── src/
│   ├── OpenSource1.Core/
│   ├── OpenSource1.Application/
│   ├── OpenSource1.Infrastructure/
│   ├── OpenSource1.Api/
│   └── OpenSource1.Blazor/
├── tests/OpenSource1.SmokeTests/  # xUnit (unitarias, integración con PostgreSQL, páginas SSR)
├── tests/e2e/                     # E2E Playwright (Node), fuera de test.slnx
├── docs/
│   ├── entregable-parte-1/        # documento .docx, presentación, diagramas, capturas y evidencias
│   ├── ENTREGABLE-Final-A00120258.md
│   ├── screenshots/entregable-3/
│   └── superpowers/               # especificaciones y planes de cada fase
├── Dockerfile.api
├── Dockerfile.blazor
├── docker-compose.yml
├── init-postgres.sh
├── rebuild.sh
├── Makefile
├── auth.http
├── .env.example
└── test.slnx
```

## Configuración local

Crear el archivo `.env` desde la plantilla:

```bash
cp .env.example .env
```

Variables:

| Variable | Uso |
| --- | --- |
| `POSTGRES_PASSWORD` | Contraseña del usuario de PostgreSQL (requerida). |
| `JWT_SIGNING_KEY` | Clave de firma del JWT, mínimo 32 caracteres aleatorios (requerida). |
| `AUTH_SEED_DEFAULT_PASSWORD` | Contraseña de los usuarios iniciales (requerida). |
| `POSTGRES_PORT` | Puerto del host para PostgreSQL (opcional, por defecto `5432`; cámbielo si ese puerto ya está ocupado). |

Una clave JWT aleatoria se puede generar con:

```bash
openssl rand -base64 48
```

No subir el archivo `.env` real al repositorio (está en `.gitignore`).

### Configuración de `OpenSource1.Api` para ejecución sin Docker

`src/OpenSource1.Api/appsettings.json` **no está versionado** (se ignora para no volver a comprometer credenciales). Para ejecutar la API fuera de Docker, crearlo desde la plantilla:

```bash
cp src/OpenSource1.Api/appsettings.Example.json src/OpenSource1.Api/appsettings.json
```

Los valores marcados como `__SET_IN_USER_SECRETS__` se rellenan con User Secrets, nunca en el propio archivo:

```bash
cd src/OpenSource1.Api
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Host=localhost;Port=5432;Database=AxionERP_App;Username=Rainiery;Password=<tu-password>"
dotnet user-secrets set "ConnectionStrings:IdentityConnection" "Host=localhost;Port=5432;Database=AxionERP_Identity;Username=Rainiery;Password=<tu-password>"
dotnet user-secrets set "Jwt:SigningKey" "<tu-clave-de-firma-jwt>"
```

User Secrets tiene prioridad sobre `appsettings.json` en `Development`. `appsettings.Development.json` también es opcional y se crea igual desde `appsettings.Development.Example.json` (comparte la misma `UserSecretsId`).

## Ejecución con Docker Compose

Requisitos: Docker con Compose v2 y el archivo `.env` configurado.

Construir las imágenes desde el código y levantar el sistema:

```bash
docker compose build
docker compose up -d
```

La API aplica las migraciones al iniciar (`Database__ApplyMigrationsOnStartup=true`) y crea los usuarios iniciales; el primer arranque tarda unos segundos más.

Servicios disponibles:

| Servicio | URL / Puerto |
| --- | --- |
| Blazor | <http://localhost:8080> |
| API | <http://localhost:8081> |
| Documentación de la API (Scalar) | <http://localhost:8081/scalar/v1> |
| PostgreSQL | `localhost:${POSTGRES_PORT}` (por defecto `5432`) |

Atajos del `Makefile`:

| Comando | Acción |
| --- | --- |
| `make up` | Levanta el stack con las imágenes existentes. |
| `make soft` | Reconstruye con caché de capas y levanta. |
| `make rebuild` | Reconstruye sin caché (equivale a `./rebuild.sh`). |
| `make down` | Detiene y elimina contenedores (conserva los datos). |
| `make logs` / `make logs-api` / `make logs-blazor` | Sigue los logs. |
| `make ps` | Estado de los contenedores. |
| `make push` | Sube las imágenes a Docker Hub. |

Eliminar también los datos persistentes:

```bash
docker compose down -v
```

> Si usa varios contextos de Docker, ejecute los comandos con `DOCKER_CONTEXT=default` (o el contexto donde corre el stack).

## Ejecución sin Docker

Requiere PostgreSQL local con las bases `AxionERP_App` y `AxionERP_Identity`, y la configuración de la API descrita arriba.

```bash
dotnet restore test.slnx
dotnet build test.slnx
dotnet run --project src/OpenSource1.Api/OpenSource1.Api.csproj
dotnet run --project src/OpenSource1.Blazor/OpenSource1.Blazor.csproj
```

Aplicar migraciones manualmente:

```bash
dotnet ef database update -p src/OpenSource1.Infrastructure -s src/OpenSource1.Api --context ApplicationDbContext
```

## Base de datos

El contenedor PostgreSQL crea dos bases de datos (`init-postgres.sh`):

- `AxionERP_App`: datos del negocio (maestros, inventario, ventas, contabilidad, series).
- `AxionERP_Identity`: usuarios y roles de ASP.NET Core Identity.

Las migraciones de la aplicación están en `src/OpenSource1.Infrastructure/Data/Migrations/Application`. Las más recientes son `SeriesNumeracionConfigurables`, `BorradoresConSeriesYPosteada`, `BorradoresEnlaceUnico` y `EnlazarBorradoresPosteadosHeredados`; todas tienen Down probado.

Volúmenes:

| Volumen | Uso |
| --- | --- |
| `postgres-data` | Datos persistentes de PostgreSQL. |
| `dataprotection-keys` | Llaves Data Protection compartidas entre API y Blazor. |
| `uploads-data` | Archivos subidos desde Blazor. |

## Pruebas

Suite xUnit (requiere Docker: las pruebas de integración crean el contenedor `opensource1-tests-postgres` en el puerto `65432` y lo eliminan al terminar; no ejecute dos suites a la vez):

```bash
dotnet build test.slnx
dotnet test test.slnx
```

Una prueba concreta o un grupo:

```bash
dotnet test tests/OpenSource1.SmokeTests --filter "FullyQualifiedName~Series"
```

Pruebas E2E con Playwright, contra el stack levantado en `http://localhost:8080`:

```bash
cd tests/e2e
npm ci
npx playwright install chromium
bash scripts/con-credenciales.sh npx playwright test
```

`con-credenciales.sh` toma `E2E_PASSWORD` de `AUTH_SEED_DEFAULT_PASSWORD` en el `.env` sin escribirla en ningún archivo; también se puede exportar `E2E_PASSWORD` a mano.

Variables opcionales: `E2E_BASE_URL`, `E2E_ADMIN_USER`, `E2E_SUPERVISOR_USER`, `E2E_EJECUTOR_USER`, `E2E_TRACE=1`. El reporte HTML se guarda en `docs/entregable-parte-1/evidencias/reporte-e2e/`.

Comprobación rápida del login desde la API:

```bash
curl -X POST http://localhost:8081/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"userNameOrEmail":"admin","password":"<contraseña>"}'
```

## Docker Hub

Repositorio `ggeasy75/opensource`:

```bash
docker pull ggeasy75/opensource:api
docker pull ggeasy75/opensource:blazor
```

## Git Flow y validación

Ramas principales:

- `main`: rama estable.
- `qa`: validación previa a release.
- `deploy`: rama lista para despliegue.

Las funcionalidades se desarrollan en ramas `feat/*` y se integran por PR. El repositorio incluye workflows (`.github/workflows`) que validan los PRs hacia `qa`, `deploy` y `main`, y promueven automáticamente `deploy` hacia `main`.

## Entregables académicos

- **Etapa I — Login y roles:** pantalla de login, validación con Identity, roles `Administrador`/`Supervisor`/`Ejecutor`, menú y acciones según permiso, bloqueo en UI y API (ver changelog 0.1).
- **Etapa III — Integración total:** [`docs/ENTREGABLE-Final-A00120258.md`](docs/ENTREGABLE-Final-A00120258.md), con capturas en [`docs/screenshots/entregable-3/`](docs/screenshots/entregable-3/).
- **Entregable final, Parte 1:** [`docs/entregable-parte-1/`](docs/entregable-parte-1/) (documento técnico `.docx`, presentación `.pptx`, guion de demostración, diagramas, capturas y reporte E2E). Se regeneran con `npm run diagramas` y `npm run entregable` en `tests/e2e`.

## Changelog

### Versión 0.5 — Series de numeración y documento único

Series de numeración configurables, flujo del borrador con series elegibles y documento en una sola página (rama `feat/no-series`).

Incluye:

- Serie con tipo de documento y estado activo; líneas por fecha con prefijo, ancho, rango, incremento, número de aviso y bloqueo; sin solapes entre series del mismo tipo. Motor de numeración por tipo y por serie, sin códigos fijos en los consumidores (facturas, notas, borradores, cobros, asientos, socios, diarios).
- API y páginas de series (`/series`) y de configuración por tipo (`/configuracion/numeracion`), con escrituras solo para `CanAdministrar`. El borrado de una serie exige su versión (`DELETE api/series/{id}?xmin=`); la ficha y la lista explican por qué no se puede borrar o cambiar el tipo de una serie asignada o referenciada.
- Borradores de factura y de nota con serie de borrador y de registro y próximo número; el posteo usa la serie de registro y deja el borrador en estado Posteada de solo lectura enlazado al documento. Copia de una factura posteada a un borrador nuevo.
- Documento en una sola página para los borradores y para los documentos posteados (con la cuenta de IVA en los totales).
- Los lotes de diario validan su serie bloqueada (`FOR SHARE`) en la misma transacción; pruebas de concurrencia real del posteo frente a la desactivación o el cambio de tipo de la serie.
- Migración de datos `EnlazarBorradoresPosteadosHeredados`: los borradores posteados antes de esta versión quedan enlazados a su documento, con sus líneas.
- Suite xUnit ampliada a 2004 tests (0 avisos) y E2E del documento en una sola página (`09-documento-unico`).

### Versión 0.4 — Fix-Features

Navegación, acciones por página, búsqueda global y entregable Parte 1 (rama `Fix-Features`).

Incluye:

- Registro único de módulos y grupos con visibilidad por permiso; menú lateral por grupos con el grupo actual abierto.
- Inicio compacto con tarjetas de grupo y páginas `/modulos/{grupo}` con indicadores (los gráficos siguen en `/dashboard/*`).
- Búsqueda global de solo lectura en la API (Dapper) para clientes, productos y documentos; página `/buscar` y paleta Ctrl+K.
- Barra de acciones, selección de filas y página de alta y edición en todos los listados de mantenimiento, con `returnUrl` validado contra redirecciones externas.
- Acciones contextuales de clientes, productos, facturas y notas de crédito; alta de cobro en `/cobros/nuevo`.
- Navegación sin destello azul (barra de progreso con retardo de 150 ms).
- Suite E2E Playwright en `tests/e2e/` con capturas; documento técnico `.docx`, presentación `.pptx`, guion, diagramas y modelo ER generados del esquema real.

### Módulos ERP (Fases 0–8)

Ampliación de AxionERP a un ERP comercial, desarrollada por fases en ramas `feat/erp-fase-*`.

Incluye:

- Núcleo: patrón `Result`/`Error`, Unit of Work, control de concurrencia con `xmin` y fechas de registro permitidas.
- Maestros: socios de negocio (clientes), productos con unidades y categorías, almacenes, términos de pago y grupos contables.
- Inventario: diarios de ajuste y transferencia con plantillas y lotes, existencias por almacén, libros de cantidad y de valor, costo unitario al día.
- Contabilidad: plan de cuentas, setups contables, movimientos, balance de comprobación y posteo del costo de inventario.
- Ventas: borradores y facturas de venta, cobros con aplicación a facturas, libro de clientes y estado de cuenta, notas de crédito con reverso de inventario y contabilidad.
- Retiro de los módulos de demostración de entradas y configuraciones de aplicación.

### Versión 0.3 — Entregable 3

Integración final de AxionERP como sistema empresarial completo, con módulos operativos, seguridad por roles y evidencias automatizadas de funcionamiento.

Incluye:

- Integración completa de login, menú principal, frontend, API y PostgreSQL.
- CRUD de usuarios exclusivo para el rol `Administrador`.
- CRUD de clientes y productos conectado a base de datos, con búsquedas y filtros por múltiples campos.
- Validaciones de formularios y reglas de negocio en frontend y backend.
- Políticas `CanConsult`, `CanAdd`, `CanModify` y `CanDelete` y navegación según rol.
- Dashboard general y dashboards de clientes y productos.
- Reportes PDF y exportaciones a Excel.
- Bitácora operativa para Administrador y Supervisor.
- Modo oscuro y diseño responsivo.
- 47 capturas E2E con evidencia de módulos, formularios, confirmaciones y permisos.

#### Matriz de autorización validada

| Acción | Administrador | Supervisor | Ejecutor |
| --- | --- | --- | --- |
| Consultar clientes y productos | Sí | Sí | Sí |
| Agregar clientes y productos | Sí | No | Sí |
| Modificar clientes y productos | Sí | Sí | No |
| Eliminar clientes y productos | Sí | No | No |
| Gestionar usuarios | Sí | No | No |
| Consultar bitácora | Sí | Sí | No |

### Versión 0.1 — Entregable 1

Primera versión del sistema, enfocada en la **Etapa I: Sistema de Login y Roles**.

Incluye:

- Proyecto cliente-servidor en .NET con arquitectura Onion.
- Backend ASP.NET Core Web API y frontend Blazor Web App Static SSR.
- Base de datos PostgreSQL.
- Autenticación con ASP.NET Core Identity; contraseñas ocultas y mensajes de error.
- Roles iniciales `Administrador`, `Supervisor` y `Ejecutor` con usuarios `admin`, `supervisor` y `ejecutor`.
- Permisos por rol para consultar, agregar, modificar y eliminar; bloqueo de acciones no autorizadas desde UI y API.
- Menú principal con opciones según permisos.
- Gestión de usuarios y roles.
- Docker Compose para API, Blazor y PostgreSQL, con inicialización de la base de datos.
- Workflows de validación y pruebas smoke.

## Notas de mantenimiento

- Target framework: `net10.0`; solución `test.slnx` (usar `dotnet build test.slnx` / `dotnet test test.slnx`).
- Los paquetes se gestionan con `PackageReference` directo en cada proyecto (sin Central Package Management).
- Nullable reference types e implicit usings están habilitados.
- El frontend Blazor usa Static SSR; no agregar `@rendermode` ni handlers interactivos salvo que se migre explícitamente a modo interactivo. Usar formularios GET/POST, `[SupplyParameterFromForm]`, `[SupplyParameterFromQuery]` y antiforgery.
- Un nuevo mantenimiento sigue el patrón existente: controlador en la API, `Features/<Entidad>` en Application, repositorio Dapper en Infrastructure y página Static SSR registrada en `CatalogoModulos`.
