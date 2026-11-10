using System.IO;
using System.Text;
using VmTyper.CodeAnalysis;
using VmTyper.Documents;
using VmTyper.Models;
using VmTyper.Services;
using VmTyper.Tests.Fakes;
using Xunit;

namespace VmTyper.Tests;

[Trait("Category", "Unit")]
public sealed class SmartEditorAndSplitterTests
{
    [Theory]
    [InlineData("unit1.pas", SourceLanguage.Delphi)]
    [InlineData("project.dpr", SourceLanguage.Delphi)]
    [InlineData("include.inc", SourceLanguage.Delphi)]
    [InlineData("script.sql", SourceLanguage.SqlFirebird)]
    [InlineData("schema.ddl", SourceLanguage.SqlFirebird)]
    [InlineData("notes.txt", SourceLanguage.PlainText)]
    public void LanguageDetection_DetectsByExtension(string fileName, SourceLanguage expected)
    {
        var result = LanguageDetectionService.Detect(fileName, "qualquer texto");
        Assert.Equal(expected, result.Language);
    }

    [Fact]
    public void LanguageDetection_DetectsDelphiFromContent()
    {
        string delphiContent = @"
unit MyUnit;

interface

uses
  System.SysUtils;

type
  TMyClass = class
  public
    procedure DoWork;
  end;

implementation

procedure TMyClass.DoWork;
begin
  WriteLn('Hello World');
end;

end.";
        var result = LanguageDetectionService.DetectFromContent(delphiContent);
        Assert.Equal(SourceLanguage.Delphi, result.Language);
    }

    [Fact]
    public void LanguageDetection_DetectsFirebirdSqlFromContent()
    {
        string sqlContent = @"
SET TERM ^ ;
CREATE OR ALTER PROCEDURE SP_GET_CUSTOMERS
RETURNS (ID INTEGER, NAME VARCHAR(100))
AS
BEGIN
  FOR SELECT C.ID, C.NAME FROM CUSTOMER C INTO :ID, :NAME DO
    SUSPEND;
END ^
SET TERM ; ^";
        var result = LanguageDetectionService.DetectFromContent(sqlContent);
        Assert.Equal(SourceLanguage.SqlFirebird, result.Language);
    }

    [Fact]
    public void LanguageDetection_DelphiWithEmbeddedSqlString_IsRecognizedAsDelphi()
    {
        string code = @"
unit DataModule;
interface
implementation
procedure ExecuteQuery;
var
  Sql: string;
begin
  Sql := 'SELECT ID, NAME, STATUS FROM CLIENTES WHERE STATUS = 1 ORDER BY NAME;';
  // Isso não deve classificar a unit inteira como SQL!
end;
end.";
        var result = LanguageDetectionService.DetectFromContent(code);
        Assert.Equal(SourceLanguage.Delphi, result.Language);
    }

