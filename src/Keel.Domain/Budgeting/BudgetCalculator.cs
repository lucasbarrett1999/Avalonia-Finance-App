namespace Keel.Domain.Budgeting;

/// <summary>
/// The envelope-budget engine: a pure function of pre-aggregated ledger data that implements
/// PRD 6.4 (normative). Interpretations of points the PRD leaves open are recorded in
/// <c>docs/decisions/0005-budget-calculation-interpretations.md</c>.
/// </summary>
/// <remarks>
/// Work is linear in (categories × months + input rows): inputs are bucketed into dense arrays
/// once, then one pass per month runs the 6.4.2 recursion for regular categories, allocates
/// covered card spending (6.4.5), runs the payment categories, and accumulates Ready to Assign
/// (6.4.1). No cell is ever recomputed.
/// </remarks>
public static class BudgetCalculator
{
    /// <summary>
    /// Computes every month from <paramref name="from"/> to <paramref name="to"/> (inclusive).
    /// Earlier data is always taken into account (carry, prior cash overspending) and assignments
    /// in months after <paramref name="to"/> reduce Ready to Assign ("assigned in future months").
    /// </summary>
    /// <exception cref="ArgumentException">Invalid range, or the input references unknown ids.</exception>
    public static BudgetSnapshot Compute(BudgetInput input, DateOnly from, DateOnly to)
    {
        ArgumentNullException.ThrowIfNull(input);
        var fromIndex = BudgetMonth.Index(from);
        var toIndex = BudgetMonth.Index(to);
        if (toIndex < fromIndex)
        {
            throw new ArgumentException("The range ends before it starts.", nameof(to));
        }

        var earliest = input.EarliestMonth;
        var start = earliest is { } e ? Math.Min(fromIndex, BudgetMonth.Index(e)) : fromIndex;
        return new Run(input, start, fromIndex, toIndex).Execute();
    }

    /// <summary>Computes a single month.</summary>
    public static BudgetMonthResult ComputeMonth(BudgetInput input, DateOnly month) =>
        Compute(input, month, month).Months[0];

    private sealed class Run
    {
        private readonly BudgetInput _input;
        private readonly int _start;
        private readonly int _from;
        private readonly int _to;
        private readonly int _months;

        private readonly BudgetAccount[] _accounts;
        private readonly Dictionary<Guid, int> _accountIndex;
        private readonly BudgetCategory[] _categories;   // non-Inflow categories in display order
        private readonly Dictionary<Guid, int> _categoryIndex;
        private readonly HashSet<Guid> _inflowCategories = [];
        private readonly HashSet<Guid> _paymentCategoryIds = [];
        private readonly bool[] _visible;
        private readonly int[] _regular;                 // indexes of regular categories
        private readonly int[] _payment;                 // indexes of payment categories
        private readonly int[] _paymentCard;             // card account index per payment category (parallel to _payment)
        private readonly BudgetGroup[] _groups;

        // Dense [category × month] arrays (month offset from _start).
        private readonly long[] _assigned;
        private readonly long[] _activity;
        private readonly long[] _available;
        private readonly List<AccountAmount>?[] _activityByAccount;

        private readonly long[] _inflow;
        private readonly long[] _uncategorized;
        private readonly long[] _assignedPerMonth;
        private long _assignedAfterRange;

        // Payments per [card account × month] and their sources.
        private readonly Dictionary<(int Card, int Month), List<AccountAmount>> _payments = [];

