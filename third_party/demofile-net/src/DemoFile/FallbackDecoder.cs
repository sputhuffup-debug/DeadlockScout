using System.Diagnostics.CodeAnalysis;
using DemoFile.Sdk;

namespace DemoFile;

public static class FallbackDecoder
{
    public readonly record struct Unit;

    public static bool TryCreate(
        string fieldName,
        FieldType fieldType,
        FieldEncodingInfo encodingInfo,
        DecoderSet decoderSet,
        [NotNullWhen(true)] out SendNodeDecoder<Unit>? decoder)
    {
        decoder = null;

        // Fallback array
        if (fieldType.ArrayLength != 0)
        {
            // Special case strings
            if (fieldType.Name == "char")
            {
                var fieldDecoder = FieldDecode.CreateDecoder_string(encodingInfo);
                decoder = (Unit _, ReadOnlySpan<int> path, ref BitBuffer buffer) =>
                    fieldDecoder(ref buffer);
                return true;
            }

            var elementType = fieldType with {ArrayLength = 0};
            if (!TryCreate(fieldName, elementType, encodingInfo, decoderSet, out var innerDecoder))
                return false;

            // DeadlockScout patch: an element that is itself a vector reads its own index at path[0] (see IsContainer).
            var arrayElementIsContainer = IsContainer(elementType);
            decoder = (Unit _, ReadOnlySpan<int> path, ref BitBuffer buffer) =>
            {
                var elementIdx = path[1];
                innerDecoder(default, arrayElementIsContainer ? path[1..] : path[2..], ref buffer);
            };
            return true;
        }

        // Fallback pointer
        if (fieldType.IsPointer)
        {
            decoder = (Unit _, ReadOnlySpan<int> path, ref BitBuffer buffer) =>
            {
                if (path.Length == 1)
                {
                    var isSet = buffer.ReadOneBit();
                    if (isSet)
                    {
                        throw new NotImplementedException($"Cannot decode fallback pointer field ({fieldType} {fieldName}) set to a non-null value");
                    }
                }
                else
                {
                    throw new NotImplementedException($"Cannot decode inner field for fallback pointer ({fieldType} {fieldName})");
                }
            };
            return true;
        }

        if (fieldType.Name is "CNetworkUtlVectorBase" or "CUtlVectorEmbeddedNetworkVar" or "CUtlVector")
        {
            if (!TryCreate(fieldName, fieldType.GenericParameter!, encodingInfo, decoderSet, out var innerDecoder))
                return false;

            // DeadlockScout patch: a vector of vectors (new in build 6826) sends the inner vector's size at [field, i] and its
            // elements at [field, i, j]. The inner decoder reads its own index at path[0], so it gets path[1..]; a scalar
            // element ignores the path, so path[2..] stays for it.
            var vectorElementIsContainer = IsContainer(fieldType.GenericParameter!);
            decoder = (Unit _, ReadOnlySpan<int> path, ref BitBuffer buffer) =>
            {
                // New size of vector
                if (path.Length == 1)
                {
                    buffer.ReadUVarInt32();
                }
                else
                {
                    var elementIdx = path[1];
                    innerDecoder(default, vectorElementIsContainer ? path[1..] : path[2..], ref buffer);
                }
            };
            return true;
        }

        switch (fieldType.Name)
        {
            case "Vector" or "VectorWS":
            {
                var fieldDecoder = FieldDecode.CreateDecoder_Vector(encodingInfo);
                decoder = (Unit _, ReadOnlySpan<int> path, ref BitBuffer buffer) =>
                    fieldDecoder(ref buffer);
                return true;
            }
            case "Quaternion":
            {
                var fieldDecoder = FieldDecode.CreateDecoder_Quaternion(encodingInfo);
                decoder = (Unit _, ReadOnlySpan<int> path, ref BitBuffer buffer) =>
                    fieldDecoder(ref buffer);
                return true;
            }
            case "Vector2D":
            {
                var fieldDecoder = FieldDecode.CreateDecoder_Vector2D(encodingInfo);
                decoder = (Unit _, ReadOnlySpan<int> path, ref BitBuffer buffer) =>
                    fieldDecoder(ref buffer);
                return true;
            }
            case "Vector4D":
            {
                var fieldDecoder = FieldDecode.CreateDecoder_Vector4D(encodingInfo);
                decoder = (Unit _, ReadOnlySpan<int> path, ref BitBuffer buffer) =>
                    fieldDecoder(ref buffer);
                return true;
            }
            case "QAngle":
            {
                var fieldDecoder = FieldDecode.CreateDecoder_QAngle(encodingInfo);
                decoder = (Unit _, ReadOnlySpan<int> path, ref BitBuffer buffer) =>
                    fieldDecoder(ref buffer);
                return true;
            }
            case "float32" or "CNetworkedQuantizedFloat" or "float64":
            {
                var fieldDecoder = FieldDecode.CreateDecoder_float(encodingInfo);
                decoder = (Unit _, ReadOnlySpan<int> path, ref BitBuffer buffer) =>
                    fieldDecoder(ref buffer);
                return true;
            }
            case "bool":
            {
                decoder = (Unit _, ReadOnlySpan<int> path, ref BitBuffer buffer) =>
                    buffer.ReadOneBit();
                return true;
            }
            case "Color":
            {
                var fieldDecoder = FieldDecode.CreateDecoder_Color(encodingInfo);
                decoder = (Unit _, ReadOnlySpan<int> path, ref BitBuffer buffer) =>
                    fieldDecoder(ref buffer);
                return true;
            }
            case "uint8" or "int8" or "int16" or "uint16" or "int32" or "uint32" or "int64" or "uint64" or "CStrongHandle" or "CEntityHandle" or "CHandle" or "HSequence" or "CSPlayerBlockingUseAction_t" or "BloodType" or "CGameSceneNodeHandle" or "ShatterPanelMode" or "CSWeaponState_t" or "WorldGroupId_t" or "attributeprovidertypes_t" or "DecalMode_t":
            {
                // DeadlockScout patch: honour fixed8/fixed64 (see FieldDecode.CreateUnsignedDecoder). The value is discarded.
                var read = FieldDecode.CreateUnsignedDecoder(encodingInfo);
                decoder = (Unit _, ReadOnlySpan<int> path, ref BitBuffer buffer) =>
                    read(ref buffer);
                return true;
            }
            case "CUtlString" or "CUtlSymbolLarge":
            {
                var fieldDecoder = FieldDecode.CreateDecoder_string(encodingInfo);
                decoder = (Unit _, ReadOnlySpan<int> path, ref BitBuffer buffer) =>
                    fieldDecoder(ref buffer);
                return true;
            }
            case "CUtlBinaryBlock":
            {
                // DeadlockScout patch: varint length + bytes, like haste. Current builds use it inside structs this
                // schema does not know (AnimGraph2SerializedPoseRecipeSlot_t.m_topology).
                var fieldDecoder = FieldDecode.CreateDecoder_CUtlBinaryBlock(encodingInfo);
                decoder = (Unit _, ReadOnlySpan<int> path, ref BitBuffer buffer) =>
                    fieldDecoder(ref buffer);
                return true;
            }
            case "CUtlStringToken":
            {
                var fieldDecoder = FieldDecode.CreateDecoder_CUtlStringToken(encodingInfo);
                decoder = (Unit _, ReadOnlySpan<int> path, ref BitBuffer buffer) =>
                    fieldDecoder(ref buffer);
                return true;
            }
            case "CGlobalSymbol":
            {
                var fieldDecoder = FieldDecode.CreateDecoder_CGlobalSymbol(encodingInfo);
                decoder = (Unit _, ReadOnlySpan<int> path, ref BitBuffer buffer) =>
                    fieldDecoder(ref buffer);
                return true;
            }
            case "CPlayerSlot":
            {
                var fieldDecoder = FieldDecode.CreateDecoder_CPlayerSlot(encodingInfo);
                decoder = (Unit _, ReadOnlySpan<int> path, ref BitBuffer buffer) =>
                    fieldDecoder(ref buffer);
                return true;
            }
            case "GameTime_t":
            {
                decoder = (Unit _, ReadOnlySpan<int> path, ref BitBuffer buffer) =>
                    FieldDecode.DecodeFloatNoscale(ref buffer);
                return true;
            }
            case "GameTick_t":
            {
                decoder = (Unit _, ReadOnlySpan<int> path, ref BitBuffer buffer) =>
                    buffer.ReadUVarInt32();
                return true;
            }
            case "CEntityIndex":
            {
                var fieldDecoder = FieldDecode.CreateDecoder_CEntityIndex(encodingInfo);
                decoder = (Unit _, ReadOnlySpan<int> path, ref BitBuffer buffer) =>
                    fieldDecoder(ref buffer);
                return true;
            }
            case "CTransform":
            {
                var fieldDecoder = FieldDecode.CreateDecoder_CTransform(encodingInfo);
                decoder = (Unit _, ReadOnlySpan<int> path, ref BitBuffer buffer) =>
                    fieldDecoder(ref buffer);
                return true;
            }
            case "CRenderComponent":
            {

                decoder = (Unit _, ReadOnlySpan<int> path, ref BitBuffer buffer) =>
                {
                    if (path.Length == 1)
                    {
                        var isSet = buffer.ReadOneBit();
                        if (isSet)
                        {
                            //throw new NotImplementedException($"Cannot decode fallback pointer field ({fieldType} {fieldName}) set to a non-null value");
                        }
                    }
                    else
                    {
                        throw new NotImplementedException($"Cannot decode inner field for fallback pointer ({fieldType} {fieldName})");
                    }
                };
                return true;
            }
            default:
                return TryCreateFallbackClassDecoder(fieldType, decoderSet, out decoder)
                       || TryCreateHeuristicDecoder(fieldName, fieldType, encodingInfo, out decoder);
        }
    }

