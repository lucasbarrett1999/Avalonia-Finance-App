using System.Globalization;
using System.Text.Json;
using Keel.Application.Accounts;
using Keel.Application.Import;
using Keel.Application.Ledger;
using Keel.Application.Messaging;
using Keel.Application.Security;
using Keel.Application.Sync;
using Keel.Application.Undo;
using Keel.Domain;
using Keel.Domain.Entities;
using Keel.Domain.Ledger;
using Keel.Infrastructure.Ledger;
using Keel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Keel.Infrastructure.Sync;

/// <summary>
/// Bank sync (F-TXN-3, PRD 7.5). Pages from a provider are mapped to one <see cref="ImportBatch"/>
/// per account and sent through <see cref="IImportService"/> with source Provider, so dedup (provider
/// id, pending to posted), rules and review work as for files; removed provider ids soft-delete their
/// rows; <c>SyncConnection.Cursor</c> is stored only after a page's writes commit. Connection state
/// (cursor, health, last sync) is not ledger data: it is saved directly, not audited or undone.
/// One sync runs at a time.
/// </summary>
public sealed partial class SyncService : ISyncService
{
    /// <summary>Plaid's code for "restart pagination from the first cursor".</summary>
    public const string MutationDuringPagination = "TRANSACTIONS_SYNC_MUTATION_DURING_PAGINATION";

    /// <summary>Setting key prefix marking a new linked account whose starting balance is set once history arrives.</summary>
    public const string StartingBalancePrefix = "sync.startingBalance.";

    /// <summary>Setting key of <see cref="SyncSettings"/>.</summary>
    public const string SettingsKey = "sync.settings";

    private const int MaxPaginationRestarts = 3;
    private const int ChunkSize = 500;

    private readonly IDbContextFactory<KeelDbContext> _factory;
    private readonly LedgerWriter _writer;
    private readonly IImportService _import;
    private readonly IAccountService _accounts;
    private readonly IMessageBus _bus;
    private readonly TimeProvider _time;
    private readonly ILogger<SyncService> _logger;
    private readonly Dictionary<string, IBankDataProvider> _providers;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _running;

    /// <summary>Creates the service.</summary>
    public SyncService(
        IDbContextFactory<KeelDbContext> factory,
        LedgerWriter writer,
        IImportService import,
        IAccountService accounts,
        IEnumerable<IBankDataProvider> providers,
        IMessageBus bus,
        TimeProvider time,
        ILogger<SyncService> logger)
    {
        _factory = factory;
        _writer = writer;
        _import = import;
        _accounts = accounts;
        _bus = bus;
        _time = time;
        _logger = logger;
        _providers = providers.ToDictionary(p => p.ProviderId, StringComparer.Ordinal);
        Providers = _providers.Keys
            .Select(id => new BankProviderInfo(id, KindOf(id)))
            .OrderBy(p => p.Kind)
            .ToList();
    }

    /// <inheritdoc />
    public IReadOnlyList<BankProviderInfo> Providers { get; }

    /// <inheritdoc />
    public bool IsSyncing => Volatile.Read(ref _running) > 0;

    /// <summary>The provider id of a stored provider kind.</summary>
    public static string IdOf(SyncProvider provider) => provider == SyncProvider.SimpleFin ? "simplefin" : "plaid";

    /// <summary>The stored kind of a provider id.</summary>
    public static SyncProvider KindOf(string providerId) =>
        string.Equals(providerId, "simplefin", StringComparison.Ordinal) ? SyncProvider.SimpleFin : SyncProvider.Plaid;

    /// <summary>Setting key of the starting-balance marker of an account.</summary>
    public static string StartingBalanceKey(Guid accountId) => string.Create(CultureInfo.InvariantCulture, $"{StartingBalancePrefix}{accountId:D}");