        public Run(BudgetInput input, int start, int from, int to)
        {
            _input = input;
            _start = start;
            _from = from;
            _to = to;
            _months = to - start + 1;

            _accounts = [.. input.Accounts];
            _accountIndex = new Dictionary<Guid, int>(_accounts.Length);
            for (var i = 0; i < _accounts.Length; i++)
            {
                if (!_accountIndex.TryAdd(_accounts[i].Id, i))
                {
                    throw new ArgumentException($"Duplicate account {_accounts[i].Id}.", nameof(input));
                }
            }

            var groupsById = new Dictionary<Guid, BudgetGroup>();
            foreach (var group in input.Groups)
            {
                if (!groupsById.TryAdd(group.Id, group))
                {
                    throw new ArgumentException($"Duplicate group {group.Id}.", nameof(input));
                }
            }

            var seen = new HashSet<Guid>();
            var budgetCategories = new List<BudgetCategory>();
            foreach (var category in input.Categories)
            {
                if (!seen.Add(category.Id))
                {
                    throw new ArgumentException($"Duplicate category {category.Id}.", nameof(input));
                }

                if (!groupsById.ContainsKey(category.GroupId))
                {
                    throw new ArgumentException($"Category {category.Id} belongs to unknown group {category.GroupId}.", nameof(input));
                }

                if (category.Kind == BudgetCategoryKind.Inflow)
                {
                    _inflowCategories.Add(category.Id);
                }
                else
                {
                    budgetCategories.Add(category);
                }
            }

            budgetCategories.Sort((a, b) =>
            {
                var byGroup = groupsById[a.GroupId].SortOrder.CompareTo(groupsById[b.GroupId].SortOrder);
                if (byGroup != 0)
                {
                    return byGroup;
                }

                var byGroupId = a.GroupId.CompareTo(b.GroupId);
                if (byGroupId != 0)
                {
                    return byGroupId;
                }

                var bySort = a.SortOrder.CompareTo(b.SortOrder);
                return bySort != 0 ? bySort : string.CompareOrdinal(a.Name, b.Name);
            });

            _categories = [.. budgetCategories];
            _categoryIndex = new Dictionary<Guid, int>(_categories.Length);
            _visible = new bool[_categories.Length];
            var regular = new List<int>();
            var payment = new List<int>();
            var paymentCard = new List<int>();
            var cardsWithCategory = new HashSet<int>();
            for (var i = 0; i < _categories.Length; i++)
            {
                var category = _categories[i];
                _categoryIndex[category.Id] = i;
                _visible[i] = !category.IsHidden && !groupsById[category.GroupId].IsHidden;
                if (category.Kind == BudgetCategoryKind.CreditCardPayment)
                {
                    if (category.LinkedAccountId is not { } linked || !_accountIndex.TryGetValue(linked, out var card) || !_accounts[card].IsBudgetCredit)
                    {
                        throw new ArgumentException($"Payment category {category.Id} is not linked to an on-budget credit account.", nameof(input));
                    }

                    if (!cardsWithCategory.Add(card))
                    {
                        throw new ArgumentException($"Credit account {linked} has more than one payment category.", nameof(input));
                    }

                    payment.Add(i);
                    paymentCard.Add(card);
                    _paymentCategoryIds.Add(category.Id);
                }
                else
                {
                    regular.Add(i);
                }
            }

            _regular = [.. regular];
            _payment = [.. payment];
            _paymentCard = [.. paymentCard];
            _groups = [.. input.Groups.Where(g => g.Id != Entities.SystemIds.InflowGroup)
                .OrderBy(g => g.SortOrder).ThenBy(g => g.Id)];

            var cells = _categories.Length * _months;
            _assigned = new long[cells];
            _activity = new long[cells];
            _available = new long[cells];
            _activityByAccount = new List<AccountAmount>?[cells];
            _inflow = new long[_months];
            _uncategorized = new long[_months];
            _assignedPerMonth = new long[_months];
        }

        private int Cell(int category, int month) => (category * _months) + month;

        public BudgetSnapshot Execute()
        {
            LoadAssignments();
            LoadActivity();
            LoadCardTransfers();

            var cashOverspent = new long[_months];
            var results = new List<BudgetMonthResult>(_to - _from + 1);
            long inflowToDate = 0;
            long assignedToDate = 0;
            long cashOverspentBefore = 0;
            var assignedTotal = _assignedPerMonth.Sum() + _assignedAfterRange;

            var coveredToCard = new long[_accounts.Length];
            var coveredFrom = new List<CategoryAmount>?[_accounts.Length];

            for (var m = 0; m < _months; m++)
            {
                var output = _start + m >= _from;
                var cells = output ? new CategoryMonthResult[_categories.Length] : null;
                Array.Clear(coveredToCard);
                Array.Clear(coveredFrom);
                long monthCash = 0;

                foreach (var c in _regular)
                {
                    var cell = RegularCategory(c, m, coveredToCard, coveredFrom, output);
                    monthCash += cell.CashOverspent;
                    if (cells is not null)
                    {
                        cells[c] = cell.Result!;
                    }
                }

                for (var p = 0; p < _payment.Length; p++)
                {
                    var cell = PaymentCategory(_payment[p], _paymentCard[p], m, coveredToCard, coveredFrom, output);
                    monthCash += cell.CashOverspent;
                    if (cells is not null)
                    {
                        cells[_payment[p]] = cell.Result!;
                    }
                }

                cashOverspent[m] = monthCash;
                inflowToDate += _inflow[m];
                assignedToDate += _assignedPerMonth[m];
                if (cells is not null)
                {
                    results.Add(MonthResult(m, cells, inflowToDate, assignedToDate, assignedTotal - assignedToDate, cashOverspentBefore, monthCash));
                }

                cashOverspentBefore += monthCash;
            }

            return new BudgetSnapshot(results, _start, cashOverspent);
        }

