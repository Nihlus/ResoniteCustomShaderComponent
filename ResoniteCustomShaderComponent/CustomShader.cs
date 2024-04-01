//
//  SPDX-FileName: CustomShader.cs
//  SPDX-FileCopyrightText: Copyright (c) Jarl Gullberg
//  SPDX-License-Identifier: AGPL-3.0-or-later
//

using Elements.Core;
using FrooxEngine;
using ResoniteCustomShaderComponent.Shaders;
using ResoniteCustomShaderComponent.TypeGeneration;
using UnityFrooxEngineRunner;

#pragma warning disable SA1401

namespace ResoniteCustomShaderComponent;

/// <summary>
/// Represents a custom, dynamically loaded shader.
/// </summary>
[Category(["Assets/Materials"])]
public class CustomShader : Component
{
    /// <summary>
    /// Gets the Uri of the shader bundle used by the shader program.
    /// </summary>
    public readonly Sync<Uri?> ShaderURL = new();

    /// <summary>
    /// Gets the loaded shader.
    /// </summary>
    public readonly AssetRef<Shader?> Shader = new();

    /// <summary>
    /// Gets the dynamically generated shader properties.
    /// </summary>
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

        if (ShaderTypeGenerator.TryGetShaderType(assetRef.Target.Asset!.AssetURL, out var existingShaderType))
        {
            if (this.ShaderProperties.Target?.GetType() == existingShaderType)
            {
                return;
            }
        }

        _ = Task.Run(() => LoadUnityShaderAsync(assetRef!))
            .ContinueWith
            (
                loadShader =>
                {
                    if (!loadShader.Result || assetRef.Target?.Asset?.Connector is not ShaderConnector)
                    {
                        UniLog.Log("Shader was not a unity shader (or did not have a loaded shader connector)");

                        this.ShaderProperties.Target?.Destroy();
                        this.ShaderProperties.ForceWrite(null);
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
                                c.Persistent = true;
                                c.Enabled = true;
                            });

                            // get outta here
                            this.ShaderProperties.Target?.Destroy();
                            this.ShaderProperties.ForceWrite(shader);
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

    private async Task<UnityEngine.Shader?> LoadUnityShaderAsync(AssetRef<Shader> assetRef)
    {
        var shaderConnector = (ShaderConnector)assetRef.Target.Asset.Connector;
        if (shaderConnector.UnityShader is not null)
        {
            return shaderConnector.UnityShader;
        }

        var liveVariantCompletionSource = new TaskCompletionSource<Shader>();
        assetRef.Target.Asset.RequestVariant(0, (_, _, variant) =>
        {
            liveVariantCompletionSource.SetResult(variant);
        });

        return (await liveVariantCompletionSource.Task).GetUnity();
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
}
