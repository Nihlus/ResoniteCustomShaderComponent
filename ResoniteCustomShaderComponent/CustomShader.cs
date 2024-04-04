//
//  SPDX-FileName: CustomShader.cs
//  SPDX-FileCopyrightText: Copyright (c) Jarl Gullberg
//  SPDX-License-Identifier: AGPL-3.0-or-later
//

using System.Collections.Concurrent;
using System.Reflection;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;
using ResoniteCustomShaderComponent.Shaders;
using ResoniteCustomShaderComponent.TypeGeneration;
using UnityFrooxEngineRunner;

#pragma warning disable SA1401

namespace ResoniteCustomShaderComponent;

/// <summary>
/// Represents a custom, dynamically loaded shader.
/// </summary>
[Category(["Assets/Materials"])]
public class CustomShader : AssetProvider<Material>, ICustomInspector
{
    /// <summary>
    /// Gets the Uri of the shader bundle used by the shader program.
    /// </summary>
    public readonly Sync<Uri?> ShaderURL = new();

    /// <summary>
    /// Gets the loaded shader.
    /// </summary>
    [HideInInspector]
    public readonly AssetRef<Shader?> Shader = new();

    /// <summary>
    /// Gets the dynamically generated shader properties.
    /// </summary>
    [HideInInspector]
    public readonly ReadOnlyRef<DynamicShader?> ShaderProperties = new();

    /// <inheritdoc />
    public CustomShader()
    {
        this.ShaderURL.OnValueChange += OnShaderURLValueChange;
        this.Shader.Changed += OnShaderChanged;
        this.Shader.ListenToAssetUpdates = true;
    }

    private void OnShaderChanged(IChangeable changeable)
    {
        var assetRef = (AssetRef<Shader?>)changeable;
        if (!assetRef.IsAssetAvailable)
        {
            return;
        }

        var shaderUrl = assetRef.Target.Asset!.AssetURL;
        if (ShaderTypeGenerator.TryGetShaderType(shaderUrl, out var existingShaderType))
        {
            if (this.ShaderProperties.Target?.GetType() == existingShaderType)
            {
                return;
            }
        }

        _ = Task.Run(() => ShaderTypeGenerator.LoadUnityShaderAsync(shaderUrl))
            .ContinueWith
            (
                loadShader =>
                {
                    if (!loadShader.Result || assetRef.Target?.Asset?.Connector is not ShaderConnector)
                    {
                        UniLog.Log("Shader was not a unity shader (or did not have a loaded shader connector)");

                        this.ShaderProperties.Target?.Destroy();
                        this.ShaderProperties.ForceWrite(null);

                        AssetRemoved();
                        return;
                    }

                    try
                    {
                        var shaderType = ShaderTypeGenerator.GetOrGenerateShaderType
                        (
                            assetRef.Target.Asset.AssetURL,
                            loadShader.Result!
                        );

                        if (this.ShaderProperties.Target?.GetType() == shaderType)
                        {
                            // same type, no need to modify
                            return;
                        }

                        this.World.RunSynchronously(() =>
                        {
                            UniLog.Log("Creating shader instance");
                            var shader = (DynamicShader)this.Slot.AttachComponent(shaderType, beforeAttach: c =>
                            {
                                ((DynamicShader)c).SetShader(assetRef.Target!);
                                ((DynamicShader)c).DriveControlFields(this.persistent, this.updateOrder, this.EnabledField);
                            });

                            // get outta here
                            this.ShaderProperties.Target?.Destroy();
                            this.ShaderProperties.ForceWrite(shader);

                            AssetUpdated();
                            shader.Changed += _ => AssetUpdated();
                        });
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e);
                        throw;
                    }
                }
            );
    }

    private void OnShaderURLValueChange(SyncField<Uri?> sync)
    {
        _ = Task.Run
            (
                async () =>
                {
                    if (sync.Value is null)
                    {
                        return;
                    }

                    // whitelist shader
                    var assetSignature = this.Cloud.Assets.DBSignature(sync.Value);

                    UniLog.Log($"Whitelisting shader with signature \"{assetSignature}\"");
                    await this.Engine.LocalDB.WriteVariableAsync(assetSignature, true);
                    UniLog.Log("Shader whitelisted");
                }
            )
            .ContinueWith(_ =>
                {
                    this.World.RunSynchronously(InitializeShader);
                }
            );
    }

    private void InitializeShader()
    {
        if (this.ShaderURL.Value is null)
        {
            this.Shader.Target?.Destroy();
            this.Shader.Target = null;
            return;
        }

        if (this.IsLocalElement)
        {
            this.Shader.Target = this.World.GetLocalRegisteredComponent<StaticShader>
            (
                this.ShaderURL.Value.OriginalString,
                provider => provider.URL.Value = this.ShaderURL.Value,
                true,
                false
            );

            return;
        }

        var componentOrCreate = this.World.GetSharedComponentOrCreate<StaticShader>
        (
            this.Cloud.Assets.DBSignature(this.ShaderURL.Value),
            provider => provider.URL.Value = this.ShaderURL.Value,
            replaceExisting: true,
            getRoot: () => this.World.AssetsSlot.FindChildOrAdd("Shaders")
        );

        componentOrCreate.Persistent = false;
        this.Shader.Target = componentOrCreate;
    }

    /// <inheritdoc />
    protected override void OnDestroy()
    {
        base.OnDestroy();
        this.ShaderProperties.Target?.Destroy();
    }

    /// <inheritdoc />
    protected override void FreeAsset()
    {
    }

    /// <inheritdoc />
    protected override void UpdateAsset()
    {
    }

    /// <inheritdoc />
    public override bool IsAssetAvailable => this.ShaderProperties.Target?.IsAssetAvailable ?? false;

    /// <inheritdoc />
    public override Material Asset => this.ShaderProperties.Target?.Asset!;

    /// <inheritdoc />
    public void BuildInspectorUI(UIBuilder ui)
    {
        WorkerInspector.BuildInspectorUI(this, ui);
        this.ShaderProperties.Target?.BuildNestedInspectorUI(ui);
    }
}
