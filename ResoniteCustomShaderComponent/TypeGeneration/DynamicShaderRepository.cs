//
//  SPDX-FileName: DynamicShaderRepository.cs
//  SPDX-FileCopyrightText: Copyright (c) Jarl Gullberg
//  SPDX-License-Identifier: AGPL-3.0-or-later
//

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Elements.Assets;
using Elements.Core;
using FrooxEngine;
using ResoniteCustomShaderComponent.Extensions;
using UnityEngine;
using Shader = UnityEngine.Shader;
using ShaderVariantDescriptor = FrooxEngine.ShaderVariantDescriptor;

namespace ResoniteCustomShaderComponent.TypeGeneration;

/// <summary>
/// Contains and manages cached data for dynamic shaders.
/// </summary>
public static class DynamicShaderRepository
{
    /// <summary>
    /// Holds a mapping between shader URIs and generated material types.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Type> _dynamicShaderTypes = new();

    /// <summary>
    /// Holds the locking object used to synchronized generation operations.
    /// </summary>
    private static readonly SemaphoreSlim _typeGenerationLock = new(1);

    private static readonly ConcurrentDictionary<Uri, Shader> _unityShaders = new();
    private static readonly SemaphoreSlim _shaderIntegrationLock = new(1);

    private static readonly string _shaderCacheDirectory = Path.Combine(Engine.Current.CachePath, "DynamicShaders");

    /// <summary>
    /// Loads cached or generates new shader types for worker nodes in the given data tree node.
    /// </summary>
    /// <param name="node">The data tree node.</param>
    /// <returns>A <see cref="Task"/> representing the result of the asynchronous operation.</returns>
    public static async Task EnsureDynamicShaderTypesAsync(DataTreeNode node)
    {
        foreach (var child in node.EnumerateTree())
        {
            switch (child)
            {
                case DataTreeDictionary dictionary:
                {
                    if (dictionary.ContainsKey("Type") && dictionary["Type"] is DataTreeValue typeNode)
                    {
                        var typename = typeNode.Extract<string>();
                        if (typename.LooksLikeSHA256Hash())
                        {
                            // probable shader type!
                            var existingType = Type.GetType(typename);
                            if (existingType is not null)
                            {
                                // already loaded or not a shader type
                                continue;
                            }

                            _ = await GetDynamicShaderTypeAsync(new Uri($"resdb:///{typename}.unityshader"));
                        }
                    }

                    break;
                }
                case DataTreeValue:
                {
                    // plain values aren't ever something we're concerned with
                    continue;
                }
            }
        }
    }

    /// <summary>
    /// Gets a previously-generated type for the given shader URL and shader combination, or generates one if one does
    /// not already exist.
    /// </summary>
    /// <remarks>This method is thread-safe.</remarks>
    /// <param name="shaderUrl">The Resonite cloud URI to the shader.</param>
    /// <returns>The generated type.</returns>
    public static async Task<Type?> GetDynamicShaderTypeAsync(Uri shaderUrl)
    {
        var shaderHash = Path.GetFileNameWithoutExtension(shaderUrl.ToString());
        if (_dynamicShaderTypes.TryGetValue(shaderHash, out var shaderType))
        {
            return shaderType;
        }

        await _typeGenerationLock.WaitAsync();
        try
        {
            if (_dynamicShaderTypes.TryGetValue(shaderHash, out shaderType))
            {
                return shaderType;
            }

            if (TryLoadCachedShaderType(shaderHash, out shaderType))
            {
                _dynamicShaderTypes[shaderHash] = shaderType;
                return shaderType;
            }

            var shader = await LoadUnityShaderAsync(shaderUrl);
            if (shader is null)
            {
                return null;
            }

            return _dynamicShaderTypes.GetOrAdd
            (
                shaderHash,
                hash => ShaderTypeGenerator.DefineDynamicShaderType(_shaderCacheDirectory, hash, shader)
            );
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            throw;
        }
        finally
        {
            _typeGenerationLock.Release();
        }
    }

    private static bool TryLoadCachedShaderType
    (
        string shaderHash,
        [NotNullWhen(true)] out Type? generatedType
    )
    {
        generatedType = null;

        var assemblyPath = Path.Combine(_shaderCacheDirectory, $"{shaderHash}.dll");
        if (!File.Exists(assemblyPath))
        {
            return false;
        }

        try
        {
            var assembly = Assembly.LoadFrom(assemblyPath);
            if (assembly.GetName().Version.Major != ShaderTypeGenerator.GeneratedShaderVersion.Major)
            {
                // can't use this, we have a different ABI
                File.Delete(assemblyPath);

                return false;
            }

            var type = assembly.GetType(shaderHash);
            if (type is null)
            {
                return false;
            }

            generatedType = type;
            return true;
        }
        catch (Exception e)
        {
            UniLog.Log("Failed to load cached shader assembly");
            UniLog.Log(e);

            File.Delete(assemblyPath);

            return false;
        }
    }

    /// <summary>
    /// Loads the unity shader associated with the given shader URI.
    /// </summary>
    /// <param name="shaderUrl">The shader URI.</param>
    /// <returns>The shader, or null if no shader could be loaded.</returns>
    private static async Task<Shader?> LoadUnityShaderAsync(Uri shaderUrl)
    {
        if (_unityShaders.TryGetValue(shaderUrl, out var shader))
        {
            return shader;
        }

        await default(ToBackground);

        await _shaderIntegrationLock.WaitAsync();
        try
        {
            if (_unityShaders.TryGetValue(shaderUrl, out shader))
            {
                return shader;
            }

            var platform = Engine.Current.Platform switch
            {
                Platform.Windows => ShaderTargetPlatform.WindowsDX11,
                Platform.Linux => ShaderTargetPlatform.LinuxOpenGL,
                Platform.Android => ShaderTargetPlatform.AndroidOpenGL,
                _ => ShaderTargetPlatform.LinuxOpenGL
            };

            var variantDescriptor = new ShaderVariantDescriptor(0, platform);
            var file = await Engine.Current.AssetManager.RequestVariant
                (shaderUrl, variantDescriptor.VariantIdentifier, variantDescriptor, false, true);
            if (file is null)
            {
                return null;
            }

            var bundleRequestSource = new TaskCompletionSource<AssetBundle?>();
            var bundleRequest = AssetBundle.LoadFromFileAsync(file);
            bundleRequest.completed += _ => bundleRequestSource.SetResult(bundleRequest.assetBundle);

            var bundle = await bundleRequestSource.Task;
            if (bundle is null)
            {
                return null;
            }

            var shaderRequestSource = new TaskCompletionSource<Shader?>();
            var shaderRequest = bundle.LoadAssetAsync<Shader>(bundle.GetAllAssetNames()[0]);
            shaderRequest.completed += _ => shaderRequestSource.SetResult((Shader?)shaderRequest.asset);

            shader = await shaderRequestSource.Task;
            if (shader is null)
            {
                // unload the bundle for the same reason as below, but destroy any assets as well
                bundle.Unload(true);
                return null;
            }

            _unityShaders[shaderUrl] = shader;

            // multiple load calls to the same bundle cause issues (null returns), so we unload here but preserve the
            // loaded shader asset
            bundle.Unload(false);
            return shader;
        }
        catch (Exception e)
        {
            UniLog.Log($"Failed to retrieve shader for URL {shaderUrl}: {e}");
            return null;
        }
        finally
        {
            _shaderIntegrationLock.Release();
        }
    }
}