    private static bool TryCreateHeuristicDecoder(
        string fieldName,
        FieldType fieldType,
        FieldEncodingInfo encodingInfo,
        out SendNodeDecoder<Unit>? decoder)
    {
        // DeadlockScout patch: honour fixed8/fixed64 (see FieldDecode.CreateUnsignedDecoder). The value is discarded.
        var read = FieldDecode.CreateUnsignedDecoder(encodingInfo);
        // Heuristic: enum types. Example: EGrenadeThrowState, EFoo
        if (fieldType.Name.Length > 2
            && fieldType.Name[0] == 'E'
            && char.IsUpper(fieldType.Name[1]))
        {
            // See also FieldDecode.CreateDecoder_enum
            decoder = (Unit _, ReadOnlySpan<int> path, ref BitBuffer buffer) =>
                read(ref buffer);
            return true;
        }

        // Heuristic: enum fields. Example: m_ePlayerFireEvent, m_eDoorState
        if (fieldName.Length > 4
            && fieldName.StartsWith("m_e")
            && char.IsUpper(fieldName[3]))
        {
            decoder = (Unit _, ReadOnlySpan<int> path, ref BitBuffer buffer) =>
                read(ref buffer);
            return true;
        }

        decoder = null;
        return false;
    }

    private static bool TryCreateFallbackClassDecoder(
        FieldType fieldType,
        DecoderSet decoderSet,
        [NotNullWhen(true)] out SendNodeDecoder<Unit>? decoder)
    {
        if (!decoderSet.TryGetDecoderByName(fieldType.Name, out var classType, out var innerDecoder))
        {
            decoder = null;
            return false;
        }

        var dummyInstance = Activator.CreateInstance(classType)!;

        decoder = (Unit _, ReadOnlySpan<int> path, ref BitBuffer buffer) =>
            innerDecoder(dummyInstance, path, ref buffer);

        return true;
    }

    /// <summary>
    /// DeadlockScout patch: a type whose fallback decoder reads its own index at path[0] (a vector, or a fixed array other
    /// than a string), so a container holding it passes path[1..] rather than path[2..].
    /// </summary>
    private static bool IsContainer(FieldType type) =>
        (type.ArrayLength != 0 && type.Name != "char")
        || (type.GenericParameter is not null && type.Name is "CNetworkUtlVectorBase" or "CUtlVectorEmbeddedNetworkVar" or "CUtlVector");
}
