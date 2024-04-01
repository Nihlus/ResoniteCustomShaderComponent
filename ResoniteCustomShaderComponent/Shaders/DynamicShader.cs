//
//  SPDX-FileName: DynamicShader.cs
//  SPDX-FileCopyrightText: Copyright (c) Jarl Gullberg
//  SPDX-License-Identifier: AGPL-3.0-or-later
//

using System.Reflection;
using Elements.Core;
using FrooxEngine;

namespace ResoniteCustomShaderComponent.Shaders;

/// <summary>
/// Represents a set of dynamically generated shader properties.
/// </summary>
public abstract class DynamicShader : MaterialProvider
{
    /// <inheritdoc />
    public override PropertyState PropertyInitializationState { get; protected set; }

    private IAssetProvider<Shader> _shader = null!;

    /// <inheritdoc />
    protected DynamicShader()
    {
    }

    /// <summary>
    /// Sets the shader wrapped by the dynamic shader.
    /// </summary>
    /// <param name="shader">The shader.</param>
    internal void SetShader(IAssetProvider<Shader> shader) => _shader = shader;

    /// <summary>
    /// Gets a list of <see cref="ISyncMember"/> values mapping to the shader's defined properties.
    /// </summary>
    /// <returns>The property members.</returns>
    public abstract IReadOnlyList<ISyncMember> GetMaterialPropertyMembers();

    /// <summary>
    /// Gets a mapping between the material properties and their names.
    /// </summary>
    /// <returns>The mapping.</returns>
    public abstract IReadOnlyDictionary<ISyncMember, MaterialProperty> GetMaterialPropertyNames();

    /// <inheritdoc />
    protected override void GetMaterialProperties(List<MaterialProperty> properties)
    {
        properties.AddRange(GetMaterialPropertyNames().Values);
    }

    /// <inheritdoc />
    protected override void UpdateKeywords(ShaderKeywords keywords)
    {
        //throw new NotImplementedException();
    }

    /// <inheritdoc />
    protected override void UpdateMaterial(Material material)
    {
        material.UpdateInstancing(true);

        var materialProperties = GetMaterialPropertyMembers();
        var materialPropertyNames = GetMaterialPropertyNames();

        foreach (var materialProperty in materialProperties)
        {
            var materialPropertyName = materialPropertyNames[materialProperty];

            switch (materialProperty)
            {
                case Sync<float> floatProperty:
                {
                    material.UpdateFloat(materialPropertyName, floatProperty);
                    break;
                }
                case Sync<colorX> colorProperty:
                {
                    material.UpdateColor(materialPropertyName, colorProperty);
                    break;
                }
                case Sync<float4> vectorProperty:
                {
                    material.UpdateFloat4(materialPropertyName, vectorProperty);
                    break;
                }
                case AssetRef<ITexture2D> texture2dProperty:
                {
                    var index = texture2dProperty.Worker.SyncMembers.TakeWhile(x => x != texture2dProperty).Count();
                    var fieldInfo = texture2dProperty.Worker.GetSyncMemberFieldInfo(index);
                    if (fieldInfo.GetCustomAttribute<NormalMapAttribute>() is not null)
                    {
                        material.UpdateNormalMap(materialPropertyName, texture2dProperty);
                    }
                    else
                    {
                        material.UpdateTexture(materialPropertyName, texture2dProperty);
                    }

                    break;
                }
                case AssetRef<Cubemap> cubemapProperty:
                {
                    material.UpdateCubemap(materialPropertyName, cubemapProperty);
                    break;
                }
            }
        }
    }

    /// <inheritdoc />
    protected override Shader GetShader() => _shader.Asset;
}
