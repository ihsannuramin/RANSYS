using Ransys.Application;
using Ransys.Configuration;
using Ransys.Domain;
using Ransys.Domain.Common;
using Ransys.Domain.Fees;
using Ransys.Domain.Monetary;
using Ransys.Domain.Transactions;
using Ransys.TransactionCore.Fees;

namespace Ransys.TransactionCore.Tests;

/// <summary>ADR-020 fee resolution without a database.</summary>
public sealed class FeeResolverTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 10, 0, 0, TimeSpan.Zero);
    private static readonly CurrencyDefinition Idr = CurrencyDefinition.Create("IDR", 1, 2).Value;
    private static readonly CurrencyDefinition Jpy = CurrencyDefinition.Create("JPY", 1, 0).Value;
    private static readonly MerchantId Merchant = new(Guid.CreateVersion7());
    private static readonly ProductId Product = new(Guid.CreateVersion7());

    [Theory]
    [InlineData(2_500, 100_000, 2_500)]
    [InlineData(2_500.005, 100_000, 2_500.01)]   // fixed value beyond the scale rounds half away from zero
    public void Fixed_fee_is_the_value_at_currency_scale(decimal value, decimal amount, decimal expected)
    {
        Assert.Equal(expected, FeeResolver.Calculate(Rule(FeeRuleType.Fixed, value), Rp(amount)).Value.Amount);
    }

    [Theory]
    [InlineData(0.015, 100_000, 1_500)]
    [InlineData(0.01, 333.50, 3.34)]     // 3.335 → 3.34 (half away from zero, not banker's rounding)
    [InlineData(0.01, 333.49, 3.33)]     // 3.3349 → 3.33
    [InlineData(0.01, 0.50, 0.01)]       // 0.005 → 0.01
    public void Percentage_is_amount_times_rate_rounded_half_away_from_zero(decimal rate, decimal amount, decimal expected)
    {
        Assert.Equal(expected, FeeResolver.Calculate(Rule(FeeRuleType.Percentage, rate), Rp(amount)).Value.Amount);
    }

    [Fact]
    public void Scale_zero_currency_rounds_to_whole_units()
    {
        var fee = FeeResolver.Calculate(Rule(FeeRuleType.Percentage, 0.015m), Money.Create(1_030m, Jpy).Value);

        Assert.Equal(15m, fee.Value.Amount); // 15.45 → 15
    }

    [Theory]
    [InlineData(10_000, 1_000)]       // 100 → clamped up to the minimum
    [InlineData(10_000_000, 25_000)]  // 100,000 → clamped down to the maximum
    [InlineData(1_000_000, 10_000)]   // within range
    public void Percentage_is_clamped_to_minimum_and_maximum(decimal amount, decimal expected)
    {
        var rule = Rule(FeeRuleType.Percentage, 0.01m, min: 1_000m, max: 25_000m);

        Assert.Equal(expected, FeeResolver.Calculate(rule, Rp(amount)).Value.Amount);
    }

    [Fact]
    public void Invalid_rule_values_fail_closed()
    {
        Assert.True(FeeResolver.Calculate(Rule(FeeRuleType.Fixed, -1m), Rp(1m)).IsFailure);
        Assert.True(FeeResolver.Calculate(Rule(FeeRuleType.Fixed, 1m, min: 10m, max: 5m), Rp(1m)).IsFailure);
    }

    [Fact]
    public async Task Merchant_specific_rule_wins_over_generic_and_the_component_captures_the_version()
    {
        var resolver = Resolver(active: true, Rule(FeeRuleType.Fixed, 2_500m), Rule(FeeRuleType.Fixed, 1_000m, merchant: Merchant));

        var resolution = (await resolver.ResolveAsync(new Session(), Product, TransactionType.Payment, Merchant, Rp(100_000m), Now)).Value;

        var component = Assert.Single(resolution.Fees.Items);
        Assert.Equal(1_000m, component.ChargedAmount.Amount);
        Assert.Equal((FeeComponentType.MerchantServiceFee, FeeRefundPolicy.None, 7L), (component.ComponentType, component.RefundPolicy, component.CalculationRuleVersion));
        Assert.Equal("RANSYS", component.Beneficiary.Type);
        Assert.Equal(VersionId, resolution.FeeConfigVersionId);
    }

    [Fact]
    public async Task No_rule_or_no_active_fee_version_is_zero_fee()
    {
        var noRule = await Resolver(active: true).ResolveAsync(new Session(), Product, TransactionType.Payment, Merchant, Rp(100_000m), Now);
        var noVersion = await Resolver(active: false, Rule(FeeRuleType.Fixed, 2_500m))
            .ResolveAsync(new Session(), Product, TransactionType.Payment, Merchant, Rp(100_000m), Now);

        Assert.Empty(noRule.Value.Fees.Items);
        Assert.Equal(VersionId, noRule.Value.FeeConfigVersionId);
        Assert.Empty(noVersion.Value.Fees.Items);
        Assert.Null(noVersion.Value.FeeConfigVersionId);
    }

    [Fact]
    public async Task Two_rules_at_the_same_precedence_fail_closed()
    {
        var resolver = Resolver(active: true, Rule(FeeRuleType.Fixed, 2_500m), Rule(FeeRuleType.Fixed, 1_000m));

        var result = await resolver.ResolveAsync(new Session(), Product, TransactionType.Payment, Merchant, Rp(100_000m), Now);

        Assert.Equal(FeeResolver.AmbiguousFeeRule, result.Error.Code);
    }

    private static readonly Guid VersionId = Guid.CreateVersion7();

    private static FeeResolver Resolver(bool active, params FeeRule[] rules) =>
        new(new ConfigurationService(new VersionStore(active), new FixedClock()), new RuleReader(rules));

    private static FeeRule Rule(FeeRuleType type, decimal value, decimal? min = null, decimal? max = null, MerchantId? merchant = null) =>
        new(Guid.CreateVersion7(), merchant, type, value, min, max);

    private static Money Rp(decimal amount) => Money.Create(amount, Idr).Value;

    private sealed class VersionStore(bool active) : IConfigVersionStore
    {
        public Task<ConfigVersion?> GetActiveAsync(IDatabaseSession session, string domain, DateTimeOffset at, CancellationToken cancellationToken = default) =>
            Task.FromResult(active && domain == ConfigDomains.Fee ? new ConfigVersion(VersionId, domain, 7, null, null) : null);
    }

    private sealed class RuleReader(IReadOnlyList<FeeRule> rules) : IFeeRuleReader
    {
        public Task<IReadOnlyList<FeeRule>> GetRulesAsync(
            IDatabaseSession session, Guid configVersionId, ProductId productId, TransactionType transactionType, MerchantId merchantId,
            CurrencyDefinition currency, DateTimeOffset at, CancellationToken cancellationToken = default) => Task.FromResult(rules);
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class Session : IDatabaseSession
    {
        public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RollbackAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task CreateSavepointAsync(string name, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RollbackToSavepointAsync(string name, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ReleaseSavepointAsync(string name, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
