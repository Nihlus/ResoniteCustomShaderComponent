//
//  SPDX-FileName: SimpleMaterialProperty.cs
//  SPDX-FileCopyrightText: Copyright (c) Jarl Gullberg
//  SPDX-License-Identifier: AGPL-3.0-or-later
//

namespace ResoniteCustomShaderComponent.TypeGeneration;

/// <summary>
/// Represents a simple, 1:1 native-to-managed property. The managed property may have a different type than the native
/// property.
/// </summary>
public sealed class SimpleMaterialProperty : ManagedMaterialProperty
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SimpleMaterialProperty"/> class.
    /// </summary>
    /// <param name="name">The user-facing name of the property.</param>
    /// <param name="type">The managed type of the property.</param>
    /// <param name="nativeProperty">The native material property associated with this property.</param>
    public SimpleMaterialProperty(string name, Type type, NativeMaterialProperty nativeProperty)
        : base(name, type, nativeProperty)
    {
    }

    /// <summary>
    /// Creates a new <see cref="SimpleMaterialProperty"/> from the given <see cref="NativeMaterialProperty"/>.
    /// </summary>
    /// <param name="nativeProperty">The native property.</param>
    /// <returns>The managed property, or null if the property cannot be mapped.</returns>
    public static SimpleMaterialProperty? FromNative(NativeMaterialProperty nativeProperty)
    {
        var managedName = MaterialPropertyMapper.GetManagedName(nativeProperty);
        var managedType = MaterialPropertyMapper.GetManagedType(nativeProperty);

        return managedType is null
            ? null
            : new SimpleMaterialProperty(managedName, managedType, nativeProperty);
    }
}
