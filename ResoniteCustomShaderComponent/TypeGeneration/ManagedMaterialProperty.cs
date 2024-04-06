//
//  SPDX-FileName: ManagedMaterialProperty.cs
//  SPDX-FileCopyrightText: Copyright (c) Jarl Gullberg
//  SPDX-License-Identifier: AGPL-3.0-or-later
//

using UnityEngine.Rendering;

namespace ResoniteCustomShaderComponent.TypeGeneration;

/// <summary>
/// Represents information about the managed representation of a material property.
/// </summary>
public abstract class ManagedMaterialProperty
{
    /// <summary>
    /// Gets the user-facing name of the property.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the managed type of the property.
    /// </summary>
    public Type Type { get; }

    /// <summary>
    /// Gets the native material property associated with this property.
    /// </summary>
    public NativeMaterialProperty NativeProperty { get; }

    /// <summary>
    /// Gets a value indicating whether the property should be hidden in inspectors.
    /// </summary>
    public bool IsHidden => this.NativeProperty.Flags.HasFlag(ShaderPropertyFlags.HideInInspector);

    /// <summary>
    /// Initializes a new instance of the <see cref="ManagedMaterialProperty"/> class.
    /// </summary>
    /// <param name="name">The user-facing name of the property.</param>
    /// <param name="type">The managed type of the property.</param>
    /// <param name="nativeProperty">The native material property associated with this property.</param>
    protected ManagedMaterialProperty(string name, Type type, NativeMaterialProperty nativeProperty)
    {
        this.Name = name;
        this.Type = type;
        this.NativeProperty = nativeProperty;
    }
}
