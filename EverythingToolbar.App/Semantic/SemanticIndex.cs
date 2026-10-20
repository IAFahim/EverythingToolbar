using System;
using System.Collections.Generic;
using System.IO;
using EverythingToolbar.App.Helpers;
using NLog;

namespace EverythingToolbar.App.Semantic
{
    public sealed record SemanticIndexEntry(string Path, long DateModified, long FileSize);

    public sealed record SemanticMatch(int Index, float Score);

    public sealed class SemanticIndex
    {
        private static readonly ILogger Logger = ToolbarLogger.GetLogger("EverythingToolbar.App.Semantic.SemanticIndex");

        private const string Magic = "ETSI";
        private const int FormatVersion = 1;

        private readonly object _lock = new();

        private List<string> _paths = [];
        private List<long> _datesModified = [];
        private List<long> _fileSizes = [];
        private float[] _scales = [];
        // int8-quantized vectors, Dimensions values per entry, contiguous for scan-friendly locality.
        private sbyte[] _vectors = [];
        private Dictionary<string, int> _pathToIndex = new(StringComparer.OrdinalIgnoreCase);

        public int Count
        {
            get
            {
                lock (_lock)
                    return _paths.Count;
            }
        }

        public string ModelTag { get; private set; } = "";

        public DateTime BuildDateUtc { get; private set; }

        public static string GetIndexPath()
        {
            return Path.Combine(ConfigPaths.GetConfigDirectory(), "semantic-index.bin");
        }

        // Returns false when the file belongs to a different model or format; the caller then
        // rebuilds from scratch rather than mixing embeddings from two vector spaces.
        public bool Load()
        {
            lock (_lock)
            {
                var file = GetIndexPath();
                if (!File.Exists(file))
                    return false;

                try
                {
                    using var reader = new BinaryReader(File.OpenRead(file));
                    if (reader.ReadString() != Magic || reader.ReadInt32() != FormatVersion)
                        return false;
                    if (reader.ReadInt32() != OllamaEmbeddingClient.Dimensions)
                        return false;

                    var modelTag = reader.ReadString();
                    if (modelTag != ModelTag && ModelTag != "")
                        return false;

                    var count = reader.ReadInt32();
                    ModelTag = modelTag;
                    BuildDateUtc = DateTime.FromBinary(reader.ReadInt64());

                    var paths = new List<string>(count);
                    var dates = new List<long>(count);
                    var sizes = new List<long>(count);
                    var scales = new float[count];
                    var vectors = new sbyte[count * OllamaEmbeddingClient.Dimensions];
                    var pathToIndex = new Dictionary<string, int>(count, StringComparer.OrdinalIgnoreCase);

                    for (var i = 0; i < count; i++)
                    {
                        paths.Add(reader.ReadString());
                        dates.Add(reader.ReadInt64());
                        sizes.Add(reader.ReadInt64());
                        scales[i] = reader.ReadSingle();
                        var bytes = reader.ReadBytes(OllamaEmbeddingClient.Dimensions);
                        for (var d = 0; d < bytes.Length; d++)
                            vectors[i * OllamaEmbeddingClient.Dimensions + d] = (sbyte)bytes[d];
                        pathToIndex[paths[i]] = i;
                    }

                    _paths = paths;
                    _datesModified = dates;
                    _fileSizes = sizes;
                    _scales = scales;
                    _vectors = vectors;
                    _pathToIndex = pathToIndex;
                    return true;
                }
                catch (Exception e)
                {
                    Logger.Error(e, "Failed to load the semantic index; it will be rebuilt.");
                    return false;
                }
            }
        }

        public void Clear(string modelTag)
        {
            lock (_lock)
            {
                _paths = [];
                _datesModified = [];
                _fileSizes = [];
                _scales = [];
                _vectors = [];
                _pathToIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                ModelTag = modelTag;
            }
        }

        public void MarkBuildFinished()
        {
            lock (_lock)
                BuildDateUtc = DateTime.UtcNow;
        }

        public void Save()
        {
            lock (_lock)
            {
                var file = GetIndexPath();
                try
                {
                    Directory.CreateDirectory(ConfigPaths.GetConfigDirectory());
                    var tempFile = file + ".tmp";

                    using (var writer = new BinaryWriter(File.Create(tempFile)))
                    {
                        writer.Write(Magic);
                        writer.Write(FormatVersion);
                        writer.Write(OllamaEmbeddingClient.Dimensions);
                        writer.Write(ModelTag);
                        writer.Write(_paths.Count);
                        writer.Write(BuildDateUtc.ToBinary());

                        var dimensions = OllamaEmbeddingClient.Dimensions;
                        var buffer = new byte[dimensions];
                        for (var i = 0; i < _paths.Count; i++)
                        {
                            writer.Write(_paths[i]);
                            writer.Write(_datesModified[i]);
                            writer.Write(_fileSizes[i]);
                            writer.Write(_scales[i]);
                            for (var d = 0; d < dimensions; d++)
                                buffer[d] = (byte)_vectors[i * dimensions + d];
                            writer.Write(buffer);
                        }
                    }

                    // Replace, not delete+rename, so a crash mid-write never leaves the machine
                    // without an index.
                    File.Move(tempFile, file, overwrite: true);
                }
                catch (Exception e)
                {
                    Logger.Error(e, "Failed to save the semantic index.");
                }
            }
        }

