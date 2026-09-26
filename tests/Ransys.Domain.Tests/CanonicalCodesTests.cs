using System.Text.RegularExpressions;
using Ransys.Domain.Attempts;
using Ransys.Domain.Transactions;

namespace Ransys.Domain.Tests;

/// <summary>
/// The canonical codes must match the CHECK constraints of the reference DDL exactly,
/// so application values can never be rejected by (or drift from) the database.
/// </summary>
public sealed partial class CanonicalCodesTests
{
    private static readonly Lazy<string> Ddl = new(() => File.ReadAllText(
        Path.Combine(RepositoryPaths.Root(), "docs", "RANSYS_PostgreSQL_Reference_DDL_v1.1.sql")));

    [Fact]
    public void Processing_status_codes_match_ddl() =>
        Assert.Equal(DdlValues("ck_tx_processing_status"), Sorted(CanonicalCodes.ProcessingStatus.Codes));

    [Fact]
    public void Financial_status_codes_match_ddl() =>
        Assert.Equal(DdlValues("ck_tx_financial_status"), Sorted(CanonicalCodes.FinancialStatus.Codes));

    [Fact]
    public void Reconciliation_status_codes_match_ddl() =>
        Assert.Equal(DdlValues("ck_tx_recon_status"), Sorted(CanonicalCodes.ReconciliationStatus.Codes));

    [Fact]
    public void Settlement_status_codes_match_ddl() =>
        Assert.Equal(DdlValues("ck_tx_settlement_status"), Sorted(CanonicalCodes.SettlementStatus.Codes));

    [Fact]
    public void Transport_status_codes_match_ddl() =>
        Assert.Equal(DdlValues("ck_attempt_transport_status"), Sorted(CanonicalCodes.TransportStatus.Codes));

    [Fact]
    public void Wallet_status_codes_match_ddl() =>
        Assert.Equal(DdlValues("ck_wallet_status"), Sorted(CanonicalCodes.WalletStatus.Codes));

    [Fact]
    public void Reservation_status_codes_match_ddl() =>
        Assert.Equal(DdlValues("ck_reservation_status"), Sorted(CanonicalCodes.ReservationStatus.Codes));

    [Fact]
    public void Account_class_codes_match_ddl() =>
        Assert.Equal(DdlValues("ck_ledger_account_class"), Sorted(CanonicalCodes.AccountClass.Codes));

    [Fact]
    public void Entry_side_codes_match_ddl() =>
        Assert.Equal(DdlValues("ck_ledger_entry_side"), Sorted(CanonicalCodes.EntrySide.Codes));

    [Fact]
    public void Provider_health_codes_match_ddl() =>
        Assert.Equal(DdlValues("ck_provider_health_state"), Sorted(CanonicalCodes.ProviderHealth.Codes));

    [Fact]
    public void Circuit_state_codes_match_ddl() =>
        Assert.Equal(DdlValues("ck_provider_circuit_state"), Sorted(CanonicalCodes.CircuitState.Codes));

    [Fact]
    public void Codes_round_trip_and_are_case_sensitive()
    {
        foreach (var status in Enum.GetValues<ProcessingStatus>())
        {
            var code = CanonicalCodes.ProcessingStatus.ToCode(status);
            Assert.Equal(status, CanonicalCodes.ProcessingStatus.Parse(code));
        }

        Assert.False(CanonicalCodes.ProcessingStatus.TryParse("in_doubt", out _));
        Assert.False(CanonicalCodes.ProcessingStatus.TryParse(null, out _));
        Assert.Throws<FormatException>(() => CanonicalCodes.TransportStatus.Parse("LOST"));
    }

    [Fact]
    public void Well_known_codes_are_spelled_as_documented()
    {
        Assert.Equal("IN_DOUBT", CanonicalCodes.ProcessingStatus.ToCode(ProcessingStatus.InDoubt));
        Assert.Equal("NOT_SENT", CanonicalCodes.TransportStatus.ToCode(TransportStatus.NotSent));
        Assert.Equal("TOPUP", CanonicalCodes.TransactionType.ToCode(TransactionType.TopUp));
        Assert.Equal("BALANCE_INQUIRY", CanonicalCodes.TransactionType.ToCode(TransactionType.BalanceInquiry));
        Assert.Equal("STATUS_CHECK", CanonicalCodes.AttemptType.ToCode(AttemptType.StatusCheck));
    }

    [Fact]
    public void Code_map_rejects_incomplete_mapping()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new CodeMap<TransportStatus>((TransportStatus.NotSent, "NOT_SENT")));
    }

    private static List<string> DdlValues(string constraintName)
    {
        var match = Regex.Match(
            Ddl.Value,
            $@"CONSTRAINT\s+{Regex.Escape(constraintName)}\s+CHECK\s*\(\s*\w+\s+IN\s*\((?<values>[^)]*)\)",
            RegexOptions.Singleline);
        Assert.True(match.Success, $"Constraint {constraintName} not found in DDL.");

        return Sorted(QuotedValue().Matches(match.Groups["values"].Value).Select(m => m.Groups[1].Value));
    }

    private static List<string> Sorted(IEnumerable<string> values) => values.Order(StringComparer.Ordinal).ToList();

    [GeneratedRegex("'([A-Z_]+)'")]
    private static partial Regex QuotedValue();
}
