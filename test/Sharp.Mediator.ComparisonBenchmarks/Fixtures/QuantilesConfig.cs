using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;

namespace Sharp.Mediator.ComparisonBenchmarks.Fixtures;

/// <summary>The lower quantiles carry a comparison in nanoseconds on a machine doing anything else.</summary>
public sealed class QuantilesConfig : ManualConfig
{
    public QuantilesConfig() => AddColumn(StatisticColumn.Min, StatisticColumn.Q1);
}