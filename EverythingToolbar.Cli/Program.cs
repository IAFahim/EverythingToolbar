using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Config.Net;
using EverythingToolbar.App;
using EverythingToolbar.App.Helpers;
using EverythingToolbar.App.Semantic;
using EverythingToolbar.Core.Data;
using EverythingToolbar.Core.Search;
using EverythingToolbar.Platform.Search;
using EverythingToolbar.Semantic;

namespace EverythingToolbar.Cli;

// semcli: command-line access to the semantic index. Shares settings.ini and the index file
// with the toolbar app, so everything built or searched here is visible there too.
internal static class Program
{
    private static readonly string[] ImageExtensions =
    [
        ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".gif", ".bmp", ".webp",
        ".tif", ".tiff", ".heic", ".heif", ".avif", ".dib",
    ];

    private const string ImageSearchText =
        "file: ext:jpg;jpeg;jpe;jfif;png;gif;bmp;webp;tif;tiff;heic;heif;avif;dib";

    private const int EmbedBatchSize = 8;

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
            return PrintUsage();

        ISettings settings = SettingsProxy.Create(
            new ConfigurationBuilder<IToolbarSettings>()
                .UseIniFile(Path.Combine(ConfigPaths.GetConfigDirectory(), "settings.ini"))
                .Build()
        );

