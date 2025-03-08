
using System.Buffers.Binary;
using System.ComponentModel;
using System.Text;

namespace Serde.MsgPack;

internal sealed partial class MsgPackWriter : ISerializer
{
    private readonly ScratchBuffer _out;
    private readonly EnumSerializer _enumSerializer;

    public MsgPackWriter(ScratchBuffer scratch)
    {
        _out = scratch;
        _enumSerializer = new EnumSerializer(this);
    }

    public void WriteBool(bool b)
    {
        _out.Add(b ? (byte)0xc3 : (byte)0xc2);
    }

    public void WriteByte(byte b) => WriteU64(b);

    public void WriteChar(char c) => WriteU64(c);

    public ISerializeCollection WriteCollection(ISerdeInfo typeInfo, int? length)
    {
        if (length is null)
        {
            throw new InvalidOperationException("Cannot serialize a collection with an unknown length.");
        }
        if (typeInfo.Kind == InfoKind.Enumerable)
        {
            if (length <= 15)
            {
                _out.Add((byte)(0x90 | length));
            }
            else if (length <= 0xffff)
            {
                _out.Add(0xdc);
                WriteBigEndian((ushort)length);
            }
            else
            {
                _out.Add(0xdd);
                WriteBigEndian((uint)length);
            }
        }
        else if (typeInfo.Kind == InfoKind.Dictionary)
        {
            WriteMapLength(length);
        }
        else
        {
            throw new InvalidOperationException("Expected a collection, found: " + typeInfo.Kind);
        }
        return this;
    }

    private void WriteMapLength(int? length)
    {
        if (length <= 15)
        {
            _out.Add((byte)(0x80 | length));
        }
        else if (length <= 0xffff)
        {
            _out.Add(0xde);
            WriteBigEndian((ushort)length);
        }
        else
        {
            _out.Add(0xdf);
            WriteBigEndian((uint)length);
        }
    }

    public void WriteDecimal(decimal d)
    {
        throw new NotImplementedException();
    }

    public void WriteDouble(double d)
    {
        _out.Add(0xcb);
        WriteBigEndian(d);
    }

    public void WriteFloat(float f)
    {
        _out.Add(0xca);
        WriteBigEndian(f);
    }

    public void WriteI16(short i16) => WriteI64(i16);

    public void WriteI32(int i32) => WriteI64(i32);

    void ISerializer.WriteI64(long i64) => WriteI64(i64);

    private void WriteI64(long i64)
    {
        if (i64 >= 0)
        {
            WriteU64((ulong)i64);
        }
        else if (i64 >= -32)
        {
            _out.Add((byte)(0xe0 | (i64 + 32)));
        }
        else if (i64 >= -128)
        {
            _out.Add(0xd0);
            _out.Add((byte)i64);
        }
        else if (i64 >= -32768)
        {
            _out.Add(0xd1);
            WriteBigEndian((short)i64);
        }
        else if (i64 >= -2147483648)
        {
            _out.Add(0xd2);
            WriteBigEndian((int)i64);
        }
        else
        {
            _out.Add(0xd3);
            WriteBigEndian(i64);
        }
    }
    public void WriteNull()
    {
        _out.Add(0xc0);
    }

    public void WriteSByte(sbyte b) => WriteI64(b);

    private static readonly Encoding _utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public void WriteString(string s)
    {
        // We can write the string directly to the output buffer, but the string
        // is length-prefixed and we don't know precisely how long it will be until
        // we encode it. So we need to write space for the length prefix first, then
        // write the string, and finally go back and fill in the length prefix.
        var sLen = s.Length;
        var maxByteCount = _utf8.GetMaxByteCount(sLen);
        var bufferSize = checked(maxByteCount + 5 /* max length prefix */);
        _out.EnsureCapacity(_out.Count + bufferSize);
        int estimatedOffset = sLen switch {
            <= 31 => 1,
            <= 255 => 2,
            <= 65535 => 3,
            _ => 5
        };
        var currentSpan = _out.BufferSpan.Slice(_out.Count);
        var u8Dest = currentSpan.Slice(estimatedOffset, maxByteCount);
        int actualStrSize = _utf8.GetBytes(s, u8Dest);
        // write prefix and move body if necessary
        int actualOffset = WriteUtf8Header(actualStrSize, currentSpan);
		if (actualOffset < estimatedOffset)
        {
            u8Dest.CopyTo(currentSpan.Slice(actualOffset));
        }
        _out.Count += actualOffset + actualStrSize;
    }

