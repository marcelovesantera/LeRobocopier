# Le Robocopier

Aplicativo desktop para organizar backups de arquivos e pastas de vários projetos, com um destino de backup exclusivo para cada projeto.

## Operações previstas

- **Copiar:** atualiza o backup, mantendo os arquivos originais.
- **Mover:** transfere os itens para o backup e remove os originais após verificar todas as cópias.
- **Restaurar:** repõe os arquivos do backup em seus caminhos originais.

## Estado do projeto

Em planejamento, com implementação do aplicativo ainda não iniciada. A tecnologia definida é C# com .NET 10 e Avalonia UI, para Windows e Linux.

Os scripts existentes em [`bat-processes/`](bat-processes/) servem como referência e usam caminhos fixos que precisam ser revisados antes da execução.

## Documentação

- [Vocabulário do projeto](CONTEXT.md)
- [Arquitetura](.docs/patterns/architecture.md)
- [Plano de desenvolvimento](.scratch/backup-multiprojetos/0001-plano-desenvolvimento.md)
