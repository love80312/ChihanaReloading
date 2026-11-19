// ============================================================================
// ScrSemanticPass.cs
// 语义分析遍: 在指令与标签之间建立跳转关系
//
// AttachInstructionLabels (反汇编方向):
//   遍历所有指令, 对含 PC 目标的操作码 (jump/call/save/if_true/if_false),
//   读取 body 中的目标偏移, 在对应位置插入标签 (如 loc_00001234),
//   并设置 instruction.TargetLabel 指向该标签
//
// MaterializeInstructionTargets (汇编方向):
//   遍历所有指令, 将 TargetLabel 通过 ScrLabelService 解析为字节偏移,
//   写回 body 中对应位置的 u32 值
// ============================================================================
using System.Buffers.Binary;
using Kaguya_YaneKit.Script.Paramsipt.Params.Model;

namespace Kaguya_YaneKit.Script.Params;

public static class ScrSemanticPass
{
    public static void AttachInstructionLabels(
        ScriptDocument script,
        int codeStart)
    {
        var knownOffsets = ScrLabelService.GetLabelOffsets(script);

        // 多個 label 可以合法地指向同一個位移（例如連續的 alias label）。
        // 反向索引必須先分組，不能直接 ToDictionary(offset)，否則重複位移會拋例外。
        // 使用名稱排序固定 canonical label，避免結果依賴 Dictionary 列舉順序。
        var labelsByOffset = knownOffsets
            .GroupBy(kv => kv.Value)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(kv => kv.Key)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .First());

        foreach (var instruction in script.Instructions.ToList())
        {
            if (!ScrOpcodeInfo.TryGetPcTargetOffset(
                    instruction.Opcode,
                    instruction.Body.Length,
                    out var operandOffset))
            {
                continue;
            }

            var targetPc = BinaryPrimitives.ReadUInt32LittleEndian(
                instruction.Body.AsSpan(operandOffset, 4));

            if (targetPc > int.MaxValue || targetPc < codeStart)
            {
                continue;
            }

            var pc = checked((int)targetPc - codeStart);

            if (!labelsByOffset.TryGetValue(pc, out var label))
            {
                label = ScrLabelService.MakeOffsetLabel(pc);

                if (!EnsureLabel(script, label, pc))
                {
                    continue;
                }

                labelsByOffset[pc] = label;
            }

            instruction.TargetLabel = label;
        }
    }

    public static void MaterializeInstructionTargets(
        ScriptDocument script,
        int codeStart)
    {
        var labelOffsets = ScrLabelService.GetLabelOffsets(script);

        foreach (var instruction in script.Instructions.ToList())
        {
            if (instruction.TargetLabel is null)
            {
                continue;
            }

            if (!ScrOpcodeInfo.TryGetPcTargetOffset(
                    instruction.Opcode,
                    instruction.Body.Length,
                    out var operandOffset))
            {
                continue;
            }

            if (!labelOffsets.TryGetValue(
                    instruction.TargetLabel,
                    out var offset))
            {
                throw new InvalidDataException(
                    $"Instruction target label was not defined: {instruction.TargetLabel}");
            }

            BinaryPrimitives.WriteUInt32LittleEndian(
                instruction.Body.AsSpan(operandOffset, 4),
                checked((uint)(offset + codeStart)));
        }
    }

    private static bool EnsureLabel(
        ScriptDocument script,
        string label,
        int targetOffset)
    {
        var offset = 0;

        for (var i = 0; i < script.Elements.Count; i++)
        {
            if (offset == targetOffset)
            {
                if (script.Elements[i] is ScriptLabel current &&
                    current.Name == label)
                {
                    return true;
                }

                if (i == 0 ||
                    script.Elements[i - 1] is not ScriptLabel existing ||
                    existing.Name != label)
                {
                    script.Elements.Insert(
                        i,
                        new ScriptLabel { Name = label });
                }

                return true;
            }

            switch (script.Elements[i])
            {
                case ScriptInstruction instruction:
                    offset += instruction.DeclaredLength;
                    break;

                case ScriptTail tail:
                    offset += tail.Data.Length;
                    break;
            }
        }

        if (offset == targetOffset)
        {
            script.AddLabel(label);
            return true;
        }

        return false;
    }
}
