namespace OpenClaw.Plugins.AiEvaluation.Models;

public sealed class ScoringCriteria
{
    public string Domain { get; set; } = "";
    public string Version { get; set; } = "";
    public ScoreDimension[] Dimensions { get; set; } = [];
}

public sealed class ScoreDimension
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public double MaxScore { get; set; }
    public string[] Indicators { get; set; } = [];
    public ScoreLevel[] Levels { get; set; } = [];
}

public sealed class ScoreLevel
{
    public string Label { get; set; } = "";
    public double RangeMin { get; set; }
    public double RangeMax { get; set; }
    public string Description { get; set; } = "";
}
