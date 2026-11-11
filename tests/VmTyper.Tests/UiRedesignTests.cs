using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using VmTyper.Documents;
using VmTyper.Editor;
using VmTyper.Helpers;
using VmTyper.Models;
using VmTyper.Services;
using VmTyper.Tests.Fakes;
using VmTyper.ViewModels;
using Xunit;
using Button = System.Windows.Controls.Button;

namespace VmTyper.Tests;

/// <summary>
/// Testes da reformulação de UI/UX (tema Dark/Light, layout em 5 regiões, barra de execução contextual,
/// estrutura do documento, correção do estado "Documento vazio"). Rodam na janela real (<see cref="UiTestHost"/>).
/// </summary>
[Trait("Category", "Unit")]
public sealed class UiRedesignTests
{
    private const string SqlScript =
        "-- Criação de tabela de clientes\n" +
        "CREATE TABLE CLIENTES (\n" +
        "  ID INTEGER NOT NULL PRIMARY KEY,\n" +
        "  NOME VARCHAR(100) NOT NULL\n" +
        ");\n\n" +
        "INSERT INTO CLIENTES (ID, NOME) VALUES (1, 'João da Silva');\n\n" +
        "COMMIT WORK;\n";

    private static System.Text.Encoding Windows1252
    {
        get
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            return System.Text.Encoding.GetEncoding(1252);
        }
    }

    // ------------------------------------------------------------------ Tema

    [Fact]
    public void ThemeSwitch_AtRuntime_ChangesResourcesEditorAndWindowWithoutRecreatingIt()
    {
        UiTestHost.Run(() =>
        {
            var (win, vm, dir) = UiTestKit.CreateWindow();
            using (dir)
            {
                vm.IsSmartEditorMode = true;
                win.Show();

                UiTestHost.ApplyTheme(AppTheme.Dark);
                UiTestHost.Settle(win);
                Assert.True(ThemeManager.Instance.IsDarkEffectively);
                Assert.Equal(Color.FromRgb(0x17, 0x1B, 0x24), UiTestHost.BrushColor("SurfaceBrush"));
                Assert.Equal(Color.FromRgb(0x0F, 0x11, 0x17), ((SolidColorBrush)win.Background).Color);
                Assert.Equal(UiTestHost.BrushColor("EditorBackgroundBrush"), ((SolidColorBrush)win.CodeEditor.TextEditor.Background).Color);
                Assert.Equal(UiTestHost.BrushColor("EditorForegroundBrush"), ((SolidColorBrush)win.CodeEditor.TextEditor.Foreground).Color);
                Assert.Equal(UiTestHost.BrushColor("EditorSelectionBrush"), ((SolidColorBrush)win.CodeEditor.TextEditor.TextArea.SelectionBrush).Color);

                var darkEditorBackground = ((SolidColorBrush)win.CodeEditor.TextEditor.Background).Color;

                UiTestHost.ApplyTheme(AppTheme.Light);
                UiTestHost.Settle(win);
                Assert.False(ThemeManager.Instance.IsDarkEffectively);
                Assert.Equal(Color.FromRgb(0xFF, 0xFF, 0xFF), UiTestHost.BrushColor("SurfaceBrush"));
                Assert.Equal(Color.FromRgb(0xEE, 0xF1, 0xF6), ((SolidColorBrush)win.Background).Color);
                Assert.NotEqual(darkEditorBackground, ((SolidColorBrush)win.CodeEditor.TextEditor.Background).Color);

                Assert.True(win.IsVisible);
                win.Close();
            }
        });
    }

    [Fact]
    public void ThemeButton_CyclesAutoDarkLightAuto_AndPersistsPreferenceInSettings()
    {
        UiTestHost.Run(() =>
        {
            var (win, vm, dir) = UiTestKit.CreateWindow();
            using (dir)
            {
                win.Show();
                UiTestHost.Settle(win);
                ThemeManager.Instance.ApplyTheme(AppTheme.Auto, force: true);

                var expected = new[] { AppTheme.Dark, AppTheme.Light, AppTheme.Auto };
                foreach (var theme in expected)
                {
                    win.ThemeButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    UiTestHost.Settle(win);
                    Assert.Equal(theme, ThemeManager.Instance.CurrentTheme);
                    Assert.Equal(theme, vm.Settings.Theme);
                }

                win.Close();
            }
        });
    }

    [Fact]
    public void Theme_IsPersistedAndReloaded()
    {
        using var dir = new TempDir();
        var service = new SettingsService(dir.File("settings.json"));
        var settings = service.Load();
        Assert.Equal(AppTheme.Auto, settings.Theme); // padrão: segue o Windows

        settings.Theme = AppTheme.Dark;
        Assert.True(service.Save(settings));

        var reloaded = new SettingsService(dir.File("settings.json")).Load();
        Assert.Equal(AppTheme.Dark, reloaded.Theme);
    }

    [Fact]
    public void ThemeConverter_ProvidesLabelGlyphAndTooltipForEachPreference()
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        var converter = ThemeConverter.Instance;

        Assert.Equal("Escuro", converter.Convert(AppTheme.Dark, typeof(string), null, culture));
        Assert.Equal("Claro", converter.Convert(AppTheme.Light, typeof(string), null, culture));
        Assert.Equal("Auto", converter.Convert(AppTheme.Auto, typeof(string), null, culture));

        var glyphs = new[] { AppTheme.Dark, AppTheme.Light, AppTheme.Auto }
            .Select(t => (string)converter.Convert(t, typeof(string), "glyph", culture)).ToArray();
        Assert.All(glyphs, g => Assert.Single(g));
        Assert.Equal(3, glyphs.Distinct().Count());

        Assert.Contains("Claro", (string)converter.Convert(AppTheme.Dark, typeof(string), "tooltip", culture));
    }

    [Theory]
    [InlineData(SourceLanguage.Delphi)]
    [InlineData(SourceLanguage.SqlFirebird)]
    public void SyntaxHighlighting_HasDistinctDefinitionsForLightAndDark(SourceLanguage language)
    {
        UiTestHost.Run(() =>
        {
            var light = SyntaxHighlightingManager.GetDefinition(language, isDark: false);
            var dark = SyntaxHighlightingManager.GetDefinition(language, isDark: true);

            Assert.NotNull(light);
            Assert.NotNull(dark);
            Assert.NotSame(light, dark);

            // Ao menos um destaque nomeado (palavra-chave, string, comentário…) muda de cor entre os temas.
            int different = 0;
            foreach (var lightColor in light!.NamedHighlightingColors)
            {
                var darkColor = dark!.GetNamedColor(lightColor.Name);
                if (darkColor?.Foreground is null || lightColor.Foreground is null) continue;
                var a = ((SolidColorBrush)lightColor.Foreground.GetBrush(null)!).Color;
                var b = ((SolidColorBrush)darkColor.Foreground.GetBrush(null)!).Color;
                if (a != b) different++;
            }

            Assert.True(different >= 2, $"Esperado ao menos 2 cores de sintaxe distintas entre temas; obtido {different}.");
        });
    }

    [Fact]
    public void ThemedHexConverter_LightensForegroundOnDark_AndTintsBackground()
    {
        var onLight = ThemedHexConverter.CreateBrush("#2563EB", dark: false, "fg");
        var onDark = ThemedHexConverter.CreateBrush("#2563EB", dark: true, "fg");
        Assert.Equal(Color.FromRgb(0x25, 0x63, 0xEB), onLight.Color);
        Assert.True(onDark.Color.R > onLight.Color.R && onDark.Color.G > onLight.Color.G);

        var tint = ThemedHexConverter.CreateBrush("#2563EB", dark: true, "tint");
        Assert.True(tint.Color.A < 0x80, "O fundo do badge deve ser translúcido para herdar a superfície do tema.");
        Assert.True(onDark.IsFrozen);
    }

    // ------------------------------------------------------------------ "Documento vazio" / divisão em blocos

    [Fact]
    public void EmptyDocument_DisablesPrimaryAction_AndExplainsWhy()
    {
        UiTestHost.Run(() =>
        {
            var (win, vm, dir) = UiTestKit.CreateWindow();
            using (dir)
            {
                vm.IsSmartEditorMode = true;
                win.Show();
                UiTestHost.Settle(win);

                Assert.Equal(string.Empty, vm.DocManager.CurrentDocument!.Text);
                Assert.Equal(ExecutionTarget.Document, vm.CurrentExecutionTarget);
                Assert.False(vm.CanStartPrimary);
                Assert.False(win.DigitarButton.IsEnabled);
                Assert.Contains("vazio", vm.PrimaryActionHint, StringComparison.OrdinalIgnoreCase);
                Assert.Equal("Digitar documento", vm.PrimaryActionTitle);

                win.Close();
            }
        });
    }

    [Fact]
    public void TypingInEditor_UpdatesBlocksAndEnablesPrimaryAction_AfterDebounce()
    {
        UiTestHost.Run(() =>
        {
            var (win, vm, dir) = UiTestKit.CreateWindow();
            using (dir)
            {
                vm.IsSmartEditorMode = true;
                vm.DocManager.SplitDebounce = TimeSpan.FromMilliseconds(40);
                win.Show();
                UiTestHost.Settle(win);
                Assert.False(vm.CanStartPrimary);

                // O editor grava direto em SourceDocument.Text; os blocos devem acompanhar sozinhos.
                win.CodeEditor.Text = SqlScript;
                Assert.True(vm.DocManager.HasPendingSplit);

                UiTestHost.Pump(TimeSpan.FromMilliseconds(500));

                Assert.False(vm.DocManager.HasPendingSplit);
                var doc = vm.DocManager.CurrentDocument!;
                Assert.True(doc.Blocks.Count >= 3, $"Esperado ao menos 3 blocos, obtido {doc.Blocks.Count}");
                Assert.True(doc.Language == SourceLanguage.SqlFirebird,
                    $"Linguagem={doc.Language}, manual={doc.IsManualLanguage}, combo={win.CodeEditor.SourceLanguage}");
                Assert.True(vm.CanStartPrimary);
                Assert.True(win.DigitarButton.IsEnabled);
                Assert.True(vm.StartDocumentCommand.CanExecute(null));

                win.Close();
            }
        });
    }

    [Fact]
    public void FlushPendingSplit_AppliesEditImmediately_SoTheActiveBlockMatchesTheText()
    {
        UiTestHost.Run(() =>
        {
            var (win, vm, dir) = UiTestKit.CreateWindow();
            using (dir)
            {
                vm.IsSmartEditorMode = true;
                win.Show();
                UiTestHost.Settle(win);

                win.CodeEditor.Text = SqlScript;
                vm.DocManager.FlushPendingSplit();

                var doc = vm.DocManager.CurrentDocument!;
                Assert.False(vm.DocManager.HasPendingSplit);
                Assert.All(doc.Blocks, b => Assert.True(b.EndOffset <= doc.Text.Length));
                Assert.False(string.IsNullOrWhiteSpace(vm.DocManager.SelectedBlock!.GetText(doc.Text)));

                // Editar de novo e achatar de novo: os intervalos acompanham o texto novo.
                win.CodeEditor.Text = SqlScript + "\nSELECT 1 FROM RDB$DATABASE;\n";
                vm.DocManager.FlushPendingSplit();
                Assert.True(doc.Blocks.Max(b => b.EndOffset) <= doc.Text.Length);
                Assert.Contains(doc.Blocks, b => b.GetText(doc.Text).Contains("RDB$DATABASE"));

                win.Close();
            }
        });
    }

    [Fact]
    public void SwitchingDocuments_KeepsEachDocumentTextAndBlocks()
    {
        UiTestHost.Run(() =>
        {
            var (win, vm, dir) = UiTestKit.CreateWindow();
            using (dir)
            {
                vm.IsSmartEditorMode = true;
                win.Show();
                UiTestHost.Settle(win);

                var first = vm.DocManager.CurrentDocument!;
                win.CodeEditor.Text = SqlScript;

                var second = vm.DocManager.CreateNewDocument("SELECT 2 FROM RDB$DATABASE;", "Documento 2");
                UiTestHost.Settle(win);
                Assert.Equal(second, vm.DocManager.CurrentDocument);
                Assert.Equal("SELECT 2 FROM RDB$DATABASE;", win.CodeEditor.Text);

                // Trocar de aba não perde a divisão pendente do documento anterior.
                vm.DocManager.CurrentDocument = first;
                UiTestHost.Settle(win);
                Assert.Equal(SqlScript, win.CodeEditor.Text);
                Assert.True(first.Blocks.Count >= 3);
                Assert.Equal(2, vm.DocManager.Documents.Count);

                win.Close();
            }
        });
    }

    // ------------------------------------------------------------------ Barra de execução contextual

    [Fact]
    public void PrimaryAction_TracksDocumentBlockSelectionAndQueue()
    {
        UiTestHost.Run(() =>
        {
            var (win, vm, dir) = UiTestKit.CreateWindow();
            using (dir)
            {
                vm.IsSmartEditorMode = true;
                win.Show();
                UiTestHost.Settle(win);

                win.CodeEditor.Text = SqlScript;
                vm.DocManager.FlushPendingSplit();
                var doc = vm.DocManager.CurrentDocument!;
                Assert.True(doc.Blocks.Count > 1);

                // Vários blocos + bloco ativo => digita o bloco.
                Assert.Equal(ExecutionTarget.Block, vm.CurrentExecutionTarget);
                Assert.StartsWith("Digitar bloco", vm.PrimaryActionTitle);
                Assert.True(vm.CanStartBlock);

                // Seleção arbitrária no editor => digita a seleção, e o rótulo diz quantos caracteres.
                vm.EditorSelectionText = "CREATE";
                Assert.Equal(ExecutionTarget.Selection, vm.CurrentExecutionTarget);
                Assert.Equal("Digitar 6 caracteres selecionados", vm.PrimaryActionTitle);
                Assert.True(vm.CanStartSelection);

                // Seleção idêntica ao bloco ativo (ex.: após clicar na estrutura) continua sendo "bloco".
                vm.EditorSelectionText = vm.DocManager.SelectedBlock!.GetText(doc.Text);
                Assert.Equal(ExecutionTarget.Block, vm.CurrentExecutionTarget);

                // Sem seleção e com um único bloco => documento.
                vm.EditorSelectionText = string.Empty;
                vm.DocManager.CurrentDocument = vm.DocManager.CreateNewDocument("SELECT 1 FROM RDB$DATABASE;", "Curto");
                vm.DocManager.FlushPendingSplit();
                Assert.Equal(ExecutionTarget.Document, vm.CurrentExecutionTarget);
                Assert.Equal("Digitar documento", vm.PrimaryActionTitle);

                // Modo Fila: a ação primária é a fila e não depende do editor.
                vm.IsSmartEditorMode = false;
                Assert.Equal(ExecutionTarget.Queue, vm.CurrentExecutionTarget);

                win.Close();
            }
        });
    }

    [Fact]
    public void QueueMode_PrimaryActionNamesNextBlock_AndMenuButtonIsHidden()
    {
        UiTestHost.Run(() =>
        {
            var (win, vm, dir) = UiTestKit.CreateWindow();
            using (dir)
            {
                win.Show();
                UiTestHost.Settle(win);
                Assert.True(vm.IsSimpleQueueMode);
                Assert.Equal(Visibility.Collapsed, win.DigitarMenuButton.Visibility);
                Assert.False(vm.CanStartPrimary);

                vm.AddBlock();
                vm.Blocks[0].Text = "linha 1";
                UiTestHost.Settle(win);

                Assert.Equal(ExecutionTarget.Queue, vm.CurrentExecutionTarget);
                Assert.True(vm.CanStartPrimary);
                Assert.Equal("Digitar bloco 1", vm.PrimaryActionTitle);
                Assert.Equal("DIGITAR BLOCO 1", vm.DigitarButtonText); // texto legado preservado

                vm.IsSmartEditorMode = true;
                UiTestHost.Settle(win);
                Assert.Equal(Visibility.Visible, win.DigitarMenuButton.Visibility);

                win.Close();
            }
        });
    }

    [Fact]
    public void PauseAndCancel_AreAlwaysVisible_AndDisabledWhenIdleWithExplanation()
    {
        UiTestHost.Run(() =>
        {
            var (win, vm, dir) = UiTestKit.CreateWindow();
            using (dir)
            {
                vm.IsSmartEditorMode = true;
                win.Show();
                UiTestHost.Settle(win);

                foreach (var button in new[] { win.PauseResumeButton, win.CancelButton, win.DigitarButton })
                {
                    Assert.Equal(Visibility.Visible, button.Visibility);
                    Assert.True(button.IsTabStop);
                    Assert.False(string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetName(button)));
                    Assert.True(ToolTipService.GetShowOnDisabled(button));
                    Assert.NotNull(button.ToolTip);
                }

                Assert.False(win.PauseResumeButton.IsEnabled);
                Assert.False(win.CancelButton.IsEnabled);

                win.Close();
            }
        });
    }

    // ------------------------------------------------------------------ Painéis e estrutura

    [Fact]
    public void StatusPanel_CollapsesAndRestoresWithTheToolbarButton()
    {
        UiTestHost.Run(() =>
        {
            var (win, vm, dir) = UiTestKit.CreateWindow();
            using (dir)
            {
                win.Show();
                UiTestHost.Settle(win);

                Assert.True(win.StatusColumn.Width.Value > 0);
                double original = win.StatusColumn.Width.Value;

                win.ToggleStatusButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                UiTestHost.Settle(win);
                Assert.Equal(0, win.StatusColumn.Width.Value);
                Assert.Equal(Visibility.Collapsed, win.StatusSplitter.Visibility);

                win.ToggleStatusButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                UiTestHost.Settle(win);
                Assert.Equal(original, win.StatusColumn.Width.Value);
                Assert.Equal(Visibility.Visible, win.StatusSplitter.Visibility);

                win.Close();
            }
        });
    }

    [Fact]
    public void StructurePanel_CollapsesAndRestores_AndEditorKeepsRoom()
    {
        UiTestHost.Run(() =>
        {
            var (win, vm, dir) = UiTestKit.CreateWindow();
            using (dir)
            {
                vm.IsSmartEditorMode = true;
                win.Show();
                UiTestHost.Settle(win);
                double editorWithPanel = win.CodeEditor.ActualWidth;

                win.ToggleStructureButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                UiTestHost.Settle(win);
                Assert.Equal(0, win.StructureColumn.Width.Value);
                Assert.True(win.CodeEditor.ActualWidth > editorWithPanel, "O editor deve ocupar o espaço liberado.");

                win.ToggleStructureButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                UiTestHost.Settle(win);
                Assert.True(win.StructureColumn.Width.Value > 0);

                win.Close();
            }
        });
    }

    [Fact]
    public void StructureNavigator_ListsBlocks_AndClickSelectsTheSectionInTheEditor()
    {
        UiTestHost.Run(() =>
        {
            var (win, vm, dir) = UiTestKit.CreateWindow();
            using (dir)
            {
                vm.IsSmartEditorMode = true;
                win.Show();
                UiTestHost.Settle(win);

                win.CodeEditor.Text = SqlScript;
                vm.DocManager.FlushPendingSplit();
                UiTestHost.Settle(win);

                var doc = vm.DocManager.CurrentDocument!;
                Assert.Equal(doc.Blocks.Count, win.StructureList.Items.Count);
                Assert.True(win.StructureList.HasItems);

                var target = doc.Blocks[^1];
                typeof(MainWindow).GetMethod("NavigateToBlock", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(win, new object[] { target });
                UiTestHost.Settle(win);

                Assert.Equal(target, vm.DocManager.SelectedBlock);
                Assert.Equal(target, win.StructureList.SelectedItem);
                Assert.Equal(target.GetText(doc.Text), win.CodeEditor.TextEditor.SelectedText);

                win.Close();
            }
        });
    }

    [Theory]
    [InlineData(1100, true)]
    [InlineData(1179, true)]
    [InlineData(1180, false)]
    [InlineData(1500, false)]
    public void CompactMode_FollowsWindowWidth(double width, bool compact)
    {
        UiTestHost.Run(() =>
        {
            var (win, _, dir) = UiTestKit.CreateWindow();
            using (dir)
            {
                win.Width = width;
                win.Height = 700;
                win.Show();
                UiTestHost.Settle(win);
                Assert.Equal(compact, win.IsCompact);
                win.Close();
            }
        });
    }

    // ------------------------------------------------------------------ Atalhos

    [Theory]
    [InlineData(Key.O, ModifierKeys.Control, true, MainWindow.WindowShortcut.OpenFile)]
    [InlineData(Key.N, ModifierKeys.Control, true, MainWindow.WindowShortcut.NewDocument)]
    [InlineData(Key.S, ModifierKeys.Control, true, MainWindow.WindowShortcut.Save)]
    [InlineData(Key.S, ModifierKeys.Control | ModifierKeys.Shift, true, MainWindow.WindowShortcut.SaveAs)]
    [InlineData(Key.W, ModifierKeys.Control, true, MainWindow.WindowShortcut.CloseDocument)]
    [InlineData(Key.F, ModifierKeys.Control, true, MainWindow.WindowShortcut.Find)]
    [InlineData(Key.G, ModifierKeys.Control, true, MainWindow.WindowShortcut.GoToLine)]
    [InlineData(Key.E, ModifierKeys.Control | ModifierKeys.Shift, true, MainWindow.WindowShortcut.ToggleStructurePanel)]
    [InlineData(Key.L, ModifierKeys.Control | ModifierKeys.Shift, true, MainWindow.WindowShortcut.ToggleStatusPanel)]
    [InlineData(Key.L, ModifierKeys.Control | ModifierKeys.Shift, false, MainWindow.WindowShortcut.ToggleStatusPanel)]
    [InlineData(Key.O, ModifierKeys.Control, false, MainWindow.WindowShortcut.None)]   // modo Fila: não compete com a edição dos blocos
    [InlineData(Key.S, ModifierKeys.None, true, MainWindow.WindowShortcut.None)]       // sem Ctrl nunca dispara
    [InlineData(Key.S, ModifierKeys.Control | ModifierKeys.Alt, true, MainWindow.WindowShortcut.None)]
    public void KeyboardShortcuts_ResolveAsDocumented(Key key, ModifierKeys modifiers, bool smartMode, MainWindow.WindowShortcut expected)
    {
        Assert.Equal(expected, MainWindow.ResolveShortcut(key, modifiers, smartMode));
    }

    // ------------------------------------------------------------------ Documentos: salvar / indicador de alterações

    [Fact]
    public void Document_TracksUnsavedChanges_AndSavingPreservesAnsiEncoding()
    {
        UiTestHost.Run(() =>
        {
            using var dir = new TempDir();
            var manager = new DocumentManager(new DocumentPersistenceService(dir.File("documents.json")));

            var doc = manager.CreateNewDocument("", "Novo");
            Assert.False(doc.IsModified, "Documento novo e vazio não é 'modificado'.");

            doc.Text = "ação";
            Assert.True(doc.IsModified);

            // Arquivo Windows-1252 (ANSI Delphi): regravar preserva a codificação original.
            string path = dir.File("ansi.pas");
            File.WriteAllBytes(path, Windows1252.GetBytes("ação"));
            var opened = manager.OpenFile(path);
            Assert.Equal("Windows-1252", opened.EncodingName);
            Assert.False(opened.IsModified);

            opened.Text = "ação!";
            Assert.True(opened.IsModified);

            Assert.True(manager.SaveDocument(opened, null, out string? error), error);
            Assert.False(opened.IsModified);
            Assert.Equal(new byte[] { 0x61, 0xE7, 0xE3, 0x6F, 0x21 }, File.ReadAllBytes(path));
        });
    }

    [Fact]
    public void Save_FallsBackToUtf8Bom_WhenAnsiCannotRepresentTheText()
    {
        UiTestHost.Run(() =>
        {
            using var dir = new TempDir();
            var manager = new DocumentManager(new DocumentPersistenceService(dir.File("documents.json")));

            string path = dir.File("ansi.pas");
            // "abç" em Windows-1252 não é UTF-8 válido, então o arquivo é aberto como ANSI.
            File.WriteAllBytes(path, Windows1252.GetBytes("abç"));
            var doc = manager.OpenFile(path);
            Assert.Equal("Windows-1252", doc.EncodingName);
            doc.Text = "abc ✓ 日本"; // não existe em Windows-1252

            Assert.True(manager.SaveDocument(doc, null, out _));
            Assert.Equal("UTF-8-BOM", doc.EncodingName);
            var bytes = File.ReadAllBytes(path);
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
            Assert.Equal("abc ✓ 日本", File.ReadAllText(path));
        });
    }

    // ------------------------------------------------------------------ Layout responsivo + captura visual

    public static IEnumerable<object[]> LayoutCases()
    {
        // (largura, altura) em unidades independentes de dispositivo:
        //   880×560   mínimo suportado
        //   1024×728  tela 1024×768 a 100% (menos barra de tarefas)
        //   1093×614  tela 1366×768 a 125%
        //   1366×728  tela 1366×768 a 100%
        //   1280×680  tela 1920×1080 a 150%
        //   1920×1040 tela 1920×1080 a 100%
        var sizes = new (int W, int H)[] { (880, 560), (1024, 728), (1093, 614), (1366, 728), (1280, 680), (1920, 1040) };
        foreach (var (w, h) in sizes)
        {
            foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            {
                yield return new object[] { w, h, theme };
            }
        }
    }

    [Theory]
    [MemberData(nameof(LayoutCases))]
    public void Layout_FitsWithoutOverlapOrClipping_AndRendersEachThemeCorrectly(int width, int height, AppTheme theme)
    {
        UiTestHost.Run(() =>
        {
            var (win, vm, dir) = UiTestKit.CreateWindow();
            using (dir)
            {
                vm.IsSmartEditorMode = true;
                win.Width = width;
                win.Height = height;
                win.Show();
                UiTestHost.ApplyTheme(theme);

                win.CodeEditor.Text = SqlScript;
                vm.DocManager.FlushPendingSplit();
                UiTestHost.Settle(win);

                var root = win.LayoutRoot;
                Rect Bounds(FrameworkElement e) => e.TransformToAncestor(root).TransformBounds(new Rect(e.RenderSize));

                // --- Barra superior: nada se sobrepõe nem sai da janela
                var top = new (string, Rect)[]
                {
                    ("Motor", Bounds(win.EngineToggle)),
                    ("Destino", Bounds(win.TargetIndicatorLabel)),
                    ("Diagnóstico", Bounds(win.DiagnosticsButton)),
                    ("Configurações", Bounds(win.SettingsButton)),
                    ("Status", Bounds(win.ToggleStatusButton)),
                    ("Tema", Bounds(win.ThemeButton)),
                };
                AssertNoOverlap(top);
                AssertInside(top, root.ActualWidth, root.ActualHeight);
                Assert.True(win.TargetIndicatorLabel.ActualWidth >= 40, "O destino precisa de espaço mínimo legível.");

                // --- Barra de execução: botões lado a lado e progresso depois deles
                var bottom = new (string, Rect)[]
                {
                    ("Digitar", Bounds(win.DigitarButton)),
                    ("Escolher alvo", Bounds(win.DigitarMenuButton)),
                    ("Pausar", Bounds(win.PauseResumeButton)),
                    ("Cancelar", Bounds(win.CancelButton)),
                    ("Progresso", Bounds(win.ExecutionProgress)),
                };
                AssertNoOverlap(bottom);
                AssertInside(bottom, root.ActualWidth, root.ActualHeight);
                Assert.True(win.ExecutionProgress.ActualWidth >= 100, $"Progresso comprimido demais: {win.ExecutionProgress.ActualWidth}");

                // --- Editor com área útil
                Assert.True(win.CodeEditor.ActualWidth >= 220, $"Editor estreito: {win.CodeEditor.ActualWidth}");
                Assert.True(win.CodeEditor.ActualHeight >= 120, $"Editor baixo: {win.CodeEditor.ActualHeight}");
                Assert.True(Bounds(win.CodeEditor).Bottom <= Bounds(win.DigitarButton).Top, "O editor não pode invadir a barra de execução.");

                // --- Renderização real: superfície da barra superior com a cor do tema
                var (pixels, w, h, bitmap) = UiTestHost.Render(root);
                var expectedSurface = UiTestHost.BrushColor("SurfaceBrush");
                var sample = UiTestHost.PixelAt(pixels, w, 3, 3);
                Assert.Equal(expectedSurface.R, sample.R);
                Assert.Equal(expectedSurface.G, sample.G);
                Assert.Equal(expectedSurface.B, sample.B);

                var expectedBackground = UiTestHost.BrushColor("AppBackgroundBrush");
                var editorBounds = Bounds(win.CodeEditor);
                var editorPixel = UiTestHost.PixelAt(pixels, w, (int)editorBounds.Right - 30, (int)editorBounds.Top + 30);
                var expectedEditor = UiTestHost.BrushColor("EditorBackgroundBrush");
                Assert.True(Math.Abs(editorPixel.R - expectedEditor.R) <= 24 && Math.Abs(editorPixel.G - expectedEditor.G) <= 24 && Math.Abs(editorPixel.B - expectedEditor.B) <= 24,
                    $"Pixel do editor {editorPixel} difere do fundo do tema {expectedEditor}.");
                Assert.NotEqual(expectedBackground, expectedEditor);

                // Imagem com variedade real de cores (não é uma tela em branco).
                Assert.True(CountDistinctColors(pixels) > 40, "Renderização aparentemente em branco.");

                if (UiTestHost.ScreenshotDirectory is { } shots)
                {
                    UiTestHost.SavePng(bitmap, Path.Combine(shots, $"editor-{theme.ToString().ToLowerInvariant()}-{width}x{height}.png"));
                }

                win.Close();
            }
        });
    }

    [Theory]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.Dark)]
    public void QueueMode_RendersWithCards_ForEachTheme(AppTheme theme)
    {
        UiTestHost.Run(() =>
        {
            var (win, vm, dir) = UiTestKit.CreateWindow();
            using (dir)
            {
                win.Width = 1280;
                win.Height = 760;
                win.Show();
                UiTestHost.ApplyTheme(theme);

                vm.AddBlock();
                vm.Blocks[0].Text = "SELECT * FROM CLIENTES;";
                vm.AddBlock();
                vm.Blocks[1].Text = "COMMIT WORK;";
                UiTestHost.Settle(win);

                Assert.Equal(Visibility.Visible, win.SimpleQueuePanel.Visibility);
                Assert.True(win.QueueBlocksItemsControl.ActualHeight > 100);

                var (pixels, _, _, bitmap) = UiTestHost.Render(win.LayoutRoot);
                Assert.True(CountDistinctColors(pixels) > 40);

                if (UiTestHost.ScreenshotDirectory is { } shots)
                {
                    UiTestHost.SavePng(bitmap, Path.Combine(shots, $"fila-{theme.ToString().ToLowerInvariant()}-1280x760.png"));
                }

                win.Close();
            }
        });
    }

    [Theory]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.Dark)]
    public void SecondaryWindows_ShareTheTheme_AndRender(AppTheme theme)
    {
        UiTestHost.Run(() =>
        {
            using var dir = new TempDir();
            UiTestHost.ApplyTheme(theme);

            var settingsWindow = new VmTyper.Views.SettingsWindow(new SettingsService(dir.File("settings.json")));
            settingsWindow.Show();
            UiTestHost.Settle(settingsWindow);
            Assert.Equal(UiTestHost.BrushColor("AppBackgroundBrush"), ((SolidColorBrush)settingsWindow.Background).Color);
            var settingsShot = UiTestHost.Render((FrameworkElement)settingsWindow.Content, background: settingsWindow.Background);
            Assert.True(CountDistinctColors(settingsShot.Pixels) > 20);

            using var harness = new EngineHarness();
            var hotkeys = new FakeHotkeyRegistrar();
            var diagnostics = new VmTyper.Views.DiagnosticsWindow(harness.Engine, harness.Log, new TypingSessionCoordinator(hotkeys, harness.Log));
            diagnostics.Show();
            UiTestHost.Settle(diagnostics);
            Assert.Equal(UiTestHost.BrushColor("AppBackgroundBrush"), ((SolidColorBrush)diagnostics.Background).Color);
            var diagnosticsShot = UiTestHost.Render((FrameworkElement)diagnostics.Content, background: diagnostics.Background);
            Assert.True(CountDistinctColors(diagnosticsShot.Pixels) > 20);

            if (UiTestHost.ScreenshotDirectory is { } shots)
            {
                string name = theme.ToString().ToLowerInvariant();
                UiTestHost.SavePng(settingsShot.Bitmap, Path.Combine(shots, $"configuracoes-{name}.png"));
                UiTestHost.SavePng(diagnosticsShot.Bitmap, Path.Combine(shots, $"diagnostico-{name}.png"));
            }

            settingsWindow.Close();
            diagnostics.Close();
        });
    }

    // ------------------------------------------------------------------ Regressão: texto dentro de abas herda a cor do tema

    [Theory]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.Dark)]
    public void TabContent_PlainText_InheritsThemeForeground(AppTheme theme)
    {
        UiTestHost.Run(() =>
        {
            using var dir = new TempDir();
            UiTestHost.ApplyTheme(theme);

            var window = new VmTyper.Views.SettingsWindow(new SettingsService(dir.File("settings.json")));
            window.Show();
            UiTestHost.Settle(window);

            // O estilo padrão do tema fixa Foreground preto em TabControl/TabItem; sem sobrescrevê-lo, texto simples
            // dentro das abas ficava preto sobre fundo escuro (e azul/menor quando herdava o estilo do cabeçalho).
            var plainText = FindVisual<TextBlock>(window, t => t.Text.StartsWith("Presets", StringComparison.Ordinal));
            Assert.NotNull(plainText);
            Assert.Equal(UiTestHost.BrushColor("TextPrimaryBrush"), ((SolidColorBrush)plainText!.Foreground).Color);
            Assert.Equal(13, plainText.FontSize);

            window.Close();
        });
    }

    private static T? FindVisual<T>(DependencyObject root, Func<T, bool> predicate) where T : DependencyObject
    {
        if (root is T match && predicate(match)) return match;
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var found = FindVisual(VisualTreeHelper.GetChild(root, i), predicate);
            if (found is not null) return found;
        }
        return null;
    }

    // ------------------------------------------------------------------ Estados da barra de execução / painel de status

    [Theory]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.Dark)]
    public void ExecutionStates_EmptyDocumentHint_AndTypingInProgress_RenderLegibly(AppTheme theme)
    {
        UiTestHost.Run(() =>
        {
            var (win, vm, dir) = UiTestKit.CreateWindow();
            using (dir)
            {
                vm.IsSmartEditorMode = true;
                win.Width = 1280;
                win.Height = 720;
                win.Show();
                UiTestHost.ApplyTheme(theme);
                UiTestHost.Settle(win);

                // 1) Documento vazio: ação desabilitada e o motivo aparece na barra (nada de botão mudo).
                Assert.False(vm.CanStartPrimary);
                var (_, _, _, emptyShot) = UiTestHost.Render(win.LayoutRoot);

                // 1b) Painel de busca do editor (Ctrl+F) aberto, para validar o tema do SearchPanel do AvalonEdit.
                win.CodeEditor.Text = SqlScript;
                vm.DocManager.FlushPendingSplit();
                win.CodeEditor.OpenSearch();
                UiTestHost.Settle(win);
                var (_, _, _, searchShot) = UiTestHost.Render(win.LayoutRoot);

                // 2) Digitação em andamento: Pausar/Cancelar habilitados, primária desabilitada, progresso visível.
                vm.DocManager.FlushPendingSplit();
                vm.TotalCharacters = 1000;
                vm.TypedCharacters = 420;
                vm.PercentComplete = 42;
                vm.ElapsedText = "00:12";
                vm.CpsText = "35";
                vm.State = TypingState.Digitando;
                vm.StatusMessage = "Digitando bloco 2 de 4…";
                win.SessionDetailsExpander.IsExpanded = true;
                UiTestHost.Settle(win);

                Assert.True(vm.IsBusy);
                Assert.False(win.DigitarButton.IsEnabled);
                Assert.True(win.PauseResumeButton.IsEnabled);
                Assert.True(win.CancelButton.IsEnabled);
                Assert.Equal(42, win.ExecutionProgress.Value);

                var (pixels, _, _, typingShot) = UiTestHost.Render(win.LayoutRoot);
                Assert.True(CountDistinctColors(pixels) > 40);

                if (UiTestHost.ScreenshotDirectory is { } shots)
                {
                    string name = theme.ToString().ToLowerInvariant();
                    UiTestHost.SavePng(emptyShot, Path.Combine(shots, $"estado-documento-vazio-{name}.png"));
                    UiTestHost.SavePng(searchShot, Path.Combine(shots, $"editor-busca-aberta-{name}.png"));
                    UiTestHost.SavePng(typingShot, Path.Combine(shots, $"estado-digitando-{name}.png"));
                }

                vm.State = TypingState.Pronto;
                win.Close();
            }
        });
    }

    // ------------------------------------------------------------------ Utilidades

    private static void AssertNoOverlap(params (string Name, Rect Bounds)[] items)
    {
        for (int i = 0; i < items.Length; i++)
        {
            for (int j = i + 1; j < items.Length; j++)
            {
                var overlap = Rect.Intersect(items[i].Bounds, items[j].Bounds);
                bool overlaps = !overlap.IsEmpty && overlap.Width > 0.5 && overlap.Height > 0.5;
                Assert.False(overlaps, $"'{items[i].Name}' {items[i].Bounds} sobrepõe '{items[j].Name}' {items[j].Bounds}.");
            }
        }
    }

    private static void AssertInside((string Name, Rect Bounds)[] items, double width, double height)
    {
        foreach (var (name, bounds) in items)
        {
            Assert.True(bounds.Left >= -0.5 && bounds.Top >= -0.5 && bounds.Right <= width + 0.5 && bounds.Bottom <= height + 0.5,
                $"'{name}' {bounds} está fora da área {width}×{height}.");
        }
    }

    private static int CountDistinctColors(byte[] pixels)
    {
        var colors = new HashSet<int>();
        for (int i = 0; i + 3 < pixels.Length && colors.Count <= 4096; i += 4)
        {
            colors.Add(pixels[i] | (pixels[i + 1] << 8) | (pixels[i + 2] << 16));
        }
        return colors.Count;
    }
}
