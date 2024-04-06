//
//  SPDX-FileName: MaterialPropertyMapper.cs
//  SPDX-FileCopyrightText: Copyright (c) Jarl Gullberg
//  SPDX-License-Identifier: AGPL-3.0-or-later
//

using System.Collections.Concurrent;
using System.Reflection;
using Elements.Core;
using FrooxEngine;
using ResoniteCustomShaderComponent.Extensions;
using ResoniteCustomShaderComponent.TypeGeneration.Properties;
using UnityEngine.Rendering;
using BlendMode = FrooxEngine.BlendMode;

namespace ResoniteCustomShaderComponent.TypeGeneration;

/// <summary>
/// Contains static information about well-known material properties and helper methods for building them.
/// </summary>
public static class MaterialPropertyMapper
{
    /// <summary>
    /// Gets the implicit conversion method for <see cref="MaterialProperty"/> instances.
    /// </summary>
    public static MethodInfo MaterialPropertyConversion { get; } = typeof(MaterialProperty)
        .GetMethods()
        .Single(m => m.Name is "op_Implicit" && m.ReturnType == typeof(int));

    /// <summary>
    /// Holds a mapping between outward-facing material properties and the runtime types they should have.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, Type> _managedTypes = new Dictionary<string, Type>
    {
        { "AlphaClip", typeof(float) },
        { "ColorMask", typeof(ColorMask) },
        { "MaskMode", typeof(MaskTextureMode) },
        { "Overlay", typeof(bool) }, // TODO: non-functional (handle bool)
        { "Rect", typeof(Rect) }, // TODO: non-functional (handle UpdateRect)
        { "RectClip", typeof(bool) }, // TODO: non-functional (handle bool)
        { "RenderQueue", typeof(int) },
        { "StencilComparison", typeof(StencilComparison) },
        { "StencilID", typeof(byte) },
        { "StencilOperation", typeof(StencilOperation) },
        { "StencilReadMask", typeof(byte) },
        { "StencilWriteMask", typeof(byte) },
        { "TextureMode", typeof(UnlitTextureMode) },
        { "ZTest", typeof(ZTest) },
    };

    /// <summary>
    /// Gets a mapping between the names of real shader properties and the outward-facing material property they should
    /// have.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> _managedNames = new Dictionary<string, string>
    {
        { "_BumpMap", "NormalMap" },
        { "_BumpScale", "NormalScale" },
        { "_Color", "AlbedoColor" },
        { "_ColorMask", "ColorMask" },
        { "_Cutoff", "AlphaCutoff" },
        { "_DetailAlbedoMap", "DetailAlbedoTexture" },
        { "_DetailNormalMap", "DetailNormalTexture" },
        { "_DetailNormalMapScale", "DetailNormalScale" },
        { "_EmissionColor", "EmissiveColor" },
        { "_EmissionMap", "EmissiveMap" },
        { "_MainTex", "AlbedoTexture" },
        { "_MaskTex", "MaskTexture" },
        { "_OffsetFactor", "OffsetFactor" },
        { "_OffsetUnits", "OffsetUnits" },
        { "_OverlayTint", "OverlayTint" },
        { "_Parallax", "HeightScale" },
        { "_ParallaxMap", "HeightMap" },
        { "_Stencil", "StencilID" },
        { "_StencilComp", "StencilComparison" },
        { "_StencilOp", "StencilOperation" },
        { "_StencilReadMask", "StencilReadMask" },
        { "_StencilWriteMask", "StencilWriteMask" },
        { "_Tex", "Texture" },
        { "_Tint", "Tint" },
        { "_ZTest", "ZTest" }
    };

    /// <summary>
    /// Gets a mapping between the names of native scale-translation material properties and the names of the
    /// associated managed properties they take their complete value from.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, (string ScaleName, string TranslationName)> _scaleTranslationNames = new Dictionary<string, (string, string)>
    {
        { "_MainTex_ST", ("TextureScale", "TextureOffset") },
        { "_DetailAlbedoMap_ST", ("DetailTextureScale", "DetailTextureOffset") },
        { "_MaskTex_ST", ("MaskScale", "MaskOffset") },
        { "_RightEye_ST", ("RightEyeTextureScale", "RightEyeTextureOffset") }
    };

