using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Avalonia.Collections;
using Keel.Application.Ledger;

namespace Keel.Desktop.ViewModels.Register;

/// <summary>
/// A virtual, paged list for the register <c>DataGrid</c> (PRD 7.3). It reports the full row count
/// but only materializes the pages the grid asks for (200 rows each, a bounded cache), fetching them
/// from <see cref="IRegisterQuery"/> on demand. It implements <see cref="IDataGridCollectionView"/>
/// itself because the grid's default view copies its source into memory; sorting is forwarded to
/// the database through <see cref="SortRequested"/>. All members are used on the UI thread.
/// </summary>
public sealed class RegisterSource : IList, IDataGridCollectionView, IReadOnlyList<RegisterRowViewModel>
{
    /// <summary>Most pages kept in memory.</summary>
    public const int MaxCachedPages = 30;

    private readonly Func<int, int, CancellationToken, Task<IReadOnlyList<RegisterRow>>> _fetch;
    private readonly Dictionary<int, RegisterRowViewModel[]> _pages = [];
    private readonly LinkedList<int> _recent = new();
    private readonly Dictionary<int, Task> _loading = [];
    private int _count;
    private int _generation;
    private int _deferLevel;
    private CancellationTokenSource _cts = new();
    private object? _currentItem;
    private int _currentPosition = -1;

    /// <summary>Creates the source over a page fetcher (skip, take).</summary>
    public RegisterSource(Func<int, int, CancellationToken, Task<IReadOnlyList<RegisterRow>>> fetch)
    {
        _fetch = fetch;
        SortDescriptions.CollectionChanged += (_, _) =>
        {
            if (_deferLevel == 0)
            {
                RaiseSortRequested();
            }
        };
    }

    /// <inheritdoc />
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    /// <inheritdoc />
    public event EventHandler<DataGridCurrentChangingEventArgs>? CurrentChanging;

    /// <inheritdoc />
    public event EventHandler? CurrentChanged;

    /// <summary>Raised when the grid asks for a new order (header click).</summary>
    public event EventHandler<RegisterSort>? SortRequested;

    /// <summary>Raised when a page fails to load.</summary>
    public event EventHandler<Exception>? PageFailed;

    /// <summary>Number of rows in the register.</summary>
    public int Count => _count;

    /// <summary>
    /// Called whenever the grid asks for a row (scrolling or paging); the register view points it at a
    /// <see cref="Services.ScrollFrameMeter"/> for the Stats page's frame times (PRD 4). Null costs nothing.
    /// </summary>
    public Action? RowRequested { get; set; }

    /// <summary>Number of pages currently materialized (for tests).</summary>
    public int CachedPageCount => _pages.Count;

    /// <summary>Loaded rows (for selection restore and tests).</summary>
    public IEnumerable<RegisterRowViewModel> LoadedRows => _pages.Values.SelectMany(p => p).Where(r => r.IsLoaded);

    /// <summary>Page loads in flight (tests await these).</summary>
    public Task WhenIdle => Task.WhenAll(_loading.Values.ToList());

    /// <inheritdoc />
    public CultureInfo Culture { get; set; } = CultureInfo.CurrentCulture;

    /// <inheritdoc />
    public IEnumerable SourceCollection => this;

    /// <inheritdoc />
    public Func<object, bool> Filter
    {
        get => null!;
        set => throw new NotSupportedException("Register filtering runs in the database.");
    }

    /// <inheritdoc />
    public bool CanFilter => false;

    /// <inheritdoc />
    public DataGridSortDescriptionCollection SortDescriptions { get; } = [];

    /// <inheritdoc />
    public bool CanSort => true;

    /// <inheritdoc />
    public bool CanGroup => false;

    /// <inheritdoc />
    public bool IsGrouping => false;

    /// <inheritdoc />
    public int GroupingDepth => 0;

    /// <inheritdoc />
    public IAvaloniaReadOnlyList<object> Groups => null!;

    /// <inheritdoc />
    public bool IsEmpty => _count == 0;

    /// <inheritdoc />
    public object CurrentItem => _currentItem!;

    /// <inheritdoc />
    public int CurrentPosition => _currentPosition;

    /// <inheritdoc />
    public bool IsCurrentAfterLast => _currentPosition >= _count;

    /// <inheritdoc />
    public bool IsCurrentBeforeFirst => _currentPosition < 0;

    bool IList.IsFixedSize => false;

    bool IList.IsReadOnly => true;

    bool ICollection.IsSynchronized => false;

    object ICollection.SyncRoot => this;

    /// <summary>The row at <paramref name="index"/>; a placeholder until its page loads.</summary>
    public RegisterRowViewModel this[int index] => GetRow(index);

    object? IList.this[int index]
    {
        get => GetRow(index);
        set => throw new NotSupportedException();
    }