        private void LoadAssignments()
        {
            foreach (var a in _input.Assignments)
            {
                if (_inflowCategories.Contains(a.CategoryId))
                {
                    continue; // Ready to Assign itself is never assigned (6.4.1 excludes Inflow).
                }

                if (!_categoryIndex.TryGetValue(a.CategoryId, out var c))
                {
                    throw new ArgumentException($"Assignment for unknown category {a.CategoryId}.", nameof(a));
                }

                var m = BudgetMonth.Index(a.Month) - _start;
                if (m >= _months)
                {
                    _assignedAfterRange += a.Assigned;
                    continue;
                }

                _assigned[Cell(c, m)] += a.Assigned;
                _assignedPerMonth[m] += a.Assigned;
            }
        }

        private void LoadActivity()
        {
            foreach (var a in _input.Activity)
            {
                var account = AccountOf(a.AccountId);
                var m = BudgetMonth.Index(a.Month) - _start;
                if (!_accounts[account].IsOnBudget || m >= _months || a.Amount == 0)
                {
                    continue; // tracking accounts never affect the budget (6.3)
                }

                if (a.CategoryId is not { } categoryId || _paymentCategoryIds.Contains(categoryId))
                {
                    _uncategorized[m] += a.Amount;
                    continue;
                }

                if (_inflowCategories.Contains(categoryId))
                {
                    _inflow[m] += a.Amount;
                    continue;
                }

                if (!_categoryIndex.TryGetValue(categoryId, out var c))
                {
                    throw new ArgumentException($"Activity for unknown category {categoryId}.", nameof(a));
                }

                var cell = Cell(c, m);
                _activity[cell] += a.Amount;
                var byAccount = _activityByAccount[cell] ??= new List<AccountAmount>(2);
                var existing = byAccount.FindIndex(x => x.AccountId == a.AccountId);
                if (existing >= 0)
                {
                    byAccount[existing] = byAccount[existing] with { Amount = byAccount[existing].Amount + a.Amount };
                }
                else
                {
                    byAccount.Add(new AccountAmount(a.AccountId, a.Amount));
                }
            }

            // Stable, input-order-independent account order for explanations and rounding ties.
            foreach (var list in _activityByAccount)
            {
                list?.Sort((x, y) => _accountIndex[x.AccountId].CompareTo(_accountIndex[y.AccountId]));
            }
        }

        private void LoadCardTransfers()
        {
            foreach (var t in _input.CardTransfers)
            {
                var card = AccountOf(t.CardAccountId);
                var source = AccountOf(t.FromAccountId);
                var m = BudgetMonth.Index(t.Month) - _start;
                if (!_accounts[card].IsBudgetCredit || !_accounts[source].IsBudgetCash || m >= _months || t.Amount == 0)
                {
                    continue; // only transfers INTO a card FROM an on-budget cash account are payments (6.4.5)
                }

                if (!_payments.TryGetValue((card, m), out var list))
                {
                    _payments[(card, m)] = list = [];
                }

                var existing = list.FindIndex(x => x.AccountId == t.FromAccountId);
                if (existing >= 0)
                {
                    list[existing] = list[existing] with { Amount = list[existing].Amount + t.Amount };
                }
                else
                {
                    list.Add(new AccountAmount(t.FromAccountId, t.Amount));
                }
            }

            foreach (var list in _payments.Values)
            {
                list.Sort((x, y) => _accountIndex[x.AccountId].CompareTo(_accountIndex[y.AccountId]));
            }
        }

        private int AccountOf(Guid id) =>
            _accountIndex.TryGetValue(id, out var index)
                ? index
                : throw new ArgumentException($"Unknown account {id}.", nameof(id));