    /// <summary>
    /// Gets a mapping between blend mode shader properties and the name of the virtual property they are a part of.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> _blendModeNames = new Dictionary<string, string>
    {
        { "_SrcBlend", "BlendMode" },
        { "_DstBlend", "BlendMode" },
        { "_SrcBlendAdd", "BlendMode" },
        { "_DstBlendAdd", "BlendMode" },
        { "_ZWrite", "ZWrite" },
        { "_Cull", "Sidedness" },
    };

    /// <summary>
    /// Gets a mapping between material keywords and the outward-facing material properties they should create.
    /// </summary>
    public static IReadOnlyDictionary<string, string> WellKnownKeywords { get; } = new Dictionary<string, string>
    {
        { "RECTCLIP", "RectClip" },
        { "ALPHACLIP", "AlphaClip" },
        { "OVERLAY", "AlphaClip" },
        { "TEXTURE_NORMALMAP", "TextureMode" },
        { "TEXTURE_LERPCOLOR", "TextureMode" },
        { "_MASK_TEXTURE_MUL", "MaskMode" },
        { "_MASK_TEXTURE_CLIP", "MaskMode" },
    };

    private static readonly IReadOnlyDictionary<Type, MethodInfo> _materialPropertyUpdateMethods = new Dictionary<Type, MethodInfo>
    {
        { typeof(float), typeof(Material).GetMethod(nameof(Material.UpdateFloat), [typeof(int), typeof(Sync<float>)]) },
        { typeof(float2), typeof(Material).GetMethod(nameof(Material.UpdateFloat2), [typeof(int), typeof(Sync<float2>)]) },
        { typeof(float3), typeof(Material).GetMethod(nameof(Material.UpdateFloat3), [typeof(int), typeof(Sync<float3>)]) },
        { typeof(float4), typeof(Material).GetMethod(nameof(Material.UpdateFloat4), [typeof(int), typeof(Sync<float4>)]) },
        { typeof(byte), typeof(Material).GetMethod(nameof(Material.UpdateByte), [typeof(int), typeof(Sync<byte>)]) },
        { typeof(int), typeof(Material).GetMethod(nameof(Material.UpdateInt), [typeof(int), typeof(Sync<int>)]) },
        { typeof(colorX), typeof(Material).GetMethod(nameof(Material.UpdateColor), [typeof(int), typeof(Sync<colorX>)]) },
    };

    private static readonly MethodInfo _materialPropertyUpdateCubemapMethod = typeof(Material).GetMethod(nameof(Material.UpdateCubemap))!;
    private static readonly MethodInfo _materialPropertyUpdateTextureMethod = typeof(MaterialExtensions).GetMethod(nameof(MaterialExtensions.UpdateTexture))!;
    private static readonly MethodInfo _materialPropertyUpdateNormalMapMethod = typeof(MaterialExtensions).GetMethod(nameof(MaterialExtensions.UpdateNormalMap))!;

    private static readonly MethodInfo _genericMaterialPropertyUpdateEnumMethod = typeof(MaterialExtensions).GetMethod(nameof(MaterialExtensions.UpdateEnum))!;
    private static readonly ConcurrentDictionary<Type, MethodInfo> _materialPropertyUpdateEnumMethods = new();

