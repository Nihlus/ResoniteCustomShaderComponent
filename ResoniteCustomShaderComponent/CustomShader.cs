﻿//
//  SPDX-FileName: CustomShader.cs
//  SPDX-FileCopyrightText: Copyright (c) Jarl Gullberg
//  SPDX-License-Identifier: AGPL-3.0-or-later
//

using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;
using ResoniteCustomShaderComponent.Shaders;
using ResoniteCustomShaderComponent.TypeGeneration;

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
    /// Gets the dynamically generated shader properties.
    /// </summary>
    public readonly ReadOnlyRef<DynamicShader?> ShaderProperties = new();

    private readonly SemaphoreSlim _shaderUpdateLock = new(1);

    /// <inheritdoc />
    protected override void OnStart()
    {
        base.OnStart();

        this.ShaderURL.OnValueChange += OnShaderURLValueChange;
    }

    private void OnShaderURLValueChange(SyncField<Uri?> shaderUrl)
    {
        _ = Task.Run(() => UpdateDynamicShaderAsync(shaderUrl.Value));
    }

    private async Task UpdateDynamicShaderAsync(Uri? shaderUrl)
    {
        await _shaderUpdateLock.WaitAsync();

        TaskCompletionSource<int>? worldCompletionSource = null;

        try
        {
            if (shaderUrl is null)
            {
                worldCompletionSource = new();
                this.World.RunSynchronously
                (
                    () =>
                    {
                        this.ShaderProperties.Target?.Destroy();
                        this.ShaderProperties.ForceWrite(null);

                        worldCompletionSource.SetResult(1);
                        AssetRemoved();
                    }
                );

                return;
            }

            // whitelist shader
            var assetSignature = this.Cloud.Assets.DBSignature(shaderUrl);

            UniLog.Log($"Whitelisting shader with signature \"{assetSignature}\"");
            await this.Engine.LocalDB.WriteVariableAsync(assetSignature, true);
            UniLog.Log("Shader whitelisted");

            var shaderType = await DynamicShaderRepository.GetDynamicShaderTypeAsync(shaderUrl);
            if (shaderType is null)
            {
                worldCompletionSource = new();
                this.World.RunSynchronously
                (
                    () =>
                    {
                        this.ShaderProperties.Target?.Destroy();
                        this.ShaderProperties.ForceWrite(null);

                        worldCompletionSource.SetResult(1);
                        AssetRemoved();
                    }
                );

                return;
            }

            if (this.ShaderProperties.Target?.GetType() == shaderType)
            {
                // no changes necessary
                return;
            }

            worldCompletionSource = new();
            this.World.RunSynchronously
            (
                () =>
                {
                    UniLog.Log("Creating shader instance");
                    var shaderProperties = (DynamicShader)this.Slot.AttachComponent
                    (
                        shaderType,
                        beforeAttach: c =>
                        {
                            ((DynamicShader)c).SetShaderURL(shaderUrl);
                            ((DynamicShader)c).DriveControlFields
                            (
                                this.persistent,
                                this.updateOrder,
                                this.EnabledField
                            );
                        }
                    );

                    // get outta here
                    this.ShaderProperties.Target?.Destroy();
                    this.ShaderProperties.ForceWrite(shaderProperties);

                    AssetUpdated();
                    worldCompletionSource.SetResult(1);
                }
            );
        }
        finally
        {
            if (worldCompletionSource is not null)
            {
                await worldCompletionSource.Task;
            }

            _shaderUpdateLock.Release();
        }
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
