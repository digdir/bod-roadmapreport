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

public class GitHubIssueCache
{
    private const string GqlEndpoint = "https://api.github.com/graphql";
    private static readonly string CacheDirectory = Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? AppContext.BaseDirectory, "cache");
    private static readonly string CacheFilePath = Path.Combine(CacheDirectory, "GitHubIssuesCache.json");

    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _clientFactory;
    private static readonly SemaphoreSlim CacheLock = new(1, 1);
    private static readonly JsonSerializerOptions JsonSerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public GitHubIssueCache(IConfiguration configuration, IHttpClientFactory clientFactory)
    {
        _configuration = configuration;
        _clientFactory = clientFactory;
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
                if (DateTime.UtcNow - fileInfo.LastWriteTimeUtc < TimeSpan.Parse(_configuration["GitHubCacheDuration"] ?? "01:00:00"))
                {
                    var cachedData = await File.ReadAllTextAsync(CacheFilePath);
                    return JsonSerializer.Deserialize<List<GitHubIssue>>(cachedData) ?? [];
                }
            }

            var token = _configuration["GitHubToken"];
            if (string.IsNullOrEmpty(token))
            {
                throw new InvalidOperationException("GitHub token is not configured.");
            }

            var httpClient = _clientFactory.CreateClient();
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("github.com_digdir_roadmap-report");

            var issues = new List<GitHubIssue>();
            string? cursor = null;
            var hasMore = true;

            while (hasMore)
            {
                var query = new
                {
                    query = """
                            query ListConnectedIssuesWithLabel($projectId: ID!, $cursor: String) {
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
                            }
                            """,    
                    variables = new { projectId = _configuration["GitHubProjectId"], cursor }
                };

                var jsonRequest = JsonSerializer.Serialize(query);
                var httpContent = new StringContent(jsonRequest);
                httpContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");

                var response = await httpClient.PostAsync(GqlEndpoint, httpContent);

                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException($"Failed to fetch data: {response.StatusCode}");
                }

                var responseContent = await response.Content.ReadAsStringAsync();
                var data = JsonSerializer.Deserialize<GqlResponse>(responseContent, JsonSerializerOptions);

                var items = data?.Data.Node.Items;
                if (items == null) break;

                foreach (var edge in items.Edges)
                {
                    var content = edge.Node.Content;
                    var fieldValues = edge.Node.FieldValues.Nodes;
                    
                    var issue = new GitHubIssue
                    {
                        Number = content.Number,
                        Title = content.Title,
                        ClosedAt = content.ClosedAt,
                        Labels = content.Labels.Nodes,
                        CustomProperties = []
                    };

                    foreach (var fieldValue in fieldValues)
                    {
                        if (fieldValue.Field == null) continue;
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

            var serializedData = JsonSerializer.Serialize(issues, JsonSerializerOptions);
            await File.WriteAllTextAsync(CacheFilePath, serializedData);

            return issues;
        }
        finally
        {
            CacheLock.Release();
        }
    }
}

public record GitHubIssue
{
    public int Number { get; init; }
    public string Title { get; init; } = null!;
    public DateTimeOffset? ClosedAt { get; init; }
    public List<GitHubLabel> Labels { get; init; } = [];
    public List<GitHubCustomProperty> CustomProperties { get; init; } = [];
}

public record GitHubLabel
{
    public string Name { get; init; } = null!;
}

public record GitHubCustomProperty
{
    public string Name { get; init; } = null!;
    public string Value { get; init; } = null!;
}


public record GqlResponse
{
    [JsonPropertyName("data")]
    public GqlData Data { get; init; } = null!;
}

public record GqlData
{
    [JsonPropertyName("node")]
    public GqlNode Node { get; init; } = null!;
}

public record GqlNode
{
    [JsonPropertyName("items")]
    public GqlItems Items { get; init; } = null!;
}

public record GqlItems
{
    [JsonPropertyName("pageInfo")]
    public GqlPageInfo PageInfo { get; init; } = null!;

    [JsonPropertyName("edges")]
    public List<GqlEdge> Edges { get; init; } = [];
}

public record GqlEdge
{
    [JsonPropertyName("node")]
    public GqlItemNode Node { get; init; } = null!;
}

public record GqlItemNode
{
    [JsonPropertyName("content")]
    public GqlContent Content { get; init; } = null!;

    [JsonPropertyName("fieldValues")]
    public GqlFieldValues FieldValues { get; init; } = null!;
}

public record GqlContent
{
    [JsonPropertyName("number")]
    public int Number { get; init; }

    [JsonPropertyName("title")]
    public string Title { get; init; } = null!;

    [JsonPropertyName("closedAt")]
    public DateTimeOffset? ClosedAt { get; init; } = null;
    
    [JsonPropertyName("labels")]
    public GqlLabels Labels { get; init; } = new();
}

public record GqlLabels
{
    [JsonPropertyName("nodes")]
    public List<GitHubLabel> Nodes { get; init; } = [];
}

public record GqlFieldValues
{
    [JsonPropertyName("nodes")]
    public List<GqlFieldValue> Nodes { get; init; } = [];
}

public record GqlFieldValue
{
    [JsonPropertyName("field")]
    public GqlField? Field { get; init; } = null!;

    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("number")]
    public double? Number { get; init; }

    [JsonPropertyName("date")]
    public string? Date { get; init; }
}

public record GqlField
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = null!;
}

public record GqlPageInfo
{
    [JsonPropertyName("hasNextPage")]
    public bool HasNextPage { get; init; }

    [JsonPropertyName("endCursor")]
    public string? EndCursor { get; init; }
}