    /// <inheritdoc />
    public Task<IReadOnlyList<SyncConnectionDto>> GetConnectionsAsync(CancellationToken ct) => Task.Run(
        async () =>
        {
            var db = _factory.CreateDbContext();
            await using (db.ConfigureAwait(false))
            {
                var connections = await db.SyncConnections.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
                var linked = await db.Accounts.AsNoTracking()
                    .Where(a => a.SyncConnectionId != null)
                    .OrderBy(a => a.SortOrder).ThenBy(a => a.Name)
                    .Select(a => new { a.Id, a.Name, a.SyncConnectionId, a.ProviderAccountId, a.ReportedBalance, a.Currency })
                    .ToListAsync(ct).ConfigureAwait(false);
                return (IReadOnlyList<SyncConnectionDto>)connections
                    .OrderBy(c => c.InstitutionName, StringComparer.CurrentCultureIgnoreCase)
                    .Select(c => new SyncConnectionDto(
                        c.Id,
                        c.Provider,
                        c.InstitutionName,
                        c.Status,
                        c.LastSyncAt,
                        c.LastError,
                        linked.Where(a => a.SyncConnectionId == c.Id)
                            .Select(a => new LinkedAccountDto(a.Id, a.Name, a.ProviderAccountId ?? string.Empty, a.ReportedBalance, a.Currency))
                            .ToList()))
                    .ToList();
            }
        },
        ct);

    /// <inheritdoc />
    public Task<LinkSession> BeginLinkAsync(string providerId, CancellationToken ct) =>
        Task.Run(() => Provider(providerId).BeginLinkAsync(LinkMode.New, null, ct), ct);

    /// <inheritdoc />
    public async Task<PendingConnection> CompleteLinkAsync(LinkSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        var provider = Provider(session.ProviderId);
        var result = await Task.Run(() => provider.CompleteLinkAsync(session, ct), ct).ConfigureAwait(false);
        if (!result.Succeeded || result.ConnectionId is null)
        {
            var code = result.Error ?? BankErrorCodes.Unknown;
            throw new BankProviderException(SyncStatus.Error, code, "The link did not finish: " + code);
        }

        IReadOnlyList<ProviderAccount> providerAccounts;
        IReadOnlyList<ProviderBalance> balances;
        try
        {
            providerAccounts = await provider.ListAccountsAsync(result.ConnectionId, ct).ConfigureAwait(false);
            balances = await provider.GetBalancesAsync(result.ConnectionId, ct).ConfigureAwait(false);
        }
        catch (BankProviderException)
        {
            await TryUnlinkAtProviderAsync(provider, result.ConnectionId).ConfigureAwait(false);
            throw;
        }

        var candidates = await Task.Run(
            async () =>
            {
                var db = _factory.CreateDbContext();
                await using (db.ConfigureAwait(false))
                {
                    return await db.Accounts.AsNoTracking()
                        .Where(a => !a.IsClosed && a.SyncConnectionId == null)
                        .Select(a => new { a.Id, a.Name, a.Type })
                        .ToListAsync(ct).ConfigureAwait(false);
                }
            },
            ct).ConfigureAwait(false);

        var suggested = new HashSet<Guid>();
        var pending = providerAccounts.Select(account =>
        {
            var match = candidates.FirstOrDefault(c => c.Type == account.SuggestedType && !suggested.Contains(c.Id)
                && (string.Equals(c.Name.Trim(), account.Name.Trim(), StringComparison.CurrentCultureIgnoreCase)
                    || (account.Mask is { Length: >= 2 } mask && c.Name.Contains(mask, StringComparison.Ordinal))));
            if (match is not null)
            {
                suggested.Add(match.Id);
            }

            var balance = balances.FirstOrDefault(b => b.ProviderAccountId == account.ProviderAccountId);
            return new PendingAccount(account, balance, match?.Id);
        }).ToList();

        LogLinked(_logger, provider.ProviderId, pending.Count);
        return new PendingConnection(
            provider.ProviderId,
            Guid.Parse(result.ConnectionId),
            string.IsNullOrWhiteSpace(result.InstitutionName) ? DefaultInstitutionName(provider.ProviderId) : result.InstitutionName.Trim(),
            result.ExternalItemId ?? string.Empty,
            pending);
    }

