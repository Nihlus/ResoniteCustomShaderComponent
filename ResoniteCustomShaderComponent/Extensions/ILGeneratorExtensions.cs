//
//  SPDX-FileName: ILGeneratorExtensions.cs
//  SPDX-FileCopyrightText: Copyright (c) Jarl Gullberg
//  SPDX-License-Identifier: AGPL-3.0-or-later
//

using System.Reflection;
using System.Reflection.Emit;
using Elements.Core;
using FrooxEngine;
using ResoniteCustomShaderComponent.TypeGeneration;
using ResoniteCustomShaderComponent.TypeGeneration.Properties;
using StrictEmit;
using UnityEngine;
using UnityEngine.Rendering;
using Rect = Elements.Core.Rect;

namespace ResoniteCustomShaderComponent.Extensions;

/// <summary>
/// Defines extension methods for the <see cref="ILGenerator"/> class.
/// </summary>
public static class ILGeneratorExtensions
{
    /// <summary>
    /// Emits requisite code to load the given native property's default value as the given target type.
    /// </summary>
    /// <param name="il">The IL generator.</param>
    /// <param name="targetType">The target type.</param>
    /// <param name="nativeProperty">The native property.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a default value can't be emitted for the given target type.
    /// </exception>
    public static void EmitInlineDefault
    (
        this ILGenerator il,
        Type targetType,
        NativeMaterialProperty nativeProperty
    )
    {
        if (targetType.IsScalar() && nativeProperty.IsScalar)
        {
            il.EmitDefaultScalar(targetType, nativeProperty.DefaultValue.Value);
        }
        else if (nativeProperty.IsVector)
        {
            il.EmitDefaultVectorLike(targetType, nativeProperty.DefaultVector.Value);
        }
        else if (targetType.IsEnum && nativeProperty.IsScalar)
        {
            il.EmitDefaultEnum(targetType, nativeProperty.DefaultValue.Value);
        }
        else
        {
            throw new InvalidOperationException();
        }
    }

    /// <summary>
    /// Emits requisite code to load the given float value as the given target enum type.
    /// </summary>
    /// <param name="il">The IL generator.</param>
    /// <param name="targetType">The target type.</param>
    /// <param name="defaultValue">The default value expressed as a float.</param>
    public static void EmitDefaultEnum(this ILGenerator il, Type targetType, float defaultValue)
    {
        var backingType = targetType.GetEnumUnderlyingType();
        if (backingType == typeof(long) || backingType == typeof(ulong))
        {
            il.EmitConstantLong((long)defaultValue);
        }
        else
        {
            il.EmitConstantInt((int)defaultValue);
        }
    }

    /// <summary>
    /// Emits requisite code to load the given vector as the given target vector-like type.
    /// </summary>
    /// <param name="il">The IL generator.</param>
    /// <param name="targetType">The target type.</param>
    /// <param name="defaultValue">The default value as a plain vector.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown if the target type isn't a supported vector-like type.
    /// </exception>
    public static void EmitDefaultVectorLike(this ILGenerator il, Type targetType, Vector4 defaultValue)
    {
        var elementCount = targetType switch
        {
            _ when targetType == typeof(float2) => 2,
            _ when targetType == typeof(float3) => 3,
            _ when targetType == typeof(float4) => 4,
            _ when targetType == typeof(colorX) => 4,
            _ when targetType == typeof(Rect) => 4,
            _ => throw new ArgumentOutOfRangeException(nameof(targetType))
        };

        var elementConstructor = targetType switch
        {
            _ when targetType == typeof(float2) => typeof(float2).GetConstructor([typeof(float), typeof(float)])!,
            _ when targetType == typeof(float3) => typeof(float3).GetConstructor([typeof(float), typeof(float), typeof(float)])!,
            _ when targetType == typeof(float4) => typeof(float4).GetConstructor([typeof(float), typeof(float), typeof(float), typeof(float)])!,
            _ when targetType == typeof(colorX) => typeof(colorX).GetConstructor([typeof(float), typeof(float), typeof(float), typeof(float), typeof(ColorProfile)])!,
            _ when targetType == typeof(Rect) => typeof(Rect).GetConstructor([typeof(float), typeof(float), typeof(float), typeof(float)])!,
            _ => throw new ArgumentOutOfRangeException(nameof(targetType))
        };

        for (var i = 0; i < elementCount; i++)
        {
            il.EmitConstantFloat(defaultValue[i]);
        }

        if (targetType == typeof(colorX))
        {
            il.EmitConstantInt((int)ColorProfile.sRGB);
        }

        il.EmitNewObject(elementConstructor);
    }

