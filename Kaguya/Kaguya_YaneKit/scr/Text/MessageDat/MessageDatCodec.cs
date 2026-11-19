// ============================================================================
// MessageDatCodec.cs
// message.dat 二進位編解碼器
//
// 核心原則:
//   - 保留原始位元組，未修改文字時優先使用原始資料
//   - 讀取所有長度欄位時確認資料完整，避免截斷資料被當成有效內容
//   - 不設定任意固定檔案大小上限，避免排除合法的大型遊戲資源
// ============================================================================
using System.Text;
using Kaguya_YaneKit.Text.MessageDat.Model;

namespace Kaguya_YaneKit.Text.MessageDat;

public sealed class MessageDatCodec
{
    private readonly Encoding _readEncoding;
    private readonly Encoding _writeEncoding;
    private readonly MessagePlaceholderConfig _placeholders;

    public MessageDatCodec(
        Encoding readEncoding,
        Encoding writeEncoding,
        MessagePlaceholderConfig? placeholders = null)
    {
        _readEncoding = readEncoding ?? throw new ArgumentNullException(nameof(readEncoding));
        _writeEncoding = writeEncoding ?? throw new ArgumentNullException(nameof(writeEncoding));
        _placeholders = placeholders ?? MessagePlaceholderConfig.Empty;
    }

    public MessageDatDocument Read(ReadOnlySpan<byte> source)
    {
        using var stream = new MemoryStream(source.ToArray(), writable: false);
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: false);

        var magicBytes = ReadExact(reader, MessageDatDocument.Magic.Length, "message.dat magic");
        var magic = Encoding.ASCII.GetString(magicBytes);
        if (!string.Equals(magic, MessageDatDocument.Magic, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Invalid message.dat magic: {magic}");
        }

        EnsureRemaining(stream, 2, "message.dat header");

        var document = new MessageDatDocument
        {
            Encrypted = reader.ReadByte() != 0,
            XorKey = reader.ReadByte()
        };

        ReadStringList(reader, document.Names, document.RawNameBytes, document);
        ReadStringList(reader, document.Choices, document.RawChoiceBytes, document);

        var messageCount = ReadCount(reader, "message", minimumBytesPerItem: sizeof(int));
        for (var i = 0; i < messageCount; i++)
        {
            document.Messages.Add(ReadMessage(reader, document));
        }

        var commandCount = ReadCount(reader, "command", minimumBytesPerItem: sizeof(int) + sizeof(byte));
        for (var i = 0; i < commandCount; i++)
        {
            document.Commands.Add(ReadCommand(reader));
        }

        var remaining = checked((int)(stream.Length - stream.Position));
        document.RawTail = remaining > 0
            ? ReadExact(reader, remaining, "message.dat raw tail")
            : [];

        return document;
    }

    public byte[] Write(MessageDatDocument document, bool? encrypted = null, byte? xorKey = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        document.Encrypted = encrypted ?? document.Encrypted;
        document.XorKey = xorKey ?? document.XorKey;

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

        writer.Write(Encoding.ASCII.GetBytes(MessageDatDocument.Magic));
        writer.Write(document.Encrypted);
        writer.Write(document.XorKey);
        WriteStringList(writer, document.Names, document.RawNameBytes, document);
        WriteStringList(writer, document.Choices, document.RawChoiceBytes, document);

        writer.Write(document.Messages.Count);
        foreach (var message in document.Messages)
        {
            WriteMessage(writer, message, document);
        }

        writer.Write(document.Commands.Count);
        foreach (var command in document.Commands)
        {
            WriteCommand(writer, command);
        }

        if (document.RawTail.Length > 0)
        {
            writer.Write(document.RawTail);
        }

        writer.Flush();
        return stream.ToArray();
    }

