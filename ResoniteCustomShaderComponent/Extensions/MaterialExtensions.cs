//
//  SPDX-FileName: MaterialExtensions.cs
//  SPDX-FileCopyrightText: Copyright (c) Jarl Gullberg
//  SPDX-License-Identifier: AGPL-3.0-or-later
//

using FrooxEngine;

namespace ResoniteCustomShaderComponent.Extensions;

/// <summary>
/// Defines extension methods for the <see cref="Material"/> class.
/// </summary>
public static class MaterialExtensions
{
    /// <summary>
    /// Updates an enumeration property on the given material.
    /// </summary>
    /// <param name="material">The material.</param>
    /// <param name="property">The index of the property.</param>
    /// <param name="field">The sync field used.</param>
    /// <typeparam name="T">The enumeration type.</typeparam>
    public static void UpdateEnum<T>(this Material material, int property, Sync<T> field) where T : struct, Enum
    {
        if (!field.GetWasChangedAndClear())
        {
            return;
        }

        material.SetFloat(property, Convert.ToSingle(field.Value));
    }

    /// <summary>
    /// Updates a texture property on the given material, using the compile-time defaults of the base method.
    /// </summary>
    /// <param name="material">The material.</param>
    /// <param name="property">The index of the property.</param>
    /// <param name="field">The sync field used.</param>
    /// <param name="unloadedOverride">The default texture to use when no texture is set.</param>
    public static void UpdateTexture(this Material material, int property, AssetRef<ITexture2D?> field, ITexture2D? unloadedOverride = null)
    {
        material.UpdateTexture(property, field, unloadedOverride: unloadedOverride);
    }

    /// <summary>
    /// Updates a texture property on the given material, using the compile-time defaults of the base method.
    /// </summary>
    /// <param name="material">The material.</param>
    /// <param name="property">The index of the property.</param>
    /// <param name="field">The sync field used.</param>
    /// <param name="unloadedOverride">The default texture to use when no texture is set.</param>
    public static void UpdateNormalMap(this Material material, int property, AssetRef<ITexture2D?> field, ITexture2D? unloadedOverride = null)
    {
        if (!field.GetWasChangedAndClear())
        {
            return;
        }

        material.EnsureNormalMap(field.Target);
        var texture = MaterialProviderBase<Material>.GetTexture(field, unloadedOverride);
        material.SetTexture(property, texture);
    }
}
