using OpenSource1.Application.Services.Inventario;

namespace OpenSource1.Infrastructure.Services.Inventario;

/// <summary>Usuario por defecto fuera de una petición HTTP (tests, procesos batch, migraciones): <c>"system"</c> sin id.</summary>
public sealed class UsuarioActualSistema : IUsuarioActual
{
    public string Nombre => "system";
    public Guid? Id => null;
    public bool EsAdministrador => false;
}