    [Fact]
    public void DelphiSplitter_ReconstitutionInvariant_PreservesEntireTextExactly()
    {
        string delphiSource = @"unit TestUnit;

{$R *.dfm}

interface

uses
  Winapi.Windows, Winapi.Messages, System.SysUtils, System.Classes;

type
  { Representa um registro }
  TUserRecord = record
    Id: Integer;
    Name: string;
  end;

  TMyService = class(TObject)
  private
    FCount: Integer;
  public
    constructor Create;
    destructor Destroy; override;
    procedure ExecuteAction(const AParam: string);
    function Calculate(A, B: Integer): Integer;
  end;

const
  MAX_RETRIES = 5;

var
  GlobalService: TMyService;

implementation

// Construtor
constructor TMyService.Create;
begin
  inherited Create;
  FCount := 0;
end;

destructor TMyService.Destroy;
begin
  inherited Destroy;
end;

procedure TMyService.ExecuteAction(const AParam: string);
var
  I: Integer;
begin
  for I := 1 to 10 do
  begin
    if I = 5 then
      Break;
  end;
end;

function TMyService.Calculate(A, B: Integer): Integer;
begin
  Result := A + B;
end;

initialization
  GlobalService := nil;

finalization
  if Assigned(GlobalService) then
    GlobalService.Free;

end.
";

        var doc = new SourceDocument
        {
            Name = "TestUnit.pas",
            Text = delphiSource,
            Language = SourceLanguage.Delphi,
            SplitLevel = 3
        };

        var blocks = DelphiCodeSplitter.Split(doc, 3);

        Assert.NotEmpty(blocks);

        // Verificação invariante inviolável: reconstituição byte-a-byte idêntica
        var reconstituted = string.Concat(blocks.Select(b => b.GetText(delphiSource)));
        Assert.Equal(delphiSource, reconstituted);

        // Verifica blocos estruturais identificados
        Assert.Contains(blocks, b => b.Kind == CodeBlockKind.DelphiUnitHeader);
        Assert.Contains(blocks, b => b.Kind == CodeBlockKind.DelphiInterface);
        Assert.Contains(blocks, b => b.Kind == CodeBlockKind.DelphiMethod && b.Title.Contains("TMyService.Create"));
        Assert.Contains(blocks, b => b.Kind == CodeBlockKind.DelphiMethod && b.Title.Contains("TMyService.Destroy"));
        Assert.Contains(blocks, b => b.Kind == CodeBlockKind.DelphiMethod && b.Title.Contains("TMyService.ExecuteAction"));
        Assert.Contains(blocks, b => b.Kind == CodeBlockKind.DelphiMethod && b.Title.Contains("TMyService.Calculate"));
        Assert.Contains(blocks, b => b.Kind == CodeBlockKind.DelphiInitialization);
        Assert.Contains(blocks, b => b.Kind == CodeBlockKind.DelphiFinalization);
        Assert.Contains(blocks, b => b.Kind == CodeBlockKind.DelphiUnitEnd);
    }

    [Fact]
    public void FirebirdSqlSplitter_ReconstitutionInvariant_PreservesEntireScriptExactly()
    {
        string sqlSource = @"/* Script de inicializacao do banco Firebird */
CREATE TABLE CLIENTES (
    ID INTEGER NOT NULL PRIMARY KEY,
    NOME VARCHAR(100) NOT NULL,
    ATIVO CHAR(1) DEFAULT 'S'
);

-- Inserindo registros iniciais
INSERT INTO CLIENTES (ID, NOME, ATIVO) VALUES (1, 'Empresa Modelo', 'S');
INSERT INTO CLIENTES (ID, NOME, ATIVO) VALUES (2, 'Filial Teste; Com Ponto e Virgula', 'S');

SET TERM ^ ;

CREATE OR ALTER PROCEDURE SP_ATIVAR_CLIENTE (
    A_ID INTEGER
)
AS
BEGIN
    UPDATE CLIENTES
    SET ATIVO = 'S'
    WHERE ID = :A_ID;
END ^

CREATE OR ALTER TRIGGER TRG_CLIENTES_BI FOR CLIENTES
ACTIVE BEFORE INSERT POSITION 0
AS
BEGIN
    IF (NEW.ID IS NULL) THEN
        NEW.ID = GEN_ID(GEN_CLIENTES_ID, 1);
END ^

SET TERM ; ^

SELECT * FROM CLIENTES WHERE ATIVO = 'S';
";

        var doc = new SourceDocument
        {
            Name = "script.sql",
            Text = sqlSource,
            Language = SourceLanguage.SqlFirebird,
            SplitLevel = 3
        };

        var blocks = FirebirdSqlSplitter.Split(doc, 3);

        Assert.NotEmpty(blocks);

        // Verificação invariante inviolável: reconstituição integral
        var reconstituted = string.Concat(blocks.Select(b => b.GetText(sqlSource)));
        Assert.Equal(sqlSource, reconstituted);

        // Verifica que o PSQL com ponto e vírgula interno não foi quebrado indevidamente
        var procBlock = blocks.FirstOrDefault(b => b.Kind == CodeBlockKind.SqlProcedure);
        Assert.NotNull(procBlock);
        string procText = procBlock.GetText(sqlSource);
        Assert.Contains("CREATE OR ALTER PROCEDURE SP_ATIVAR_CLIENTE", procText);
        Assert.Contains("WHERE ID = :A_ID;", procText);
        Assert.Contains("END ^", procText);
    }

