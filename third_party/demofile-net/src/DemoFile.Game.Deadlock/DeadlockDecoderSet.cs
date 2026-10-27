using System.Diagnostics.CodeAnalysis;
using DemoFile.Sdk;

namespace DemoFile.Game.Deadlock;

internal partial class DeadlockDecoderSet
{
    public override bool TryCreateFallbackDecoder(
        SerializableField field,
        DecoderSet decoderSet,
        [NotNullWhen(true)] out SendNodeDecoder<object>? decoder)
    {
        if (FallbackDecoder.TryCreate(field.VarName, field.VarType, field.FieldEncodingInfo, decoderSet, out var engineFallbackDecoder))
        {
            decoder = (object _, ReadOnlySpan<int> path, ref BitBuffer buffer) =>
            {
                engineFallbackDecoder(default, path, ref buffer);
            };
            return true;
        }

        // Unknown scalar type (e.g. CitadelAbilityUpgradeInfoPacked_t, added after this schema was generated).
        // Like the haste parser, decode any plain identifier without its own serializer as an unsigned integer:
        // fixed64 / fixed8 when the encoder says so, otherwise a varint. Value is discarded; this keeps the bit stream aligned.
        if (field.FieldSerializerKey is null
            && field.VarType is { GenericParameter: null, IsPointer: false, ArrayLength: 0 })
        {
            switch (field.FieldEncodingInfo.VarEncoder)
            {
                case null or "fixed64":
                {
                    var u64 = FieldDecode.CreateDecoder_UInt64(field.FieldEncodingInfo);
                    decoder = (object _, ReadOnlySpan<int> path, ref BitBuffer buffer) => u64(ref buffer);
                    return true;
                }
                case "fixed8":
                    decoder = (object _, ReadOnlySpan<int> path, ref BitBuffer buffer) => buffer.ReadUBits(8);
                    return true;
            }
        }

        decoder = null;
        return false;
    }
}