        private (long CashOverspent, CategoryMonthResult? Result) RegularCategory(
            int c, int m, long[] coveredToCard, List<CategoryAmount>?[] coveredFrom, bool output)
        {
            var cell = Cell(c, m);
            var previous = m > 0 ? _available[cell - 1] : 0;
            var carry = Math.Max(0, previous);                               // 6.4.2
            var assigned = _assigned[cell];
            var activity = _activity[cell];
            var raw = carry + assigned + activity;
            _available[cell] = raw;

            // 6.4.4: CreditSpendingMagnitude = Σ_K max(0, −CardActivity(c, K, M)).
            var byAccount = _activityByAccount[cell];
            long magnitude = 0;
            var cards = 0;
            if (byAccount is not null)
            {
                foreach (var entry in byAccount)
                {
                    if (_accounts[_accountIndex[entry.AccountId]].IsBudgetCredit && entry.Amount < 0)
                    {
                        magnitude += -entry.Amount;
                        cards++;
                    }
                }
            }

            long creditOverspent = 0;
            long cashOverspent = 0;
            if (raw < 0)
            {
                creditOverspent = -Math.Min(-raw, magnitude);
                cashOverspent = raw - creditOverspent;
            }

            AccountAmount[] spendByCard = [];
            AccountAmount[] coveredByCard = [];
            long coveredTotal = 0;
            if (cards > 0)
            {
                spendByCard = new AccountAmount[cards];
                var k = 0;
                foreach (var entry in byAccount!)
                {
                    if (_accounts[_accountIndex[entry.AccountId]].IsBudgetCredit && entry.Amount < 0)
                    {
                        spendByCard[k++] = new AccountAmount(entry.AccountId, -entry.Amount);
                    }
                }

                coveredByCard = AllocateCovered(spendByCard, magnitude, -creditOverspent);
                var categoryId = _categories[c].Id;
                foreach (var covered in coveredByCard)
                {
                    var card = _accountIndex[covered.AccountId];
                    coveredToCard[card] += covered.Amount;
                    coveredTotal += covered.Amount;
                    if (covered.Amount != 0)
                    {
                        (coveredFrom[card] ??= []).Add(new CategoryAmount(categoryId, covered.Amount));
                    }
                }
            }

            if (!output)
            {
                return (cashOverspent, null);
            }

            return (cashOverspent, new CategoryMonthResult
            {
                CategoryId = _categories[c].Id,
                Month = BudgetMonth.FromIndex(_start + m),
                Kind = BudgetCategoryKind.Regular,
                IsVisible = _visible[c],
                PreviousAvailable = previous,
                Carry = carry,
                Assigned = assigned,
                Activity = activity,
                RawAvailable = raw,
                Available = raw,
                CashOverspent = cashOverspent,
                CreditOverspent = creditOverspent,
                CreditSpending = magnitude,
                Covered = coveredTotal,
                ActivityByAccount = byAccount is null ? [] : [.. byAccount],
                CardSpendByCard = spendByCard,
                CoveredByCard = coveredByCard,
            });
        }

        /// <summary>
        /// 6.4.5: Covered(c, K) = CardSpend(c, K) − Uncovered × CardSpend(c, K) / Magnitude. Each
        /// card's uncovered share is rounded down; the rounding remainder goes to the card with the
        /// largest spend (ties: account order). If that card cannot absorb all of it, the rest
        /// spills to the next largest, so 0 ≤ Covered(c, K) ≤ CardSpend(c, K) always holds.
        /// </summary>
        private AccountAmount[] AllocateCovered(AccountAmount[] spendByCard, long magnitude, long uncovered)
        {
            var covered = new AccountAmount[spendByCard.Length];
            if (uncovered == 0)
            {
                for (var i = 0; i < spendByCard.Length; i++)
                {
                    covered[i] = spendByCard[i];
                }

                return covered;
            }

            var share = new long[spendByCard.Length];
            long allocated = 0;
            for (var i = 0; i < spendByCard.Length; i++)
            {
                share[i] = (long)((Int128)uncovered * spendByCard[i].Amount / magnitude);
                allocated += share[i];
            }

            var remainder = uncovered - allocated;
            if (remainder > 0)
            {
                var order = Enumerable.Range(0, spendByCard.Length)
                    .OrderByDescending(i => spendByCard[i].Amount)
                    .ThenBy(i => _accountIndex[spendByCard[i].AccountId]);
                foreach (var i in order)
                {
                    var take = Math.Min(remainder, spendByCard[i].Amount - share[i]);
                    share[i] += take;
                    remainder -= take;
                    if (remainder == 0)
                    {
                        break;
                    }
                }
            }

            for (var i = 0; i < spendByCard.Length; i++)
            {
                covered[i] = new AccountAmount(spendByCard[i].AccountId, spendByCard[i].Amount - share[i]);
            }

            return covered;
        }

