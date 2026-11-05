using OpenSource1.Application.Security;
using OpenSource1.Core.ValueObjects;

namespace OpenSource1.SmokeTests;

public class UsuarioAndApplicationRolesTests
{
    [Fact]
    public void Usuario_Constructor_NormalizesValues()
    {
        var usuario = Usuario.Of("  rainiery  ", " USER@MAIL.COM ", "  Rainiery Penia  ");

        Assert.Equal("rainiery", usuario.UserName);
        Assert.Equal("user@mail.com", usuario.Email);
        Assert.Equal("Rainiery Penia", usuario.FullName);
    }

    [Fact]
    public void Usuario_Equality_IsCaseInsensitiveForUserName()
    {
        var first = Usuario.Of("rainiery", "user@mail.com", "Rainiery Penia");
        var second = Usuario.Of("RAINIERY", "user@mail.com", "Rainiery Penia");

        Assert.Equal(first, second);
    }

    [Fact]
    // Legacy: public void ApplicationRoles_All_ContainsThreeExpectedRoles()
    public void ApplicationRoles_All_ContainsLegacyRolesAndVendedor()
    {
        // Legacy: Assert.Equal(3, ApplicationRoles.All.Length);
        Assert.Equal(4, ApplicationRoles.All.Length);
        Assert.Contains(ApplicationRoles.Administrator, ApplicationRoles.All);
        Assert.Contains(ApplicationRoles.Supervisor, ApplicationRoles.All);
        Assert.Contains(ApplicationRoles.Executor, ApplicationRoles.All);
        Assert.Contains(ApplicationRoles.Salesperson, ApplicationRoles.All);
    }
}
