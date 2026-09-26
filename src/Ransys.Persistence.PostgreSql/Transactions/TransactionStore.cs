using Dapper;
using Ransys.Domain;
using Ransys.Domain.Common;
using Ransys.Domain.Fees;
using Ransys.Domain.Monetary;
using Ransys.Domain.Routing;
using Ransys.Domain.Transactions;
using Ransys.Persistence.PostgreSql.ReferenceData;

namespace Ransys.Persistence.PostgreSql.Transactions;

/// <summary>Row lock requested when loading a transaction.</summary>
public enum RowLock
{
    None,

    /// <summary><c>FOR UPDATE</c>: step 1 of the finalization lock order (ERD v1.1 §23).</summary>
    ForUpdate,

    /// <summary><c>FOR UPDATE NOWAIT</c>: fails immediately if another session holds the row.</summary>
    ForUpdateNoWait,
}

/// <summary>
/// Persists the <see cref="Transaction"/> aggregate to <c>core.transactions</c>, its fee components and its
/// state history (ADR-004), inside the caller's <see cref="PostgresSession"/>.
/// Updates use optimistic concurrency on <c>row_version</c> (State Transition Matrix §57, ERD v1.1 §44).
/// </summary>
public sealed class TransactionStore
{
    private const string SelectSql = """
        SELECT t.ransys_transaction_id, t.merchant_id, t.channel_id, t.product_id, t.transaction_type,
               t.client_reference, t.idempotency_key, t.transaction_fingerprint, t.original_transaction_id,
               t.amount, t.merchant_charge_amount, t.total_reserve_amount,
               cd.currency_code, cd.version_no AS currency_version_no, cd.scale AS currency_scale,
               t.processing_status, t.financial_status, t.reconciliation_status, t.settlement_status,
               t.ransys_response_code, t.reason_code, t.reason_description,
               t.initial_selected_provider_id,
               pi.provider_code AS initial_provider_code, pi.adapter_service_name AS initial_adapter_service_name,
               t.actual_provider_id,
               pa.provider_code AS actual_provider_code, pa.adapter_service_name AS actual_adapter_service_name,
               t.routing_rule_version, t.failover_count, t.failover_reason, t.routing_decided_at,
               t.config_version_id, t.routing_config_version_id, t.fee_config_version_id, t.provider_policy_version,
               t.metadata::text AS metadata, t.canonical_detail::text AS canonical_detail,
               t.received_at, t.validated_at, t.financial_posted_at, t.completed_at, t.updated_at, t.row_version
        FROM core.transactions t
        JOIN core.currency_definitions cd ON cd.currency_definition_id = t.currency_definition_id
        LEFT JOIN integration.providers pi ON pi.provider_id = t.initial_selected_provider_id
        LEFT JOIN integration.providers pa ON pa.provider_id = t.actual_provider_id
        WHERE t.ransys_transaction_id = @Id
        """;

    private const string InsertSql = """
        INSERT INTO core.transactions (
            ransys_transaction_id, merchant_id, channel_id, product_id, transaction_type,
            client_reference, idempotency_key, transaction_fingerprint, original_transaction_id,
            amount, currency_definition_id, merchant_charge_amount, total_reserve_amount,
            processing_status, financial_status, reconciliation_status, settlement_status,
            ransys_response_code, reason_code, reason_description,
            initial_selected_provider_id, actual_provider_id,
            routing_rule_version, failover_count, failover_reason, routing_decided_at,
            config_version_id, routing_config_version_id, fee_config_version_id, provider_policy_version,
            metadata, canonical_detail,
            received_at, validated_at, financial_posted_at, completed_at, updated_at, row_version)
        VALUES (
            @Id, @MerchantId, @ChannelId, @ProductId, @TransactionType,
            @ClientReference, @IdempotencyKey, @Fingerprint, @OriginalTransactionId,
            @Amount, @CurrencyDefinitionId, @MerchantChargeAmount, @TotalReserveAmount,
            @ProcessingStatus, @FinancialStatus, @ReconciliationStatus, @SettlementStatus,
            @ResponseCode, @ReasonCode, @ReasonDescription,
            @InitialProviderId, @ActualProviderId,
            @RoutingRuleVersion, @FailoverCount, @FailoverReason, @RoutingDecidedAt,
            @ConfigVersionId, @RoutingConfigVersionId, @FeeConfigVersionId, @ProviderPolicyVersion,
            CAST(@Metadata AS jsonb), CAST(@CanonicalDetail AS jsonb),
            @ReceivedAt, @ValidatedAt, @FinancialPostedAt, @CompletedAt, @UpdatedAt, 1)
        """;

