using System.Xml.Linq;
using LeRobocopier.Core;
using Xunit;

namespace LeRobocopier.Core.Tests;

public sealed class CadastroProjetosTests : IDisposable
{
    private readonly string diretorio = Path.Combine(Path.GetTempPath(), "LeRobocopier-Tests", Guid.NewGuid().ToString("N"));
    private string Arquivo => Path.Combine(diretorio, "settings.xml");
    private string BaseBackup => Path.Combine(diretorio, "Backups");

    [Fact]
    public void Criar_AposReabrir_PreservaCadastroEPreferencias()
    {
        var cadastro = new CadastroProjetos(Arquivo, BaseBackup);
        var projeto = cadastro.Criar("Pesquisa & documentação");
        var reaberto = new CadastroProjetos(Arquivo, BaseBackup);

        var salvo = Assert.Single(reaberto.Estado.Projetos);
        Assert.Equal(projeto.Id, salvo.Id);
        Assert.Equal(projeto.Nome, salvo.Nome);
        Assert.True(salvo.Ativo);
        Assert.Empty(salvo.Itens);
        Assert.Equal(1, reaberto.Estado.Versao);
        Assert.Equal(BaseBackup, reaberto.Estado.BaseBackup);
        Assert.Equal(new Confirmacoes(true, true, true), reaberto.Estado.Confirmacoes);
        Assert.False(Directory.Exists(BaseBackup));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("nome/filho")]
    [InlineData(" nome")]
    public void Criar_ComNomeInvalido_PreservaDiscoEMemoria(string nome)
    {
        var cadastro = new CadastroProjetos(Arquivo, BaseBackup);
        cadastro.Criar("Existente");
        var bytes = File.ReadAllBytes(Arquivo);

        Assert.Throws<ArgumentException>(() => cadastro.Criar(nome));

        Assert.Single(cadastro.Estado.Projetos);
        Assert.Equal(bytes, File.ReadAllBytes(Arquivo));
    }

    [Fact]
    public void Criar_ComNomeReservadoPorInativo_RejeitaSemAlterarCadastro()
    {
        var cadastro = new CadastroProjetos(Arquivo, BaseBackup);
        var projeto = cadastro.Criar("Existente");
        var xml = XDocument.Load(Arquivo);
        xml.Root!.Element("Projetos")!.Element("Projeto")!.SetAttributeValue("ativo", false);
        xml.Root.Element("Confirmacoes")!.SetAttributeValue("copiar", false);
        xml.Save(Arquivo);
        var reaberto = new CadastroProjetos(Arquivo, BaseBackup);
        var bytes = File.ReadAllBytes(Arquivo);

        Assert.Throws<ArgumentException>(() => reaberto.Criar("EXISTENTE"));

        Assert.Equal(projeto.Id, Assert.Single(reaberto.Estado.Projetos).Id);
        Assert.False(reaberto.Estado.Projetos[0].Ativo);
        Assert.False(reaberto.Estado.Confirmacoes.Copiar);
        Assert.True(reaberto.Estado.Confirmacoes.Mover);
        Assert.True(reaberto.Estado.Confirmacoes.Restaurar);
        Assert.Equal(bytes, File.ReadAllBytes(Arquivo));
    }

    [Fact]
    public void Criar_QuandoSubstituicaoFalha_PreservaUltimoEstadoPersistido()
    {
        var cadastro = new CadastroProjetos(Arquivo, BaseBackup);
        cadastro.Criar("Existente");
        var bytes = File.ReadAllBytes(Arquivo);
        var falha = new CadastroProjetos(Arquivo, BaseBackup,
            (_, _) => throw new IOException("Falha de disco simulada."));

        Assert.Throws<IOException>(() => falha.Criar("Novo"));

        Assert.Single(falha.Estado.Projetos);
        Assert.Equal(bytes, File.ReadAllBytes(Arquivo));
        Assert.Single(new CadastroProjetos(Arquivo, BaseBackup).Estado.Projetos);
        Assert.Empty(Directory.GetFiles(diretorio, "*.tmp"));
    }

    [Fact]
    public void Criar_QuandoPrimeiraGravacaoFalha_NaoPublicaProjeto()
    {
        var cadastro = new CadastroProjetos(Arquivo, BaseBackup,
            (_, _) => throw new UnauthorizedAccessException("Acesso negado simulado."));

        Assert.Throws<IOException>(() => cadastro.Criar("Novo"));

        Assert.Empty(cadastro.Estado.Projetos);
        Assert.False(File.Exists(Arquivo));
        Assert.Empty(Directory.GetFiles(diretorio, "*.tmp"));
    }

