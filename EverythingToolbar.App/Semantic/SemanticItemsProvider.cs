using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EverythingToolbar.Core.Data;
using EverythingToolbar.Core.Search;

namespace EverythingToolbar.App.Semantic
{
    // Adapts a top-K semantic search into the paging model the results list expects. The full
    // result list is fetched once in FetchCount; FetchRange then slices that cached list.
    public sealed class SemanticItemsProvider : IItemsProvider<SearchResult>
    {
        private readonly SemanticSearchService _service;
        private readonly string _queryText;
        private IList<SearchResult>? _results;

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (_isBusy != value)
                {
                    _isBusy = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsBusy)));
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public SemanticItemsProvider(SemanticSearchService service, string queryText)
        {
            _service = service;
            _queryText = queryText;
        }

        public async Task<int> FetchCount(int pageSize, bool isAsync, CancellationToken cancellationToken)
        {
            if (!isAsync)
            {
                _results = _service.SearchAsync(_queryText, cancellationToken).GetAwaiter().GetResult();
                return _results.Count;
            }

            try
            {
                IsBusy = true;
                _results = await _service.SearchAsync(_queryText, cancellationToken).ConfigureAwait(false);
                return _results.Count;
            }
            finally
            {
                IsBusy = false;
            }
        }

        public Task<IList<SearchResult>> FetchRange(
            int startIndex,
            int pageSize,
            bool isAsync,
            CancellationToken cancellationToken
        )
        {
            // FetchCount always runs first and fills the cache; an empty answer here simply means
            // the search was cancelled before completing.
            IList<SearchResult> page = _results == null
                ? Array.Empty<SearchResult>()
                : _results.Skip(startIndex).Take(pageSize).ToList();
            return Task.FromResult(page);
        }

        public bool TryFetchCachedFirstPage(out IList<SearchResult> items)
        {
            if (_results != null)
            {
                items = _results.Take(256).ToList();
                return true;
            }

            items = [];
            return false;
        }
    }
}