        public bool Contains(string path, long dateModified)
        {
            lock (_lock)
                return _pathToIndex.TryGetValue(path, out var index) && _datesModified[index] == dateModified;
        }

        // Replaces the entry when the path is already indexed (file changed), appends otherwise.
        public void Add(string path, long dateModified, long fileSize, float[] vector)
        {
            lock (_lock)
            {
                var dimensions = OllamaEmbeddingClient.Dimensions;
                var quantized = Quantize(vector, out var scale);

                if (_pathToIndex.TryGetValue(path, out var index))
                {
                    _datesModified[index] = dateModified;
                    _fileSizes[index] = fileSize;
                    _scales[index] = scale;
                    Array.Copy(quantized, 0, _vectors, index * dimensions, dimensions);
                    return;
                }

                index = _paths.Count;
                _paths.Add(path);
                _datesModified.Add(dateModified);
                _fileSizes.Add(fileSize);

                if (_scales.Length < index + 1)
                    Array.Resize(ref _scales, Math.Max(_scales.Length * 2, 64));
                _scales[index] = scale;

                if (_vectors.Length < (index + 1) * dimensions)
                    Array.Resize(ref _vectors, Math.Max(_vectors.Length * 2, dimensions * 64));
                Array.Copy(quantized, 0, _vectors, index * dimensions, dimensions);

                _pathToIndex[path] = index;
            }
        }

        public SemanticMatch[] Search(float[] query, int topK)
        {
            lock (_lock)
            {
                var count = _paths.Count;
                if (count == 0)
                    return [];

                var dimensions = OllamaEmbeddingClient.Dimensions;
                var quantizedQuery = Quantize(query, out var queryScale);
                var vectors = _vectors;
                var scales = _scales;

                // Min-heap capped at topK: dequeue the worst score whenever the heap is full.
                var heap = new PriorityQueue<int, float>();
                for (var i = 0; i < count; i++)
                {
                    var offset = i * dimensions;
                    var dot = 0;

                    // Manual unrolling: this loop dominates query latency.
                    int d;
                    for (d = 0; d + 4 <= dimensions; d += 4)
                    {
                        dot += quantizedQuery[d] * vectors[offset + d];
                        dot += quantizedQuery[d + 1] * vectors[offset + d + 1];
                        dot += quantizedQuery[d + 2] * vectors[offset + d + 2];
                        dot += quantizedQuery[d + 3] * vectors[offset + d + 3];
                    }
                    for (; d < dimensions; d++)
                        dot += quantizedQuery[d] * vectors[offset + d];

                    // Both vectors are normalized before quantization, so the scaled integer dot
                    // product approximates the cosine similarity.
                    var score = dot * scales[i] * queryScale / (127f * 127f);

                    if (heap.Count < topK)
                    {
                        heap.Enqueue(i, score);
                    }
                    else if (heap.TryPeek(out _, out var worst) && score > worst)
                    {
                        heap.Dequeue();
                        heap.Enqueue(i, score);
                    }
                }

                var matches = new SemanticMatch[heap.Count];
                for (var i = heap.Count - 1; i >= 0; i--)
                {
                    heap.TryDequeue(out var index, out var score);
                    matches[i] = new SemanticMatch(index, score);
                }

                return matches;
            }
        }

        public SemanticIndexEntry GetEntry(int index)
        {
            lock (_lock)
                return new SemanticIndexEntry(_paths[index], _datesModified[index], _fileSizes[index]);
        }

        // Scalar quantization to [-127, 127] with a per-vector scale factor, the maximum absolute
        // component. Keeps the dot product a pure integer operation during scans.
        private static sbyte[] Quantize(float[] vector, out float scale)
        {
            var dimensions = OllamaEmbeddingClient.Dimensions;
            var max = 0f;
            foreach (var value in vector)
                max = Math.Max(max, Math.Abs(value));

            scale = max;
            var quantized = new sbyte[dimensions];
            if (max <= float.Epsilon)
                return quantized;

            for (var i = 0; i < dimensions && i < vector.Length; i++)
                quantized[i] = (sbyte)Math.Clamp(Math.Round(vector[i] / max * 127f), -127, 127);

            return quantized;
        }
    }
}
