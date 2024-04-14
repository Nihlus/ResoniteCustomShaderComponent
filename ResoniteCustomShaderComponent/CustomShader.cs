//
//  SPDX-FileName: CustomShader.cs
//  SPDX-FileCopyrightText: Copyright (c) Jarl Gullberg
//  SPDX-License-Identifier: AGPL-3.0-or-later
//

using Elements.Core;
using FrooxEngine;
using ResoniteCustomShaderComponent.Shaders;
using ResoniteCustomShaderComponent.TypeGeneration;

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
    /// Gets the dynamically generated material.
    /// </summary>
    public readonly ReadOnlyRef<DynamicShader?> Material = new();

    /// <summary>
    /// Gets the load state of the dynamic shader.
    /// </summary>
    public readonly Sync<AssetLoadState> Status = new();

    private readonly SemaphoreSlim _shaderUpdateLock = new(1);

    /// <inheritdoc />
    protected override void OnStart()
    {
        base.OnStart();

        ShaderURL.OnValueChange += OnShaderURLValueChange;

        if (ShaderURL.Value is not null && Material.Target is null)
        {
            // force load if we start with an uninitialized material
            OnShaderURLValueChange(ShaderURL);
        }
    }

    private void OnShaderURLValueChange(SyncField<Uri?> shaderUrl)
    {
        _ = Task.Run(() => UpdateDynamicShaderAsync(shaderUrl.Value));
    }

    private async Task UpdateDynamicShaderAsync(Uri? shaderUrl)
    {
        await _shaderUpdateLock.WaitAsync();
        World.RunSynchronously(() => Status.Value = AssetLoadState.LoadStarted);

        TaskCompletionSource<int>? worldCompletionSource = null;

        try
        {
            if (shaderUrl is null)
            {
                worldCompletionSource = new();
                World.RunSynchronously
                (
                    () =>
                    {
                        Material.Target?.Destroy();
                        Material.ForceWrite(null);

                        Status.Value = AssetLoadState.Unloaded;
                        TriggerChangedEvent();
                        worldCompletionSource.SetResult(1);
                    }
                );

                return;
            }

            // whitelist shader
            var assetSignature = Cloud.Assets.DBSignature(shaderUrl);

            UniLog.Log($"Whitelisting shader with signature \"{assetSignature}\"");
            await Engine.LocalDB.WriteVariableAsync(assetSignature, true);
            UniLog.Log("Shader whitelisted");

            var shaderType = await DynamicShaderRepository.GetDynamicShaderTypeAsync(shaderUrl);
            if (shaderType is null)
            {
                worldCompletionSource = new();
                World.RunSynchronously
                (
                    () =>
                    {
                        Material.Target?.Destroy();
                        Material.ForceWrite(null);

                        Status.Value = AssetLoadState.Failed;
                        TriggerChangedEvent();

                        worldCompletionSource.SetResult(1);
                    }
                );

                return;
            }

            if (Material.Target?.GetType() == shaderType)
            {
                // no changes necessary
                World.RunSynchronously(() => Status.Value = AssetLoadState.FullyLoaded);

                return;
            }

            World.RunSynchronously(() => Status.Value = AssetLoadState.PartiallyLoaded);

            worldCompletionSource = new();
            World.RunSynchronously
            (
                () =>
                {
                    UniLog.Log("Creating shader instance");
                    try
                    {
                        var shaderProperties = (DynamicShader)Slot.AttachComponent
                        (
                            shaderType,
                            beforeAttach: c =>
                            {
                                ((DynamicShader)c).SetShaderURL(shaderUrl);
                                ((DynamicShader)c).DriveControlFields
                                (
                                    persistent,
                                    updateOrder,
                                    EnabledField
                                );
                            }
                        );

                        // get outta here
                        Material.Target?.Destroy();
                        Material.ForceWrite(shaderProperties);

                        World.RunSynchronously(() => Status.Value = AssetLoadState.FullyLoaded);

                        worldCompletionSource.SetResult(1);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e);
                        throw;
                    }
                }
            );
        }
        catch (Exception e)
        {
            UniLog.Log("Failed to load shader");
            UniLog.Log(e);

            World.RunSynchronously(() => Status.Value = AssetLoadState.Failed);
        }
        finally
        {
            try
            {
                if (worldCompletionSource is not null)
                {
                    await worldCompletionSource.Task;
                }
            }
            finally
            {
                _shaderUpdateLock.Release();
            }
        }
    }

    /// <inheritdoc />
    protected override void InitializeSyncMemberDefaults()
    {
        base.InitializeSyncMemberDefaults();
        Status.Value = AssetLoadState.Unloaded;
    }

    /// <inheritdoc />
    protected override void OnDestroy()
    {
        base.OnDestroy();
        Material.Target?.Destroy();
    }
}
