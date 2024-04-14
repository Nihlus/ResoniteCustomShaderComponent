//
//  SPDX-FileName: ManagedMaterialProperty.cs
//  SPDX-FileCopyrightText: Copyright (c) Jarl Gullberg
//  SPDX-License-Identifier: AGPL-3.0-or-later
//

using System.Reflection.Emit;

namespace ResoniteCustomShaderComponent.TypeGeneration.Properties;

/// <summary>
/// Represents information about the managed representation of a material property.
/// </summary>
public class ManagedMaterialProperty
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
    /// Gets the custom attributes that should be applied to the managed property.
    /// </summary>
    public IReadOnlyList<CustomAttributeBuilder> CustomAttributes { get; }

    /// <summary>
    /// Gets or sets the generated field associated with the managed property.
    /// </summary>
    public FieldBuilder? Field { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ManagedMaterialProperty"/> class.
    /// </summary>
    /// <param name="name">The user-facing name of the property.</param>
    /// <param name="type">The managed type of the property.</param>
    /// <param name="customAttributes">The custom attributes that should be applied to the managed property.</param>
    public ManagedMaterialProperty
    (
        string name,
        Type type,
        IReadOnlyList<CustomAttributeBuilder>? customAttributes = null
    )
    {
        Name = name;
        Type = type;
        CustomAttributes = customAttributes ?? Array.Empty<CustomAttributeBuilder>();
    }
}
