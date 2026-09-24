using System.Text.Json;
using System.Text.Json.Serialization;
using Keel.Application.Budget;
using Keel.Application.Messaging;
using Keel.Domain;
using Keel.Domain.Budgeting;
using Keel.Domain.Entities;
using Keel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Keel.Infrastructure.Budgeting;

/// <summary>
/// <see cref="IBudgetService"/> over the open budget file: aggregates in SQL
/// (<see cref="BudgetAggregationQuery"/>), computes with <see cref="BudgetCalculator"/>, and maps to
/// DTOs. Mutations run in one short-lived context (one database transaction), write an
/// <see cref="AuditEvent"/> per changed row with before/after JSON, and publish
/// <see cref="BudgetChanged"/> after the commit.
/// </summary>
public sealed class BudgetService(IDbContextFactory<KeelDbContext> contextFactory, IMessageBus messageBus, TimeProvider timeProvider) : IBudgetService
{
    /// <summary>Audit entity type of assignment rows.</summary>
    public const string AssignmentEntityType = nameof(BudgetAssignment);

    /// <summary>Audit entity type of target rows.</summary>
    public const string TargetEntityType = nameof(Target);

    private static readonly JsonSerializerOptions JsonOptions = new() { Converters = { new JsonStringEnumConverter() } };

    /// <inheritdoc />
    public async Task<BudgetMonthDto> GetMonthAsync(DateOnly month, CancellationToken ct) =>
        (await GetRangeAsync(month, month, ct).ConfigureAwait(false))[0];

    /// <inheritdoc />
    public async Task<IReadOnlyList<BudgetMonthDto>> GetRangeAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        from = BudgetMonth.Of(from);
        to = BudgetMonth.Of(to);
        if (to < from)
        {
            throw new ArgumentException("The range ends before it starts.", nameof(to));
        }

