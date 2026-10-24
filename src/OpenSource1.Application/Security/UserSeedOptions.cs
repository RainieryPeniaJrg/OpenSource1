namespace OpenSource1.Application.Security;

public sealed class UserSeedOptions
{
    public const string SectionName = "UserSeed";

    public bool Enabled { get; init; }
    public string? DefaultPassword { get; init; }

    /// <summary>
    /// Si es <c>true</c>, al arrancar se vuelve a poner <see cref="DefaultPassword"/> a los usuarios semilla que ya existen con otra
    /// contraseña. Por defecto <c>false</c>: la semilla solo fija la contraseña al crear el usuario.
    /// </summary>
    public bool ResetPasswords { get; init; }
}
