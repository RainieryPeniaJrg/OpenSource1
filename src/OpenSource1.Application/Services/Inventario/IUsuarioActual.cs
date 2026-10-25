namespace OpenSource1.Application.Services.Inventario;

/// <summary>
/// Usuario en cuyo nombre se escribe el libro (<c>CreatedBy</c>/<c>UsuarioId</c>). En la API sale de los claims del
/// JWT; fuera de una petición HTTP (tests, procesos batch) vale <c>"system"</c> sin id.
/// </summary>
public interface IUsuarioActual
{
    string Nombre { get; }
    Guid? Id { get; }

    /// <summary>
    /// El usuario tiene el rol Administrador, que es lo que la API exige para <c>CanAdministrar</c> (spec alta rápida: solo él
    /// elige la serie de registro). Fuera de una petición HTTP es <see langword="false"/>: un proceso del sistema no elige series.
    /// </summary>
    bool EsAdministrador { get; }
}
