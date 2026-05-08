namespace OpenClaw.Plugins.AiEvaluation.Models;

public sealed class EvaluationReport
{
    public string ReportId { get; set; } = "";
    public string EvaluatedAt { get; set; } = "";
    public string TargetEndpoint { get; set; } = "";
    public DimensionScore[] Scores { get; set; } = [];
    public double TotalScore { get; set; }
    public double MaxPossibleScore { get; set; }
    public string OverallRating { get; set; } = "";
    public string[] Strengths { get; set; } = [];
    public string[] Weaknesses { get; set; } = [];
    public ImprovementSuggestion[] Suggestions { get; set; } = [];
    public string? Summary { get; set; }
}

public sealed class DimensionScore
{
    public string Dimension { get; set; } = "";
    public double Score { get; set; }
    public double MaxScore { get; set; }
    public string Comment { get; set; } = "";
}

public sealed class ImprovementSuggestion
{
    public string Area { get; set; } = "";
    public string Suggestion { get; set; } = "";
    public string Priority { get; set; } = "medium";
}