    // Identity, amount, currency, canonical detail and metadata are immutable after creation and never updated.
    private const string UpdateSql = """
        UPDATE core.transactions SET
            merchant_charge_amount = @MerchantChargeAmount,
            total_reserve_amount = @TotalReserveAmount,
            processing_status = @ProcessingStatus,
            financial_status = @FinancialStatus,
            reconciliation_status = @ReconciliationStatus,
            settlement_status = @SettlementStatus,
            ransys_response_code = @ResponseCode,
            reason_code = @ReasonCode,
            reason_description = @ReasonDescription,
            initial_selected_provider_id = @InitialProviderId,
            actual_provider_id = @ActualProviderId,
            routing_rule_version = @RoutingRuleVersion,
            failover_count = @FailoverCount,
            failover_reason = @FailoverReason,
            routing_decided_at = @RoutingDecidedAt,
            config_version_id = @ConfigVersionId,
            routing_config_version_id = @RoutingConfigVersionId,
            fee_config_version_id = @FeeConfigVersionId,
            provider_policy_version = @ProviderPolicyVersion,
            validated_at = @ValidatedAt,
            financial_posted_at = @FinancialPostedAt,
            completed_at = @CompletedAt,
            updated_at = @UpdatedAt,
            row_version = row_version + 1
        WHERE ransys_transaction_id = @Id
          AND row_version = @ExpectedRowVersion
        """;

    private const string InsertFeeSql = """
        INSERT INTO core.transaction_fee_components (
            fee_component_id, ransys_transaction_id, component_type, charged_amount, accounting_amount,
            currency_definition_id, beneficiary_type, beneficiary_id, refundable, calculation_rule_version, created_at)
        VALUES (
            @FeeComponentId, @TransactionId, @ComponentType, @ChargedAmount, @AccountingAmount,
            @CurrencyDefinitionId, @BeneficiaryType, @BeneficiaryId, @Refundable, @CalculationRuleVersion, @CreatedAt)
        """;

    private const string InsertHistorySql = """
        INSERT INTO core.transaction_state_history (
            history_id, ransys_transaction_id, transaction_attempt_id, status_dimension,
            previous_status, new_status, reason_code, reason_description, change_source, created_at)
        VALUES (
            @HistoryId, @TransactionId, @AttemptId, @Dimension,
            @PreviousStatus, @NewStatus, @ReasonCode, @ReasonDescription, @ChangeSource, @CreatedAt)
        """;

    private const string SelectFeesSql = """
        SELECT component_type, charged_amount, accounting_amount, beneficiary_type, beneficiary_id,
               refundable, calculation_rule_version
        FROM core.transaction_fee_components
        WHERE ransys_transaction_id = @Id
        ORDER BY created_at, fee_component_id
        """;

    private static readonly string ValidatedCode = CanonicalCodes.ProcessingStatus.ToCode(ProcessingStatus.Validated);

    private readonly ReferenceDataStore _referenceData;

    static TransactionStore() => DbValues.EnsureConfigured();

    public TransactionStore(ReferenceDataStore referenceData) =>
        _referenceData = referenceData ?? throw new ArgumentNullException(nameof(referenceData));

