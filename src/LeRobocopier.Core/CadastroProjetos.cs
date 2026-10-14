using System.Xml;
using System.Xml.Linq;

namespace LeRobocopier.Core;

// A alteração só se torna visível depois da substituição do XML no disco.
public sealed class CadastroProjetos
{
    private readonly string arquivo;
    private readonly Action<string, string> substituirArquivo;

    public Configuracao Estado { get; private set; }

    public CadastroProjetos(string arquivo, string basePadrao,
        Action<string, string>? substituirArquivo = null)
    {
        this.arquivo = Path.GetFullPath(arquivo);
        this.substituirArquivo = substituirArquivo ?? ((temporario, destino) => File.Move(temporario, destino, true));
        // File.Exists também retorna false em erros de acesso: tentar abrir evita apagar estado ilegível.
        try
        {
            using var stream = File.OpenRead(this.arquivo);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            Estado = Ler(XDocument.Load(reader));
        }
        catch (FileNotFoundException)
        {
            Estado = Inicial(basePadrao);
        }
        catch (DirectoryNotFoundException)
        {
            Estado = Inicial(basePadrao);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or XmlException
            or FormatException or InvalidOperationException or ArgumentException)
        {
            throw new InvalidDataException($"Não foi possível ler {this.arquivo}. O arquivo existente foi preservado.", error);
        }
    }