    /// <summary>Replaces the contents: a new count, an empty cache, and a Reset notification.</summary>
    public void Reset(int count)
    {
        _cts.Cancel();
        _cts.Dispose();
        _cts = new CancellationTokenSource();
        _generation++;
        _count = count;
        _pages.Clear();
        _recent.Clear();
        _loading.Clear();
        _currentItem = null;
        _currentPosition = -1;
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// <summary>Re-reads every cached page in place (same count), so visible rows update without a reset.</summary>
    public async Task RefreshLoadedAsync()
    {
        var pages = _pages.Keys.ToList();
        await Task.WhenAll(pages.Select(LoadPageAsync));
    }

    /// <summary>Loads the page containing <paramref name="index"/> and returns its row.</summary>
    public async Task<RegisterRowViewModel> GetLoadedAsync(int index)
    {
        var row = GetRow(index);
        if (_loading.TryGetValue(index / IRegisterQuery.PageSize, out var task))
        {
            await task;
        }

        return row;
    }

    /// <inheritdoc />
    public int IndexOf(object? value) =>
        value is RegisterRowViewModel row && ReferenceEquals(row.Owner, this) && row.Index < _count
            && _pages.TryGetValue(row.Index / IRegisterQuery.PageSize, out var page) && ReferenceEquals(page[row.Index % IRegisterQuery.PageSize], row)
            ? row.Index
            : -1;

    /// <inheritdoc />
    public bool Contains(object? value) => IndexOf(value) >= 0;

    /// <inheritdoc />
    public IEnumerator<RegisterRowViewModel> GetEnumerator()
    {
        for (var i = 0; i < _count; i++)
        {
            yield return GetRow(i);
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <inheritdoc />
    public void Refresh() => RaiseSortRequested();

    /// <inheritdoc />
    public IDisposable DeferRefresh()
    {
        _deferLevel++;
        return new Deferral(this);
    }

    /// <inheritdoc />
    public bool MoveCurrentTo(object? item)
    {
        if (Equals(_currentItem, item) && (item is not null || _currentPosition < 0))
        {
            return item is not null;
        }

        return MoveCurrentToPosition(item is null ? -1 : IndexOf(item));
    }

    /// <inheritdoc />
    public bool MoveCurrentToFirst() => MoveCurrentToPosition(0);

    /// <inheritdoc />
    public bool MoveCurrentToLast() => MoveCurrentToPosition(_count - 1);

    /// <inheritdoc />
    public bool MoveCurrentToNext() => _currentPosition + 1 <= _count && MoveCurrentToPosition(_currentPosition + 1);

    /// <inheritdoc />
    public bool MoveCurrentToPrevious() => _currentPosition - 1 >= -1 && MoveCurrentToPosition(_currentPosition - 1);

    /// <inheritdoc />
    public bool MoveCurrentToPosition(int position)
    {
        if (position < -1 || position > _count)
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }

        var changing = new DataGridCurrentChangingEventArgs();
        CurrentChanging?.Invoke(this, changing);
        if (changing.Cancel)
        {
            return false;
        }

        _currentPosition = position;
        _currentItem = position >= 0 && position < _count ? GetRow(position) : null;
        CurrentChanged?.Invoke(this, EventArgs.Empty);
        return _currentItem is not null;
    }

    /// <inheritdoc />
    public string GetGroupingPropertyNameAtDepth(int level) => null!;

    int IList.Add(object? value) => throw new NotSupportedException();

    void IList.Clear() => throw new NotSupportedException();

    void IList.Insert(int index, object? value) => throw new NotSupportedException();

    void IList.Remove(object? value) => throw new NotSupportedException();

    void IList.RemoveAt(int index) => throw new NotSupportedException();

    void ICollection.CopyTo(Array array, int index)
    {
        ArgumentNullException.ThrowIfNull(array);
        for (var i = 0; i < _count; i++)
        {
            array.SetValue(GetRow(i), index + i);
        }
    }

    private RegisterRowViewModel GetRow(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _count);
        RowRequested?.Invoke();
        var pageIndex = index / IRegisterQuery.PageSize;
        if (!_pages.TryGetValue(pageIndex, out var page))
        {
            var start = pageIndex * IRegisterQuery.PageSize;
            page = new RegisterRowViewModel[Math.Min(IRegisterQuery.PageSize, _count - start)];
            for (var i = 0; i < page.Length; i++)
            {
                page[i] = new RegisterRowViewModel(this, start + i);
            }

            _pages[pageIndex] = page;
            _loading[pageIndex] = LoadPageAsync(pageIndex);
            Evict(pageIndex);
        }

        Touch(pageIndex);
        return page[index % IRegisterQuery.PageSize];
    }

    private async Task LoadPageAsync(int pageIndex)
    {
        var generation = _generation;
        var token = _cts.Token;
        try
        {
            var rows = await _fetch(pageIndex * IRegisterQuery.PageSize, IRegisterQuery.PageSize, token);
            if (generation != _generation || !_pages.TryGetValue(pageIndex, out var page))
            {
                return;
            }

            for (var i = 0; i < page.Length && i < rows.Count; i++)
            {
                page[i].Fill(rows[i]);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (generation == _generation)
            {
                _pages.Remove(pageIndex);
                PageFailed?.Invoke(this, ex);
            }
        }
        finally
        {
            if (generation == _generation)
            {
                _loading.Remove(pageIndex);
            }
        }
    }

    private void Touch(int pageIndex)
    {
        if (_recent.First?.Value == pageIndex)
        {
            return;
        }

        _recent.Remove(pageIndex);
        _recent.AddFirst(pageIndex);
    }

    private void Evict(int keep)
    {
        while (_pages.Count > MaxCachedPages && _recent.Last is { } oldest && oldest.Value != keep)
        {
            _recent.RemoveLast();
            if (!_loading.ContainsKey(oldest.Value))
            {
                _pages.Remove(oldest.Value);
            }
        }
    }

    private void RaiseSortRequested()
    {
        var first = SortDescriptions.FirstOrDefault(d => d.HasPropertyPath);
        var sort = first is null
            ? RegisterSort.Default
            : new RegisterSort(
                Enum.TryParse<RegisterSortColumn>(first.PropertyPath, out var column) ? column : RegisterSortColumn.Date,
                first.Direction == ListSortDirection.Descending);
        SortRequested?.Invoke(this, sort);
    }

    private sealed class Deferral(RegisterSource owner) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (--owner._deferLevel == 0)
            {
                owner.RaiseSortRequested();
            }
        }
    }
}
