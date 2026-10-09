namespace Jarvis.Core.Performance;

public sealed record ParameterDiff(ParameterDescriptor Parameter, double Left, double Right)
{
    public bool Equal => Parameter.Distance(Left, Right) < 1e-9;
}

/// <summary>Сравнение профилей и наборов значений.</summary>
public static class ProfileComparisonService
{
    public static IReadOnlyList<ParameterDiff> Compare(IReadOnlyDictionary<string, double> left, IReadOnlyDictionary<string, double> right) =>
        PerformanceCatalog.All.Select(d => new ParameterDiff(d, Get(left, d), Get(right, d))).ToList();

    public static IReadOnlyList<ParameterDiff> Compare(PerformanceProfile a, PerformanceProfile b) => Compare(a.Parameters, b.Parameters);

    /// <summary>Средняя нормированная разница по всем параметрам (0 — совпадают).</summary>
    public static double Distance(IReadOnlyDictionary<string, double> a, IReadOnlyDictionary<string, double> b) =>
        PerformanceCatalog.All.Average(d => d.Distance(Get(a, d), Get(b, d)));

    private static double Get(IReadOnlyDictionary<string, double> values, ParameterDescriptor d) =>
        values.TryGetValue(d.Key, out var v) ? v : BuiltInProfiles.Medium.Parameters[d.Key];
}