    [Fact]
    public void ManualSplitAndMerge_WorkCorrectly()
    {
        string text = "Linha 1\nLinha 2\nLinha 3\nLinha 4\n";
        var doc = new SourceDocument
        {
            Name = "Doc.txt",
            Text = text,
            Language = SourceLanguage.PlainText,
            SplitLevel = 1
        };

        var manager = new DocumentManager();
        manager.Documents.Add(doc);
        manager.CurrentDocument = doc;
        manager.ReSplitCurrentDocument();

        Assert.Single(doc.Blocks);

        // Divisão no meio
        int midOffset = text.IndexOf("Linha 3");
        bool split = manager.SplitCurrentBlockAtCaret(midOffset);
        Assert.True(split);
        Assert.Equal(2, doc.Blocks.Count);

        // Reconstituição após divisão manual
        string recon = string.Concat(doc.Blocks.Select(b => b.GetText(text)));
        Assert.Equal(text, recon);

        // Mesclar de volta
        bool merged = manager.MergeBlockWithNext(doc.Blocks[0]);
        Assert.True(merged);
        Assert.Single(doc.Blocks);
        Assert.Equal(text, doc.Blocks[0].GetText(text));
    }

    [Fact]
    public void FileTextReader_DetectsEncodings_AndReadsCorrectly()
    {
        using var dir = new TempDir();

        // 1. UTF-8 com BOM
        string utf8Path = dir.File("utf8bom.pas");
        File.WriteAllText(utf8Path, "unit TestUtf8; // Olá Acentuação", Encoding.UTF8);
        var resUtf8 = FileTextReader.ReadFile(utf8Path);
        Assert.Contains("Olá Acentuação", resUtf8.Text);

        // 2. ANSI / Windows-1252
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var win1252 = Encoding.GetEncoding(1252);
        string ansiPath = dir.File("ansi.pas");
        File.WriteAllText(ansiPath, "unit TestAnsi; // Configuração & Parâmetros", win1252);
        var resAnsi = FileTextReader.ReadFile(ansiPath);
        Assert.Contains("Configuração & Parâmetros", resAnsi.Text);
    }

    [Fact]
    public void DocumentPersistenceService_SavesAndLoadsCorrectly()
    {
        using var dir = new TempDir();
        string storePath = dir.File("documents.json");
        var persistence = new DocumentPersistenceService(storePath);

        var docs = new List<SourceDocument>
        {
            new()
            {
                Name = "Test.pas",
                Text = "unit Test;\ninterface\nimplementation\nend.",
                Language = SourceLanguage.Delphi,
                SplitLevel = 2,
                Blocks = new()
                {
                    new() { StartOffset = 0, EndOffset = 11, Title = "Unit Header", Kind = CodeBlockKind.DelphiUnitHeader, StartLine = 1, EndLine = 1 }
                }
            }
        };

        persistence.Save(docs);

        var loaded = persistence.Load();
        Assert.Single(loaded);
        Assert.Equal("Test.pas", loaded[0].Name);
        Assert.Equal(SourceLanguage.Delphi, loaded[0].Language);
        Assert.Equal(2, loaded[0].SplitLevel);
        Assert.Single(loaded[0].Blocks);
        Assert.Equal("Unit Header", loaded[0].Blocks[0].Title);
    }
}
