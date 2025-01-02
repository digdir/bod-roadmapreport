namespace Digdir.Bod.RoadmapReport;

internal class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        if (builder.Environment.IsDevelopment())
        {
            builder.Configuration.AddUserSecrets<Program>();
        }
        
        builder.Services
            .AddSingleton<GitHubIssueCache>()
            .AddHttpClient()
            .AddOpenApi();

        var app = builder.Build();
        
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
        }

        app.UseHttpsRedirection();

        app.MapGet("/report", async (GitHubIssueCache gic) =>
            {
                var issues = await gic.GetIssuesWithCustomPropertiesAsync();
                return issues.ToRoadmapIssues();
            })
            .WithName("report");

        app.Run();
        
    }
    
}