    /// <inheritdoc />
    public async Task<SyncConnectionDto> SaveLinkAsync(PendingConnection pending, IReadOnlyList<AccountLinkChoice> choices, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(choices);
        var known = pending.Accounts.ToDictionary(a => a.Account.ProviderAccountId, StringComparer.Ordinal);
        var active = choices.Where(c => c.Action != AccountLinkAction.Skip).ToList();
        if (active.Any(c => !known.ContainsKey(c.ProviderAccountId))
            || active.Select(c => c.ProviderAccountId).Distinct(StringComparer.Ordinal).Count() != active.Count)
        {
            throw new ArgumentException("Each provider account can be mapped once.", nameof(choices));
        }

        var existingTargets = active.Where(c => c.Action == AccountLinkAction.LinkExisting).Select(c => c.ExistingAccountId ?? Guid.Empty).ToList();
        if (existingTargets.Contains(Guid.Empty) || existingTargets.Distinct().Count() != existingTargets.Count)
        {
            throw new ArgumentException("Each existing account can be linked once.", nameof(choices));
        }

        // 1. The connection row (not ledger data).
        await Task.Run(
            async () =>
            {
                var db = _factory.CreateDbContext();
                await using (db.ConfigureAwait(false))
                {
                    var linkedElsewhere = await db.Accounts.AsNoTracking()
                        .AnyAsync(a => existingTargets.Contains(a.Id) && (a.SyncConnectionId != null || a.IsClosed), ct).ConfigureAwait(false);
                    if (linkedElsewhere)
                    {
                        throw new InvalidOperationException("An existing account chosen for the link is closed or already linked.");
                    }

                    db.SyncConnections.Add(new SyncConnection
                    {
                        Id = pending.ConnectionId,
                        Provider = KindOf(pending.ProviderId),
                        InstitutionName = Truncate(pending.InstitutionName, 200),
                        ExternalItemId = Truncate(pending.ExternalItemId, 200),
                        Status = SyncStatus.Ok,
                        SecretRef = SecretKeys.Connection(pending.ConnectionId),
                    });
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                }
            },
            ct).ConfigureAwait(false);

        // 2. New accounts (F-ACC-1, each undoable), then one action that links them all.
        var links = new List<(Guid AccountId, string ProviderAccountId)>();
        var today = DateOnly.FromDateTime(_time.GetLocalNow().DateTime);
        foreach (var choice in active)
        {
            if (choice.Action == AccountLinkAction.LinkExisting)
            {
                links.Add((choice.ExistingAccountId!.Value, choice.ProviderAccountId));
                continue;
            }

            var providerAccount = known[choice.ProviderAccountId].Account;
            var name = string.IsNullOrWhiteSpace(choice.NewName) ? providerAccount.Name : choice.NewName.Trim();
            var created = await _accounts.CreateAccountAsync(new CreateAccountRequest(name, choice.NewType, providerAccount.Currency, today, 0), ct).ConfigureAwait(false);
            links.Add((created.Id, choice.ProviderAccountId));
            await WriteSettingAsync(StartingBalanceKey(created.Id), "true", ct).ConfigureAwait(false);
        }

        if (links.Count > 0)
        {
            await _writer.RunAsync(
                LedgerAction.UpdateAccount,
                async session =>
                {
                    foreach (var (accountId, providerAccountId) in links)
                    {
                        var account = await LedgerLookups.OpenAccountAsync(session.Db, accountId, ct).ConfigureAwait(false);
                        account.SyncConnectionId = pending.ConnectionId;
                        account.ProviderAccountId = providerAccountId;
                    }

                    return links.Count;
                },
                ct).ConfigureAwait(false);
        }

        LogSaved(_logger, links.Count);
        _bus.Publish(new SyncConnectionsChanged([pending.ConnectionId]));
        return (await GetConnectionsAsync(ct).ConfigureAwait(false)).Single(c => c.Id == pending.ConnectionId);
    }

