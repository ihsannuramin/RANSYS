using DomainCapabilities = Ransys.Domain.Routing.ProviderCapabilities;

namespace Ransys.Adapter.Contracts.V1;

/// <summary>
/// Capability catalog reported through <see cref="IProviderAdapter.GetCapabilitiesAsync"/>
/// (Provider Adapter Contract v1 §2, ADR-018). The values are the routing constants in
/// <c>Ransys.Domain.Routing.ProviderCapabilities</c>, so adapters and routing share one vocabulary.
/// VOID is explicit and is never mapped to REVERSAL or REFUND (ADR-017).
/// </summary>
public static class ProviderCapabilityCodes
{
    public const string Inquiry = DomainCapabilities.Inquiry;
    public const string Payment = DomainCapabilities.Payment;
    public const string Purchase = DomainCapabilities.Purchase;
    public const string Transfer = DomainCapabilities.Transfer;
    public const string Void = DomainCapabilities.Void;
    public const string StatusCheck = DomainCapabilities.StatusCheck;
    public const string Reversal = DomainCapabilities.Reversal;
    public const string Refund = DomainCapabilities.Refund;
    public const string Advice = DomainCapabilities.Advice;
    public const string Callback = DomainCapabilities.Callback;
    public const string BalanceCheck = DomainCapabilities.BalanceCheck;
    public const string Reconciliation = DomainCapabilities.Reconciliation;
    public const string SettlementFile = DomainCapabilities.SettlementFile;

    /// <summary>The whole catalog, in contract order.</summary>
    public static IReadOnlyList<string> All => DomainCapabilities.All;
}
