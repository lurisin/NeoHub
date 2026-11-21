// DSC TLink - a communications library for DSC Powerseries NEO alarm panels
// Copyright (C) 2024 Brian Humlicek
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

namespace DSC.TLink.Serialization
{
    /// <summary>
    /// Handles serialization of integers with [CompactInteger] attribute.
    /// Only stores significant bytes with a leading length prefix.
    /// </summary>
    internal static class CompactIntegerSerializer
    {
        internal static void Write(List<byte> bytes, Type propertyType, object? value)
        {
            var underlyingType = propertyType.IsEnum ? Enum.GetUnderlyingType(propertyType) : propertyType;
            var rawValue = value != null && propertyType.IsEnum
                ? Convert.ChangeType(value, underlyingType)
                : value ?? GetDefaultValue(underlyingType);

            byte[] fullBytes = PrimitiveSerializer.GetBytes(rawValue, underlyingType);
            int startIndex = FindSignificantByteIndex(fullBytes);
            int length = fullBytes.Length - startIndex;

            bytes.Add((byte)length);
            bytes.AddRange(fullBytes.AsSpan(startIndex));
        }

        /// <summary>
        /// Reads one compact integer (<c>[length][value, big-endian]</c>) without converting it: <paramref name="value"/>
        /// holds the value bytes exactly as received. Stricter than <see cref="Read"/>: false, and the offset unchanged,
        /// for a zero length or when the payload ends before the value does.
        /// </summary>
        internal static bool TryReadValueBytes(ReadOnlySpan<byte> bytes, ref int offset, out byte[] value)
        {
            value = Array.Empty<byte>();
            if (offset >= bytes.Length)
                return false;

            int length = bytes[offset];
            if (length == 0 || offset + 1 + length > bytes.Length)
                return false;

            value = bytes.Slice(offset + 1, length).ToArray();
            offset += 1 + length;
            return true;
        }

        /// <summary>
        /// <see cref="TryReadValueBytes"/> for a non-negative number that fits in an int (for example a partition):
        /// false as well when the value is too large.
        /// </summary>
        internal static bool TryReadInt32(ReadOnlySpan<byte> bytes, ref int offset, out int value)
        {
            value = 0;
            var start = offset;
            if (!TryReadValueBytes(bytes, ref offset, out var raw))
                return false;

            long result = 0;
            foreach (var b in raw)
            {
                result = (result << 8) | b;
                if (result > int.MaxValue)
                {
                    offset = start;
                    return false;
                }
            }
            value = (int)result;
            return true;
        }

        internal static object Read(ReadOnlySpan<byte> bytes, ref int offset, Type propertyType)
        {
            if (offset >= bytes.Length)
                throw new InvalidOperationException(
                    "Not enough bytes to read length prefix for compact integer");

            int length = bytes[offset++];

            if (offset + length > bytes.Length)
                throw new InvalidOperationException(
                    $"Not enough bytes to read compact integer (expected {length}, got {bytes.Length - offset})");

            var compactBytes = bytes.Slice(offset, length);
            offset += length;

            var underlyingType = propertyType.IsEnum ? Enum.GetUnderlyingType(propertyType) : propertyType;
            var rawValue = PrimitiveSerializer.ReadFromBytes(compactBytes, underlyingType, signExtend: false);

            return propertyType.IsEnum ? Enum.ToObject(propertyType, rawValue) : rawValue;
        }

        private static int FindSignificantByteIndex(byte[] fullBytes)
        {
            int startIndex = 0;

            // Skip leading zero bytes, always keep at least one byte
            while (startIndex < fullBytes.Length - 1 && fullBytes[startIndex] == 0)
            {
                startIndex++;
            }

            return startIndex;
        }

        private static object GetDefaultValue(Type type)
        {
            return Type.GetTypeCode(type) switch
            {
                TypeCode.Byte => (byte)0,
                TypeCode.SByte => (sbyte)0,
                TypeCode.UInt16 => (ushort)0,
                TypeCode.Int16 => (short)0,
                TypeCode.UInt32 => (uint)0,
                TypeCode.Int32 => 0,
                TypeCode.UInt64 => (ulong)0,
                TypeCode.Int64 => (long)0,
                _ => throw new NotSupportedException()
            };
        }

        // Remove all the duplicate GetBytes* and Read* methods - use PrimitiveSerializer instead
    }
}