    /// <inheritdoc />
    public Task DiscardLinkAsync(PendingConnection pending, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pending);
        return TryUnlinkAtProviderAsync(Provider(pending.ProviderId), pending.ConnectionId.ToString());
    }

    /// <inheritdoc />
    public async Task<LinkSession> BeginReconnectAsync(Guid connectionId, CancellationToken ct)
    {
        var connection = await LoadConnectionAsync(connectionId, ct).ConfigureAwait(false);
        var provider = Provider(IdOf(connection.Provider));
        return await Task.Run(() => provider.BeginLinkAsync(LinkMode.Update, connectionId.ToString(), ct), ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<SyncConnectionDto> CompleteReconnectAsync(LinkSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!Guid.TryParse(session.ExistingConnectionId, out var connectionId))
        {
            throw new ArgumentException("Not a reconnect session.", nameof(session));
        }

        var result = await Task.Run(() => Provider(session.ProviderId).CompleteLinkAsync(session, ct), ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            var code = result.Error ?? BankErrorCodes.Unknown;
            throw new BankProviderException(SyncStatus.NeedsReauth, code, "The reconnect did not finish: " + code);
        }

        await UpdateConnectionAsync(connectionId, c => (c.Status, c.LastError) = (SyncStatus.Ok, null), ct).ConfigureAwait(false);
        LogReconnected(_logger);
        return (await GetConnectionsAsync(ct).ConfigureAwait(false)).Single(c => c.Id == connectionId);
    }

    /// <inheritdoc />
    public async Task<SyncRunResult> SyncConnectionAsync(Guid connectionId, IProgress<SyncProgress>? progress, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        Interlocked.Increment(ref _running);
        try
        {
            return await Task.Run(() => SyncCoreAsync(connectionId, 1, 1, progress, ct), ct).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _running);
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<SyncRunResult?> SyncAccountAsync(Guid accountId, IProgress<SyncProgress>? progress, CancellationToken ct)
    {
        var connectionId = await Task.Run(
            async () =>
            {
                var db = _factory.CreateDbContext();
                await using (db.ConfigureAwait(false))
                {
                    return await db.Accounts.AsNoTracking().Where(a => a.Id == accountId).Select(a => a.SyncConnectionId).SingleOrDefaultAsync(ct).ConfigureAwait(false);
                }
            },
            ct).ConfigureAwait(false);
        return connectionId is { } id ? await SyncConnectionAsync(id, progress, ct).ConfigureAwait(false) : null;
    }

    /// <inheritdoc />
    public async Task<SyncAllResult> SyncAllAsync(IProgress<SyncProgress>? progress, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        Interlocked.Increment(ref _running);
        try
        {
            return await Task.Run(
                async () =>
                {
                    var connections = (await GetConnectionsAsync(ct).ConfigureAwait(false))
                        .Where(c => c.Status != SyncStatus.Disabled)
                        .ToList();
                    var results = new List<SyncRunResult>();
                    for (var i = 0; i < connections.Count; i++)
                    {
                        results.Add(await SyncCoreAsync(connections[i].Id, i + 1, connections.Count, progress, ct).ConfigureAwait(false));
                    }

                    return new SyncAllResult(results);
                },
                ct).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _running);
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task UnlinkAsync(Guid connectionId, CancellationToken ct)
    {
        var connection = await LoadConnectionAsync(connectionId, ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await TryUnlinkAtProviderAsync(Provider(IdOf(connection.Provider)), connectionId.ToString()).ConfigureAwait(false);

            // Unlink the accounts (one undoable account edit), keep every transaction, then drop the connection.
            await _writer.RunAsync(
                LedgerAction.UpdateAccount,
                async session =>
                {
                    var accounts = await session.Db.Accounts.Where(a => a.SyncConnectionId == connectionId).ToListAsync(ct).ConfigureAwait(false);
                    foreach (var account in accounts)
                    {
                        account.SyncConnectionId = null;
                        account.ProviderAccountId = null;
                    }

                    return accounts.Count;
                },
                ct).ConfigureAwait(false);

            await Task.Run(
                async () =>
                {
                    var db = _factory.CreateDbContext();
                    await using (db.ConfigureAwait(false))
                    {
                        await db.SyncConnections.Where(c => c.Id == connectionId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
                    }
                },
                ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        LogUnlinked(_logger);
        _bus.Publish(new SyncConnectionsChanged([connectionId]));
    }

    /// <inheritdoc />
    public async Task<SyncSettings> GetSettingsAsync(CancellationToken ct)
    {
        var json = await ReadSettingAsync(SettingsKey, ct).ConfigureAwait(false);
        if (json is null)
        {
            return new SyncSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<SyncSettings>(json) ?? new SyncSettings();
        }
        catch (JsonException)
        {
            return new SyncSettings();
        }
    }

    /// <inheritdoc />
    public Task SaveSettingsAsync(SyncSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var clean = settings with { IntervalHours = Math.Clamp(settings.IntervalHours, 0, 24 * 7) };
        return WriteSettingAsync(SettingsKey, JsonSerializer.Serialize(clean), ct);
    }

    // One connection: balances first (so the last page can carry them), then pages until HasMore is false.
    private async Task<SyncRunResult> SyncCoreAsync(Guid connectionId, int index, int count, IProgress<SyncProgress>? progress, CancellationToken ct)
    {
        var connection = await LoadConnectionAsync(connectionId, ct).ConfigureAwait(false);
        if (connection.Status == SyncStatus.Disabled)
        {
            return new SyncRunResult(connectionId, connection.InstitutionName, connection.Status, 0, 0, 0, null);
        }

        int added = 0, updated = 0, removed = 0, received = 0;
        progress?.Report(new SyncProgress(connection.InstitutionName, index, count, 0));
        try
        {
            var provider = Provider(IdOf(connection.Provider));
            var accounts = await LinkedAccountsAsync(connectionId, ct).ConfigureAwait(false);
            var key = connectionId.ToString();
            var balances = await provider.GetBalancesAsync(key, ct).ConfigureAwait(false);
            var start = connection.Cursor;
            var cursor = start;
            var restarts = 0;
            var historyComplete = true;
            while (true)
            {
                SyncResult page;
                try
                {
                    page = await provider.SyncTransactionsAsync(key, cursor, ct).ConfigureAwait(false);
                }
                catch (BankProviderException ex) when (ex.Code == MutationDuringPagination && restarts < MaxPaginationRestarts)
                {
                    // Plaid: restart from the cursor this sync began with; provider-id dedup absorbs repeats.
                    restarts++;
                    cursor = start;
                    continue;
                }

                received += page.Added.Count + page.Modified.Count + page.Removed.Count;
                progress?.Report(new SyncProgress(connection.InstitutionName, index, count, received));

                var (pageAdded, pageUpdated) = await ImportPageAsync(connectionId, accounts, page, page.HasMore ? [] : balances, ct).ConfigureAwait(false);
                added += pageAdded;
                updated += pageUpdated;
                removed += await RemoveAsync(accounts.Values.Select(a => a.Id).ToList(), page.Removed, ct).ConfigureAwait(false);

                // Only now, with the page's rows committed, does the cursor move.
                await UpdateConnectionAsync(connectionId, c => c.Cursor = page.NextCursor, ct).ConfigureAwait(false);
                cursor = page.NextCursor;
                historyComplete = page.IsHistoryComplete;
                if (!page.HasMore)
                {
                    break;
                }
            }

            if (historyComplete)
            {
                await ApplyStartingBalancesAsync(accounts.Values, balances, ct).ConfigureAwait(false);
            }

            var now = _time.GetUtcNow().UtcDateTime;
            await UpdateConnectionAsync(connectionId, c => (c.Status, c.LastError, c.LastSyncAt) = (SyncStatus.Ok, null, now), ct).ConfigureAwait(false);
            LogSynced(_logger, added, updated, removed);
            return new SyncRunResult(connectionId, connection.InstitutionName, SyncStatus.Ok, added, updated, removed, null);
        }
        catch (BankProviderException ex)
        {
            return await RecordFailureAsync(connection, ex.Status, ex.Code, added, updated, removed, ct).ConfigureAwait(false);
        }
        catch (SecretStoreException)
        {
            return await RecordFailureAsync(connection, SyncStatus.Error, SyncErrorCodes.SecretStore, added, updated, removed, ct).ConfigureAwait(false);
        }
        catch (LedgerValidationException ex)
        {
            return await RecordFailureAsync(connection, SyncStatus.Error, "KEEL_" + ex.Error, added, updated, removed, ct).ConfigureAwait(false);
        }
        finally
        {
            _bus.Publish(new SyncConnectionsChanged([connectionId]));
        }
    }

    private async Task<SyncRunResult> RecordFailureAsync(SyncConnection connection, SyncStatus status, string code, int added, int updated, int removed, CancellationToken ct)
    {
        LogSyncFailed(_logger, code, status);
        await UpdateConnectionAsync(connection.Id, c => (c.Status, c.LastError) = (status, code), CancellationToken.None).ConfigureAwait(false);
        return new SyncRunResult(connection.Id, connection.InstitutionName, status, added, updated, removed, code);
    }

    // One import batch per linked, open account; rows of unmapped accounts are ignored.
    private async Task<(int Added, int Updated)> ImportPageAsync(
        Guid connectionId,
        IReadOnlyDictionary<string, LinkedAccount> accounts,
        SyncResult page,
        IReadOnlyList<ProviderBalance> balances,
        CancellationToken ct)
    {
        var rows = page.Added.Concat(page.Modified)
            .Where(t => accounts.ContainsKey(t.ProviderAccountId))
            .GroupBy(t => t.ProviderAccountId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var withBalance = balances.Where(b => accounts.ContainsKey(b.ProviderAccountId)).ToDictionary(b => b.ProviderAccountId, StringComparer.Ordinal);

        int added = 0, updated = 0;
        foreach (var providerAccountId in rows.Keys.Union(withBalance.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var account = accounts[providerAccountId];
            var incoming = rows.TryGetValue(providerAccountId, out var list)
                ? list.Select(t => new IncomingTransaction(t.Date, t.Amount, t.PayeeRaw, t.Memo, t.ProviderTransactionId, t.PendingTransactionId, t.IsPending)).ToList()
                : [];
            var batch = new ImportBatch(account.Id, incoming, connectionId)
            {
                ReportedBalance = withBalance.TryGetValue(providerAccountId, out var balance) && balance.Currency == account.Currency
                    ? new StatementBalance(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(balance.AsOf, _time.LocalTimeZone).DateTime), balance.Current)
                    : null,
            };
            if (batch.Transactions.Count == 0 && batch.ReportedBalance is null)
            {
                continue;
            }

            var summary = await _import.ImportTransactionsAsync(TransactionSource.Provider, batch, ct).ConfigureAwait(false);
            added += summary.Added;
            updated += summary.Updated + summary.MatchedToExisting;
        }

        return (added, updated);
    }

    // Removed provider ids soft-delete their rows (one undoable action). Reconciled rows stay; a
    // removed transfer side leaves its partner as an ordinary row instead of deleting user data.
    private async Task<int> RemoveAsync(IReadOnlyList<Guid> accountIds, IReadOnlyList<string> providerIds, CancellationToken ct)
    {
        if (providerIds.Count == 0 || accountIds.Count == 0)
        {
            return 0;
        }

        return await _writer.RunAsync(
            LedgerAction.DeleteTransactions,
            async session =>
            {
                var db = session.Db;
                var removed = 0;
                foreach (var chunk in providerIds.Distinct(StringComparer.Ordinal).Chunk(ChunkSize))
                {
                    var rows = await db.Transactions
                        .Where(t => accountIds.Contains(t.AccountId) && t.ProviderTransactionId != null && chunk.Contains(t.ProviderTransactionId))
                        .ToListAsync(ct).ConfigureAwait(false);
                    foreach (var row in rows.Where(r => r.Status != TransactionStatus.Reconciled))
                    {
                        if (row.TransferPairId is { } pairId)
                        {
                            var partner = await db.Transactions.SingleOrDefaultAsync(t => t.TransferPairId == pairId && t.Id != row.Id, ct).ConfigureAwait(false);
                            if (partner is not null)
                            {
                                partner.TransferPairId = null;
                                partner.TransferAccountId = null;
                            }

                            row.TransferPairId = null;
                            row.TransferAccountId = null;
                        }

                        row.IsDeleted = true;
                        removed++;
                    }
                }

                return removed;
            },
            ct).ConfigureAwait(false);
    }

    // New linked accounts start at the bank's balance: once history is in, a cleared "Starting
    // Balance" on the earliest date makes the cleared balance equal the reported balance (ADR 0071).
    private async Task ApplyStartingBalancesAsync(IEnumerable<LinkedAccount> accounts, IReadOnlyList<ProviderBalance> balances, CancellationToken ct)
    {
        foreach (var linked in accounts)
        {
            var key = StartingBalanceKey(linked.Id);
            if (await ReadSettingAsync(key, ct).ConfigureAwait(false) is null)
            {
                continue;
            }

            var balance = balances.FirstOrDefault(b => b.ProviderAccountId == linked.ProviderAccountId);
            if (balance is null || balance.Currency != linked.Currency)
            {
                continue;
            }

            await _writer.RunAsync(
                LedgerAction.AddTransaction,
                async session =>
                {
                    var db = session.Db;
                    var account = await LedgerLookups.AccountAsync(db, linked.Id, ct).ConfigureAwait(false);
                    var cleared = await db.Transactions
                        .Where(t => t.AccountId == account.Id && t.Status != TransactionStatus.Uncleared)
                        .SumAsync(t => t.Amount, ct).ConfigureAwait(false);
                    var earliest = await db.Transactions.Where(t => t.AccountId == account.Id).MinAsync(t => (DateOnly?)t.Date, ct).ConfigureAwait(false)
                        ?? account.OpeningDate;
                    var difference = balance.Current - cleared;
                    if (earliest < account.OpeningDate)
                    {
                        account.OpeningDate = earliest;
                    }

                    if (difference != 0)
                    {
                        var payee = await LedgerLookups.GetOrAddPayeeAsync(db, LedgerLookups.StartingBalancePayee, ct).ConfigureAwait(false);
                        db.Transactions.Add(new Transaction
                        {
                            AccountId = account.Id,
                            Date = earliest,
                            PayeeId = payee!.Id,
                            PayeeRaw = LedgerLookups.StartingBalancePayee,
                            Amount = difference,
                            CategoryId = LedgerLookups.SystemInflowCategory(account),
                            Status = TransactionStatus.Cleared,
                            IsApproved = true,
                            Source = TransactionSource.System,
                        });
                    }

                    return difference;
                },
                ct).ConfigureAwait(false);
            await DeleteSettingAsync(key, ct).ConfigureAwait(false);
        }
    }

    private async Task<Dictionary<string, LinkedAccount>> LinkedAccountsAsync(Guid connectionId, CancellationToken ct)
    {
        var db = _factory.CreateDbContext();
        await using (db.ConfigureAwait(false))
        {
            var rows = await db.Accounts.AsNoTracking()
                .Where(a => a.SyncConnectionId == connectionId && !a.IsClosed && a.ProviderAccountId != null)
                .Select(a => new LinkedAccount(a.Id, a.ProviderAccountId!, a.Currency))
                .ToListAsync(ct).ConfigureAwait(false);
            return rows.GroupBy(a => a.ProviderAccountId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        }
    }

    private async Task<SyncConnection> LoadConnectionAsync(Guid connectionId, CancellationToken ct)
    {
        var db = _factory.CreateDbContext();
        await using (db.ConfigureAwait(false))
        {
            return await db.SyncConnections.AsNoTracking().SingleOrDefaultAsync(c => c.Id == connectionId, ct).ConfigureAwait(false)
                ?? throw new ArgumentException("No such connection.", nameof(connectionId));
        }
    }

    private async Task UpdateConnectionAsync(Guid connectionId, Action<SyncConnection> update, CancellationToken ct)
    {
        var db = _factory.CreateDbContext();
        await using (db.ConfigureAwait(false))
        {
            var connection = await db.SyncConnections.SingleOrDefaultAsync(c => c.Id == connectionId, ct).ConfigureAwait(false);
            if (connection is null)
            {
                return;
            }

            update(connection);
            if (connection.LastError is { Length: > 2000 } error)
            {
                connection.LastError = error[..2000];
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task TryUnlinkAtProviderAsync(IBankDataProvider provider, string connectionId)
    {
        try
        {
            await provider.UnlinkAsync(connectionId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (BankProviderException ex)
        {
            // The local unlink still happens; the item may remain at the provider (the user can remove it there).
            LogUnlinkAtProviderFailed(_logger, ex.Code);
        }
        catch (SecretStoreException)
        {
            LogUnlinkAtProviderFailed(_logger, SyncErrorCodes.SecretStore);
        }
    }

    private IBankDataProvider Provider(string providerId) =>
        _providers.TryGetValue(providerId, out var provider) ? provider : throw new ArgumentException("Unknown provider.", nameof(providerId));

    private static string DefaultInstitutionName(string providerId) => providerId == "simplefin" ? "SimpleFIN" : "Bank";

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private async Task<string?> ReadSettingAsync(string key, CancellationToken ct)
    {
        var db = _factory.CreateDbContext();
        await using (db.ConfigureAwait(false))
        {
            return await db.Settings.AsNoTracking().Where(s => s.Key == key).Select(s => s.ValueJson).SingleOrDefaultAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task WriteSettingAsync(string key, string json, CancellationToken ct)
    {
        var db = _factory.CreateDbContext();
        await using (db.ConfigureAwait(false))
        {
            var setting = await db.Settings.SingleOrDefaultAsync(s => s.Key == key, ct).ConfigureAwait(false);
            if (setting is null)
            {
                db.Settings.Add(new Setting { Key = key, ValueJson = json });
            }
            else
            {
                setting.ValueJson = json;
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task DeleteSettingAsync(string key, CancellationToken ct)
    {
        var db = _factory.CreateDbContext();
        await using (db.ConfigureAwait(false))
        {
            await db.Settings.Where(s => s.Key == key).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Linked a {Provider} item with {Accounts} accounts")]
    private static partial void LogLinked(ILogger logger, string provider, int accounts);

    [LoggerMessage(Level = LogLevel.Information, Message = "Saved a connection mapping {Accounts} accounts")]
    private static partial void LogSaved(ILogger logger, int accounts);

    [LoggerMessage(Level = LogLevel.Information, Message = "Connection reconnected")]
    private static partial void LogReconnected(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Sync finished: {Added} added, {Updated} updated, {Removed} removed")]
    private static partial void LogSynced(ILogger logger, int added, int updated, int removed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sync failed: {Code} ({Status})")]
    private static partial void LogSyncFailed(ILogger logger, string code, SyncStatus status);

    [LoggerMessage(Level = LogLevel.Information, Message = "Connection unlinked")]
    private static partial void LogUnlinked(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Removing the item at the provider failed: {Code}")]
    private static partial void LogUnlinkAtProviderFailed(ILogger logger, string code);

    private sealed record LinkedAccount(Guid Id, string ProviderAccountId, string Currency);
}

/// <summary>Keel error codes recorded on a connection for failures outside the provider.</summary>
public static class SyncErrorCodes
{
    /// <summary>The OS secret store failed (locked keyring, unreadable blob).</summary>
    public const string SecretStore = "KEEL_SECRET_STORE";
}
