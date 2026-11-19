// ============================================================================
// BmrDecoder.cs
// BMR 压缩格式解码器: LINK 档案内嵌的自定义压缩算法
//
// BMR 文件头 (0x14 字节):
//   [0..2]  "BMR"        -- 魔数
//   [3]     step         -- RLE 步长 (0 = 跳过 RLE 阶段)
//   [4..7]  finalSize    -- 最终解压大小
//   [8..B]  key          -- BWT 逆变换的起始位置索引
//   [C..F]  unpackedSize -- Huffman 解码后的中间缓冲区大小
//
// 解压流程 (Unpack):
//   1. UnpackHuffman    -- 从偏移 0x14 开始, MSB-first 位流读取
//   2. UndoMoveToFront  -- Move-To-Front 逆变换
//   3. Decode           -- BWT 逆变换
//   4. DecompressRLE    -- 步进式 RLE 解压 (仅当 step != 0)
//
// 位读取器: MsbBitReader -- MSB-first 位流读取, 逐字节填充
// ============================================================================
using System.Buffers.Binary;

namespace Kaguya_YaneKit.Formats.Archive;

public sealed class BmrDecoder
{
    private const int HeaderSize = 0x14;
    private const int MaxHuffmanInternalNodes = 255;
    private const int MaxHuffmanDepth = 255;

    private readonly int _step;
    private readonly int _finalSize;
    private readonly int _key;
    private readonly int _unpackedSize;
    private readonly int _huffmanSize;
    private readonly byte[] _input;

