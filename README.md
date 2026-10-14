# Le Robocopier

Aplicativo desktop C#/.NET 10 e Avalonia para organizar backups por Projeto, com cadastro persistido em XML.

Funcionalidades implementadas:

- Cadastro de projetos pelo botão Novo, com salvamento automático.
- Cards de projetos ativos com seleção de um único projeto.
- Painel de Configurações com a base de backup e as preferências de confirmação.
- Validação de nomes vazios, inválidos ou repetidos, incluindo projetos inativos.
- Preservação do cadastro anterior em falhas de gravação e bloqueio do cadastro quando o XML existente é ilegível.

Copiar, Mover e Restaurar estão indisponíveis. A base de backup e as preferências são exibidas para consulta.

Configuração: `%LOCALAPPDATA%\LeRobocopier\settings.xml` no Windows; `$XDG_CONFIG_HOME/LeRobocopier/settings.xml` no Linux, com `~/.config` quando XDG está ausente, vazio ou relativo. A base inicial é `LeRobocopier/Backups` dentro da pasta do usuário. O XML é criado ao salvar o primeiro projeto. Se o arquivo estiver ilegível, ele deve ser corrigido antes de reiniciar o aplicativo.
