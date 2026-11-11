using System.IO;
using System.Windows;
using VmTyper.Models;
using Xunit;

namespace VmTyper.Tests;

/// <summary>
/// Cenários de diagnóstico do editor inteligente na janela real. Todos rodam na thread de UI compartilhada
/// (<see cref="UiTestHost"/>), com os mesmos recursos do App.xaml.
/// </summary>
[Trait("Category", "Unit")]
public sealed class MainWindowDiagnosticTests
{
    [Fact]
    public void MainWindow_InstantiatesAndCanToggleSmartEditorMode()
    {
        UiTestHost.Run(() =>
        {
            var (win, vm, dir) = UiTestKit.CreateWindow();
            using (dir)
            {
                Assert.NotNull(win.CodeEditor);

                vm.IsSmartEditorMode = true;
                Assert.True(vm.IsSmartEditorMode);
                Assert.False(vm.IsSimpleQueueMode);

                var smartGrid = (UIElement)win.FindName("SmartEditorGrid");
                Assert.NotNull(smartGrid);
                Assert.Equal(Visibility.Visible, smartGrid.Visibility);

                win.Show();
                UiTestHost.Settle(win);

                Assert.Equal(Visibility.Visible, win.CodeEditor.Visibility);
                Assert.True(win.CodeEditor.ActualWidth > 0, $"CodeEditor.ActualWidth = {win.CodeEditor.ActualWidth}");
                Assert.True(win.CodeEditor.ActualHeight > 0, $"CodeEditor.ActualHeight = {win.CodeEditor.ActualHeight}");
                Assert.True(win.CodeEditor.TextEditor.ActualWidth > 0, $"TextEditor.ActualWidth = {win.CodeEditor.TextEditor.ActualWidth}");
                Assert.True(win.CodeEditor.TextEditor.ActualHeight > 0, $"TextEditor.ActualHeight = {win.CodeEditor.TextEditor.ActualHeight}");

                win.Close();

                vm.IsSmartEditorMode = false;
                Assert.False(vm.IsSmartEditorMode);
                Assert.True(vm.IsSimpleQueueMode);
            }
        });
    }

    [Fact]
    public void ScenarioA_EmptyInitialization_EditorIsVisibleWithLineNumbersAndReadyForEditing()
    {
        UiTestHost.Run(() =>
        {
            var (win, vm, dir) = UiTestKit.CreateWindow();
            using (dir)
            {
                vm.IsSmartEditorMode = true;
                win.Show();
                UiTestHost.Settle(win);

                Assert.NotNull(vm.DocManager.CurrentDocument);
                Assert.Equal(Visibility.Visible, win.CodeEditor.Visibility);
                Assert.True(win.CodeEditor.TextEditor.ShowLineNumbers);

                // Testa digitação / colagem direta no editor
                const string testCode = "begin\n  Writeln('Linha 2');\nend.";
                win.CodeEditor.Text = testCode;

                Assert.Equal(testCode, vm.DocManager.CurrentDocument!.Text);
                Assert.Equal(3, vm.DocManager.CurrentDocument.LineCount);
                Assert.True(vm.DocManager.CurrentDocument.CharacterCount > 0);

                win.Close();
            }
        });
    }