    /// <summary>Inserts a new transaction with its pending history (and fee components if already validated).</summary>
    public async Task<Result> InsertAsync(PostgresSession session, Transaction transaction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(transaction);
        if (transaction.IsPersisted)
        {
            throw new InvalidOperationException($"Transaction {transaction.Id} is already persisted; use UpdateAsync.");
        }

        var currencyId = await _referenceData.ResolveCurrencyDefinitionIdAsync(session, transaction.Amount.Currency, cancellationToken);
        if (currencyId.IsFailure)
        {
            return currencyId.Error;
        }

        var parameters = new DynamicParameters(StateParameters(transaction));
        parameters.AddDynamicParams(new
        {
            MerchantId = transaction.MerchantId.Value,
            ChannelId = transaction.ChannelId.Value,
            ProductId = transaction.ProductId.Value,
            TransactionType = CanonicalCodes.TransactionType.ToCode(transaction.Type),
            transaction.Identity.ClientReference,
            transaction.Identity.IdempotencyKey,
            Fingerprint = transaction.Identity.Fingerprint.ToPersistedString(),
            OriginalTransactionId = transaction.Identity.OriginalTransactionId?.Value,
            transaction.Amount.Amount,
            CurrencyDefinitionId = currencyId.Value,
            Metadata = DbValues.MetadataToJson(transaction.Metadata),
            CanonicalDetail = DbValues.ToJson(CanonicalDetailDocument.From(transaction)),
            ReceivedAt = DbValues.ToDb(transaction.ReceivedAt),
        });

        await Execute(session, InsertSql, parameters, cancellationToken);

        if (transaction.ValidatedAt is not null)
        {
            await InsertFeesAsync(session, transaction, currencyId.Value, cancellationToken);
        }

        await InsertHistoryAsync(session, transaction, cancellationToken);
        transaction.MarkPersisted(1);
        return Result.Success();
    }

    /// <summary>
    /// Writes state changes of a loaded transaction. Fails with <see cref="ErrorCodes.ConcurrencyConflict"/>
    /// when another writer updated the row since it was loaded; the caller reloads and re-evaluates.
    /// </summary>
    public async Task<Result> UpdateAsync(PostgresSession session, Transaction transaction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(transaction);
        if (!transaction.IsPersisted)
        {
            throw new InvalidOperationException($"Transaction {transaction.Id} has not been inserted yet.");
        }

        var parameters = new DynamicParameters(StateParameters(transaction));
        parameters.Add("ExpectedRowVersion", transaction.RowVersion);

        var affected = await Execute(session, UpdateSql, parameters, cancellationToken);
        if (affected == 0)
        {
            return RansysError.Conflict(
                ErrorCodes.ConcurrencyConflict,
                $"Transaction {transaction.Id} was modified concurrently (expected row version {transaction.RowVersion}).");
        }

        var becameValidated = transaction.PendingStateChanges.Any(c =>
            c.Dimension == StatusDimension.Processing && c.NewStatus == ValidatedCode);
        if (becameValidated)
        {
            var currencyId = await _referenceData.ResolveCurrencyDefinitionIdAsync(session, transaction.Amount.Currency, cancellationToken);
            if (currencyId.IsFailure)
            {
                return currencyId.Error;
            }

            await InsertFeesAsync(session, transaction, currencyId.Value, cancellationToken);
        }

        await InsertHistoryAsync(session, transaction, cancellationToken);
        transaction.MarkPersisted(transaction.RowVersion + 1);
        return Result.Success();
    }

    /// <summary>
    /// Loads a transaction, optionally locking its row. Returns null when it does not exist, and a
    /// <see cref="ErrorCodes.PersistedStateInvalid"/> failure when persisted data violates domain rules (fail closed).
    /// </summary>
    public async Task<Result<Transaction?>> GetAsync(
        PostgresSession session, TransactionId id, RowLock rowLock = RowLock.None, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var sql = rowLock switch
        {
            RowLock.ForUpdate => SelectSql + "\nFOR UPDATE OF t",
            RowLock.ForUpdateNoWait => SelectSql + "\nFOR UPDATE OF t NOWAIT",
            _ => SelectSql,
        };

        var row = await session.Connection.QuerySingleOrDefaultAsync<TransactionRow>(new CommandDefinition(
            sql, new { Id = id.Value }, session.Transaction, cancellationToken: cancellationToken));
        if (row is null)
        {
            return Result<Transaction?>.Success(null);
        }

        var fees = row.ValidatedAt is null
            ? []
            : (await session.Connection.QueryAsync<FeeComponentRow>(new CommandDefinition(
                SelectFeesSql, new { Id = id.Value }, session.Transaction, cancellationToken: cancellationToken))).ToList();

        var transaction = TransactionRowMapper.ToAggregate(row, fees);
        return transaction.IsSuccess ? transaction.Value : transaction.Error;
    }