        private (long CashOverspent, CategoryMonthResult? Result) PaymentCategory(
            int c, int card, int m, long[] coveredToCard, List<CategoryAmount>?[] coveredFrom, bool output)
        {
            var cell = Cell(c, m);
            var previous = m > 0 ? _available[cell - 1] : 0;
            var carry = Math.Max(0, previous);
            var assigned = _assigned[cell];
            _payments.TryGetValue((card, m), out var payments);
            long paid = 0;
            if (payments is not null)
            {
                foreach (var payment in payments)
                {
                    paid += payment.Amount;
                }
            }

            var covered = coveredToCard[card];
            var activity = covered - paid;                                    // Activity(Pay_K, M)
            var raw = carry + assigned + activity;
            _available[cell] = raw;

            // No card activity is categorized to Pay_K, so CreditSpendingMagnitude(Pay_K) = 0 and any
            // overspending (paying more than was set aside) is cash overspending (6.4.4).
            var cashOverspent = Math.Min(0, raw);

            if (!output)
            {
                return (cashOverspent, null);
            }

            return (cashOverspent, new CategoryMonthResult
            {
                CategoryId = _categories[c].Id,
                Month = BudgetMonth.FromIndex(_start + m),
                Kind = BudgetCategoryKind.CreditCardPayment,
                IsVisible = _visible[c],
                PreviousAvailable = previous,
                Carry = carry,
                Assigned = assigned,
                Activity = activity,
                RawAvailable = raw,
                Available = raw,
                CashOverspent = cashOverspent,
                CreditOverspent = 0,
                Covered = covered,
                Payments = paid,
                CardAccountId = _accounts[card].Id,
                CoveredFromCategories = coveredFrom[card] is { } from ? [.. from] : [],
                PaymentsByAccount = payments is null ? [] : [.. payments],
            });
        }

        private BudgetMonthResult MonthResult(
            int m, CategoryMonthResult[] cells, long inflowToDate, long assignedToDate, long assignedInFuture, long cashOverspentBefore, long monthCash)
        {
            long totalAssigned = 0, totalActivity = 0, totalAvailable = 0;
            foreach (var cell in cells)
            {
                totalAssigned += cell.Assigned;
                totalActivity += cell.Activity;
                totalAvailable += cell.Available;
            }

            var groups = new List<GroupMonthResult>(_groups.Length);
            foreach (var group in _groups)
            {
                long assigned = 0, activity = 0, available = 0;
                var ids = new List<Guid>();
                for (var c = 0; c < cells.Length; c++)
                {
                    if (_categories[c].GroupId != group.Id)
                    {
                        continue;
                    }

                    ids.Add(_categories[c].Id);
                    if (_visible[c])
                    {
                        assigned += cells[c].Assigned;
                        activity += cells[c].Activity;
                        available += cells[c].Available;
                    }
                }

                groups.Add(new GroupMonthResult
                {
                    GroupId = group.Id,
                    IsHidden = group.IsHidden,
                    Assigned = assigned,
                    Activity = activity,
                    Available = available,
                    CategoryIds = ids,
                });
            }

            return new BudgetMonthResult
            {
                Month = BudgetMonth.FromIndex(_start + m),
                ReadyToAssign = inflowToDate - assignedToDate - assignedInFuture + cashOverspentBefore,   // 6.4.1
                InflowThroughMonth = inflowToDate,
                InflowThisMonth = _inflow[m],
                AssignedThroughMonth = assignedToDate,
                AssignedInFuture = assignedInFuture,
                CashOverspentBefore = cashOverspentBefore,
                CashOverspentThisMonth = monthCash,
                TotalAssigned = totalAssigned,
                TotalActivity = totalActivity,
                TotalAvailable = totalAvailable,
                UncategorizedActivity = _uncategorized[m],
                Groups = groups,
                Categories = cells,
            };
        }
    }
}