    public BmrDecoder(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (data.Length < HeaderSize)
        {
            throw new InvalidDataException("BMR data too short.");
        }

        if (data[0] != 'B' || data[1] != 'M' || data[2] != 'R')
        {
            throw new InvalidDataException("Invalid BMR magic.");
        }

        _step = data[3];
        _finalSize = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4));
        _key = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(8));
        _unpackedSize = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(12));
        _huffmanSize = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(16));

        if (_finalSize < 0 || _finalSize > Array.MaxLength)
        {
            throw new InvalidDataException(
                $"BMR final output size is invalid: {_finalSize}.");
        }

        if (_unpackedSize < 0 || _unpackedSize > Array.MaxLength)
        {
            throw new InvalidDataException(
                $"BMR unpacked size is invalid: {_unpackedSize}.");
        }

        if (_huffmanSize < 0 || _huffmanSize > data.Length - HeaderSize)
        {
            throw new InvalidDataException(
                "BMR Huffman stream size is invalid.");
        }

        if (_step != 0 && _finalSize > 0 && _step > _finalSize)
        {
            throw new InvalidDataException(
                $"BMR RLE step ({_step}) exceeds final output size ({_finalSize}).");
        }

        _input = data;
    }

    public static bool IsBmr(byte[] data) =>
        data is { Length: >= 3 } &&
        data[0] == 'B' && data[1] == 'M' && data[2] == 'R';

    public byte[] Unpack()
    {
        if (_unpackedSize == 0)
        {
            if (_step != 0 && _finalSize != 0)
            {
                throw new InvalidDataException(
                    "BMR stream has no Huffman output but requests a non-empty RLE output.");
            }

            return [];
        }

        if (_huffmanSize == 0)
        {
            throw new InvalidDataException(
                "BMR Huffman stream is empty.");
        }

        var bitReader = new MsbBitReader(
            _input,
            HeaderSize,
            _huffmanSize);

        var output = UnpackHuffman(bitReader, _unpackedSize);

        UndoMoveToFront(output);
        output = Decode(output, _key);

        if (_step != 0)
        {
            output = DecompressRLE(
                output,
                _finalSize,
                _step);
        }

        return output;
    }

    private static byte[] UnpackHuffman(
        MsbBitReader reader,
        int outputSize)
    {
        if (outputSize == 0)
        {
            return [];
        }

        var output = new byte[outputSize];

        ushort token = 256;
        var tree = new ushort[2, 256];

        var root = CreateHuffmanTree(
            reader,
            tree,
            ref token,
            depth: 0);

        for (var dst = 0; dst < output.Length; dst++)
        {
            var symbol = root;

            while (symbol >= 0x100)
            {
                var nodeIndex = symbol - 256;

                if (nodeIndex >= token - 256)
                {
                    throw new InvalidDataException(
                        "BMR Huffman tree references an undefined node.");
                }

                var bit = reader.GetNextBit();

                if (bit < 0)
                {
                    throw new EndOfStreamException(
                        "Unexpected end of BMR Huffman stream.");
                }

                symbol = tree[bit, nodeIndex];
            }

            output[dst] = (byte)symbol;
        }

        return output;
    }

    private static ushort CreateHuffmanTree(
        MsbBitReader reader,
        ushort[,] tree,
        ref ushort token,
        int depth)
    {
        if (depth > MaxHuffmanDepth)
        {
            throw new InvalidDataException(
                "BMR Huffman tree exceeds the maximum depth.");
        }

        var bit = reader.GetNextBit();

        if (bit < 0)
        {
            throw new EndOfStreamException(
                "Unexpected end of BMR Huffman tree.");
        }

        if (bit == 0)
        {
            return (ushort)reader.GetBits(8);
        }

        if (token - 256 >= MaxHuffmanInternalNodes)
        {
            throw new InvalidDataException(
                "BMR Huffman tree contains too many internal nodes.");
        }

        var node = token++;
        var index = node - 256;

        tree[0, index] = CreateHuffmanTree(
            reader,
            tree,
            ref token,
            depth + 1);

        tree[1, index] = CreateHuffmanTree(
            reader,
            tree,
            ref token,
            depth + 1);

        return node;
    }

    private static void UndoMoveToFront(byte[] data)
    {
        var dict = new byte[256];

        for (var i = 0; i < 256; i++)
        {
            dict[i] = (byte)i;
        }

        for (var i = 0; i < data.Length; i++)
        {
            var value = data[i];
            var saved = dict[value];

            data[i] = saved;

            for (var j = value; j > 0; j--)
            {
                dict[j] = dict[j - 1];
            }

            dict[0] = saved;
        }
    }

    private static byte[] Decode(byte[] input, int key)
    {
        if (input.Length == 0)
        {
            if (key != 0)
            {
                throw new InvalidDataException(
                    "BMR BWT key is invalid for an empty input.");
            }

            return [];
        }

        if (key < 0 || key >= input.Length)
        {
            throw new InvalidDataException(
                $"BMR BWT key is outside the decoded buffer: {key}.");
        }

        var freqTable = new int[256];

        for (var i = 0; i < input.Length; i++)
        {
            freqTable[input[i]]++;
        }

        for (var i = 1; i < 256; i++)
        {
            freqTable[i] += freqTable[i - 1];
        }

        var distribTable = new int[input.Length];

        for (var i = input.Length - 1; i >= 0; i--)
        {
            var value = input[i];
            var frequency = --freqTable[value];

            distribTable[frequency] = i;
        }

        var position = key;
        var output = new byte[input.Length];

        for (var i = 0; i < output.Length; i++)
        {
            position = distribTable[position];
            output[i] = input[position];
        }

        return output;
    }

    private static byte[] DecompressRLE(
        byte[] input,
        int finalSize,
        int step)
    {
        if (finalSize == 0)
        {
            return [];
        }

        if (step <= 0 || step > finalSize)
        {
            throw new InvalidDataException(
                $"BMR RLE step is invalid: {step}.");
        }

        var result = new byte[finalSize];
        var source = 0;

        byte ReadRleByte()
        {
            if ((uint)source >= (uint)input.Length)
            {
                throw new EndOfStreamException(
                    "Unexpected end of BMR RLE data.");
            }

            return input[source++];
        }

        for (var lane = 0; lane < step; lane++)
        {
            var previous = ReadRleByte();

            result[lane] = previous;

            var destination = lane + step;

            while (destination < result.Length)
            {
                var value = ReadRleByte();

                result[destination] = value;
                destination += step;

                if (value == previous)
                {
                    var countByte = ReadRleByte();

                    // 必須明確宣告為 int，因為後續可能讀取兩位元組計數值。
                    var count = (int)countByte;

                    if ((countByte & 0x80) != 0)
                    {
                        count = ReadRleByte()
                            + ((countByte & 0x7F) << 8)
                            + 128;
                    }

                    // 保留原始演算法行為：
                    // 重複次數只填入尚未超出輸出陣列的範圍。
                    while (count-- > 0 && destination < result.Length)
                    {
                        result[destination] = value;
                        destination += step;
                    }

                    if (destination < result.Length)
                    {
                        value = ReadRleByte();

                        result[destination] = value;
                        destination += step;
                    }
                }

                previous = value;
            }
        }

        return result;
    }

    private sealed class MsbBitReader
    {
        private readonly byte[] _data;
        private int _position;
        private readonly int _end;
        private int _bitsLeft;
        private int _currentByte;

        public MsbBitReader(byte[] data, int offset, int length)
        {
            _data = data;
            _position = offset;
            _end = checked(offset + length);
        }

        public int GetNextBit()
        {
            if (_bitsLeft == 0)
            {
                if (_position >= _end)
                {
                    return -1;
                }

                _currentByte = _data[_position++];
                _bitsLeft = 8;
            }

            _bitsLeft--;

            return (_currentByte >> _bitsLeft) & 1;
        }

        public int GetBits(int count)
        {
            var value = 0;

            for (var i = 0; i < count; i++)
            {
                var bit = GetNextBit();

                if (bit < 0)
                {
                    throw new EndOfStreamException(
                        "Unexpected end of BMR bit stream.");
                }

                value = (value << 1) | bit;
            }

            return value;
        }
    }
}
