#if NETFRAMEWORK
using System;
using System.Buffers;
using System.IO;

namespace AssetStudio
{
    // .NET Framework's Stream has no Write(ReadOnlySpan<byte>) overload. Extension methods bind
    // only when no instance overload applies, so modern targets never see this.
    internal static class StreamSpanWriteExtensions
    {
        public static void Write(this Stream stream, ReadOnlySpan<byte> buffer)
        {
            var array = ArrayPool<byte>.Shared.Rent(buffer.Length);
            try
            {
                buffer.CopyTo(array);
                stream.Write(array, 0, buffer.Length);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(array);
            }
        }
    }
}
#endif