        var db = contextFactory.CreateDbContext();
        await using (db.ConfigureAwait(false))
        {
            var input = await BudgetAggregationQuery.LoadInputAsync(db, ct).ConfigureAwait(false);
            var targets = await db.Targets.AsNoTracking().ToDictionaryAsync(t => t.CategoryId, ct).ConfigureAwait(false);
            var balances = await BudgetAggregationQuery.CardBalancesAsync(db, from, to, ct).ConfigureAwait(false);
            var currency = await CurrencyAsync(db, ct).ConfigureAwait(false);
            var snapshot = BudgetCalculator.Compute(input, from, to);
            return [.. snapshot.Months.Select(m => BudgetDtoMapper.ToDto(m, input, targets, balances[m.Month], currency))];
        }
    }

    /// <inheritdoc />
    public async Task<BudgetExplanationDto> ExplainAsync(Guid? categoryId, DateOnly month, CancellationToken ct)
    {
        month = BudgetMonth.Of(month);
        var db = contextFactory.CreateDbContext();
        await using (db.ConfigureAwait(false))
        {
            var input = await BudgetAggregationQuery.LoadInputAsync(db, ct).ConfigureAwait(false);
            var snapshot = BudgetCalculator.Compute(input, month, month);
            var explanation = categoryId is { } id ? snapshot.Explain(id, month) : snapshot.ExplainReadyToAssign(month);
            return BudgetDtoMapper.ToDto(explanation, input, await CurrencyAsync(db, ct).ConfigureAwait(false));
        }
    }

    /// <inheritdoc />
    public async Task AssignAsync(Guid categoryId, DateOnly month, long assigned, CancellationToken ct)
    {
        month = BudgetMonth.Of(month);
        var db = contextFactory.CreateDbContext();
        await using (db.ConfigureAwait(false))
        {
            await RequireAssignableAsync(db, categoryId, ct).ConfigureAwait(false);
            var changed = await SetAssignedAsync(db, categoryId, month, _ => assigned, ct).ConfigureAwait(false);
            if (!changed)
            {
                return;
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        messageBus.Publish(new BudgetChanged([month]));
    }

    /// <inheritdoc />
    public async Task MoveMoneyAsync(MoveMoneyRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Amount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.Amount, "The amount to move must be positive.");
        }

        if (request.FromCategoryId == request.ToCategoryId)
        {
            throw new ArgumentException("Money must move between two different categories.", nameof(request));
        }

        var month = BudgetMonth.Of(request.Month);
        var db = contextFactory.CreateDbContext();
        await using (db.ConfigureAwait(false))
        {
            // Ready to Assign (null) needs no row: moving to or from it only changes the other side.
            if (request.FromCategoryId is { } from)
            {
                await RequireAssignableAsync(db, from, ct).ConfigureAwait(false);
                await SetAssignedAsync(db, from, month, current => checked(current - request.Amount), ct).ConfigureAwait(false);
            }

            if (request.ToCategoryId is { } to)
            {
                await RequireAssignableAsync(db, to, ct).ConfigureAwait(false);
                await SetAssignedAsync(db, to, month, current => checked(current + request.Amount), ct).ConfigureAwait(false);
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        messageBus.Publish(new BudgetChanged([month]));
    }

    /// <inheritdoc />
    public async Task<TargetDto?> GetTargetAsync(Guid categoryId, CancellationToken ct)
    {
        var db = contextFactory.CreateDbContext();
        await using (db.ConfigureAwait(false))
        {
            var target = await db.Targets.AsNoTracking().SingleOrDefaultAsync(t => t.CategoryId == categoryId, ct).ConfigureAwait(false);
            return target is null ? null : new TargetDto(target.CategoryId, target.Type, target.Amount, target.TargetDate, target.LinkedAccountId);
        }
    }

    /// <inheritdoc />
    public async Task SetTargetAsync(TargetDto target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Amount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(target), target.Amount, "A target amount must be positive.");
        }

        if (target.Type == TargetType.SavingsBalanceByDate && target.TargetDate is null)
        {
            throw new ArgumentException("A savings-balance target needs a target date.", nameof(target));
        }

        var db = contextFactory.CreateDbContext();
        await using (db.ConfigureAwait(false))
        {
            await RequireAssignableAsync(db, target.CategoryId, ct).ConfigureAwait(false);
            if (target.Type == TargetType.DebtPayment)
            {
                var account = target.LinkedAccountId is { } linked
                    ? await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == linked, ct).ConfigureAwait(false)
                    : null;
                if (account is null || !AccountTypeInfo.IsLiability(account.Type))
                {
                    throw new ArgumentException("A debt-payment target needs a linked loan or credit account.", nameof(target));
                }
            }
            else if (target.LinkedAccountId is { } linked && !await db.Accounts.AnyAsync(a => a.Id == linked, ct).ConfigureAwait(false))
            {
                throw new ArgumentException($"Account {linked} does not exist.", nameof(target));
            }

            var row = await db.Targets.SingleOrDefaultAsync(t => t.CategoryId == target.CategoryId, ct).ConfigureAwait(false);
            var before = row is null ? null : Serialize(ToState(row));
            if (row is null)
            {
                row = new Target { CategoryId = target.CategoryId };
                db.Targets.Add(row);
            }

            row.Type = target.Type;
            row.Amount = target.Amount;
            row.TargetDate = target.TargetDate;
            row.LinkedAccountId = target.LinkedAccountId;
            row.Cadence = target.Type == TargetType.SavingsBalanceByDate ? null : RecurrenceCadence.Monthly;
            var after = Serialize(ToState(row));
            if (before == after)
            {
                return;
            }

            Audit(db, before is null ? AuditEventKind.Created : AuditEventKind.Updated, TargetEntityType, target.CategoryId.ToString("D"), before, after);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        messageBus.Publish(new BudgetChanged([CurrentMonth()]));
    }

    /// <inheritdoc />
    public async Task DeleteTargetAsync(Guid categoryId, CancellationToken ct)
    {
        var db = contextFactory.CreateDbContext();
        await using (db.ConfigureAwait(false))
        {
            var row = await db.Targets.SingleOrDefaultAsync(t => t.CategoryId == categoryId, ct).ConfigureAwait(false);
            if (row is null)
            {
                return;
            }

            db.Targets.Remove(row);
            Audit(db, AuditEventKind.Deleted, TargetEntityType, categoryId.ToString("D"), Serialize(ToState(row)), null);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        messageBus.Publish(new BudgetChanged([CurrentMonth()]));
    }

    /// <inheritdoc />
    public async Task<FundTargetsResult> FundTargetsAsync(DateOnly month, CancellationToken ct)
    {
        month = BudgetMonth.Of(month);
        long funded = 0;
        long shortfall = 0;
        var count = 0;
        string currency;
        var db = contextFactory.CreateDbContext();
        await using (db.ConfigureAwait(false))
        {
            var input = await BudgetAggregationQuery.LoadInputAsync(db, ct).ConfigureAwait(false);
            currency = await CurrencyAsync(db, ct).ConfigureAwait(false);
            var targets = await db.Targets.AsNoTracking().ToDictionaryAsync(t => t.CategoryId, ct).ConfigureAwait(false);
            var result = BudgetCalculator.ComputeMonth(input, month);
            var remaining = Math.Max(0, result.ReadyToAssign);

            // Display order; hidden categories are not funded (the user cannot see them).
            foreach (var cell in result.Categories)
            {
                if (!cell.IsVisible || !targets.TryGetValue(cell.CategoryId, out var target))
                {
                    continue;
                }

                var need = TargetCalculator.Compute(target, cell).Underfunded;
                if (need <= 0)
                {
                    continue;
                }

                var give = Math.Min(need, remaining);
                shortfall += need - give;
                if (give == 0)
                {
                    continue;
                }

                await SetAssignedAsync(db, cell.CategoryId, month, current => checked(current + give), ct).ConfigureAwait(false);
                remaining -= give;
                funded += give;
                count++;
            }

            if (count > 0)
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
        }

        if (count > 0)
        {
            messageBus.Publish(new BudgetChanged([month]));
        }

        return new FundTargetsResult(new Money(funded, currency), new Money(shortfall, currency), count);
    }

    /// <inheritdoc />
    public async Task<QuickAssignDto> GetQuickAssignAsync(Guid categoryId, DateOnly month, CancellationToken ct)
    {
        month = BudgetMonth.Of(month);
        var db = contextFactory.CreateDbContext();
        await using (db.ConfigureAwait(false))
        {
            var input = await BudgetAggregationQuery.LoadInputAsync(db, ct).ConfigureAwait(false);
            var target = await db.Targets.AsNoTracking().SingleOrDefaultAsync(t => t.CategoryId == categoryId, ct).ConfigureAwait(false);
            var snapshot = BudgetCalculator.Compute(input, BudgetMonth.Add(month, -3), month);
            var status = target is null ? null : TargetCalculator.Compute(target, snapshot.Cell(categoryId, month));
            return BudgetDtoMapper.ToDto(QuickAssign.Compute(snapshot, categoryId, month, status), categoryId, month, await CurrencyAsync(db, ct).ConfigureAwait(false));
        }
    }

    /// <summary>The budget's currency: that of the first on-budget account (single base currency, PRD D3).</summary>
    private static async Task<string> CurrencyAsync(KeelDbContext db, CancellationToken ct) =>
        await db.Accounts.AsNoTracking()
            .Where(a => a.IsOnBudget)
            .OrderBy(a => a.SortOrder).ThenBy(a => a.Id)
            .Select(a => a.Currency)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false) ?? Currency.Default;

    private static async Task RequireAssignableAsync(KeelDbContext db, Guid categoryId, CancellationToken ct)
    {
        var category = await db.Categories.AsNoTracking().SingleOrDefaultAsync(c => c.Id == categoryId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Category {categoryId} does not exist.");
        if (category.Id == SystemIds.ReadyToAssignCategory || category.GroupId == SystemIds.InflowGroup)
        {
            throw new InvalidOperationException("Inflow categories such as Ready to Assign cannot be assigned money.");
        }
    }

    /// <summary>Applies a change to one assignment row with its audit event; returns false when nothing changed.</summary>
    private async Task<bool> SetAssignedAsync(KeelDbContext db, Guid categoryId, DateOnly month, Func<long, long> change, CancellationToken ct)
    {
        var row = db.BudgetAssignments.Local.SingleOrDefault(a => a.CategoryId == categoryId && a.Month == month)
            ?? await db.BudgetAssignments.SingleOrDefaultAsync(a => a.CategoryId == categoryId && a.Month == month, ct).ConfigureAwait(false);
        var current = row?.Assigned ?? 0;
        var next = change(current);
        if (next == current)
        {
            return false;
        }

        var id = $"{categoryId:D}/{month:yyyy-MM-dd}";
        var before = row is null ? null : Serialize(new AssignmentState(categoryId, month, current));
        var after = next == 0 ? null : Serialize(new AssignmentState(categoryId, month, next));
        if (row is null)
        {
            db.BudgetAssignments.Add(new BudgetAssignment { CategoryId = categoryId, Month = month, Assigned = next });
            Audit(db, AuditEventKind.Created, AssignmentEntityType, id, null, after);
        }
        else if (next == 0)
        {
            db.BudgetAssignments.Remove(row);                  // absent row means 0
            Audit(db, AuditEventKind.Deleted, AssignmentEntityType, id, before, null);
        }
        else
        {
            row.Assigned = next;
            Audit(db, AuditEventKind.Updated, AssignmentEntityType, id, before, after);
        }

        return true;
    }

    private void Audit(KeelDbContext db, AuditEventKind kind, string entityType, string entityId, string? before, string? after) =>
        db.AuditEvents.Add(new AuditEvent
        {
            At = timeProvider.GetUtcNow().UtcDateTime,
            Kind = kind,
            EntityType = entityType,
            EntityId = entityId,
            BeforeJson = before,
            AfterJson = after,
        });

    private DateOnly CurrentMonth() => BudgetMonth.Of(DateOnly.FromDateTime(timeProvider.GetLocalNow().DateTime));

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    private static TargetState ToState(Target t) => new(t.CategoryId, t.Type, t.Amount, t.TargetDate, t.Cadence, t.LinkedAccountId);

    /// <summary>Audit JSON of an assignment row.</summary>
    internal sealed record AssignmentState(Guid CategoryId, DateOnly Month, long Assigned);

    /// <summary>Audit JSON of a target row.</summary>
    internal sealed record TargetState(Guid CategoryId, TargetType Type, long Amount, DateOnly? TargetDate, RecurrenceCadence? Cadence, Guid? LinkedAccountId);
}
