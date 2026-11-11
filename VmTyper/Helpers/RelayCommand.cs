using System;
using System.Windows.Input;

namespace VmTyper.Helpers;

/// <summary>
/// Implementação simples de ICommand, evitando dependência de frameworks
/// MVVM externos.
/// </summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;
    private EventHandler? _internalCanExecuteChanged;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute())
    {
    }

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => _execute(parameter);

    public event EventHandler? CanExecuteChanged
    {
        add
        {
            _internalCanExecuteChanged += value;
            try { CommandManager.RequerySuggested += value; } catch { }
        }
        remove
        {
            _internalCanExecuteChanged -= value;
            try { CommandManager.RequerySuggested -= value; } catch { }
        }
    }

    public void RaiseCanExecuteChanged()
    {
        _internalCanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            CommandManager.InvalidateRequerySuggested();
        }
        catch
        {
            // Seguro fora do contexto de UI / testes unitários
        }
    }
}