    public Projeto Criar(string nome)
    {
        ValidarNome(nome);
        if (Estado.Projetos.Any(projeto => string.Equals(projeto.Nome, nome, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Já existe um projeto ativo ou inativo com esse nome.", nameof(nome));

        var projeto = new Projeto(Guid.NewGuid(), nome, true, Array.Empty<string>());
        var projetos = Array.AsReadOnly(Estado.Projetos.Append(projeto).ToArray());
        Salvar(Estado with { Projetos = projetos });
        return projeto;
    }

    private static Configuracao Inicial(string basePadrao)
    {
        if (!Path.IsPathFullyQualified(basePadrao))
            throw new ArgumentException("A base de backup deve ter um caminho absoluto.", nameof(basePadrao));
        return new Configuracao(1, basePadrao, new Confirmacoes(), Array.Empty<Projeto>());
    }

    private void Salvar(Configuracao proposta)
    {
        var temporario = Path.Combine(Path.GetDirectoryName(arquivo)!, $".settings-{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(arquivo)!);
            var xml = new XDocument(new XElement("Configuracao",
                new XAttribute("versao", proposta.Versao),
                new XElement("BaseBackup", proposta.BaseBackup),
                new XElement("Confirmacoes", new XAttribute("copiar", proposta.Confirmacoes.Copiar),
                    new XAttribute("mover", proposta.Confirmacoes.Mover),
                    new XAttribute("restaurar", proposta.Confirmacoes.Restaurar)),
                new XElement("Projetos", proposta.Projetos.Select(projeto => new XElement("Projeto",
                    new XAttribute("id", projeto.Id), new XAttribute("nome", projeto.Nome),
                    new XAttribute("ativo", projeto.Ativo), new XElement("Itens"))))));
            using (var stream = new FileStream(temporario, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                xml.Save(stream);
                stream.Flush(true);
            }
            substituirArquivo(temporario, arquivo);
            Estado = proposta;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new IOException("Não foi possível salvar o projeto. O cadastro anterior foi preservado.", error);
        }
        finally
        {
            try { File.Delete(temporario); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static Configuracao Ler(XDocument xml)
    {
        var root = xml.Root ?? throw new InvalidDataException("XML sem configuração.");
        if (root.Name != "Configuracao" || (int?)root.Attribute("versao") != 1)
            throw new InvalidDataException("Formato de configuração não suportado.");
        ValidarEstrutura(root, ["versao"], ["BaseBackup", "Confirmacoes", "Projetos"]);
        var baseXml = root.Element("BaseBackup")!;
        if (baseXml.HasAttributes || baseXml.HasElements)
            throw new InvalidDataException("Estrutura da base de backup não suportada.");
        var baseBackup = baseXml.Value;
        if (!Path.IsPathFullyQualified(baseBackup))
            throw new InvalidDataException("Base de backup inválida.");
        var confirmacoes = root.Element("Confirmacoes") ?? throw new InvalidDataException("Confirmações ausentes.");
        ValidarEstrutura(confirmacoes, ["copiar", "mover", "restaurar"], []);
        var preferencias = new Confirmacoes(
            (bool?)confirmacoes.Attribute("copiar") ?? throw new InvalidDataException("Confirmação Copiar ausente."),
            (bool?)confirmacoes.Attribute("mover") ?? throw new InvalidDataException("Confirmação Mover ausente."),
            (bool?)confirmacoes.Attribute("restaurar") ?? throw new InvalidDataException("Confirmação Restaurar ausente."));
        var projetosXml = root.Element("Projetos") ?? throw new InvalidDataException("Projetos ausentes.");
        if (projetosXml.HasAttributes || projetosXml.Nodes().OfType<XText>().Any(texto => !string.IsNullOrWhiteSpace(texto.Value)))
            throw new InvalidDataException("Atributos de projetos não suportados.");
        var projetos = new List<Projeto>();
        foreach (var node in projetosXml.Elements())
        {
            if (node.Name != "Projeto") throw new InvalidDataException("Registro de projeto desconhecido.");
            ValidarEstrutura(node, ["id", "nome", "ativo"], ["Itens"]);
            var id = Guid.Parse((string?)node.Attribute("id") ?? "");
            var nome = (string?)node.Attribute("nome") ?? "";
            ValidarNome(nome);
            if (id == Guid.Empty || projetos.Any(projeto => projeto.Id == id ||
                string.Equals(projeto.Nome, nome, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Identidade ou nome de projeto repetido.");
            var itens = node.Element("Itens") ?? throw new InvalidDataException("Lista de itens ausente.");
            // O ticket 01 não escreve itens; rejeitar conteúdo evita perda silenciosa de dados futuros.
            if (itens.HasAttributes || itens.HasElements || !string.IsNullOrWhiteSpace(itens.Value))
                throw new InvalidDataException("Esta versão ainda não suporta itens cadastrados.");
            projetos.Add(new Projeto(id, nome,
                (bool?)node.Attribute("ativo") ?? throw new InvalidDataException("Indicação de ativo ausente."),
                Array.Empty<string>()));
        }
        return new Configuracao(1, baseBackup, preferencias, projetos.AsReadOnly());
    }

    private static void ValidarEstrutura(XElement node, string[] atributos, string[] filhos)
    {
        if (node.Nodes().OfType<XText>().Any(texto => !string.IsNullOrWhiteSpace(texto.Value)) ||
            node.Attributes().Count() != atributos.Length ||
            node.Attributes().Any(atributo => !atributos.Contains(atributo.Name.ToString())) ||
            node.Elements().Count() != filhos.Length ||
            filhos.Any(filho => node.Elements(filho).Count() != 1))
            throw new InvalidDataException($"Estrutura de {node.Name} não suportada. O documento foi preservado.");
    }

    private static void ValidarNome(string nome)
    {
        if (string.IsNullOrWhiteSpace(nome) || nome is "." or ".." || nome != nome.Trim() ||
            nome.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || nome.Contains('/'))
            throw new ArgumentException("Informe um nome não vazio, válido para uma subpasta, sem espaços nas extremidades.", nameof(nome));
        try { XmlConvert.VerifyXmlChars(nome); }
        catch (XmlException error)
        {
            throw new ArgumentException("O nome contém caracteres que não podem ser salvos na configuração XML.", nameof(nome), error);
        }
        if (OperatingSystem.IsWindows())
        {
            var stem = nome.Split('.')[0].TrimEnd(' ');
            string[] reservados = ["CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$"];
            if (nome.EndsWith('.') || nome.Length > 255 || reservados.Contains(stem, StringComparer.OrdinalIgnoreCase) ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                    stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && "123456789¹²³".Contains(stem[3])))
                throw new ArgumentException("Esse nome é reservado ou inválido para uma subpasta no Windows.", nameof(nome));
        }
        else if (System.Text.Encoding.UTF8.GetByteCount(nome) > 255)
            throw new ArgumentException("O nome excede o limite de uma subpasta no Linux.", nameof(nome));
    }
}
