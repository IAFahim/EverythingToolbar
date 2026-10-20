using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EverythingToolbar.App.Semantic;
using NLog;

namespace EverythingToolbar.Semantic
{
    public sealed class WicImageEncoder : IEmbeddingImageEncoder
    {
        private static readonly ILogger Logger = ToolbarLogger.GetLogger("EverythingToolbar.Semantic.WicImageEncoder");

        public byte[]? EncodeToJpeg(string path, int maxDimension)
        {
            try
            {
                // WIC objects are thread-affine; this method creates and consumes them on the
                // same background thread, so no dispatcher is involved.
                using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read | FileShare.Delete,
                    bufferSize: 4096,
                    FileOptions.SequentialScan
                );

                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
                if (decoder.Frames.Count == 0)
                    return null;

                var frame = decoder.Frames[0];
                var longestSide = System.Math.Max(frame.PixelWidth, frame.PixelHeight);
                var scale = System.Math.Min(1.0, (double)maxDimension / longestSide);

                BitmapSource source = frame;
                if (scale < 1.0)
                    source = new TransformedBitmap(frame, new ScaleTransform(scale, scale));

                var encoder = new JpegBitmapEncoder { QualityLevel = 70 };
                encoder.Frames.Add(BitmapFrame.Create(source));

                using var memory = new MemoryStream();
                encoder.Save(memory);
                return memory.ToArray();
            }
            catch (Exception e)
            {
                // Common for HEIC/AVIF without an installed codec, or corrupt files.
                Logger.Debug(e, "Failed to encode image for embedding: {path}", path);
                return null;
            }
        }
    }
}
