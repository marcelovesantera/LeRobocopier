using Avalonia.Controls;
using Avalonia.Interactivity;

namespace LeRobocopier.Desktop;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private void Configuracoes_Click(object? sender, RoutedEventArgs args) =>
        (DataContext as MainWindowViewModel)?.Navegar(PainelPrincipal.Configuracoes);

    private void Novo_Click(object? sender, RoutedEventArgs args) =>
        (DataContext as MainWindowViewModel)?.Navegar(PainelPrincipal.Novo);

    private void Projetos_Click(object? sender, RoutedEventArgs args) =>
        (DataContext as MainWindowViewModel)?.Navegar(PainelPrincipal.Projetos);

    private void Criar_Click(object? sender, RoutedEventArgs args) =>
        (DataContext as MainWindowViewModel)?.Criar();
}