    /// <summary>
    /// Emits requisite code to load a reference to a built-in default texture. If there's no supported built-in
    /// texture, a null value is pushed onto the stack.
    /// </summary>
    /// <param name="il">The IL generator.</param>
    /// <param name="nativeProperty">The native property.</param>
    public static void EmitDefaultTexture(this ILGenerator il, NativeMaterialProperty nativeProperty)
    {
        if (string.IsNullOrWhiteSpace(nativeProperty.DefaultTextureName))
        {
            il.EmitLoadNull();
            return;
        }

        il.EmitGetProperty<Engine>(nameof(Engine.Current), BindingFlags.Static | BindingFlags.Public);
        il.EmitGetProperty<Engine>(nameof(Engine.AssetManager));
        switch (nativeProperty.DefaultTextureName!.ToLowerInvariant())
        {
            case "white":
            {
                il.EmitGetProperty<AssetManager>(nameof(AssetManager.WhiteTexture));
                break;
            }
            case "black":
            {
                il.EmitGetProperty<AssetManager>(nameof(AssetManager.BlackTexture));
                break;
            }
            case "clear":
            {
                il.EmitGetProperty<AssetManager>(nameof(AssetManager.ClearTexture));
                break;
            }
            case "darkchecker":
            {
                il.EmitGetProperty<AssetManager>(nameof(AssetManager.DarkCheckerTexture));
                break;
            }
            default:
            {
                il.EmitLoadNull();
                break;
            }
        }
    }

    /// <summary>
    /// Emits requisite code to load a reference to a built-in default cubemap. If there's no supported built-in
    /// cubemap, a null value is pushed onto the stack.
    /// </summary>
    /// <param name="il">The IL generator.</param>
    /// <param name="nativeProperty">The native property.</param>
    public static void EmitDefaultCubemap(this ILGenerator il, NativeMaterialProperty nativeProperty)
    {
        if (string.IsNullOrWhiteSpace(nativeProperty.DefaultTextureName))
        {
            il.EmitLoadNull();
            return;
        }

        il.EmitGetProperty<Engine>(nameof(Engine.Current), BindingFlags.Static | BindingFlags.Public);
        il.EmitGetProperty<Engine>(nameof(Engine.AssetManager));
        switch (nativeProperty.DefaultTextureName!.ToLowerInvariant())
        {
            case "darkchecker":
            {
                il.EmitGetProperty<AssetManager>(nameof(AssetManager.DarkCheckerCubemap));
                break;
            }
            default:
            {
                il.EmitLoadNull();
                break;
            }
        }
    }

    /// <summary>
    /// Emits requisite code to load a default scalar as the given target type onto the stack.
    /// </summary>
    /// <param name="il">The IL generator.</param>
    /// <param name="targetType">The target type.</param>
    /// <param name="defaultValue">The default value represented as a float.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a default value can't be emitted for the given target type.
    /// </exception>
    public static void EmitDefaultScalar(this ILGenerator il, Type targetType, float defaultValue)
    {
        switch (targetType)
        {
            case var _ when targetType == typeof(sbyte):
            {
                il.EmitConstantInt((int)defaultValue);
                il.EmitConvertToByte();
                break;
            }
            case var _ when targetType == typeof(byte):
            {
                il.EmitConstantInt((int)defaultValue);
                il.EmitConvertToUByte();
                break;
            }
            case var _ when targetType == typeof(ushort):
            {
                il.EmitConstantInt((int)defaultValue);
                il.EmitConvertToUShort();
                break;
            }
            case var _ when targetType == typeof(short):
            {
                il.EmitConstantInt((int)defaultValue);
                il.EmitConvertToShort();
                break;
            }
            case var _ when targetType == typeof(uint):
            {
                il.EmitConstantInt((int)defaultValue);
                il.EmitConvertToUInt();
                break;
            }
            case var _ when targetType == typeof(int):
            {
                il.EmitConstantInt((int)defaultValue);
                break;
            }
            case var _ when targetType == typeof(ulong):
            {
                il.EmitConstantLong((long)defaultValue);
                il.EmitConvertToULong();
                break;
            }
            case var _ when targetType == typeof(long):
            {
                il.EmitConstantLong((long)defaultValue);
                break;
            }
            case var _ when targetType == typeof(float):
            {
                il.EmitConstantFloat(defaultValue);
                break;
            }
            case var _ when targetType == typeof(double):
            {
                il.EmitConstantDouble(defaultValue);
                break;
            }
            case var _ when targetType == typeof(decimal):
            {
                il.EmitConstantFloat(defaultValue);
                il.EmitCallDirect<decimal>("op_Implicit", typeof(float));
                break;
            }
            default:
            {
                throw new ArgumentOutOfRangeException(nameof(targetType));
            }
        }
    }