    public static Encoding ResolveEncoding(string? value)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        if (string.IsNullOrWhiteSpace(value))
        {
            return Encoding.GetEncoding(932);
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "cp932" or "sjis" or "shift-jis" or "shift_jis" => Encoding.GetEncoding(932),
            "cp936" or "gbk" => Encoding.GetEncoding(936),
            "utf8" or "utf-8" => Encoding.UTF8,
            _ => int.TryParse(value, out var codePage)
                ? Encoding.GetEncoding(codePage)
                : Encoding.GetEncoding(value)
        };
    }

    private void ReadStringList(
        BinaryReader reader,
        List<string> target,
        List<byte[]> rawTarget,
        MessageDatDocument document)
    {
        var count = ReadCount(reader, "string list", minimumBytesPerItem: sizeof(ushort));

        for (var i = 0; i < count; i++)
        {
            EnsureRemaining(reader.BaseStream, sizeof(ushort), "string length");
            var length = reader.ReadUInt16();
            var bytes = ReadExact(reader, length, $"string entry {i}");
            XorIfNeeded(bytes, document);

            rawTarget.Add(bytes.ToArray());
            target.Add(_placeholders.Decode(bytes, _readEncoding));
        }
    }

    private void WriteStringList(
        BinaryWriter writer,
        IReadOnlyList<string> values,
        IReadOnlyList<byte[]> rawValues,
        MessageDatDocument document)
    {
        writer.Write(values.Count);

        for (var i = 0; i < values.Count; i++)
        {
            var value = values[i];
            var bytes = TryUseRawBytes(rawValues, i, value)
                ?? _placeholders.Encode(value, _writeEncoding);

            if (bytes.Length > ushort.MaxValue)
            {
                throw new InvalidDataException("message.dat string entry is too large.");
            }

            XorIfNeeded(bytes, document);
            writer.Write((ushort)bytes.Length);
            writer.Write(bytes);
        }
    }

    private MessageEntry ReadMessage(BinaryReader reader, MessageDatDocument document)
    {
        EnsureRemaining(reader.BaseStream, sizeof(int), "message block length");
        var blockLength = reader.ReadInt32();

        if (blockLength < sizeof(int) + sizeof(byte))
        {
            throw new InvalidDataException($"Invalid message block length: {blockLength}");
        }

        var block = ReadExact(reader, blockLength, "message block");
        XorIfNeeded(block, document);

        using var stream = new MemoryStream(block, writable: false);
        using var blockReader = new BinaryReader(stream, Encoding.Unicode, leaveOpen: false);
        var message = new MessageEntry();

        EnsureRemaining(stream, sizeof(int), "message text length");
        var textLength = blockReader.ReadInt32();

        if (textLength < 0 || (long)textLength + sizeof(byte) > stream.Length - stream.Position)
        {
            throw new InvalidDataException($"Invalid message text length: {textLength}");
        }

        var textBytes = ReadExact(blockReader, textLength, "message text");
        message.Text = _placeholders.Decode(textBytes, _readEncoding);
        message.RawTextBytes = textBytes.ToArray();

        var voiceCount = blockReader.ReadByte();
        for (var i = 0; i < voiceCount; i++)
        {
            message.Voices.Add(ReadNullTerminatedUtf16(blockReader));
        }

        if (stream.Position != stream.Length)
        {
            throw new InvalidDataException("message block has unread trailing bytes.");
        }

        return message;
    }

    private void WriteMessage(BinaryWriter writer, MessageEntry message, MessageDatDocument document)
    {
        using var stream = new MemoryStream();

        using (var blockWriter = new BinaryWriter(stream, Encoding.Unicode, leaveOpen: true))
        {
            var textBytes = TryUseRawBytes(message.RawTextBytes, message.Text)
                ?? _placeholders.Encode(message.Text, _writeEncoding);

            blockWriter.Write(textBytes.Length);
            blockWriter.Write(textBytes);

            if (message.Voices.Count > byte.MaxValue)
            {
                throw new InvalidDataException("message entry has too many voices.");
            }

            blockWriter.Write((byte)message.Voices.Count);
            foreach (var voice in message.Voices)
            {
                blockWriter.Write(Encoding.Unicode.GetBytes(voice));
                blockWriter.Write((ushort)0);
            }
        }

        var block = stream.ToArray();
        XorIfNeeded(block, document);
        writer.Write(block.Length);
        writer.Write(block);
    }

    private static MessageCommand ReadCommand(BinaryReader reader)
    {
        EnsureRemaining(reader.BaseStream, sizeof(int) + sizeof(byte), "message command header");

        var command = new MessageCommand
        {
            Id = reader.ReadInt32()
        };

        var count = reader.ReadByte();
        EnsureRemaining(reader.BaseStream, checked(count * sizeof(int)), "message command parameters");

        for (var i = 0; i < count; i++)
        {
            command.Params.Add(reader.ReadInt32());
        }

        return command;
    }

    private byte[]? TryUseRawBytes(IReadOnlyList<byte[]> rawValues, int index, string currentText)
    {
        if (index >= rawValues.Count)
        {
            return null;
        }

        return TryUseRawBytes(rawValues[index], currentText);
    }

    private byte[]? TryUseRawBytes(byte[] rawBytes, string currentText)
    {
        if (rawBytes.Length == 0 && currentText.Length != 0)
        {
            return null;
        }

        return _placeholders.Decode(rawBytes, _readEncoding) == currentText
            ? rawBytes.ToArray()
            : null;
    }

    private static void WriteCommand(BinaryWriter writer, MessageCommand command)
    {
        if (command.Params.Count > byte.MaxValue)
        {
            throw new InvalidDataException("message command has too many params.");
        }

        writer.Write(command.Id);
        writer.Write((byte)command.Params.Count);

        foreach (var param in command.Params)
        {
            writer.Write(param);
        }
    }

    private static string ReadNullTerminatedUtf16(BinaryReader reader)
    {
        var bytes = new List<byte>();

        while (true)
        {
            EnsureRemaining(reader.BaseStream, sizeof(ushort), "UTF-16 voice terminator");
            var value = reader.ReadUInt16();

            if (value == 0)
            {
                break;
            }

            bytes.Add((byte)(value & 0xff));
            bytes.Add((byte)(value >> 8));
        }

        return Encoding.Unicode.GetString(bytes.ToArray());
    }

    private static int ReadCount(BinaryReader reader, string fieldName, int minimumBytesPerItem)
    {
        EnsureRemaining(reader.BaseStream, sizeof(int), $"{fieldName} count");
        var count = reader.ReadInt32();

        if (count < 0)
        {
            throw new InvalidDataException($"Negative {fieldName} count: {count}");
        }

        var remaining = reader.BaseStream.Length - reader.BaseStream.Position;
        if (minimumBytesPerItem > 0 && count > remaining / minimumBytesPerItem)
        {
            throw new InvalidDataException(
                $"Invalid {fieldName} count: {count}; remaining data is too short.");
        }

        return count;
    }

    private static byte[] ReadExact(BinaryReader reader, int count, string fieldName)
    {
        if (count < 0)
        {
            throw new InvalidDataException($"Negative length for {fieldName}: {count}");
        }

        EnsureRemaining(reader.BaseStream, count, fieldName);

        var bytes = reader.ReadBytes(count);
        if (bytes.Length != count)
        {
            throw new EndOfStreamException(
                $"Unexpected end of file while reading {fieldName}: expected {count} bytes, got {bytes.Length}.");
        }

        return bytes;
    }

    private static void EnsureRemaining(Stream stream, long requiredBytes, string fieldName)
    {
        if (requiredBytes < 0 || requiredBytes > stream.Length - stream.Position)
        {
            throw new EndOfStreamException($"Truncated message.dat while reading {fieldName}.");
        }
    }

    private static void XorIfNeeded(byte[] bytes, MessageDatDocument document)
    {
        if (!document.Encrypted)
        {
            return;
        }

        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] ^= document.XorKey;
        }
    }
}
