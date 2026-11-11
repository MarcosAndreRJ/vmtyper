using VmTyper.Resources;
using VmTyper.Services;

namespace VmTyper.Models;

/// <summary>
/// Os cinco estados semânticos de entrega obrigatórios na Fase 5 §4.
/// Cada estado possui ícone, rótulo textual e cor de apoio distintos (a cor nunca é a única pista).
/// Regra inviolável: <see cref="Confirmed"/> nunca é derivado apenas do retorno de <c>SendInput</c>/CDP
/// (<see cref="UnitStatus.Sent"/> ou <see cref="TextBlockStatus.Sent"/>).
/// </summary>
public enum DeliverySemanticState
{
    /// <summary>SendInput/CDP retornou sucesso. Nada se sabe do destino.</summary>
    Sent,

    /// <summary>O receptor ou a página observou a unidade exatamente como esperado.</summary>
    Confirmed,

    /// <summary>Há indício (repeat, Down duplo, foco instável), sem confirmação.</summary>
    Suspicious,

    /// <summary>Não existe observação disponível.</summary>
    Unknown,

    /// <summary>Erro nativo, unidade pulada ou divergência confirmada.</summary>
    Failed
}

public sealed record DeliveryVisualBadge(
    DeliverySemanticState State,
    string Icon,
    string Text,
    string FullLabel,
    string ColorHex,
    string BackgroundHex,
    string Description);

public static class DeliverySemantics
{
    public static readonly IReadOnlyList<DeliveryVisualBadge> AllLegendBadges = new[]
    {
        GetBadge(DeliverySemanticState.Sent),
        GetBadge(DeliverySemanticState.Confirmed),
        GetBadge(DeliverySemanticState.Suspicious),
        GetBadge(DeliverySemanticState.Unknown),
        GetBadge(DeliverySemanticState.Failed)
    };

    public static DeliveryVisualBadge GetBadge(DeliverySemanticState state) => state switch
    {
        DeliverySemanticState.Sent => new DeliveryVisualBadge(
            DeliverySemanticState.Sent,
            Icon: "↗",
            Text: "Enviado",
            FullLabel: Strings.DeliverySentLabel,
            ColorHex: "#1E6BB8",
            BackgroundHex: "#EBF3FA",
            Description: "SendInput/CDP retornou sucesso. Nada se sabe do destino."),

        DeliverySemanticState.Confirmed => new DeliveryVisualBadge(
            DeliverySemanticState.Confirmed,
            Icon: "✓",
            Text: "Confirmado",
            FullLabel: Strings.DeliveryConfirmedLabel,
            ColorHex: "#1F7A3E",
            BackgroundHex: "#EAF6EE",
            Description: "O receptor ou a página observou a unidade exatamente como esperado."),

        DeliverySemanticState.Suspicious => new DeliveryVisualBadge(
            DeliverySemanticState.Suspicious,
            Icon: "⚠",
            Text: "Suspeito",
            FullLabel: Strings.DeliverySuspiciousLabel,
            ColorHex: "#B86B11",
            BackgroundHex: "#FFF5E5",
            Description: "Há indício (repeat, Down duplo, foco instável), sem confirmação."),

        DeliverySemanticState.Unknown => new DeliveryVisualBadge(
            DeliverySemanticState.Unknown,
            Icon: "?",
            Text: "Desconhecido",
            FullLabel: Strings.DeliveryUnknownLabel,
            ColorHex: "#555555",
            BackgroundHex: "#F2F2F2",
            Description: "Não existe observação disponível."),

        DeliverySemanticState.Failed => new DeliveryVisualBadge(
            DeliverySemanticState.Failed,
            Icon: "✗",
            Text: "Falha",
            FullLabel: Strings.DeliveryFailedLabel,
            ColorHex: "#B82E2E",
            BackgroundHex: "#FDECEC",
            Description: "Erro nativo, unidade pulada ou divergência confirmada."),

        _ => new DeliveryVisualBadge(
            DeliverySemanticState.Unknown,
            Icon: "?",
            Text: "Desconhecido",
            FullLabel: Strings.DeliveryUnknownLabel,
            ColorHex: "#555555",
            BackgroundHex: "#F2F2F2",
            Description: "Não existe observação disponível.")
    };

    /// <summary>
    /// Deriva o estado semântico apenas a partir do resultado local de envio (sem receptor).
    /// NUNCA retorna <see cref="DeliverySemanticState.Confirmed"/>.
    /// </summary>
    public static DeliverySemanticState FromLocalSendResult(
        UnitOutcome unitOutcome,
        bool hadFocusInstability = false)
    {
        if (unitOutcome is UnitOutcome.Failed or UnitOutcome.Skipped)
        {
            return DeliverySemanticState.Failed;
        }

        if (hadFocusInstability)
        {
            return DeliverySemanticState.Suspicious;
        }

        // SendInput/CDP retornou sucesso -> Enviado (nunca Confirmado!).
        return DeliverySemanticState.Sent;
    }

    /// <summary>
    /// Deriva o estado semântico de um bloco da fila (sem receptor conectado).
    /// NUNCA retorna <see cref="DeliverySemanticState.Confirmed"/>.
    /// </summary>
    public static DeliverySemanticState FromBlockStatus(TextBlockStatus status, bool hadFocusInstability = false) => status switch
    {
        TextBlockStatus.Sent => hadFocusInstability ? DeliverySemanticState.Suspicious : DeliverySemanticState.Sent,
        TextBlockStatus.PartiallySent or TextBlockStatus.Failed => DeliverySemanticState.Failed,
        TextBlockStatus.Interrupted => DeliverySemanticState.Suspicious,
        _ => DeliverySemanticState.Unknown
    };

    /// <summary>
    /// Deriva o estado semântico após cruzamento com observação real do receptor ou da página HTML.
    /// Só retorna <see cref="DeliverySemanticState.Confirmed"/> quando houver relatório de observação
    /// com <c>IsIdentical == true</c> e zero eventos suspeitos.
    /// </summary>
    public static DeliverySemanticState FromReceiverComparison(
        DivergenceReport? report,
        int suspiciousEventCount = 0)
    {
        if (report is null)
        {
            return DeliverySemanticState.Unknown;
        }

        if (!report.IsIdentical || report.TotalDivergences > 0)
        {
            return DeliverySemanticState.Failed;
        }

        if (suspiciousEventCount > 0)
        {
            return DeliverySemanticState.Suspicious;
        }

        return DeliverySemanticState.Confirmed;
    }
}
