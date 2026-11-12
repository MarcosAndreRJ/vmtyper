using System.Windows;
using VmTyper.Models;
using VmTyper.ViewModels;
using VmTyper.Views;
using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;

namespace VmTyper;

public partial class MainWindow : Window
{
    private MainViewModel? _viewModel;
    private bool _isExiting;

    /// <summary>Largura (em unidades independentes de dispositivo) abaixo da qual a interface entra em modo compacto.</summary>
    public const double CompactWidthThreshold = 1180;

    public static readonly DependencyProperty IsCompactProperty =
        DependencyProperty.Register(nameof(IsCompact), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));

    /// <summary>Modo compacto: rótulos secundários são ocultados (ícones e tooltips continuam) e os painéis nascem mais estreitos.</summary>
    public bool IsCompact
    {
        get => (bool)GetValue(IsCompactProperty);
        private set => SetValue(IsCompactProperty, value);
    }

    private bool _compactLayoutApplied;

    public MainWindow()
    {
        InitializeComponent();

        // Telas pequenas (ex.: 1024×768, 1366×768 com 125%): a janela nunca nasce maior que a área útil do monitor.
        var work = SystemParameters.WorkArea;
        Width = Math.Min(Width, Math.Max(MinWidth, work.Width - 16));
        Height = Math.Min(Height, Math.Max(MinHeight, work.Height - 16));

        SizeChanged += (_, e) => UpdateCompactMode(e.NewSize.Width);
        UpdateCompactMode(Width);
    }

    private void UpdateCompactMode(double width)
    {
        IsCompact = width < CompactWidthThreshold;

        if (IsCompact && !_compactLayoutApplied)
        {
            // Primeira vez em largura reduzida: painéis laterais mais estreitos (o usuário pode redimensionar depois).
            _compactLayoutApplied = true;
            if (StructureColumn.Width.Value > 210) StructureColumn.Width = new GridLength(210);
            if (StatusColumn.Width.Value > 290) StatusColumn.Width = new GridLength(290);
        }
    }

    public void AttachViewModel(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        ApplyBehaviorSettings();

        viewModel.ConfirmPartialResume = ConfirmPartialResumeDialog;
        viewModel.ConfirmPreflightUnsupported = ConfirmPreflightUnsupportedDialog;
        viewModel.ConfirmTargetChanged = ConfirmTargetChangedDialog;
        viewModel.EditorSelectionStartProvider = () => CodeEditor.SelectionStart;
        // O progresso chega da thread do motor; o editor só aceita chamadas da thread da interface.
        viewModel.TypingHighlightChanged += (offset, length) =>
            Dispatcher.InvokeAsync(() => CodeEditor.SetTypingHighlight(offset, length));

        if (App.QueueLoadCorruptPath is { } corruptPath)
        {
            MessageBox.Show(this,
                $"A fila salva não pôde ser lida.\nUma cópia foi preservada em:\n{corruptPath}\n\nComeçar com fila vazia?",
                "Fila corrompida preservada",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        if (App.TrayIconServiceInstance is { } tray)
        {
            tray.OpenRequested += TrayOpen;
            tray.TypeRequested += () => _viewModel?.StartCommand.Execute(null);
            tray.PauseResumeRequested += () => _viewModel?.PauseResumeCommand.Execute(null);
            tray.CancelRequested += () => _viewModel?.CancelCommand.Execute(null);
            tray.ExitRequested += () =>
            {
                _isExiting = true;
                System.Windows.Application.Current.Shutdown();
            };
        }
    }

    private void ApplyBehaviorSettings()
    {
        if (_viewModel is null) return;
        Topmost = _viewModel.Settings.AlwaysOnTop;
    }

    // Reaplica as configurações de comportamento (Sempre no topo etc.) após
    // o usuário salvar alterações na tela de Configurações.
    public void RefreshBehaviorSettings() => ApplyBehaviorSettings();

    private void TrayOpen()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        if (App.TrayIconServiceInstance is not null)
        {
            App.TrayIconServiceInstance.Visible = false;
        }
    }

    private void Window_StateChanged(object sender, EventArgs e)
    {
        if (_viewModel is null) return;

        if (WindowState == WindowState.Minimized && _viewModel.Settings.MinimizeToTray)
        {
            Hide();
            if (App.TrayIconServiceInstance is not null)
            {
                App.TrayIconServiceInstance.Visible = true;
            }
        }
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Persiste qualquer edição ainda dentro da janela de debounce da divisão em blocos.
        _viewModel?.DocManager.FlushPendingSplit();

        if (!_isExiting && _viewModel is not null && _viewModel.Settings.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            if (App.TrayIconServiceInstance is not null)
            {
                App.TrayIconServiceInstance.Visible = true;
            }
            return;
        }

        // §2.4 (H5): se houver sessão ativa ao fechar de verdade, cancelar e aguardar até 2 s.
        App.CoordinatorInstance?.CancelAndWait(TimeSpan.FromSeconds(2));
    }

    private bool? ConfirmPartialResumeDialog(TextBlockItem block)
    {
        if (block.SentUpToIndex > 0 && block.SentUpToIndex < block.Text.Length)
        {
            string remaining = block.Text.Substring(block.SentUpToIndex);
            string preview = remaining.Length > 40 ? remaining.Substring(0, 40) + "…" : remaining;
            var res = MessageBox.Show(this,
                $"O Bloco {block.Order} está no estado '{block.StatusLabel}'.\n\n" +
                $"• Sim: Reenviar do início (recomendado)\n" +
                $"• Não: Continuar da unidade {block.SentUpToIndex} (texto restante: \"{preview}\")\n" +
                $"• Cancelar: Não iniciar agora",
                "Confirmar envio de bloco interrompido/parcial",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question,
                MessageBoxResult.Yes);

            return res switch
            {
                MessageBoxResult.Yes => false,
                MessageBoxResult.No => true,
                _ => null
            };
        }

        var simple = MessageBox.Show(this,
            $"O Bloco {block.Order} está no estado '{block.StatusLabel}'. Deseja reenviar do início?",
            "Confirmar reenvio",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question,
            MessageBoxResult.OK);
        return simple == MessageBoxResult.OK ? false : null;
    }

    private PreflightDecision ConfirmPreflightUnsupportedDialog(Services.PreflightReport report)
    {
        var summary = string.Join("\n", report.Unsupported
            .Take(10)
            .Select(u => $"• Posição #{u.Index} ({u.Category}): {u.Reason}"));
        if (report.UnsupportedCount > 10)
        {
            summary += $"\n… e mais {report.UnsupportedCount - 10} unidade(s).";
        }

        var res = MessageBox.Show(this,
            $"O texto contém {report.UnsupportedCount} unidade(s) não suportada(s) no layout físico:\n\n" +
            $"{summary}\n\n" +
            $"• Sim: Usar Unicode para essas {report.UnsupportedCount} unidade(s)\n" +
            $"• Não: Enviar mesmo assim, pulando {report.UnsupportedCount} unidade(s)\n" +
            $"• Cancelar (padrão): Cancelar envio",
            "Pré-voo: unidades não suportadas",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);

        return res switch
        {
            MessageBoxResult.Yes => PreflightDecision.FallbackToUnicode,
            MessageBoxResult.No => PreflightDecision.SkipUnsupported,
            _ => PreflightDecision.Cancel
        };
    }

    private bool ConfirmTargetChangedDialog(Services.TargetAnchor previous, Services.TargetAnchor current)
    {
        var res = MessageBox.Show(this,
            $"O destino mudou.\n" +
            $"Anterior: {previous.ProcessName} (0x{previous.Hwnd:X})\n" +
            $"Atual: {current.ProcessName} (0x{current.Hwnd:X})\n\n" +
            $"Usar a nova janela como destino?",
            "Destino alterado",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        return res == MessageBoxResult.Yes;
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is { CanEditQueue: false }) return;
        var window = new SettingsWindow(App.SettingsServiceInstance)
        {
            Owner = this
        };
        if (window.ShowDialog() == true)
        {
            _viewModel?.ReloadSettings();
            ApplyBehaviorSettings();
        }
    }

    private void OpenDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is { CanEditQueue: false }) return;
        var window = new DiagnosticsWindow(App.TypingServiceInstance, App.LogServiceInstance, App.CoordinatorInstance)
        {
            Owner = this
        };
        window.Show();
    }

    private void EnvironmentCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_viewModel is null || !_viewModel.CanEditQueue) return;
        if (string.Equals(_viewModel.SelectedEnvironmentTag, "Embedded", StringComparison.OrdinalIgnoreCase) &&
            _viewModel.Settings.Features.EmbeddedBrowser)
        {
            var embeddedWindow = new EmbeddedBrowserWindow(App.LogServiceInstance, App.CoordinatorInstance, _viewModel.Settings.Features)
            {
                Owner = this
            };
            embeddedWindow.Show();
        }
    }

    private void AddBlock_Click(object sender, RoutedEventArgs e)
    {
        _viewModel?.AddBlock();
    }

    private void RestartQueue_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(this,
            "Deseja reiniciar a fila? Todos os blocos voltarão ao estado pendente.",
            "Reiniciar fila", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result == MessageBoxResult.Yes)
            _viewModel?.RestartQueue();
    }

    private void NewQueue_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(this,
            "Isso removerá todos os blocos preparados. Deseja continuar?",
            "Nova fila", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result == MessageBoxResult.Yes)
            _viewModel?.NewQueue();
    }

    private void RemoveBlock_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TextBlockItem item }) return;

        if (!item.IsEmpty)
        {
            var result = MessageBox.Show(this,
                $"Deseja remover o Bloco {item.Order}?",
                "Remover bloco", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;
        }

        _viewModel?.RemoveBlock(item.Id);
    }

    private void OpenCodeFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Arquivos de Código e Texto (*.pas;*.dpr;*.inc;*.sql;*.ddl;*.txt)|*.pas;*.dpr;*.inc;*.sql;*.ddl;*.txt|Arquivos Delphi (*.pas;*.dpr;*.inc)|*.pas;*.dpr;*.inc|Scripts SQL (*.sql;*.ddl)|*.sql;*.ddl|Todos os arquivos (*.*)|*.*",
            Title = "Abrir Arquivo de Código"
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                _viewModel?.DocManager.OpenFile(dialog.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Erro ao abrir arquivo:\n{ex.Message}", "Erro ao abrir arquivo", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void NewCodeDocument_Click(object sender, RoutedEventArgs e)
    {
        int count = (_viewModel?.DocManager.Documents.Count ?? 0) + 1;
        _viewModel?.DocManager.CreateNewDocument(string.Empty, $"Documento {count}");
    }

    private void SelectDocumentTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.FrameworkElement { Tag: SourceDocument doc } && _viewModel != null)
        {
            _viewModel.DocManager.CurrentDocument = doc;
        }
    }

    private void CloseDocumentTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.FrameworkElement { Tag: SourceDocument doc } && _viewModel != null)
        {
            CloseDocumentWithConfirmation(doc);
        }
    }

    private void CloseCodeDocument_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.FrameworkElement { Tag: SourceDocument doc } && _viewModel != null)
        {
            CloseDocumentWithConfirmation(doc);
        }
        else if (sender is System.Windows.FrameworkElement { DataContext: SourceDocument docCtx } && _viewModel != null)
        {
            CloseDocumentWithConfirmation(docCtx);
        }
    }

    private void CloseCurrentDocument_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.DocManager.CurrentDocument is { } doc)
        {
            CloseDocumentWithConfirmation(doc);
        }
    }

    /// <summary>Fecha o documento; se houver alterações não salvas, pergunta antes (salvar / descartar / manter).</summary>
    private void CloseDocumentWithConfirmation(SourceDocument doc)
    {
        if (_viewModel is null) return;
        _viewModel.DocManager.FlushPendingSplit();

        if (doc.IsModified)
        {
            var res = MessageBox.Show(this,
                $"O documento '{doc.Name}' tem alterações não salvas.\n\n" +
                "• Sim: salvar e fechar\n• Não: fechar sem salvar\n• Cancelar: manter aberto",
                "Fechar documento",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning,
                MessageBoxResult.Yes);

            if (res == MessageBoxResult.Cancel) return;
            if (res == MessageBoxResult.Yes && !SaveDocumentInteractive(doc, forceDialog: false)) return;
        }

        _viewModel.DocManager.CloseDocument(doc);
    }

    private void SelectBlockTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.FrameworkElement { Tag: CodeBlock block } && _viewModel != null)
        {
            _viewModel.DocManager.SelectedBlock = block;
            CodeEditor.ScrollToLine(block.StartLine);
            CodeEditor.SelectRange(block.StartOffset, block.Length);
        }
    }

    private void SplitAtCaret_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel == null) return;
        int caret = CodeEditor.CaretOffset;
        bool split = _viewModel.DocManager.SplitCurrentBlockAtCaret(caret);
        if (!split)
        {
            MessageBox.Show(this, "Não foi possível dividir no cursor. Certifique-se de que o cursor está dentro do bloco selecionado.", "Divisão de Bloco", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void MergeNextBlock_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.DocManager.SelectedBlock is { } sb)
        {
            bool merged = _viewModel.DocManager.MergeBlockWithNext(sb);
            if (!merged)
            {
                MessageBox.Show(this, "Não há bloco seguinte consecutivo para mesclar.", "Unir Blocos", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
    }

    private GridLength _structurePreviousWidth = new GridLength(240);
    private GridLength _statusPreviousWidth = new GridLength(320);

    private void ToggleTheme_Click(object sender, RoutedEventArgs e)
    {
        // Automático → Escuro → Claro → Automático. A escolha é persistida nas configurações.
        Services.ThemeManager.Instance.CycleTheme();
        if (_viewModel is not null)
        {
            _viewModel.Settings.Theme = Services.ThemeManager.Instance.CurrentTheme;
            App.SettingsServiceInstance?.Save(_viewModel.Settings);
        }
    }

    private void ToggleStructure_Click(object sender, RoutedEventArgs e)
    {
        if (StructureColumn.Width.Value > 0)
        {
            _structurePreviousWidth = StructureColumn.Width;
            StructureColumn.Width = new GridLength(0);
            StructureSplitter.Visibility = Visibility.Collapsed;
        }
        else
        {
            StructureColumn.Width = _structurePreviousWidth.Value > 50 ? _structurePreviousWidth : new GridLength(240);
            StructureSplitter.Visibility = Visibility.Visible;
        }
    }

    private void ToggleStatus_Click(object sender, RoutedEventArgs e)
    {
        if (StatusColumn.Width.Value > 0)
        {
            _statusPreviousWidth = StatusColumn.Width;
            StatusColumn.Width = new GridLength(0);
            StatusSplitter.Visibility = Visibility.Collapsed;
        }
        else
        {
            StatusColumn.Width = _statusPreviousWidth.Value > 50 ? _statusPreviousWidth : new GridLength(320);
            StatusSplitter.Visibility = Visibility.Visible;
        }
    }

    private void SelectStructureItem_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CodeBlock block })
        {
            NavigateToBlock(block);
        }
    }

    /// <summary>Torna o bloco ativo, rola o editor até ele e seleciona seu trecho.</summary>
    private void NavigateToBlock(CodeBlock block)
    {
        if (_viewModel is null) return;

        _viewModel.DocManager.SelectedBlock = block;
        if (_viewModel.DocManager.CurrentDocument is { } doc)
        {
            int start = Math.Clamp(block.StartOffset, 0, doc.Text.Length);
            int end = Math.Clamp(block.EndOffset, start, doc.Text.Length);
            CodeEditor?.ScrollToAndSelect(start, end, block.StartLine);
        }
    }

    private void StructureList_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter && StructureList.SelectedItem is CodeBlock block)
        {
            NavigateToBlock(block);
            e.Handled = true;
        }
    }

    // --- Menus, documentos e atalhos -------------------------------------------------------------------
    private void OpenContextMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { ContextMenu: { } menu } element)
        {
            menu.DataContext = element.DataContext; // o menu não faz parte da árvore visual
            menu.PlacementTarget = element;
            if (menu.Placement == System.Windows.Controls.Primitives.PlacementMode.MousePoint)
            {
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            }
            menu.IsOpen = true;
        }
    }

    private void DocumentOverflow_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || sender is not FrameworkElement element) return;

        var menu = new System.Windows.Controls.ContextMenu
        {
            PlacementTarget = element,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom
        };

        foreach (var doc in _viewModel.DocManager.Documents.ToList())
        {
            var target = doc;
            var item = new System.Windows.Controls.MenuItem
            {
                // TextBlock evita que "_" no nome do arquivo seja interpretado como tecla de acesso.
                Header = new System.Windows.Controls.TextBlock { Text = target.IsModified ? $"{target.Name}  ●" : target.Name },
                IsChecked = target.IsSelected,
                ToolTip = target.FilePath
            };
            item.Click += (_, _) => _viewModel.DocManager.CurrentDocument = target;
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    private void ReSplit_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.ReSplitCommand.CanExecute(null) == true)
        {
            _viewModel.ReSplitCommand.Execute(null);
        }
    }

    private void SaveCodeDocument_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.DocManager.CurrentDocument is { } doc)
        {
            SaveDocumentInteractive(doc, forceDialog: false);
        }
    }

    private void SaveAsCodeDocument_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.DocManager.CurrentDocument is { } doc)
        {
            SaveDocumentInteractive(doc, forceDialog: true);
        }
    }

    /// <summary>Salva o documento (pede o caminho se for novo ou se "Salvar como"). Retorna false se cancelado/falhou.</summary>
    private bool SaveDocumentInteractive(SourceDocument doc, bool forceDialog)
    {
        if (_viewModel is null) return false;
        _viewModel.DocManager.FlushPendingSplit();

        string? path = doc.FilePath;
        if (forceDialog || string.IsNullOrWhiteSpace(path))
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Salvar documento",
                FileName = doc.Name,
                AddExtension = true,
                DefaultExt = doc.Language switch
                {
                    SourceLanguage.Delphi => ".pas",
                    SourceLanguage.SqlFirebird => ".sql",
                    _ => ".txt"
                },
                Filter = "Arquivos de Código e Texto (*.pas;*.dpr;*.inc;*.sql;*.ddl;*.txt)|*.pas;*.dpr;*.inc;*.sql;*.ddl;*.txt|Todos os arquivos (*.*)|*.*"
            };
            if (dialog.ShowDialog(this) != true) return false;
            path = dialog.FileName;
        }

        if (!_viewModel.DocManager.SaveDocument(doc, path, out string? error))
        {
            MessageBox.Show(this, $"Não foi possível salvar o arquivo:\n{error}", "Salvar documento",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        return true;
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => CodeEditor.Undo();
    private void Redo_Click(object sender, RoutedEventArgs e) => CodeEditor.Redo();
    private void Search_Click(object sender, RoutedEventArgs e) => CodeEditor.OpenSearch();
    private void GoToLine_Click(object sender, RoutedEventArgs e) => CodeEditor.ShowGoToLineDialog();
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => CodeEditor.ZoomIn();
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => CodeEditor.ZoomOut();

    /// <summary>Atalhos de teclado da janela principal (todos com Ctrl).</summary>
    public enum WindowShortcut
    {
        None,
        ToggleStatusPanel,
        ToggleStructurePanel,
        OpenFile,
        NewDocument,
        Save,
        SaveAs,
        CloseDocument,
        Find,
        GoToLine
    }

    /// <summary>
    /// Resolve a combinação de teclas para um atalho. Painéis valem nos dois modos; atalhos de documento
    /// só no modo Editor, para não competir com a edição dos blocos da fila.
    /// </summary>
    public static WindowShortcut ResolveShortcut(System.Windows.Input.Key key, System.Windows.Input.ModifierKeys modifiers, bool smartEditorMode)
    {
        bool ctrl = modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control);
        bool shift = modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift);
        bool alt = modifiers.HasFlag(System.Windows.Input.ModifierKeys.Alt);
        if (!ctrl || alt) return WindowShortcut.None;

        if (shift && key == System.Windows.Input.Key.L) return WindowShortcut.ToggleStatusPanel; // Ctrl+Shift+P é o atalho global de pausa
        if (!smartEditorMode) return WindowShortcut.None;

        return key switch
        {
            System.Windows.Input.Key.O when !shift => WindowShortcut.OpenFile,
            System.Windows.Input.Key.N when !shift => WindowShortcut.NewDocument,
            System.Windows.Input.Key.S => shift ? WindowShortcut.SaveAs : WindowShortcut.Save,
            System.Windows.Input.Key.W when !shift => WindowShortcut.CloseDocument,
            System.Windows.Input.Key.F when !shift => WindowShortcut.Find,
            System.Windows.Input.Key.G when !shift => WindowShortcut.GoToLine,
            System.Windows.Input.Key.E when shift => WindowShortcut.ToggleStructurePanel,
            _ => WindowShortcut.None
        };
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (_viewModel is null) return;

        var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
        var shortcut = ResolveShortcut(key, System.Windows.Input.Keyboard.Modifiers, _viewModel.IsSmartEditorMode);
        if (shortcut == WindowShortcut.None) return;

        var args = new RoutedEventArgs();
        switch (shortcut)
        {
            case WindowShortcut.ToggleStatusPanel: ToggleStatus_Click(this, args); break;
            case WindowShortcut.ToggleStructurePanel: ToggleStructure_Click(this, args); break;
            case WindowShortcut.OpenFile: OpenCodeFile_Click(this, args); break;
            case WindowShortcut.NewDocument: NewCodeDocument_Click(this, args); break;
            case WindowShortcut.Save: SaveCodeDocument_Click(this, args); break;
            case WindowShortcut.SaveAs: SaveAsCodeDocument_Click(this, args); break;
            case WindowShortcut.CloseDocument: CloseCurrentDocument_Click(this, args); break;
            case WindowShortcut.Find: CodeEditor.OpenSearch(); break;
            case WindowShortcut.GoToLine: CodeEditor.ShowGoToLineDialog(); break;
        }

        e.Handled = true;
    }
}