    /// <summary>Columns that change during the transaction lifecycle.</summary>
    private static object StateParameters(Transaction t) => new
    {
        Id = t.Id.Value,
        MerchantChargeAmount = t.Fees?.MerchantChargeTotal.Amount ?? 0m,
        TotalReserveAmount = t.ReserveAmount?.Amount ?? 0m,
        ProcessingStatus = CanonicalCodes.ProcessingStatus.ToCode(t.ProcessingStatus),
        FinancialStatus = CanonicalCodes.FinancialStatus.ToCode(t.FinancialStatus),
        ReconciliationStatus = CanonicalCodes.ReconciliationStatus.ToCode(t.ReconciliationStatus),
        SettlementStatus = CanonicalCodes.SettlementStatus.ToCode(t.SettlementStatus),
        t.ResponseCode,
        t.ReasonCode,
        t.ReasonDescription,
        InitialProviderId = t.Routing?.InitialProvider.ProviderId.Value,
        ActualProviderId = t.Routing?.CurrentProvider.ProviderId.Value,
        RoutingRuleVersion = t.Routing?.RuleVersion,
        FailoverCount = t.Routing?.FailoverCount ?? 0,
        t.Routing?.FailoverReason,
        RoutingDecidedAt = DbValues.ToDb(t.Routing?.DecisionTimestamp),
        t.Configuration.ConfigVersionId,
        t.Configuration.RoutingConfigVersionId,
        t.Configuration.FeeConfigVersionId,
        t.Configuration.ProviderPolicyVersion,
        ValidatedAt = DbValues.ToDb(t.ValidatedAt),
        FinancialPostedAt = DbValues.ToDb(t.FinancialPostedAt),
        CompletedAt = DbValues.ToDb(t.CompletedAt),
        UpdatedAt = DbValues.ToDb(t.UpdatedAt),
    };

    private static async Task InsertFeesAsync(
        PostgresSession session, Transaction transaction, Guid currencyDefinitionId, CancellationToken cancellationToken)
    {
        var fees = transaction.Fees?.Items ?? [];
        if (fees.IsEmpty)
        {
            return;
        }

        var rows = fees.Select(f => new
        {
            FeeComponentId = Guid.CreateVersion7(),
            TransactionId = transaction.Id.Value,
            ComponentType = CanonicalCodes.FeeComponentType.ToCode(f.ComponentType),
            ChargedAmount = f.ChargedAmount.Amount,
            AccountingAmount = f.AccountingAmount.Amount,
            CurrencyDefinitionId = currencyDefinitionId,
            BeneficiaryType = f.Beneficiary.Type,
            BeneficiaryId = f.Beneficiary.Id,
            f.Refundable,
            f.CalculationRuleVersion,
            CreatedAt = DbValues.ToDb(transaction.ValidatedAt!.Value),
        });

        await Execute(session, InsertFeeSql, rows, cancellationToken);
    }

    private static async Task InsertHistoryAsync(PostgresSession session, Transaction transaction, CancellationToken cancellationToken)
    {
        if (transaction.PendingStateChanges.Count == 0)
        {
            return;
        }

        var rows = transaction.PendingStateChanges.Select(c => new
        {
            HistoryId = Guid.CreateVersion7(),
            TransactionId = transaction.Id.Value,
            AttemptId = c.AttemptId?.Value,
            Dimension = CanonicalCodes.StatusDimension.ToCode(c.Dimension),
            c.PreviousStatus,
            c.NewStatus,
            c.ReasonCode,
            c.ReasonDescription,
            ChangeSource = CanonicalCodes.ChangeSource.ToCode(c.Source),
            CreatedAt = DbValues.ToDb(c.OccurredAt),
        });

        await Execute(session, InsertHistorySql, rows, cancellationToken);
    }

