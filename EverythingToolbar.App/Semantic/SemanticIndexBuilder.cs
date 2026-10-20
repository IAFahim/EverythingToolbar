using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EverythingToolbar.App.Helpers;
using EverythingToolbar.Core.Data;
using EverythingToolbar.Core.Search;
using NLog;

namespace EverythingToolbar.App.Semantic
{
    public sealed class SemanticIndexBuilder
    {
        private static readonly ILogger Logger = ToolbarLogger.GetLogger("EverythingToolbar.App.Semantic.SemanticIndexBuilder");

        // Everything search text selecting the corpus to embed. Extensions without an installed
        // Windows codec simply fail to decode and are skipped.
        private const string ImageSearchText =
            "file: ext:jpg;jpeg;jpe;jfif;png;gif;bmp;webp;tif;tiff;heic;heif;avif;dib";

        private const int EnumPageSize = 5000;
        private const int EmbedBatchSize = 8;
        private const int SaveInterval = 200;
        private const int MaxRetriesPerBatch = 2;

        private readonly IEmbeddingClient _client;
        private readonly IEverythingClient _everythingClient;
        private readonly IEmbeddingImageEncoder _encoder;
        private readonly SemanticIndex _index;
        private readonly SemanticState _state;
        private readonly ISettings _settings;

        private readonly object _gate = new();
        private CancellationTokenSource? _buildCancellation;
        private Task? _buildTask;
        private bool _isLoaded;

        public SemanticIndexBuilder(
            IEmbeddingClient client,
            IEverythingClient everythingClient,
            IEmbeddingImageEncoder encoder,
            SemanticIndex index,
            SemanticState state,
            ISettings settings
        )
        {
            _client = client;
            _everythingClient = everythingClient;
            _encoder = encoder;
            _index = index;
            _state = state;
            _settings = settings;

            _settings.PropertyChanged += OnSettingsChanged;
        }

        public bool IsBuilding
        {
            get
            {
                lock (_gate)
                    return _buildCancellation != null;
            }
        }

        // Called at startup and whenever the feature is turned on: picks up the persisted index
        // and embeds anything new or changed since the last build.
        public void StartIncremental()
        {
            BeginBuild(rebuild: false);
        }

        public void BeginBuild(bool rebuild)
        {
            lock (_gate)
            {
                if (_buildCancellation != null)
                    return;

                _buildCancellation = new CancellationTokenSource();
                var token = _buildCancellation.Token;
                _buildTask = Task.Run(() => BuildAsync(rebuild, token));
            }
        }

        public void Pause()
        {
            lock (_gate)
            {
                _buildCancellation?.Cancel();
            }
        }

        private async Task BuildAsync(bool rebuild, CancellationToken cancellationToken)
        {
            try
            {
                if (!_isLoaded || rebuild)
                {
                    _isLoaded = true;
                    _index.Clear(_settings.SemanticModelTag);
                    if (!rebuild)
                        _index.Load();
                }

                var status = await _client.GetStatusAsync(cancellationToken).ConfigureAwait(false);
                if (!status.IsReachable)
                {
                    SetStatus(
                        SemanticSearchStatus.Unavailable,
                        "Ollama is not reachable. Install Ollama, start it and check the server URL."
                    );
                    return;
                }

                if (!status.IsModelAvailable)
                {
                    SetStatus(
                        SemanticSearchStatus.Unavailable,
                        $"Model \"{_settings.SemanticModelTag}\" is not available in Ollama. Run: ollama pull {_settings.SemanticModelTag}"
                    );
                    return;
                }

                SetStatus(SemanticSearchStatus.Indexing, "");

                var query = new SearchQuery(ImageSearchText, SortBy.Path, SortDescending: false, MatchCase: false,
                    MatchPath: false, MatchWholeWord: false, UseRegex: false);

                var totalCount = Math.Min(
                    _everythingClient.QueryCountSync(query, EnumPageSize, cancellationToken),
                    int.MaxValue - 1
                );
                // A canceled query reports 0 results; bail out so the build is labeled paused
                // rather than finished.
                cancellationToken.ThrowIfCancellationRequested();

                _state.TotalCount = totalCount;
                _state.IndexedCount = _index.Count;
                _state.LastBuildTimeUtc = _index.BuildDateUtc == default ? null : _index.BuildDateUtc;

                var saveCounter = 0;
                for (var offset = 0; ; offset += EnumPageSize)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var page = _everythingClient.QueryRangeSync(query, offset, EnumPageSize, cancellationToken);
                    // A canceled query comes back empty; treat it as cancellation, not as the
                    // end of the corpus.
                    cancellationToken.ThrowIfCancellationRequested();

                    if (page.Count == 0)
                        break;

                    for (var i = 0; i < page.Count; i += EmbedBatchSize)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var added = await EmbedBatchAsync(page.Skip(i).Take(EmbedBatchSize).ToList(), cancellationToken)
                            .ConfigureAwait(false);
                        _state.IndexedCount = _index.Count;
                        saveCounter += added;

                        if (saveCounter >= SaveInterval)
                        {
                            saveCounter = 0;
                            Checkpoint();
                        }
                    }

                    if (page.Count < EnumPageSize)
                        break;
                }