        try
        {
            switch (args[0].ToLowerInvariant())
            {
                case "status":
                    return await StatusAsync(settings);
                case "search":
                    return await SearchAsync(settings, args.Skip(1).ToList());
                case "index":
                    return await IndexAsync(settings, args.Skip(1).ToList());
                case "help":
                case "--help":
                case "-h":
                    return PrintUsage();
                default:
                    Console.Error.WriteLine($"Unknown command: {args[0]}");
                    return PrintUsage();
            }
        }
        catch (EmbeddingClientException e)
        {
            Console.Error.WriteLine($"Embedding error: {e.Message}");
            return 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 1;
        }
    }

    private static int PrintUsage()
    {
        Console.WriteLine(
            """
            semcli - semantic image search for EverythingToolbar (EmbeddingGemma via Ollama)

            Usage:
              semcli status                          Ollama/model/index status
              semcli search <query> [-k <count>]     Semantic search, prints "score<TAB>path"
              semcli index [--path <dir>]            Update the index (all images from Everything,
                                                     or all images under a directory)
                      [--max <count>] [--rebuild]

            The settings.ini of the toolbar (Ollama URL, model tag) applies.
            """
        );
        return 0;
    }

    private static async Task<int> StatusAsync(ISettings settings)
    {
        var client = new OllamaEmbeddingClient(settings);
        var status = await client.GetStatusAsync(CancellationToken.None);

        Console.WriteLine($"Ollama:   {(status.IsReachable ? "reachable" : "NOT reachable")} at {settings.SemanticOllamaUrl}");
        Console.WriteLine(
            $"Model:    {(status.IsModelAvailable ? "available" : "MISSING")} ({settings.SemanticModelTag})"
        );

        var index = new SemanticIndex();
        if (index.Load())
            Console.WriteLine($"Index:    {index.Count} images, built {index.BuildDateUtc.ToLocalTime():g}");
        else
            Console.WriteLine("Index:    empty (run: semcli index)");

        return 0;
    }

    private static async Task<int> SearchAsync(ISettings settings, List<string> args)
    {
        var topK = SemanticSearchService.MaxResults;
        var queryParts = new List<string>();

        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] is "-k" or "--top" && i + 1 < args.Count && int.TryParse(args[++i], out var k))
            {
                topK = Math.Clamp(k, 1, SemanticSearchService.MaxResults);
                continue;
            }
            queryParts.Add(args[i]);
        }

        var query = string.Join(" ", queryParts);
        if (query.Length == 0)
        {
            Console.Error.WriteLine("No query given. Usage: semcli search <query> [-k <count>]");
            return 1;
        }

        var index = new SemanticIndex();
        if (!index.Load() || index.Count == 0)
        {
            Console.Error.WriteLine("The semantic index is empty. Run: semcli index");
            return 1;
        }

        Console.Error.WriteLine($"Searching {index.Count} images for \"{query}\"…");
        var client = new OllamaEmbeddingClient(settings);
        var queryVector = await client.EmbedTextAsync(query, CancellationToken.None);

        foreach (var match in index.Search(queryVector, topK))
        {
            var entry = index.GetEntry(match.Index);
            Console.WriteLine($"{match.Score,8:0.000}\t{entry.Path}");
        }

        return 0;
    }

    private static async Task<int> IndexAsync(ISettings settings, List<string> args)
    {
        string? directory = null;
        var max = int.MaxValue;
        var rebuild = false;

        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--path" when i + 1 < args.Count:
                    directory = args[++i];
                    break;
                case "--max" when i + 1 < args.Count && int.TryParse(args[i + 1], out var limit):
                    max = limit;
                    i++;
                    break;
                case "--rebuild":
                    rebuild = true;
                    break;
            }
        }

        var client = new OllamaEmbeddingClient(settings);
        var status = await client.GetStatusAsync(CancellationToken.None);
        if (!status.IsReachable)
        {
            Console.Error.WriteLine($"Ollama is not reachable at {settings.SemanticOllamaUrl}. Start it and retry.");
            return 1;
        }
        if (!status.IsModelAvailable)
        {
            Console.Error.WriteLine($"Model \"{settings.SemanticModelTag}\" is missing. Run: ollama pull {settings.SemanticModelTag}");
            return 1;
        }

        var index = new SemanticIndex();
        index.Clear(settings.SemanticModelTag);
        if (!rebuild)
            index.Load();
        else if (index.Count > 0)
            Console.WriteLine("Rebuilding from scratch.");

        var encoder = new WicImageEncoder();
        var added = 0;
        var skipped = 0;

        async Task ProcessBatchAsync(List<SearchResult> batch)
        {
            var toEmbed = new List<SearchResult>();
            var images = new List<byte[]>();

            foreach (var item in batch)
            {
                var fileTime = ToFileTime(item.DateModified);
                if (index.Contains(item.FullPathAndFileName, fileTime))
                {
                    skipped++;
                    continue;
                }

                var encoded = encoder.EncodeToJpeg(item.FullPathAndFileName, maxDimension: 512);
                if (encoded == null)
                    continue;

                toEmbed.Add(item);
                images.Add(encoded);
            }

            if (images.Count == 0)
                return;

            var embeddings = await client.EmbedImagesAsync(images, CancellationToken.None);
            for (var i = 0; i < toEmbed.Count; i++)
                index.Add(toEmbed[i].FullPathAndFileName, ToFileTime(toEmbed[i].DateModified), toEmbed[i].FileSize, embeddings[i]);

            added += toEmbed.Count;
            index.Save();
            Console.Error.WriteLine($"  {added} embedded, {skipped} unchanged…");
        }

        if (directory != null)
        {
            directory = Path.GetFullPath(directory);
            if (!Directory.Exists(directory))
            {
                Console.Error.WriteLine($"Directory not found: {directory}");
                return 1;
            }

            Console.WriteLine($"Indexing images under {directory} (max {max})…");

            var batch = new List<SearchResult>();
            foreach (var item in EnumerateDirectory(directory, max))
            {
                batch.Add(item);
                if (batch.Count >= EmbedBatchSize)
                {
                    await ProcessBatchAsync(batch);
                    batch.Clear();
                }
            }

            if (batch.Count > 0)
                await ProcessBatchAsync(batch);
        }
        else
        {
            Console.WriteLine("Indexing images from Everything…");

            var everything = new EverythingClientRouter(new EverythingPipeClient(), new EverythingIpcClient());
            var query = new SearchQuery(
                ImageSearchText,
                SortBy.Path,
                SortDescending: false,
                MatchCase: false,
                MatchPath: false,
                MatchWholeWord: false,
                UseRegex: false
            );

            var batch = new List<SearchResult>();
            for (var offset = 0; ; offset += 5000)
            {
                IList<SearchResult> page;
                try
                {
                    page = everything.QueryRangeSync(query, offset, 5000, CancellationToken.None);
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine($"Everything query failed: {e.Message}");
                    return 1;
                }

                if (page.Count == 0)
                    break;

                foreach (var item in page.Take(max))
                {
                    batch.Add(item);
                    if (batch.Count >= EmbedBatchSize)
                    {
                        await ProcessBatchAsync(batch);
                        batch.Clear();
                    }
                }

                if (page.Count < 5000)
                    break;
            }

            if (batch.Count > 0)
                await ProcessBatchAsync(batch);
        }

        index.MarkBuildFinished();
        index.Save();
        Console.WriteLine($"Done. Index now holds {index.Count} images.");
        return 0;
    }

    private static IEnumerable<SearchResult> EnumerateDirectory(string directory, int max)
    {
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            if (count >= max)
                yield break;

            var extension = Path.GetExtension(file);
            if (!ImageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                continue;

            FileInfo info;
            try
            {
                info = new FileInfo(file);
            }
            catch (Exception)
            {
                continue;
            }

            count++;
            yield return new SearchResult(
                "",
                info.Name,
                file,
                IsFile: true,
                info.Length,
                FromFileTime(info.LastWriteTimeUtc.ToFileTime())
            );
        }
    }

    private static long ToFileTime(System.Runtime.InteropServices.ComTypes.FILETIME fileTime)
    {
        return ((long)fileTime.dwHighDateTime << 32) | (uint)fileTime.dwLowDateTime;
    }

    private static System.Runtime.InteropServices.ComTypes.FILETIME FromFileTime(long fileTime)
    {
        return new System.Runtime.InteropServices.ComTypes.FILETIME
        {
            dwLowDateTime = (int)(fileTime & 0xFFFFFFFF),
            dwHighDateTime = (int)((ulong)fileTime >> 32),
        };
    }
}
