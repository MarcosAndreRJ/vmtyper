namespace VmTyper.Models;

/// <summary>
/// Categoria estrutural de um bloco de código identificado pelo Smart Split.
/// </summary>
public enum CodeBlockKind
{
    FullDocument,
    DelphiUnitHeader,
    DelphiInterface,
    DelphiUses,
    DelphiTypeSection,
    DelphiConstSection,
    DelphiVarSection,
    DelphiImplementation,
    DelphiMethod,
    DelphiInitialization,
    DelphiFinalization,
    DelphiUnitEnd,
    SqlCommand,
    SqlDdl,
    SqlProcedure,
    SqlTrigger,
    SqlExecuteBlock,
    SqlSetTerm,
    CustomSelection,
    Unclassified
}
