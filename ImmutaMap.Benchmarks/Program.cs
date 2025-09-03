using BenchmarkDotNet.Running;
using ImmutaMap.Benchmarks;

namespace ImmutaMap.Benchmarks;

public class Program
{
	public static void Main(string[] args)
	{
		BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
	}
}
