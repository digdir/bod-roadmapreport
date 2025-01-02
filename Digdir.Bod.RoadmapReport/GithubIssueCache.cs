namespace Digdir.Bod.RoadmapReport;

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

public class GitHubIssue
{
    public int Number { get; init; }
    public string Title { get; init; } = null!;
    public DateTimeOffset? ClosedAt { get; init; }
    public List<GitHubLabel> Labels { get; init; } = new();
    public List<GitHubCustomProperty> CustomProperties { get; init; } = new();
}

public class GitHubLabel
{
    public string Name { get; init; } = null!;
}

public class GitHubCustomProperty
{
    public string Name { get; init; } = null!;
    public string Value { get; init; } = null!;
}

public class GitHubIssueCache
{
    private const string GraphQlEndpoint = "https://api.github.com/graphql";
    private const string ProjectId = "PVT_kwDOAwyZKM4ANYNj"; // Replace with the actual project ID from GitHub
    private static readonly string CacheDirectory = Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? AppContext.BaseDirectory, "cache");
    private static readonly string CacheFilePath = Path.Combine(CacheDirectory, "GitHubIssuesCache.json");
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);

    private readonly IConfiguration _configuration;
    private static readonly SemaphoreSlim CacheLock = new(1, 1);

    public GitHubIssueCache(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public async Task<List<GitHubIssue>> GetIssuesWithCustomPropertiesAsync()
    {
        await CacheLock.WaitAsync();
        try
        {
            Directory.CreateDirectory(CacheDirectory);

            if (File.Exists(CacheFilePath))
            {
                var fileInfo = new FileInfo(CacheFilePath);
                if (DateTime.UtcNow - fileInfo.LastWriteTimeUtc < CacheDuration)
                {
                    var cachedData = await File.ReadAllTextAsync(CacheFilePath);
                    return JsonSerializer.Deserialize<List<GitHubIssue>>(cachedData) ?? new List<GitHubIssue>();
                }
            }

            var token = _configuration["GitHubToken"];
            if (string.IsNullOrEmpty(token))
            {
                throw new InvalidOperationException("GitHub token is not configured.");
            }

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("github.com_digdir_roadmap-report");

            var issues = new List<GitHubIssue>();
            string? cursor = null;
            bool hasMore = true;

            while (hasMore)
            {
                var query = new
                {
                    query = @"query ListConnectedIssuesWithLabel($projectId: ID!, $cursor: String) {
                        node(id: $projectId) {
                            ... on ProjectV2 {
                                items(first: 100, after: $cursor) {
                                    pageInfo {
                                        hasNextPage
                                        endCursor
                                    }
                                    edges {
                                        node {
                                            content {
                                                ... on Issue {
                                                    number
                                                    title
                                                    closedAt
                                                    labels(first: 10) {
                                                        nodes {
                                                            name
                                                        }
                                                    }
                                                }
                                            }
                                            fieldValues(first: 100) {
                                                nodes {
                                                    ... on ProjectV2ItemFieldTextValue {
                                                        text
                                                        field {
                                                            ... on ProjectV2FieldCommon {
                                                                name
                                                            }
                                                        }
                                                    }
                                                    ... on ProjectV2ItemFieldSingleSelectValue {
                                                        name
                                                        optionId
                                                        field {
                                                            ... on ProjectV2FieldCommon {
                                                                name
                                                            }
                                                        }
                                                    }
                                                    ... on ProjectV2ItemFieldNumberValue {
                                                        number
                                                        field {
                                                            ... on ProjectV2FieldCommon {
                                                                name
                                                            }
                                                        }
                                                    }
                                                    ... on ProjectV2ItemFieldDateValue {
                                                        date
                                                        field {
                                                            ... on ProjectV2FieldCommon {
                                                                name
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }",
                    variables = new { projectId = ProjectId, cursor }
                };

                var jsonRequest = JsonSerializer.Serialize(query);
                var httpContent = new StringContent(jsonRequest);
                httpContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");

                var response = await httpClient.PostAsync(GraphQlEndpoint, httpContent);

                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException($"Failed to fetch data: {response.StatusCode}");
                }

                var responseContent = await response.Content.ReadAsStringAsync();
                var data = JsonSerializer.Deserialize<GraphQLResponse>(responseContent, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                var items = data?.Data.Node.Items;
                if (items == null) break;

                foreach (var edge in items.Edges)
                {
                    var content = edge.Node.Content;
                    var fieldValues = edge.Node.FieldValues.Nodes;

                    if (!content.Labels.Nodes.Exists(x => x.Name == "program/nye-altinn")) continue;
                    
                    var issue = new GitHubIssue
                    {
                        Number = content.Number,
                        Title = content.Title,
                        ClosedAt = content.ClosedAt,
                        Labels = content.Labels.Nodes,
                        CustomProperties = new List<GitHubCustomProperty>()
                    };

                    foreach (var fieldValue in fieldValues)
                    {
                        if (fieldValue.Field == null) continue;
                        //if (fieldValue.Date == null) continue;
                        issue.CustomProperties.Add(new GitHubCustomProperty
                        {
                            Name = fieldValue.Field.Name,
                            Value = fieldValue.Text ?? fieldValue.Name ?? fieldValue.Number?.ToString() ?? fieldValue.Date ?? string.Empty
                        });
                    }

                    issues.Add(issue);
                }

                hasMore = items.PageInfo.HasNextPage;
                cursor = items.PageInfo.EndCursor;
            }

            var serializedData = JsonSerializer.Serialize(issues, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(CacheFilePath, serializedData);

            return issues;
        }
        finally
        {
            CacheLock.Release();
        }
    }
}

public class GraphQLResponse
{
    [JsonPropertyName("data")]
    public GraphQLData Data { get; init; } = null!;
}

public class GraphQLData
{
    [JsonPropertyName("node")]
    public GraphQLNode Node { get; init; } = null!;
}

public class GraphQLNode
{
    [JsonPropertyName("items")]
    public GraphQLItems Items { get; init; } = null!;
}

public class GraphQLItems
{
    [JsonPropertyName("pageInfo")]
    public GraphQLPageInfo PageInfo { get; init; } = null!;

    [JsonPropertyName("edges")]
    public List<GraphQLEdge> Edges { get; init; } = new();
}

public class GraphQLEdge
{
    [JsonPropertyName("node")]
    public GraphQLItemNode Node { get; init; } = null!;
}

public class GraphQLItemNode
{
    [JsonPropertyName("content")]
    public GraphQLContent Content { get; init; } = null!;

    [JsonPropertyName("fieldValues")]
    public GraphQLFieldValues FieldValues { get; init; } = null!;
}

public class GraphQLContent
{
    [JsonPropertyName("number")]
    public int Number { get; init; }

    [JsonPropertyName("title")]
    public string Title { get; init; } = null!;

    [JsonPropertyName("closedAt")]
    public DateTimeOffset? ClosedAt { get; init; } = null;
    
    [JsonPropertyName("labels")]
    public GraphQLLabels Labels { get; init; } = new();
}

public class GraphQLLabels
{
    [JsonPropertyName("nodes")]
    public List<GitHubLabel> Nodes { get; init; } = new();
}

public class GraphQLFieldValues
{
    [JsonPropertyName("nodes")]
    public List<GraphQLFieldValue> Nodes { get; init; } = new();
}

public class GraphQLFieldValue
{
    [JsonPropertyName("field")]
    public GraphQLField Field { get; init; } = null!;

    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("number")]
    public double? Number { get; init; }

    [JsonPropertyName("date")]
    public string? Date { get; init; }
}

public class GraphQLField
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = null!;
}

public class GraphQLPageInfo
{
    [JsonPropertyName("hasNextPage")]
    public bool HasNextPage { get; init; }

    [JsonPropertyName("endCursor")]
    public string? EndCursor { get; init; }
}