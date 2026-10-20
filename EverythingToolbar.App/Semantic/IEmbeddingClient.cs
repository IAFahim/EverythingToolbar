using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace EverythingToolbar.App.Semantic
{
    public sealed record EmbeddingServiceStatus(bool IsReachable, bool IsModelAvailable)
    {
        public bool IsReady => IsReachable && IsModelAvailable;
    }

    public interface IEmbeddingClient
    {
        Task<EmbeddingServiceStatus> GetStatusAsync(CancellationToken cancellationToken);

        Task<float[]> EmbedTextAsync(string text, CancellationToken cancellationToken);

        Task<IList<float[]>> EmbedImagesAsync(IReadOnlyList<byte[]> images, CancellationToken cancellationToken);
    }
}
