using BenchmarkDotNet.Running;

// Usage: dotnet run -c Release --project tests/Keel.Benchmarks -- --filter '*'
BenchmarkSwitcher.FromAssembly(typeof(Keel.Benchmarks.MoneyBenchmarks).Assembly).Run(args);
