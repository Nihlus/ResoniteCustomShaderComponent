//
//  SPDX-FileName: MaterialPropertyGroup.cs
//  SPDX-FileCopyrightText: Copyright (c) Jarl Gullberg
//  SPDX-License-Identifier: AGPL-3.0-or-later
//

using System.Reflection.Emit;

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

    /// <summary>
    /// Emits requisite code to initialize the managed members in the group with
    /// their default values.
    /// </summary>
    /// <param name="il">The IL generator to generate in.</param>
    public virtual void EmitInitializeSyncMemberDefaults(ILGenerator il)
    {
    }

    /// <summary>
    /// Emits requisite code to update the material's keywords using the group's properties.
    /// </summary>
    /// <param name="il">The IL generator to generate in.</param>
    public virtual void EmitUpdateKeywords(ILGenerator il)
    {
    }

    /// <summary>
    /// Emits requisite code to update the material using the group's properties.
    /// </summary>
    /// <param name="il">The IL generator to generate in.</param>
    public virtual void EmitUpdateMaterial(ILGenerator il)
    {
    }
}
