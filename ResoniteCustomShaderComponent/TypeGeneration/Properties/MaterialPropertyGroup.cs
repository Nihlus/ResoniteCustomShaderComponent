//
//  SPDX-FileName: MaterialPropertyGroup.cs
//  SPDX-FileCopyrightText: Copyright (c) Jarl Gullberg
//  SPDX-License-Identifier: AGPL-3.0-or-later
//

namespace ResoniteCustomShaderComponent.TypeGeneration.Properties;

/// <summary>
/// Represents a group of managed and native properties that belong to each other.
/// </summary>
public abstract class MaterialPropertyGroup
{
    /// <summary>
    /// Gets the managed properties in this group.
    /// </summary>
    /// <returns>The properties.</returns>
    public abstract IEnumerable<ManagedMaterialProperty> GetManagedProperties();

    /// <summary>
    /// Gets the native properties in this group.
    /// </summary>
    /// <returns>The properties.</returns>
    public abstract IEnumerable<NativeMaterialProperty> GetNativeProperties();
}
