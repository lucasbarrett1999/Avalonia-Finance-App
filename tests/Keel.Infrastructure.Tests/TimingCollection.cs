using Xunit;

namespace Keel.Infrastructure.Tests;

/// <summary>
/// Test classes that assert wall-clock timings run in this collection so they never share the
/// process with other parallel test classes (the CI workflow also runs test projects one at a time).
/// </summary>
[CollectionDefinition(nameof(TimingCollection), DisableParallelization = true)]
public sealed class TimingCollection;
