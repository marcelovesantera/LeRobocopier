using System.ComponentModel;
using System.Runtime.CompilerServices;
using LeRobocopier.Core;

namespace LeRobocopier.Desktop;

public enum PainelPrincipal { Projetos, Configuracoes, Novo }

public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly CadastroProjetos? cadastro;
    private PainelPrincipal painel = PainelPrincipal.Projetos;
    private string mensagem = "";
    private string nomeNovo = "";
    private Projeto? selecionado;

    public event PropertyChangedEventHandler? PropertyChanged;
    public IReadOnlyList<Projeto> Projetos => cadastro?.Estado.Projetos.Where(projeto => projeto.Ativo).ToArray()
        ?? Array.Empty<Projeto>();
    public string BaseBackup => cadastro?.Estado.BaseBackup ?? "Indisponível";
    public bool ConfirmarCopiar => cadastro?.Estado.Confirmacoes.Copiar ?? true;
    public bool ConfirmarMover => cadastro?.Estado.Confirmacoes.Mover ?? true;
    public bool ConfirmarRestaurar => cadastro?.Estado.Confirmacoes.Restaurar ?? true;
    public bool PodeCriar => cadastro is not null;
    public bool MostrarProjetos => painel == PainelPrincipal.Projetos;
    public bool MostrarConfiguracoes => painel == PainelPrincipal.Configuracoes;
    public bool MostrarNovo => painel == PainelPrincipal.Novo;
    public bool SemProjetos => Projetos.Count == 0;
    public string Mensagem { get => mensagem; private set { mensagem = value; Notificar(); } }
    public string NomeNovo { get => nomeNovo; set { nomeNovo = value; Notificar(); } }
    public Projeto? Selecionado { get => selecionado; set { selecionado = value; Notificar(); Notificar(nameof(Selecao)); } }
    public string Selecao => Selecionado is null ? "Selecione um projeto." : $"Projeto selecionado: {Selecionado.Nome}";

    public MainWindowViewModel()
    {
        try { cadastro = new CadastroProjetos(DiretoriosUsuario.Configuracao(), DiretoriosUsuario.BaseBackup()); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Mensagem = error.Message + " Corrija a configuração e reinicie o aplicativo.";
        }
    }

    public void Navegar(PainelPrincipal destino)
    {
        painel = destino;
        Notificar(nameof(MostrarProjetos));
        Notificar(nameof(MostrarConfiguracoes));
        Notificar(nameof(MostrarNovo));
    }

    public void Criar()
    {
        if (cadastro is null) return;
        try
        {
            var projeto = cadastro.Criar(NomeNovo);
            Notificar(nameof(Projetos));
            Notificar(nameof(SemProjetos));
            Selecionado = projeto;
            NomeNovo = "";
            Mensagem = "Projeto salvo.";
            Navegar(PainelPrincipal.Projetos);
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException
            or System.Xml.XmlException)
        {
            Mensagem = error.Message;
        }
    }

    private void Notificar([CallerMemberName] string? nome = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nome));
}
