using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EverythingToolbar.App.Helpers;
using NLog;

namespace EverythingToolbar.App.Semantic
{
    public sealed class OllamaEmbeddingClient : IEmbeddingClient
    {
        private static readonly ILogger Logger = ToolbarLogger.GetLogger("EverythingToolbar.App.Semantic.OllamaEmbeddingClient");

        // EmbeddingGemma 2 natively emits 768 dimensions; MRL truncation to 256 keeps a full
        // search scan cheap (256 bytes per indexed image once quantized) with minimal quality loss.
        public const int Dimensions = 256;

        private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan BatchTimeout = TimeSpan.FromMinutes(10);

        // Deliberately static: the settings URL can change between calls, so this is only a
        // connection pool, never a BaseAddress holder.
        private static readonly HttpClient Http = new();

        private readonly ISettings _settings;

        public OllamaEmbeddingClient(ISettings settings)
        {
            _settings = settings;
        }

        public async Task<EmbeddingServiceStatus> GetStatusAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(StatusTimeout);

                using var response = await Http
                    .GetAsync(BuildUrl("/api/tags"), timeout.Token)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return new EmbeddingServiceStatus(IsReachable: true, IsModelAvailable: false);

                using var document = await JsonDocument
                    .ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false), cancellationToken: timeout.Token)
                    .ConfigureAwait(false);

                var modelTag = _settings.SemanticModelTag;
                if (document.RootElement.TryGetProperty("models", out var models))
                {
                    foreach (var model in models.EnumerateArray())
                    {
                        if (
                            model.TryGetProperty("name", out var name)
                            && MatchesTag(name.GetString(), modelTag)
                        )
                            return new EmbeddingServiceStatus(true, true);
                    }
                }

                return new EmbeddingServiceStatus(IsReachable: true, IsModelAvailable: false);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
            {
                Logger.Debug(e, "Failed to reach the Ollama server.");
                return new EmbeddingServiceStatus(IsReachable: false, IsModelAvailable: false);
            }
        }

        public async Task<float[]> EmbedTextAsync(string text, CancellationToken cancellationToken)
        {
            var embeddings = await EmbedAsync([text], QueryTimeout, cancellationToken).ConfigureAwait(false);
            return embeddings[0];
        }

        public Task<IList<float[]>> EmbedImagesAsync(IReadOnlyList<byte[]> images, CancellationToken cancellationToken)
        {
            var inputs = new List<object>(images.Count);
            foreach (var image in images)
                inputs.Add(new Dictionary<string, object> { ["image"] = Convert.ToBase64String(image) });

            return EmbedAsync(inputs, BatchTimeout, cancellationToken);
        }

        private async Task<IList<float[]>> EmbedAsync(
            List<object> inputs,
            TimeSpan timeout,
            CancellationToken cancellationToken
        )
        {
            // keep_alive holds the model in memory between searches so a query after a quiet
            // period doesn't pay the model load latency again.
            var payload = new Dictionary<string, object>
            {
                ["model"] = _settings.SemanticModelTag,
                ["input"] = inputs,
                ["dimensions"] = Dimensions,
                ["keep_alive"] = "15m",
            };

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);

            using var content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json"
            );
            using var response = await Http
                .PostAsync(BuildUrl("/api/embed"), content, timeoutSource.Token)
                .ConfigureAwait(false);

            var body = await response.Content.ReadAsStringAsync(timeoutSource.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new EmbeddingClientException($"Ollama returned {(int)response.StatusCode}: {Truncate(body)}");

            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("embeddings", out var embeddings))
                throw new EmbeddingClientException($"Ollama response has no embeddings: {Truncate(body)}");

            var result = new List<float[]>(embeddings.GetArrayLength());
            foreach (var embedding in embeddings.EnumerateArray())
            {
                // Older servers ignore the dimensions parameter and return the full-width vector;
                // truncating here keeps the index format identical either way.
                var vector = new float[Dimensions];
                var index = 0;
                foreach (var value in embedding.EnumerateArray())
                {
                    if (index >= Dimensions)
                        break;
                    vector[index++] = value.GetSingle();
                }
                Normalize(vector);
                result.Add(vector);
            }

            if (result.Count != inputs.Count)
                throw new EmbeddingClientException($"Ollama returned {result.Count} embeddings for {inputs.Count} inputs.");

            return result;
        }

        private static void Normalize(float[] vector)
        {
            var sum = 0.0;
            foreach (var value in vector)
                sum += value * value;

            var norm = Math.Sqrt(sum);
            if (norm <= float.Epsilon)
                return;

            for (var i = 0; i < vector.Length; i++)
                vector[i] = (float)(vector[i] / norm);
        }

        private string BuildUrl(string path)
        {
            return _settings.SemanticOllamaUrl.TrimEnd('/') + path;
        }

        // Ollama lists models with their full tag, e.g. "embeddinggemma-2:latest".
        private static bool MatchesTag(string? modelName, string tag)
        {
            return modelName == tag
                || modelName == tag + ":latest"
                || (modelName?.StartsWith(tag + ":") ?? false);
        }

        private static string Truncate(string text)
        {
            return text.Length <= 200 ? text : text[..200] + "…";
        }
    }

    public sealed class EmbeddingClientException(string message) : Exception(message);
}