    [Fact]
    public void ScenarioB_RepeatedModeToggling_NoBlankScreenNoFreezePreservesState()
    {
        UiTestHost.Run(() =>
        {
            var (win, vm, dir) = UiTestKit.CreateWindow();
            using (dir)
            {
                win.Show();
                UiTestHost.Settle(win);

                var smartGrid = (UIElement)win.FindName("SmartEditorGrid");
                var queuePanel = (UIElement)win.FindName("SimpleQueuePanel");
                Assert.NotNull(smartGrid);
                Assert.NotNull(queuePanel);

                // Inicia em modo fila simples
                Assert.Equal(Visibility.Visible, queuePanel.Visibility);
                Assert.Equal(Visibility.Collapsed, smartGrid.Visibility);

                vm.IsSmartEditorMode = true;
                UiTestHost.Settle(win);
                const string persistedText = "procedure DoSomething;\nbegin\nend;";
                win.CodeEditor.Text = persistedText;

                // Alterna repetidamente 5 vezes entre Fila Simples e Editor Inteligente
                for (int i = 0; i < 5; i++)
                {
                    vm.IsSmartEditorMode = false;
                    UiTestHost.Settle(win);
                    Assert.Equal(Visibility.Visible, queuePanel.Visibility);
                    Assert.Equal(Visibility.Collapsed, smartGrid.Visibility);

                    vm.IsSmartEditorMode = true;
                    UiTestHost.Settle(win);
                    Assert.Equal(Visibility.Collapsed, queuePanel.Visibility);
                    Assert.Equal(Visibility.Visible, smartGrid.Visibility);
                    Assert.True(win.CodeEditor.ActualWidth > 0);
                    Assert.True(win.CodeEditor.ActualHeight > 0);
                }

                // Verifica persistência intacta do texto e estado
                Assert.Equal(persistedText, win.CodeEditor.Text);
                Assert.Equal(persistedText, vm.DocManager.CurrentDocument?.Text);

                win.Close();
            }
        });
    }

    [Fact]
    public void ScenarioC_DelphiFileLoading_DetectsSyntaxHighlightingAndSmartSplit()
    {
        UiTestHost.Run(() =>
        {
            var (win, vm, dir) = UiTestKit.CreateWindow();
            using (dir)
            {
                vm.IsSmartEditorMode = true;
                win.Show();
                UiTestHost.Settle(win);

                const string delphiCode =
                    "unit TestUnit;\n\n" +
                    "interface\n\n" +
                    "// Função de validação de configuração com acentuação\n" +
                    "function ObterConfiguração: string;\n\n" +
                    "implementation\n\n" +
                    "function ObterConfiguração: string;\n" +
                    "begin\n" +
                    "  Result := 'Acentuação corrigida com sucesso!';\n" +
                    "end;\n\n" +
                    "end.\n";

                string pasFile = dir.File("TestUnit.pas");
                File.WriteAllText(pasFile, delphiCode, System.Text.Encoding.UTF8);

                var doc = vm.DocManager.OpenFile(pasFile);
                UiTestHost.Settle(win);

                Assert.Equal(SourceLanguage.Delphi, doc.Language);
                Assert.Equal(SourceLanguage.Delphi, win.CodeEditor.SourceLanguage);
                Assert.True(doc.Blocks.Count >= 2, $"Esperado ao menos 2 blocos para Delphi, obtido: {doc.Blocks.Count}");
                Assert.Equal(doc, vm.DocManager.CurrentDocument);

                win.Close();
            }
        });
    }

    [Fact]
    public void ScenarioD_SqlFileLoading_DetectsSyntaxHighlightingAndSmartSplit()
    {
        UiTestHost.Run(() =>
        {
            var (win, vm, dir) = UiTestKit.CreateWindow();
            using (dir)
            {
                vm.IsSmartEditorMode = true;
                win.Show();
                UiTestHost.Settle(win);

                const string sqlCode =
                    "-- Criação de tabela de clientes\n" +
                    "CREATE TABLE CLIENTES (\n" +
                    "  ID INTEGER NOT NULL PRIMARY KEY,\n" +
                    "  NOME VARCHAR(100) NOT NULL\n" +
                    ");\n\n" +
                    "INSERT INTO CLIENTES (ID, NOME) VALUES (1, 'João da Silva');\n\n" +
                    "COMMIT WORK;\n";

                string sqlFile = dir.File("script.sql");
                File.WriteAllText(sqlFile, sqlCode, System.Text.Encoding.UTF8);

                var doc = vm.DocManager.OpenFile(sqlFile);
                UiTestHost.Settle(win);

                Assert.Equal(SourceLanguage.SqlFirebird, doc.Language);
                Assert.Equal(SourceLanguage.SqlFirebird, win.CodeEditor.SourceLanguage);
                Assert.True(doc.Blocks.Count >= 3, $"Esperado ao menos 3 blocos para SQL, obtido: {doc.Blocks.Count}");
                Assert.Equal(doc, vm.DocManager.CurrentDocument);

                win.Close();
            }
        });
    }
}