    [Theory]
    [InlineData("<XML quebrado")]
    [InlineData("<Configuracao versao=\"99\" />")]
    [InlineData("<!DOCTYPE x [<!ENTITY texto 'valor'>]><Configuracao versao=\"1\" />")]
    public void Abrir_ComXmlIlegivel_PreservaBytesENaoPermiteSobrescrita(string conteudo)
    {
        Directory.CreateDirectory(diretorio);
        File.WriteAllText(Arquivo, conteudo);
        var bytes = File.ReadAllBytes(Arquivo);

        Assert.Throws<InvalidDataException>(() => new CadastroProjetos(Arquivo, BaseBackup));

        Assert.Equal(bytes, File.ReadAllBytes(Arquivo));
        Assert.Single(Directory.GetFiles(diretorio));
    }

    [Fact]
    public void Criar_ComNomeReservadoWindows_RejeitaNoWindows()
    {
        if (!OperatingSystem.IsWindows()) return;
        var cadastro = new CadastroProjetos(Arquivo, BaseBackup);
        foreach (var nome in new[] { "CON", "CON .txt", "nul.txt", "COM1", "LPT².txt", "nome.", "nome:filho", "nome\\filho" })
            Assert.Throws<ArgumentException>(() => cadastro.Criar(nome));
        Assert.Empty(cadastro.Estado.Projetos);
        Assert.False(File.Exists(Arquivo));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relativo")]
    public void Diretorios_SemXdgAbsoluto_UsaConfigNaPastaUsuario(string? xdg)
    {
        var caminho = DiretoriosUsuario.Configuracao(false, diretorio, Path.Combine(diretorio, "Local"), xdg);
        Assert.Equal(Path.Combine(diretorio, ".config", "LeRobocopier", "settings.xml"), caminho);
        Assert.False(Directory.Exists(diretorio));
    }

    [Fact]
    public void Diretorios_ComContextoUsuario_RespeitaXdgEWindows()
    {
        var xdg = Path.Combine(diretorio, "xdg");
        var local = Path.Combine(diretorio, "Local");
        Assert.Equal(Path.Combine(xdg, "LeRobocopier", "settings.xml"),
            DiretoriosUsuario.Configuracao(false, diretorio, local, xdg));
        Assert.Equal(Path.Combine(local, "LeRobocopier", "settings.xml"),
            DiretoriosUsuario.Configuracao(true, diretorio, local, xdg));
        Assert.False(Directory.Exists(diretorio));
    }

    [Fact]
    public void Criar_ComBarraInvertidaLinux_PreservaNome()
    {
        if (OperatingSystem.IsWindows()) return;
        var cadastro = new CadastroProjetos(Arquivo, BaseBackup);
        var projeto = cadastro.Criar("Pesquisa\\dados");
        Assert.Equal(projeto.Nome, Assert.Single(new CadastroProjetos(Arquivo, BaseBackup).Estado.Projetos).Nome);
    }

    [Theory]
    [InlineData("BaseBackup")]
    [InlineData("Projetos")]
    [InlineData("Desconhecido")]
    public void Abrir_ComEstruturaDesconhecida_PreservaDocumento(string elemento)
    {
        new CadastroProjetos(Arquivo, BaseBackup).Criar("Existente");
        var xml = XDocument.Load(Arquivo);
        xml.Root!.Add(new XElement(elemento));
        xml.Save(Arquivo);
        var bytes = File.ReadAllBytes(Arquivo);
        Assert.Throws<InvalidDataException>(() => new CadastroProjetos(Arquivo, BaseBackup));
        Assert.Equal(bytes, File.ReadAllBytes(Arquivo));
    }

    [Fact]
    public void Criar_ComCaractereXmlInvalido_RejeitaComMensagemClara()
    {
        var cadastro = new CadastroProjetos(Arquivo, BaseBackup);
        var erro = Assert.Throws<ArgumentException>(() => cadastro.Criar("Pesquisa\uFFFF"));
        Assert.Contains("configuração XML", erro.Message);
        Assert.Empty(cadastro.Estado.Projetos);
        Assert.False(File.Exists(Arquivo));
    }

    [Fact]
    public void Abrir_ComAtributoDesconhecidoNaBase_PreservaDocumento()
    {
        new CadastroProjetos(Arquivo, BaseBackup).Criar("Existente");
        var xml = XDocument.Load(Arquivo);
        xml.Root!.Element("BaseBackup")!.SetAttributeValue("origem", "desconhecida");
        xml.Save(Arquivo);
        var bytes = File.ReadAllBytes(Arquivo);
        Assert.Throws<InvalidDataException>(() => new CadastroProjetos(Arquivo, BaseBackup));
        Assert.Equal(bytes, File.ReadAllBytes(Arquivo));
    }

    public void Dispose()
    {
        // Somente o diretório aleatório criado por este teste; nunca usa origens dos scripts.
        if (Directory.Exists(diretorio)) Directory.Delete(diretorio, true);
    }
}
