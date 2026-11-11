namespace VmTyper.Models;

/// <summary>Trecho (início e comprimento) de um texto em destaque.</summary>
public readonly record struct TextHighlight(int Start, int Length);