    private static Task<int> Execute(PostgresSession session, string sql, object parameters, CancellationToken cancellationToken) =>
        session.Connection.ExecuteAsync(new CommandDefinition(sql, parameters, session.Transaction, cancellationToken: cancellationToken));
}

/// <summary>Maps rows back to the aggregate through validating domain factories.</summary>
internal static class TransactionRowMapper
{
    public static Result<Transaction> ToAggregate(TransactionRow row, IReadOnlyList<FeeComponentRow> feeRows)
    {
        var currency = CurrencyDefinition.Create(row.CurrencyCode, row.CurrencyVersionNo, row.CurrencyScale);
        if (currency.IsFailure)
        {
            return Invalid(row, currency.Error);
        }

        var amount = Money.Create(row.Amount, currency.Value);
        var fingerprint = TransactionFingerprint.Parse(row.TransactionFingerprint);
        if (amount.IsFailure || fingerprint.IsFailure)
        {
            return Invalid(row, amount.IsFailure ? amount.Error : fingerprint.Error);
        }

        var identity = TransactionIdentity.Create(
            new TransactionId(row.RansysTransactionId),
            row.ClientReference,
            row.IdempotencyKey,
            fingerprint.Value,
            row.OriginalTransactionId is { } original ? new TransactionId(original) : null);
        if (identity.IsFailure)
        {
            return Invalid(row, identity.Error);
        }

        var detail = DbValues.FromJson<CanonicalDetailDocument>(row.CanonicalDetail);
        var customer = detail.ToCustomer();
        var source = CanonicalDetailDocument.ToEndpoint(detail.Source);
        var destination = CanonicalDetailDocument.ToEndpoint(detail.Destination);
        var references = detail.ToReferences(row.ClientReference);
        var metadata = DbValues.MetadataFromJson(row.Metadata);
        var fees = row.ValidatedAt is null ? Result<FeeComponents?>.Success(null) : ToFees(feeRows, currency.Value);
        var routing = ToRouting(row);
        var reserve = ToReserve(row, currency.Value);

        var firstError = new[]
        {
            customer.IsFailure ? customer.Error : null,
            source.IsFailure ? source.Error : null,
            destination.IsFailure ? destination.Error : null,
            references.IsFailure ? references.Error : null,
            metadata.IsFailure ? metadata.Error : null,
            fees.IsFailure ? fees.Error : null,
            routing.IsFailure ? routing.Error : null,
            reserve.IsFailure ? reserve.Error : null,
        }.FirstOrDefault(e => e is not null);
        if (firstError is not null)
        {
            return Invalid(row, firstError);
        }

        if (!CanonicalCodes.TransactionType.TryParse(row.TransactionType, out var type)
            || !CanonicalCodes.ProcessingStatus.TryParse(row.ProcessingStatus, out var processing)
            || !CanonicalCodes.FinancialStatus.TryParse(row.FinancialStatus, out var financial)
            || !CanonicalCodes.ReconciliationStatus.TryParse(row.ReconciliationStatus, out var reconciliation)
            || !CanonicalCodes.SettlementStatus.TryParse(row.SettlementStatus, out var settlement))
        {
            return Invalid(row, RansysError.Validation(ErrorCodes.OutOfRange, "Unknown type or status code.", "status"));
        }

        if (fees.Value is { } feeSet && feeSet.MerchantChargeTotal.Amount != row.MerchantChargeAmount)
        {
            return Invalid(row, RansysError.Validation(
                ErrorCodes.PersistedStateInvalid, "merchant_charge_amount differs from the sum of fee components.", "fees"));
        }

        var snapshot = new TransactionSnapshot(
            identity.Value,
            type,
            new MerchantId(row.MerchantId),
            new ChannelId(row.ChannelId),
            new ProductId(row.ProductId),
            amount.Value,
            fees.Value,
            reserve.Value,
            customer.Value,
            source.Value,
            destination.Value,
            references.Value,
            routing.Value,
            new TransactionConfigurationSnapshot(
                row.ConfigVersionId, row.RoutingConfigVersionId, row.FeeConfigVersionId, row.ProviderPolicyVersion),
            metadata.Value,
            processing,
            financial,
            reconciliation,
            settlement,
            row.RansysResponseCode,
            row.ReasonCode,
            row.ReasonDescription,
            DbValues.FromDb(row.ReceivedAt),
            DbValues.FromDb(row.ValidatedAt),
            DbValues.FromDb(row.FinancialPostedAt),
            DbValues.FromDb(row.CompletedAt),
            DbValues.FromDb(row.UpdatedAt),
            row.RowVersion);

        return Transaction.Rehydrate(snapshot);
    }

