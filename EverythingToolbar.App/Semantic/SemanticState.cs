using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace EverythingToolbar.App.Semantic
{
    public enum SemanticSearchStatus
    {
        // Feature disabled in settings.
        Disabled,
        // Ollama unreachable or the embedding model is not pulled.
        Unavailable,
        // A build is running.
        Indexing,
        // A build was paused by the user or cancelled; the partial index stays searchable.
        Paused,
        // Index loaded/built and queries can run. An empty index counts as ready too; searches
        // simply return nothing until the first build.
        Ready,
    }

    public sealed partial class SemanticState : ObservableObject
    {
        [ObservableProperty]
        private SemanticSearchStatus _status = SemanticSearchStatus.Disabled;

        [ObservableProperty]
        private string _statusMessage = "";

        [ObservableProperty]
        private int _indexedCount;

        [ObservableProperty]
        private int _totalCount;

        [ObservableProperty]
        private DateTime? _lastBuildTimeUtc;

        public float Progress => TotalCount > 0 ? (float)IndexedCount / TotalCount : 0f;

        public bool IsIndexing => Status == SemanticSearchStatus.Indexing;

        partial void OnIndexedCountChanged(int value) => OnPropertyChanged(nameof(Progress));

        partial void OnTotalCountChanged(int value) => OnPropertyChanged(nameof(Progress));

        partial void OnStatusChanged(SemanticSearchStatus value) => OnPropertyChanged(nameof(IsIndexing));
    }
}
