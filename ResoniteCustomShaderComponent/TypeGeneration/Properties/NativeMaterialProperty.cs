//
//  SPDX-FileName: NativeMaterialProperty.cs
//  SPDX-FileCopyrightText: Copyright (c) Jarl Gullberg
//  SPDX-License-Identifier: AGPL-3.0-or-later
//

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

namespace ResoniteCustomShaderComponent.TypeGeneration;

/// <summary>
/// Represents information about a native material property.
/// </summary>
public class NativeMaterialProperty
{
    private static readonly ConcurrentDictionary<Shader, IEnumerable<NativeMaterialProperty>> _cachedProperties = new();

    /// <summary>
    /// Gets the name of the material property.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the description of the material property.
    /// </summary>
    public string? Description { get; }

    /// <summary>
    /// Gets the type of the material property.
    /// </summary>
    public ShaderPropertyType Type { get; }

    /// <summary>
    /// Gets the flags of the material property.
    /// </summary>
    public ShaderPropertyFlags Flags { get; }

    /// <summary>
    /// Gets the dimension of the material property.
    /// </summary>
    public TextureDimension? TextureDimension { get; }

    /// <summary>
    /// Gets the range limits of the material property.
    /// </summary>
    public Vector2? RangeLimits { get; }

    /// <summary>
    /// Gets the default value of the property.
    /// </summary>
    public float? DefaultValue { get; }

    /// <summary>
    /// Gets the default vector value of the property.
    /// </summary>
    public Vector4? DefaultVector { get; }

    /// <summary>
    /// Gets the default name of the texture to use.
    /// </summary>
    public string? DefaultTextureName { get; }

    /// <summary>
    /// Gets or sets the field in which the native property's <see cref="FrooxEngine.MaterialProperty"/> is contained.
    /// </summary>
    public FieldInfo? PropertyNameField { get; set; }

    /// <summary>
    /// Gets a value indicating whether the property is a texture.
    /// </summary>
    [MemberNotNullWhen(true, nameof(TextureDimension), nameof(DefaultTextureName))]
    public bool IsTexture => this.Type is ShaderPropertyType.Texture;

    /// <summary>
    /// Gets a value indicating whether the property is a range.
    /// </summary>
    [MemberNotNullWhen(true, nameof(RangeLimits), nameof(DefaultValue))]
    public bool IsRange => this.Type is ShaderPropertyType.Range;

    /// <summary>
    /// Gets a value indicating whether the property is a vector or vector-like value.
    /// </summary>
    [MemberNotNullWhen(true, nameof(DefaultVector))]
    public bool IsVector => this.Type is ShaderPropertyType.Vector or ShaderPropertyType.Color;

    /// <summary>
    /// Gets a value indicating whether the property is a scalar or scalar-like value.
    /// </summary>
    [MemberNotNullWhen(true, nameof(DefaultValue))]
    public bool IsScalar => this.Type is ShaderPropertyType.Float or ShaderPropertyType.Range;

    /// <summary>
    /// Gets a value indicating whether the property has a default value.
    /// </summary>
    public bool HasDefaultValue => this.DefaultValue is not null
                                   || this.DefaultVector is not null
                                   || (this.DefaultTextureName is not null && this.HasSupportedDefaultTextureName);

    /// <summary>
    /// Gets a value indicating whether the property has a supported default texture name.
    /// </summary>
    public bool HasSupportedDefaultTextureName =>
        this.TextureDimension is UnityEngine.Rendering.TextureDimension.Cube
            ? this.DefaultTextureName?.ToLowerInvariant() is "darkchecker"
            : this.DefaultTextureName?.ToLowerInvariant() is "white" or "black" or "clear" or "darkchecker";

    /// <summary>
    /// Initializes a new instance of the <see cref="NativeMaterialProperty"/> class from the given shader
    /// at the given index.
    /// </summary>
    /// <param name="shader">The shader.</param>
    /// <param name="propertyIndex">The index of the property.</param>
    public NativeMaterialProperty(Shader shader, int propertyIndex)
    {
        this.Name = shader.GetPropertyName(propertyIndex);
        this.Description = shader.GetPropertyDescription(propertyIndex);
        this.Type = shader.GetPropertyType(propertyIndex);
        this.Flags = shader.GetPropertyFlags(propertyIndex);

        this.TextureDimension = this.IsTexture ? shader.GetPropertyTextureDimension(propertyIndex) : null;
        this.RangeLimits = this.IsRange ? shader.GetPropertyRangeLimits(propertyIndex) : null;
        this.DefaultVector = this.IsVector ? shader.GetPropertyDefaultVectorValue(propertyIndex) : null;
        this.DefaultValue = this.IsScalar ? shader.GetPropertyDefaultFloatValue(propertyIndex) : null;
        this.DefaultTextureName = this.IsTexture ? shader.GetPropertyTextureDefaultName(propertyIndex) : null;
    }

    /// <summary>
    /// Gets the properties in the given shader.
    /// </summary>
    /// <param name="shader">The shader.</param>
    /// <returns>The properties.</returns>
    public static IEnumerable<NativeMaterialProperty> GetProperties(Shader shader)
    {
        return _cachedProperties.GetOrAdd
        (
            shader,
            s =>
            {
                var properties = new List<NativeMaterialProperty>();
                for (var i = 0; i < s.GetPropertyCount(); ++i)
                {
                    properties.Add(new NativeMaterialProperty(s, i));
                }

                return properties;
            }
        );
    }
}