    /// <summary><c>total_reserve_amount</c> is 0 until a reservation exists (DDL default).</summary>
    private static Result<Money?> ToReserve(TransactionRow row, CurrencyDefinition currency)
    {
        if (row.TotalReserveAmount == 0m)
        {
            return Result<Money?>.Success(null);
        }

        var reserve = Money.Create(row.TotalReserveAmount, currency);
        return reserve.IsSuccess ? reserve.Value : reserve.Error;
    }

    private static Result<FeeComponents?> ToFees(IReadOnlyList<FeeComponentRow> rows, CurrencyDefinition currency)
    {
        var components = new List<FeeComponent>();
        foreach (var row in rows)
        {
            if (!CanonicalCodes.FeeComponentType.TryParse(row.ComponentType, out var type))
            {
                return RansysError.Validation(ErrorCodes.OutOfRange, $"Unknown fee component type '{row.ComponentType}'.", "fees");
            }

            var charged = Money.Create(row.ChargedAmount, currency);
            var accounting = Money.Create(row.AccountingAmount, currency);
            var beneficiary = FeeBeneficiary.Create(row.BeneficiaryType, row.BeneficiaryId);
            if (charged.IsFailure || accounting.IsFailure || beneficiary.IsFailure)
            {
                return charged.IsFailure ? charged.Error : accounting.IsFailure ? accounting.Error : beneficiary.Error;
            }

            var component = FeeComponent.Create(type, charged.Value, accounting.Value, beneficiary.Value, row.Refundable, row.CalculationRuleVersion);
            if (component.IsFailure)
            {
                return component.Error;
            }

            components.Add(component.Value);
        }

        var fees = FeeComponents.Create(components, currency);
        return fees.IsSuccess ? fees.Value : fees.Error;
    }

    private static Result<RoutingDecision?> ToRouting(TransactionRow row)
    {
        if (row.InitialSelectedProviderId is null && row.ActualProviderId is null)
        {
            return Result<RoutingDecision?>.Success(null);
        }

        if (row.InitialSelectedProviderId is not { } initialId || row.ActualProviderId is not { } actualId
            || row.RoutingRuleVersion is not { } ruleVersion || row.RoutingDecidedAt is not { } decidedAt)
        {
            return RansysError.Validation(ErrorCodes.RoutingInvalidDecision, "Routing columns are incomplete.", "routing");
        }

        var initial = ProviderReference.Create(new ProviderId(initialId), row.InitialProviderCode, row.InitialAdapterServiceName);
        var actual = ProviderReference.Create(new ProviderId(actualId), row.ActualProviderCode, row.ActualAdapterServiceName);
        if (initial.IsFailure || actual.IsFailure)
        {
            return initial.IsFailure ? initial.Error : actual.Error;
        }

        var decision = RoutingDecision.Create(
            initial.Value, actual.Value, ruleVersion, row.FailoverCount, row.FailoverReason, DbValues.FromDb(decidedAt));
        return decision.IsSuccess ? decision.Value : decision.Error;
    }

    private static RansysError Invalid(TransactionRow row, RansysError cause) =>
        new(ErrorCodes.PersistedStateInvalid, ErrorCategory.Internal,
            $"Persisted transaction {row.RansysTransactionId} cannot be loaded: {cause}.");
}
