namespace Antigen.SDK.Analyzers;

/// <summary>
/// An analyzer of the whole load order whose work splits into parts that can be analyzed at once on several threads, so
/// a host can spread it over its workers rather than leave one analyzing it all. <see cref="IContextualAnalyzer.Analyze"/>
/// still analyzes it whole.
/// </summary>
public interface IPartitionedContextualAnalyzer : IContextualAnalyzer
{
    /// <summary>Works out the parts: what's needed to know how many there are, and nothing more.</summary>
    IContextualAnalysisPlan Plan(ContextualAnalyzerParams param);
}

/// <summary>An analysis of the whole load order in parts (<see cref="IPartitionedContextualAnalyzer"/>).</summary>
public interface IContextualAnalysisPlan
{
    /// <summary>How many parts there are.</summary>
    int Count { get; }

    /// <summary>
    /// Analyzes the parts from <paramref name="start"/> to before <paramref name="end"/>, reporting through
    /// <paramref name="param"/>; called on several threads at once, for different parts, each with its own parameters.
    /// </summary>
    void Analyze(ContextualAnalyzerParams param, int start, int end);
}
