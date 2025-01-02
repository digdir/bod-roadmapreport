namespace Digdir.Bod.RoadmapReport;

public static class IssuesHelper
{
    private const int ReductionPerPercentOverdue = 3;
    
    public static List<RoadmapIssue> ToRoadmapIssues(this IEnumerable<GitHubIssue> issues)
    {
        return issues.Select(x => x.ToRoadmapIssue()).ToList();
    }
    
    public static RoadmapIssue ToRoadmapIssue(this GitHubIssue issue)
    {
        // Find product name in labels
        var product = issue.Labels.FirstOrDefault(x => x.Name.StartsWith("product/"))?.Name.Replace("product/", string.Empty);
        if (product == null)
        {
            throw new Exception("Product not found in labels");
        }
        
        // Find progression in custom properties
        var progressionString = issue.CustomProperties.FirstOrDefault(x => x.Name == "Progresjon (%)")?.Value;
        if (!int.TryParse(progressionString, out var progression))
        {
            // Assume 100 if issue is closed, if not assume 0
            progression = issue.ClosedAt.HasValue ? 100 : 0;
        }
        
        // Find start and end date in custom properties
        var startDateString = issue.CustomProperties.FirstOrDefault(x => x.Name == "Start")?.Value;
        if (!DateTimeOffset.TryParse(startDateString, out var startDate))
        {
            startDate = DateTimeOffset.MinValue;
        }
        var endDateString = issue.CustomProperties.FirstOrDefault(x => x.Name == "Sluttdato")?.Value;
        if (!DateTimeOffset.TryParse(endDateString, out var endDate))
        {
            startDate = DateTimeOffset.MinValue;
        }
        
        // Find days overdue. If the issue is not closed, and the end date is passed, the issue is overdue. If the issue is closed, the issue is overdue if the closed date is after the end date
        var daysOverdue = 0;
        if (!issue.ClosedAt.HasValue)
        {
            if (endDate < DateTimeOffset.UtcNow)
            {
                daysOverdue = (int) Math.Round((DateTimeOffset.UtcNow - endDate).TotalDays);
            }
        }
        else
        {
            if (issue.ClosedAt > endDate)
            {
                daysOverdue = (int) Math.Round((issue.ClosedAt.Value - endDate).TotalDays);
            }
        }
        
        // Find percentage overdue. Take the total number of days between start and end date, divide by 100, and multiply by the number of days overdue
        var percentageOverdue = Math.Max((int) Math.Round(daysOverdue / (endDate - startDate).TotalDays * 100), 0);
        
        // Find estimated man weeks. The number has a comma within it, so we need to replace it with a dot before parsing
        var estimatedManWeeksString = issue.CustomProperties.FirstOrDefault(x => x.Name == "Estimerte ukesverk")?.Value;
        if (!float.TryParse(estimatedManWeeksString?.Replace(",", "."), out var estimatedManWeeks))
        {
            estimatedManWeeks = 0;
        }

        // Successindicator is calculated like this:
        // - If the issue has a start date not yet passed, the success indicator is null
        // - If the issue is not closed, and the current date is within start and end date, the success indicator is equal to the progression.
        //   If the end date is passed, the success indicator is reduced by ReductionPerPercentOverdue for each percentage point the issue is overdue in days
        // - If the issue is closed, the success indicator is equal to the progression. If the issue is closed after the end date, the success indicator is reduced by ReductionPerPercentOverdue for each percentage point the issue is overdue in days
        int? successIndicator = 0;
        if (startDate > DateTimeOffset.UtcNow)
        {
            successIndicator = null;
        }
        else
        {
            successIndicator = Math.Clamp(progression - percentageOverdue * ReductionPerPercentOverdue, 0, 100);
        }
        
        
        // Expected linear progression is calculated as the percentage of days passed since start date
        var expectedLinearProgression = Math.Clamp((int) Math.Round((DateTimeOffset.UtcNow - startDate).TotalDays / (endDate - startDate).TotalDays * 100), 0, 100);
        
        var roadmapIssue = new RoadmapIssue
        {
            IssueNumber = issue.Number,
            Product = product,
            Title = issue.Title,
            EstimatedManWeeks = estimatedManWeeks,
            Progression = progression,
            StartDate = startDate,
            EndDate = endDate,
            ClosedDate = issue.ClosedAt,
            TotalDays = (int) Math.Round((endDate - startDate).TotalDays),
            DaysOverdue = daysOverdue,
            PercentageOverdue = percentageOverdue,
            SuccessIndicator = successIndicator,
            ExpectedLinearProgression = expectedLinearProgression
        };

        return roadmapIssue;
    }
}


public class RoadmapIssue
{
    public int IssueNumber { get; init; }
    public string Product { get; init; } = null!;
    public string Title { get; init; } = null!;
    public float EstimatedManWeeks { get; init; }
    public int Progression { get; init; }
    public DateTimeOffset StartDate { get; init; }
    public DateTimeOffset EndDate { get; init; }
    public DateTimeOffset? ClosedDate { get; init; }
    public int TotalDays { get; init; }
    public int DaysOverdue { get; init; }
    public int PercentageOverdue { get; init; }
    public int ExpectedLinearProgression { get; init; }
    public int? SuccessIndicator { get; init; }
}