    /// <summary>
    /// Gets the property groups in the given shader.
    /// </summary>
    /// <param name="shader">The shader.</param>
    /// <returns>The property groups.</returns>
    public static IReadOnlyList<MaterialPropertyGroup> GetPropertyGroups(UnityEngine.Shader shader)
    {
        var propertyGroups = new List<MaterialPropertyGroup>();

        var nativeProperties = NativeMaterialProperty.GetProperties(shader).ToDictionary(p => p.Name, p => p);
        if (nativeProperties.TryGetValue("_SrcBlend", out var srcBlend) && nativeProperties.TryGetValue("_DstBlend", out var dstBlend))
        {
            _ = nativeProperties.TryGetValue("_ZWrite", out var nativeZWrite);
            _ = nativeProperties.TryGetValue("_Cull", out var nativeCull);
            _ = nativeProperties.TryGetValue("_SrcBlendAdd", out var srcBlendAdd);
            _ = nativeProperties.TryGetValue("_SrcBlendAdd", out var dstBlendAdd);
            propertyGroups.Add
            (
                new BlendModePropertyGroup
                (
                    shader,
                    srcBlend,
                    dstBlend,
                    srcBlendAdd,
                    dstBlendAdd,
                    nativeZWrite,
                    nativeCull
                )
            );
        }

        foreach (var (name, property) in nativeProperties)
        {
            if (_blendModeNames.ContainsKey(name))
            {
                continue;
            }

            if (_scaleTranslationNames.TryGetValue(name, out var names))
            {
                propertyGroups.Add(new ScaleTranslationPropertyGroup(names.ScaleName, names.TranslationName, property));
                continue;
            }

            var simpleProperty = SimpleMaterialPropertyGroup.FromNative(property);
            if (simpleProperty is null)
            {
                // unsupported
                continue;
            }

            propertyGroups.Add(simpleProperty);
        }

        return propertyGroups;
    }

    /// <summary>
    /// Gets the update method used for the given managed/native property combination.
    /// </summary>
    /// <param name="managedProperty">The managed property.</param>
    /// <param name="nativeProperty">The native property.</param>
    /// <returns>The method to call.</returns>
    /// <exception cref="MissingMethodException">Thrown if no matching method can be found.</exception>
    public static MethodInfo GetMaterialPropertyUpdateMethod
    (
        ManagedMaterialProperty managedProperty,
        NativeMaterialProperty nativeProperty
    )
    {
        if (_materialPropertyUpdateMethods.TryGetValue(managedProperty.Type, out var updateMethod))
        {
            return updateMethod;
        }

        if (nativeProperty.IsTexture)
        {
            return nativeProperty.Flags.HasFlag(ShaderPropertyFlags.Normal)
                ? _materialPropertyUpdateNormalMapMethod
                : nativeProperty.TextureDimension is TextureDimension.Cube
                    ? _materialPropertyUpdateCubemapMethod
                    : _materialPropertyUpdateTextureMethod;
        }

        if (managedProperty.Type.IsEnum)
        {
            return _materialPropertyUpdateEnumMethods.GetOrAdd
            (
                managedProperty.Type,
                t => _genericMaterialPropertyUpdateEnumMethod.MakeGenericMethod(t)
            );
        }

        throw new MissingMethodException("There is no update method available for this property type.");
    }

    /// <summary>
    /// Gets the managed name of the given native material property.
    /// </summary>
    /// <param name="nativeProperty">The native property.</param>
    /// <returns>The managed name.</returns>
    public static string GetManagedName(NativeMaterialProperty nativeProperty)
    {
        if (_managedNames.TryGetValue(nativeProperty.Name, out var managedName))
        {
            return managedName;
        }

        // use the description if it looks somewhat sane
        if (nativeProperty.Description is not null && nativeProperty.Description.Count(char.IsWhiteSpace) <= 4)
        {
            return new(nativeProperty.Description.Where(c => !char.IsWhiteSpace(c)).ToArray());
        }

        // otherwise, fall back to the native property name
        return nativeProperty.Name;
    }

    /// <summary>
    /// Gets the managed type of the given native material property.
    /// </summary>
    /// <param name="nativeProperty">The native property.</param>
    /// <returns>The managed type, or <value>null</value> if the property cannot be mapped.</returns>
    public static Type? GetManagedType(NativeMaterialProperty nativeProperty) =>
        _managedTypes.TryGetValue(GetManagedName(nativeProperty), out var managedType)
            ? managedType
            : nativeProperty.Type switch
            {
                ShaderPropertyType.Color => typeof(colorX),
                ShaderPropertyType.Vector => typeof(float4),
                ShaderPropertyType.Float => typeof(float),
                ShaderPropertyType.Range => typeof(float),
                ShaderPropertyType.Texture => nativeProperty.TextureDimension switch
                {
                    TextureDimension.Tex2D => typeof(ITexture2D),
                    TextureDimension.Cube => typeof(Cubemap),
                    _ => null
                },
                _ => null
            };
}
