using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenSearch.Net;
using Ruoyu.Study.DocLibrary.Domain.Models;

namespace Ruoyu.Study.DocLibrary.Service;

// MIGRATION NOTICE (2026-06-27):
// One-time migration: renames OpenSearch index from docretrieval-segments to doclibrary-segments.
//
// Phase 1 (old index name in config): creates new index with same mappings, reindexes data from old to new.
// Phase 2 (new index name in config): reindexes again (catch docs added between phases), deletes old index.
//
// Safe to delete after 2026-07-07.
public static class SearchIndexMigrationService
{
    private const string OldIndexName = "docretrieval-segments";
    private const string NewIndexName = "doclibrary-segments";

    public static async Task MigrateAsync(OpenSearchOptions options, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(options.Url))
            return;

        var config = new ConnectionConfiguration(new Uri(options.Url))
            .RequestTimeout(TimeSpan.FromMinutes(5));
        var client = new OpenSearchLowLevelClient(config);

        var currentIndexName = options.IndexName;

        if (currentIndexName == OldIndexName)
        {
            await CreateAndReindexAsync(client, logger, options.Url);
        }
        else if (currentIndexName == NewIndexName)
        {
            await ReindexAndDeleteOldAsync(client, logger, options.Url);
        }
    }

    private static async Task CreateAndReindexAsync(OpenSearchLowLevelClient client, ILogger logger, string openSearchUrl)
    {
        // Check if old index exists (if not, nothing to migrate)
        var oldExistsResp = await client.Indices.ExistsAsync<BytesResponse>(OldIndexName);
        if (!oldExistsResp.Success || oldExistsResp.HttpStatusCode != 200)
        {
            logger.LogInformation("Old index {OldIndex} does not exist, skip migration", OldIndexName);
            return;
        }

        // Create new index with same mappings (EnsureIndexExistsAsync skips if already exists)
        await OpenSearchIndexService.EnsureIndexExistsAsync(client, NewIndexName, logger);

        // Reindex data from old to new
        await ReindexAsync(client, OldIndexName, NewIndexName, logger, openSearchUrl);

        logger.LogInformation("Index migration completed: {OldIndex} -> {NewIndex}", OldIndexName, NewIndexName);
    }

    private static async Task ReindexAndDeleteOldAsync(OpenSearchLowLevelClient client, ILogger logger, string openSearchUrl)
    {
        // Check if old index exists
        var oldExistsResp = await client.Indices.ExistsAsync<BytesResponse>(OldIndexName);
        if (!oldExistsResp.Success || oldExistsResp.HttpStatusCode != 200)
        {
            logger.LogInformation("Old index {OldIndex} does not exist, skip cleanup", OldIndexName);
            return;
        }

        // Reindex again to catch any docs added between Phase 1 and Phase 2
        await ReindexAsync(client, OldIndexName, NewIndexName, logger, openSearchUrl);

        // Delete old index
        var deleteResp = await client.Indices.DeleteAsync<BytesResponse>(OldIndexName);
        if (deleteResp.Success && (deleteResp.HttpStatusCode == 200 || deleteResp.HttpStatusCode == 204))
        {
            logger.LogInformation("Old index {OldIndex} deleted", OldIndexName);
        }
        else
        {
            logger.LogWarning("Failed to delete old index {OldIndex}, status code: {StatusCode}", OldIndexName, deleteResp.HttpStatusCode);
        }
    }

    private static async Task ReindexAsync(OpenSearchLowLevelClient client, string sourceIndex, string destIndex, ILogger logger, string openSearchUrl)
    {
        var reindexBody = JsonSerializer.Serialize(new
        {
            source = new { index = sourceIndex },
            dest = new { index = destIndex }
        });

        // Use HttpClient for _reindex API (OpenSearch.Net low-level client API varies between versions)
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var content = new StringContent(reindexBody, System.Text.Encoding.UTF8, "application/json");
        var response = await httpClient.PostAsync($"{openSearchUrl.TrimEnd('/')}/_reindex", content);

        if (response.IsSuccessStatusCode)
        {
            logger.LogInformation("Reindex completed: {Source} -> {Dest}", sourceIndex, destIndex);
        }
        else
        {
            logger.LogWarning("Reindex failed: {Source} -> {Dest}, status code: {StatusCode}", sourceIndex, destIndex, (int)response.StatusCode);
        }
    }
}