    /// <summary>
    /// Emits a plain call to material.UpdateXXX, where XXX is either UpdateTexture, UpdateNormalMap, or UpdateCubemap.
    /// </summary>
    /// <param name="il">The IL generator.</param>
    /// <param name="managedProperty">The managed property.</param>
    /// <param name="nativeProperty">The native property.</param>
    public static void EmitTextureUpdateCall
    (
        this ILGenerator il,
        ManagedMaterialProperty managedProperty,
        NativeMaterialProperty nativeProperty
    )
    {
        if (managedProperty.Field is null || nativeProperty.PropertyNameField is null)
        {
            throw new InvalidOperationException();
        }

        // stack:
        //  <empty>
        il.EmitLoadArgument(1);

        // stack:
        //  Material
        il.EmitLoadStaticField(nativeProperty.PropertyNameField);

        // stack:
        //  Material
        //  MaterialProperty
        il.EmitCallDirect(MaterialPropertyMapper.MaterialPropertyConversion);

        // stack:
        //  Material
        //  int
        il.EmitLoadArgument(0);

        // stack:
        //  Material
        //  int
        //  this
        il.EmitLoadField(managedProperty.Field);

        // stack:
        //  Material
        //  int
        //  ISyncMember
        if (nativeProperty.TextureDimension is TextureDimension.Cube)
        {
            il.EmitDefaultCubemap(nativeProperty);
        }
        else
        {
            il.EmitDefaultTexture(nativeProperty);
        }

        // stack:
        //  Material
        //  int
        //  ISyncMember
        //  ITexture2D? | Cubemap?
        il.EmitCallVirtual
        (
            MaterialPropertyMapper.GetMaterialPropertyUpdateMethod(managedProperty, nativeProperty)
        );
    }

    /// <summary>
    /// Emits a plain call to material.UpdateXXX, where XXX is the update method corresponding to the managed property's
    /// type..
    /// </summary>
    /// <param name="il">The IL generator.</param>
    /// <param name="managedProperty">The managed property.</param>
    /// <param name="nativeProperty">The native property.</param>
    public static void EmitSimpleUpdateCall
    (
        this ILGenerator il,
        ManagedMaterialProperty managedProperty,
        NativeMaterialProperty nativeProperty
    )
    {
        if (managedProperty.Field is null || nativeProperty.PropertyNameField is null)
        {
            throw new InvalidOperationException();
        }

        // stack:
        //  <empty>
        il.EmitLoadArgument(1);

        // stack:
        //  Material
        il.EmitLoadStaticField(nativeProperty.PropertyNameField);

        // stack:
        //  Material
        //  MaterialProperty
        il.EmitCallDirect(MaterialPropertyMapper.MaterialPropertyConversion);

        // stack:
        //  Material
        //  int
        il.EmitLoadArgument(0);

        // stack:
        //  Material
        //  int
        //  this
        il.EmitLoadField(managedProperty.Field);

        // stack:
        //  Material
        //  int
        //  ISyncMember
        il.EmitCallVirtual
        (
            MaterialPropertyMapper.GetMaterialPropertyUpdateMethod(managedProperty, nativeProperty)
        );
    }
}
