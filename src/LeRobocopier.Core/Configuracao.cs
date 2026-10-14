namespace LeRobocopier.Core;

public sealed record Projeto(Guid Id, string Nome, bool Ativo, IReadOnlyList<string> Itens);

public sealed record Confirmacoes(bool Copiar = true, bool Mover = true, bool Restaurar = true);

public sealed record Configuracao(int Versao, string BaseBackup, Confirmacoes Confirmacoes,
    IReadOnlyList<Projeto> Projetos);

public static class DiretoriosUsuario
{
    public static string Configuracao() => Configuracao(
        OperatingSystem.IsWindows(),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"));

    // Contexto explícito permite validar a resolução sem alterar o ambiente ou perfil real.
    public static string Configuracao(bool windows, string pastaUsuario, string localAppData, string? xdgConfigHome)
    {
        var baseConfiguracao = localAppData;
        if (!windows)
        {
            baseConfiguracao = !string.IsNullOrEmpty(xdgConfigHome) && Path.IsPathFullyQualified(xdgConfigHome)
                ? xdgConfigHome : Path.Combine(pastaUsuario, ".config");
        }
        return Path.Combine(baseConfiguracao, "LeRobocopier", "settings.xml");
    }

    public static string BaseBackup() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "LeRobocopier", "Backups");
}