    private void WriteUtf8(ReadOnlySpan<byte> str)
    {
        var count = _out.Count;
        _out.EnsureCapacity(count + str.Length + 5);
        var currentSpan = _out.BufferSpan.Slice(count);
        int offset = WriteUtf8Header(str.Length, currentSpan);
        str.CopyTo(currentSpan.Slice(offset));
        _out.Count = count + offset + str.Length;
    }

    private static int WriteUtf8Header(int length, Span<byte> span)
    {
        int offset;
        if (length <= 31)
        {
            offset = 1;
            span[0] = (byte)(0xa0 | length);
        }
        else if (length <= 0xff)
        {
            offset = 2;
            span[0] = 0xd9;
            span[1] = unchecked((byte)length);
        }
        else if (length <= 0xffff)
        {
            offset = 3;
            span[0] = 0xda;
            BinaryPrimitives.WriteUInt16BigEndian(span.Slice(1), (ushort)length);
        }
        else
        {
            offset = 5;
            span[0] = 0xdb;
            BinaryPrimitives.WriteUInt32BigEndian(span.Slice(1), (uint)length);
        }
        return offset;
    }

    public ISerializeType WriteType(ISerdeInfo typeInfo)
    {
        if (typeInfo.Kind == InfoKind.CustomType)
        {
            // Custom types are serialized as a map
            WriteMapLength(typeInfo.FieldCount);
        }
        else if (typeInfo.Kind == InfoKind.Enum)
        {
            return _enumSerializer;
        }
        return this;
    }

    public void WriteU16(ushort u16) => WriteU64(u16);

    public void WriteU32(uint u32) => WriteU64(u32);

    void ISerializer.WriteU64(ulong u64) => WriteU64(u64);

    private void WriteU64(ulong u64)
    {
        if (u64 <= 0x7f)
        {
            _out.Add((byte)u64);
        }
        else if (u64 <= 0xff)
        {
            _out.Add(0xcc);
            _out.Add((byte)u64);
        }
        else if (u64 <= 0xffff)
        {
            _out.Add(0xcd);
            WriteBigEndian((ushort)u64);
        }
        else if (u64 <= 0xffffffff)
        {
            _out.Add(0xce);
            WriteBigEndian((uint)u64);
        }
        else
        {
            _out.Add(0xcf);
            WriteBigEndian(u64);
        }
    }

    private void WriteBigEndian(ushort value)
    {
        _out.EnsureCapacity(_out.Count + 2);
        BinaryPrimitives.WriteUInt16BigEndian(
            _out.BufferSpan.Slice(_out.Count),
            value);
        _out.Count += 2;
    }

    private static void WriteBigEndian(ushort value, Span<byte> span)
    {
        span[0] = (byte)(value >> 8);
        span[1] = (byte)value;
    }

    private void WriteBigEndian(uint value)
    {
        _out.Add((byte)(value >> 24));
        _out.Add((byte)(value >> 16));
        _out.Add((byte)(value >> 8));
        _out.Add((byte)value);
    }

    private void WriteBigEndian(ulong value)
    {
        _out.Add((byte)(value >> 56));
        _out.Add((byte)(value >> 48));
        _out.Add((byte)(value >> 40));
        _out.Add((byte)(value >> 32));
        _out.Add((byte)(value >> 24));
        _out.Add((byte)(value >> 16));
        _out.Add((byte)(value >> 8));
        _out.Add((byte)value);
    }

    private void WriteBigEndian(short value) => WriteBigEndian((ushort)value);
    private void WriteBigEndian(int value) => WriteBigEndian((uint)value);
    private void WriteBigEndian(long value) => WriteBigEndian((ulong)value);
    private void WriteBigEndian(float f)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteSingleBigEndian(bytes, f);
        _out.AddRange(bytes);
    }
    private void WriteBigEndian(double d)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(bytes, d);
        _out.AddRange(bytes);
    }
}