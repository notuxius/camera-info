using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace CameraInspector;

internal sealed class TiffMetadata
{
    private readonly Stream _stream;
    private readonly long _start;
    private readonly long _end;
    private readonly bool _littleEndian;

    internal uint FirstDirectory { get; }

    internal TiffMetadata ReadNestedTiff(uint offset, uint size, int headerSize) =>
        new(_stream, _start + offset + headerSize, _start + offset + size);

    internal TiffMetadata(Stream stream, long start, long end, bool littleEndian)
    {
        _stream = stream;
        _start = start;
        _end = end;
        _littleEndian = littleEndian;
    }

    internal TiffMetadata(Stream stream, long start, long end) : this(stream, start, end, true)
    {
        Span<byte> header = stackalloc byte[8];
        ReadAt(0, header);
        _littleEndian = header[..2].SequenceEqual("II"u8);
        if ((!_littleEndian && !header[..2].SequenceEqual("MM"u8)) || UInt16(header[2..]) != 42)
            throw new InvalidDataException("Invalid TIFF header.");
        FirstDirectory = UInt32(header[4..]);
    }

    internal static TiffMetadata FromJpeg(Stream file, long start, long end)
    {
        file.Position = start;
        if (file.ReadByte() != 0xFF || file.ReadByte() != 0xD8)
            throw new InvalidDataException("Invalid JPEG header.");

        Span<byte> length = stackalloc byte[2];
        Span<byte> signature = stackalloc byte[6];
        while (file.Position <= end - 4 && file.ReadByte() == 0xFF)
        {
            int marker;
            do { marker = file.ReadByte(); }
            while (marker == 0xFF && file.Position < end);
            if (marker is <= 0 or 0xFF or 0xD9 or 0xDA || file.Position > end - 2)
                break;
            file.ReadExactly(length);
            int size = BinaryPrimitives.ReadUInt16BigEndian(length) - 2;
            long next = file.Position + size;
            if (size < 0 || next > end)
                break;
            if (marker == 0xE1 && size >= signature.Length + 8)
            {
                file.ReadExactly(signature);
                if (signature.SequenceEqual("Exif\0\0"u8))
                    return new(file, file.Position, next);
            }
            file.Position = next;
        }
        throw new InvalidDataException("JPEG contains no readable Exif segment.");
    }

    internal byte[] ReadDirectory(uint offset, uint available = uint.MaxValue)
    {
        if (offset < 8 || available < 2)
            throw new InvalidDataException("Invalid TIFF directory offset.");
        Span<byte> count = stackalloc byte[2];
        ReadAt(offset, count);
        int size = UInt16(count) * 12;
        if (size + 2 > available)
            throw new InvalidDataException("TIFF directory extends beyond its metadata block.");
        var entries = new byte[size];
        ReadAt((long)offset + 2, entries);
        return entries;
    }

    internal ReadOnlySpan<byte> Find(ReadOnlySpan<byte> entries, ushort id)
    {
        for (int i = 0; i < entries.Length; i += 12)
            if (UInt16(entries[i..]) == id)
                return entries.Slice(i, 12);
        return default;
    }

    internal string? Text(ReadOnlySpan<byte> entries, ushort id)
    {
        var tag = Find(entries, id);
        if (tag.IsEmpty || UInt16(tag[2..]) != 2 || UInt32(tag[4..]) is not (> 0 and <= 256))
            return null;

        int length = (int)UInt32(tag[4..]);
        Span<byte> value = stackalloc byte[length];
        if (length <= 4)
            tag.Slice(8, length).CopyTo(value);
        else
            ReadAt(UInt32(tag[8..]), value);
        int end = value.IndexOf((byte)0);
        string text = Encoding.ASCII.GetString(end < 0 ? value : value[..end]).Trim();
        return text.Length == 0 ? null : text;
    }

    internal uint? Number(ReadOnlySpan<byte> entries, ushort id)
    {
        var tag = Find(entries, id);
        return !tag.IsEmpty && UInt16(tag[2..]) == 4 && UInt32(tag[4..]) == 1 ? UInt32(tag[8..]) : null;
    }

    internal ushort? Short(ReadOnlySpan<byte> entries, ushort id)
    {
        var tag = Find(entries, id);
        return !tag.IsEmpty && UInt16(tag[2..]) == 3 && UInt32(tag[4..]) == 1 ? UInt16(tag[8..]) : null;
    }

    internal (uint Offset, uint Size)? Block(ReadOnlySpan<byte> entries, ushort id, ushort type, uint minimumSize)
    {
        var tag = Find(entries, id);
        if (tag.IsEmpty || UInt16(tag[2..]) != type)
            return null;

        uint size = UInt32(tag[4..]);
        uint offset = UInt32(tag[8..]);
        if (size < minimumSize || size > _end - _start - offset)
            throw new InvalidDataException("Invalid TIFF metadata block.");
        return (offset, size);
    }

    internal ushort UInt16(ReadOnlySpan<byte> bytes) => _littleEndian
        ? BinaryPrimitives.ReadUInt16LittleEndian(bytes) : BinaryPrimitives.ReadUInt16BigEndian(bytes);

    internal uint UInt32(ReadOnlySpan<byte> bytes) => _littleEndian
        ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : BinaryPrimitives.ReadUInt32BigEndian(bytes);

    internal void ReadAt(long offset, Span<byte> destination)
    {
        if (offset < 0 || offset > _end - _start - destination.Length)
            throw new InvalidDataException("TIFF value extends beyond its metadata block.");
        _stream.Position = _start + offset;
        _stream.ReadExactly(destination);
    }
}
