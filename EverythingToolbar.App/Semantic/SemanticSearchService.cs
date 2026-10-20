using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using EverythingToolbar.App.Search;
using EverythingToolbar.Core.Data;
using FILETIME = System.Runtime.InteropServices.ComTypes.FILETIME;

namespace EverythingToolbar.App.Semantic
{
    public enum SemanticNoticeKind
    {
        None,
        Unavailable,
        EmptyIndex,
    }

    public sealed partial class SemanticSearchService : ObservableObject
    {
        public const int MaxResults = 200;

        private readonly IEmbeddingClient _client;
        private readonly SemanticIndex _index;
        private readonly SemanticState _state;
        private readonly SearchState _searchState;
        private readonly ISettings _settings;

        public SemanticSearchService(
            IEmbeddingClient client,
            SemanticIndex index,
            SemanticState state,
            SearchState searchState,
            ISettings settings
        )
        {
            _client = client;
            _index = index;
            _state = state;
            _searchState = searchState;
            _settings = settings;

            _settings.PropertyChanged += OnDependencyChanged;
            _searchState.PropertyChanged += OnDependencyChanged;
            _state.PropertyChanged += OnDependencyChanged;
        }

        // The Semantic filter is selected and the feature is enabled in settings.
        public bool IsSemanticMode => _settings.IsSemanticSearchEnabled && _searchState.Filter.IsSemantic;

        // True when the vector index has content to search. A build still in progress counts:
        // searches then run against the partial index.
        public bool IsReady =>
            IsSemanticMode
            && _index.Count > 0
            && _state.Status
                is SemanticSearchStatus.Indexing
                or SemanticSearchStatus.Paused
                or SemanticSearchStatus.Ready;

        // Bound by the results view: a hint whenever semantic mode is on but cannot deliver.
        // The localized text lives in the main assembly, which owns all resources; the view
        // selects it by notice kind.
        public bool ShowNotice => IsSemanticMode && !IsReady;

        public SemanticNoticeKind NoticeKind
        {
            get
            {
                if (!ShowNotice)
                    return SemanticNoticeKind.None;

                return _state.Status == SemanticSearchStatus.Unavailable
                    ? SemanticNoticeKind.Unavailable
                    : SemanticNoticeKind.EmptyIndex;
            }
        }

        // Raw, non-localized detail for the unavailable case (e.g. the pull command to run).
        public string UnavailableDetail => _state.StatusMessage;

        public async Task<IList<SearchResult>> SearchAsync(string queryText, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(queryText) || _index.Count == 0)
                return Array.Empty<SearchResult>();

            var queryVector = await _client.EmbedTextAsync(queryText.Trim(), cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            var matches = _index.Search(queryVector, MaxResults);
            var results = new List<SearchResult>(matches.Length);

            foreach (var match in matches)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var entry = _index.GetEntry(match.Index);
                if (!File.Exists(entry.Path))
                    continue;

                results.Add(
                    new SearchResult(
                        Path.GetDirectoryName(entry.Path) ?? "",
                        Path.GetFileName(entry.Path),
                        entry.Path,
                        IsFile: true,
                        entry.FileSize,
                        ToFileTime(entry.DateModified)
                    )
                );
            }

            return results;
        }

        private static FILETIME ToFileTime(long fileTime)
        {
            return new FILETIME
            {
                dwLowDateTime = (int)(fileTime & 0xFFFFFFFF),
                dwHighDateTime = (int)((ulong)fileTime >> 32),
            };
        }

        private void OnDependencyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(ISettings.IsSemanticSearchEnabled):
                case nameof(SearchState.Filter):
                case nameof(SemanticState.Status):
                case nameof(SemanticState.StatusMessage):
                    OnPropertyChanged(nameof(IsSemanticMode));
                    OnPropertyChanged(nameof(IsReady));
                    OnPropertyChanged(nameof(ShowNotice));
                    OnPropertyChanged(nameof(NoticeKind));
                    OnPropertyChanged(nameof(UnavailableDetail));
                    break;
            }
        }
    }
}
