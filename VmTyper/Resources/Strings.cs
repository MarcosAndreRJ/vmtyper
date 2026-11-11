using System.Globalization;
using System.Resources;

namespace VmTyper.Resources;

/// <summary>
/// Acessor centralizado para os textos em pt-BR de <c>Resources/Strings.resx</c> (Fase 5 §5).
/// </summary>
public static class Strings
{
    private static readonly ResourceManager ResourceManager =
        new("VmTyper.Resources.Strings", typeof(Strings).Assembly);

    public static string AppTitle => GetString(nameof(AppTitle), "VmTyper — Digitação automatizada para VM via navegador");
    public static string ReceiptUnverifiedSuffix => GetString(nameof(ReceiptUnverifiedSuffix), "Recebimento não verificado.");
    public static string BlockSentTemplate => GetString(nameof(BlockSentTemplate), "Bloco {0}: {1:N0} unidades enviadas. Recebimento não verificado.");
    public static string BlockPartialSentTemplate => GetString(nameof(BlockPartialSentTemplate), "Bloco {0} enviado parcialmente ({1:N0} enviadas, {2:N0} puladas, {3:N0} falhas). Recebimento não verificado.");

    public static string DeliverySentLabel => GetString(nameof(DeliverySentLabel), "↗ Enviado");
    public static string DeliveryConfirmedLabel => GetString(nameof(DeliveryConfirmedLabel), "✓ Confirmado");
    public static string DeliverySuspiciousLabel => GetString(nameof(DeliverySuspiciousLabel), "⚠ Suspeito");
    public static string DeliveryUnknownLabel => GetString(nameof(DeliveryUnknownLabel), "? Desconhecido");
    public static string DeliveryFailedLabel => GetString(nameof(DeliveryFailedLabel), "✗ Falha");

    public static string TargetGuardMonitoring => GetString(nameof(TargetGuardMonitoring), "🛡 Monitorando");
    public static string TargetGuardLost => GetString(nameof(TargetGuardLost), "⚠ Perdido");
    public static string TargetGuardOff => GetString(nameof(TargetGuardOff), "○ Desligado");

    public static string EnvironmentExternal => GetString(nameof(EnvironmentExternal), "Externo");
    public static string EnvironmentEmbedded => GetString(nameof(EnvironmentEmbedded), "Integrado (experimental)");

    public static string FormatBlockSent(int blockOrder, int sentUnits) =>
        string.Format(CultureInfo.GetCultureInfo("pt-BR"), BlockSentTemplate, blockOrder, sentUnits);

    public static string FormatBlockPartialSent(int blockOrder, int sentUnits, int skippedUnits, int failedUnits) =>
        string.Format(CultureInfo.GetCultureInfo("pt-BR"), BlockPartialSentTemplate, blockOrder, sentUnits, skippedUnits, failedUnits);

    private static string GetString(string name, string fallback)
    {
        try
        {
            return ResourceManager.GetString(name, CultureInfo.GetCultureInfo("pt-BR")) ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }
}
