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
        // DeadlockScout patch: an unknown pointer-to-struct with a serializer (e.g. CCitadelPlayOfTheGame* m_pPlayOfTheGame)
        // is decoded from that serializer. DemoFile's own pointer fallback only copes while the pointer is null.
        if (field.VarType.IsPointer && field.FieldSerializerKey is { } pointerKey && TryCreateUnknownStructDecoder(field, pointerKey, out decoder))
            return true;

        if (FallbackDecoder.TryCreate(field.VarName, field.VarType, field.FieldEncodingInfo, decoderSet, out var engineFallbackDecoder))
        {
            decoder = (object _, ReadOnlySpan<int> path, ref BitBuffer buffer) =>
            {
                engineFallbackDecoder(default, path, ref buffer);
            };
            return true;
        }

        // DeadlockScout patch (see VENDORED.md): fields this schema predates. Like the haste parser, decode them from the
        // layout the broadcast sends and discard the values, which keeps the bit stream aligned.
        if (field.FieldSerializerKey is { } serializerKey && TryCreateUnknownStructDecoder(field, serializerKey, out decoder))
            return true;
        if (field.FieldSerializerKey is null && TryCreateUnknownScalarDecoder(field, out decoder))
            return true;

        decoder = null;
        return false;
    }

    private static bool IsVector(FieldType type) =>
        type.GenericParameter is not null
        && type.Name is "CNetworkUtlVectorBase" or "CUtlVectorEmbeddedNetworkVar" or "CUtlVector";

    /// <summary>
    /// A struct or class field whose type this schema does not know (e.g. <c>AnimGraph2SerializedPoseRecipeSlot_t</c>):
    /// embedded, behind a pointer, in a fixed array or in a vector. Path shapes follow the generated decoders.
    /// </summary>
    private bool TryCreateUnknownStructDecoder(SerializableField field, SerializerKey key, [NotNullWhen(true)] out SendNodeDecoder<object>? decoder)
    {
        if (!TryCreateSerializerDecoder(key, out var inner))
        {
            decoder = null;
            return false;
        }

        var type = field.VarType;
        if (type.IsPointer)
        {
            decoder = (object _, ReadOnlySpan<int> path, ref BitBuffer buffer) =>
            {
                if (path.Length == 1) buffer.ReadOneBit();
                else inner(null!, path[1..], ref buffer);
            };
        }
        else if (IsVector(type) || type.ArrayLength != 0)
        {
            bool vector = IsVector(type);
            decoder = (object _, ReadOnlySpan<int> path, ref BitBuffer buffer) =>
            {
                if (path.Length == 1)
                {
                    if (vector) buffer.ReadUVarInt32(); // new size
                }
                else if (path.Length > 2)
                {
                    inner(null!, path[2..], ref buffer); // path[1] is the element index
                }
            };
        }
        else
        {
            decoder = (object _, ReadOnlySpan<int> path, ref BitBuffer buffer) => inner(null!, path[1..], ref buffer);
        }
        return true;
    }

    /// <summary>Decodes every field of a serializer with the fallbacks above, built lazily so recursive layouts work.</summary>
    private bool TryCreateSerializerDecoder(SerializerKey key, [NotNullWhen(true)] out SendNodeDecoder<object>? decoder)
    {
        if (!TryGetSerializer(key, out var serializer))
        {
            decoder = null;
            return false;
        }

        var fieldDecoders = new SendNodeDecoder<object>?[serializer.Fields.Length];
        decoder = (object _, ReadOnlySpan<int> path, ref BitBuffer buffer) =>
        {
            var index = path[0];
            var fieldDecoder = fieldDecoders[index];
            if (fieldDecoder is null)
            {
                var field = serializer.Fields[index];
                if (!TryCreateFallbackDecoder(field, this, out fieldDecoder))
                    throw new NotSupportedException($"Unrecognised serializer field: {field}");
                fieldDecoders[index] = fieldDecoder;
            }
            fieldDecoder(null!, path, ref buffer);
        };
        return true;
    }

    /// <summary>
    /// A scalar type this schema does not know (e.g. <c>CitadelAbilityUpgradeInfoPacked_t</c>), alone or in a vector or
    /// fixed array: an unsigned integer, varint unless the encoder says fixed64 or fixed8.
    /// </summary>
    private static bool TryCreateUnknownScalarDecoder(SerializableField field, [NotNullWhen(true)] out SendNodeDecoder<object>? decoder)
    {
        decoder = null;
        var type = field.VarType;
        if (type.IsPointer) return false;
        var element = IsVector(type) ? type.GenericParameter! : type;
        if (element.GenericParameter is not null || element.IsPointer) return false;

        FieldDecode.FieldDecoder<ulong> read;
        switch (field.FieldEncodingInfo.VarEncoder)
        {
            case null or "fixed64":
                read = FieldDecode.CreateDecoder_UInt64(field.FieldEncodingInfo);
                break;
            case "fixed8":
                read = (ref BitBuffer buffer) => buffer.ReadUBits(8);
                break;
            default:
                return false;
        }

        if (IsVector(type))
        {
            decoder = (object _, ReadOnlySpan<int> path, ref BitBuffer buffer) =>
            {
                if (path.Length == 1) buffer.ReadUVarInt32(); // new size
                else read(ref buffer);
            };
        }
        else
        {
            // plain field, or one element of a fixed array (path[1] is the index)
            decoder = (object _, ReadOnlySpan<int> path, ref BitBuffer buffer) => read(ref buffer);
        }
        return true;
    }
}
