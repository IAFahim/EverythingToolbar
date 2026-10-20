namespace EverythingToolbar.App.Semantic
{
    /// <summary>
    /// Prepares an image file for embedding: decode, downscale and re-encode as JPEG.
    /// Returns null when the file cannot be decoded (missing codec, corrupt file, …).
    /// Implemented in the main assembly where WPF's imaging stack is available.
    /// </summary>
    public interface IEmbeddingImageEncoder
    {
        byte[]? EncodeToJpeg(string path, int maxDimension);
    }
}