                Checkpoint(finished: true);
                SetStatus(SemanticSearchStatus.Ready, "");
                Logger.Info("Semantic index build finished with {count} images.", _index.Count);
            }
            catch (OperationCanceledException)
            {
                Checkpoint();
                SetStatus(SemanticSearchStatus.Paused, "");
                Logger.Info("Semantic index build paused at {count} images.", _index.Count);
            }
            catch (Exception e)
            {
                Logger.Error(e, "Semantic index build failed.");
                SetStatus(SemanticSearchStatus.Unavailable, e.Message);
            }
            finally
            {
                lock (_gate)
                {
                    _buildCancellation?.Dispose();
                    _buildCancellation = null;
                }
            }
        }

        private async Task<int> EmbedBatchAsync(IList<SearchResult> batch, CancellationToken cancellationToken)
        {
            var toEmbed = new List<SearchResult>(batch.Count);
            var images = new List<byte[]>(batch.Count);

            foreach (var result in batch)
            {
                var dateModified = ToFileTime(result.DateModified);
                if (_index.Contains(result.FullPathAndFileName, dateModified))
                    continue;

                // EmbeddingGemma's vision encoder resizes anyway; 512px JPEG keeps the HTTP
                // payload small without losing detail the model can use.
                var encoded = _encoder.EncodeToJpeg(result.FullPathAndFileName, maxDimension: 512);
                if (encoded == null)
                    continue;

                toEmbed.Add(result);
                images.Add(encoded);
            }

            if (images.Count == 0)
                return 0;

            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    var embeddings = await _client.EmbedImagesAsync(images, cancellationToken).ConfigureAwait(false);
                    for (var i = 0; i < toEmbed.Count; i++)
                    {
                        _index.Add(
                            toEmbed[i].FullPathAndFileName,
                            ToFileTime(toEmbed[i].DateModified),
                            toEmbed[i].FileSize,
                            embeddings[i]
                        );
                    }
                    return toEmbed.Count;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e) when (attempt < MaxRetriesPerBatch)
                {
                    Logger.Warn(e, "Embedding batch failed (attempt {attempt}); retrying.", attempt + 1);
                    await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private void Checkpoint(bool finished = false)
        {
            _index.Save();
            if (finished)
            {
                _index.MarkBuildFinished();
                _state.LastBuildTimeUtc = _index.BuildDateUtc;
            }
        }

        private void SetStatus(SemanticSearchStatus status, string message)
        {
            _state.Status = status;
            _state.StatusMessage = message;
        }

        private static long ToFileTime(System.Runtime.InteropServices.ComTypes.FILETIME fileTime)
        {
            return ((long)fileTime.dwHighDateTime << 32) | (uint)fileTime.dwLowDateTime;
        }

        private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(ISettings.IsSemanticSearchEnabled):
                    if (_settings.IsSemanticSearchEnabled)
                        StartIncremental();
                    else
                        Pause();
                    break;

                // A different model produces vectors in a different space; the index is discarded
                // so the two spaces are never mixed.
                case nameof(ISettings.SemanticModelTag):
                {
                    Pause();
                    _isLoaded = false;

                    if (!_settings.IsSemanticSearchEnabled)
                        break;

                    // Pause only requests cancellation; wait for the running build to unwind
                    // before the rebuild starts, otherwise BeginBuild sees it still active and
                    // drops the request.
                    var runningBuild = _buildTask;
                    Task.Run(async () =>
                    {
                        if (runningBuild != null)
                            await runningBuild.ConfigureAwait(false);
                        BeginBuild(rebuild: true);
                    });
                    break;
                }
            }
        }
    }
}
