using BenchmarkDotNet.Running;

// dotnet run -c Release --project benchmarks/LinqToRest.Benchmarks -- --filter '*'   (add --job short while iterating)
BenchmarkSwitcher.FromAssembly(typeof(LinqToRest.Benchmarks.BenchmarkConfig).Assembly).Run(args, new LinqToRest.Benchmarks.BenchmarkConfig());
