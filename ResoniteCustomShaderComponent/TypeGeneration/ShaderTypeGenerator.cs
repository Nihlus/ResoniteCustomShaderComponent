//
//  SPDX-FileName: ShaderTypeGenerator.cs
//  SPDX-FileCopyrightText: Copyright (c) Jarl Gullberg
//  SPDX-License-Identifier: AGPL-3.0-or-later
//

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Emit;
using Elements.Assets;
using Elements.Core;
using FrooxEngine;
using ResoniteCustomShaderComponent.Extensions;
using ResoniteCustomShaderComponent.Shaders;
using StrictEmit;
using UnityEngine;
using UnityEngine.Rendering;
using UnityFrooxEngineRunner;
using Cubemap = FrooxEngine.Cubemap;
using Material = FrooxEngine.Material;
using RangeAttribute = FrooxEngine.RangeAttribute;
using Shader = UnityEngine.Shader;
using ShaderVariantDescriptor = FrooxEngine.ShaderVariantDescriptor;

namespace ResoniteCustomShaderComponent.TypeGeneration;

/// <summary>
/// Generates FrooxEngine material types based on unity shaders.
/// </summary>
public static class ShaderTypeGenerator
{
    private const string _dynamicAssemblyName = "__customShaders";
    private const string _dynamicModuleName = "__customShaderModule";

    private static readonly Version _generatedShaderVersion = new(1, 0, 0);

    private static readonly ConcurrentDictionary<Uri, Shader> _unityShaders = new();
    private static readonly SemaphoreSlim _shaderIntegrationLock = new(1);

    /// <summary>
    /// Holds a mapping between shader URIs and generated material types.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Type> _dynamicShaderTypes = new();

    /// <summary>
    /// Holds the locking object used to synchronized generation operations.
    /// </summary>
    private static readonly SemaphoreSlim _typeGenerationLock = new(1);

    private static readonly IReadOnlyDictionary<Type, MethodInfo> _materialPropertyUpdateMethods = new Dictionary<Type, MethodInfo>
    {
        { typeof(float), typeof(Material).GetMethod(nameof(Material.UpdateFloat), [typeof(int), typeof(Sync<float>)]) },
        { typeof(float2), typeof(Material).GetMethod(nameof(Material.UpdateFloat2), [typeof(int), typeof(Sync<float2>)]) },
        { typeof(float3), typeof(Material).GetMethod(nameof(Material.UpdateFloat3), [typeof(int), typeof(Sync<float3>)]) },
        { typeof(float4), typeof(Material).GetMethod(nameof(Material.UpdateFloat4), [typeof(int), typeof(Sync<float4>)]) },
        { typeof(byte), typeof(Material).GetMethod(nameof(Material.UpdateByte), [typeof(int), typeof(Sync<byte>)]) },
        { typeof(int), typeof(Material).GetMethod(nameof(Material.UpdateInt), [typeof(int), typeof(Sync<int>)]) },
        { typeof(colorX), typeof(Material).GetMethod(nameof(Material.UpdateColor), [typeof(int), typeof(Sync<colorX>)]) },
        { typeof(Cubemap), typeof(Material).GetMethod(nameof(Material.UpdateCubemap), [typeof(int), typeof(AssetRef<Cubemap>)]) }
    };

    private static readonly MethodInfo _materialPropertyUpdateSTMethod = typeof(Material).GetMethod(nameof(Material.UpdateST))!;
    private static readonly MethodInfo _materialPropertyUpdateTextureMethod = typeof(Material).GetMethod(nameof(Material.UpdateTexture), [typeof(int), typeof(AssetRef<ITexture2D>), typeof(ColorProfile), typeof(ColorProfileRequirement)])!;
    private static readonly MethodInfo _materialPropertyUpdateNormalMapMethod = typeof(Material).GetMethod(nameof(Material.UpdateNormalMap))!;

    /// <summary>
    /// Loads cached or generates new shader types for worker nodes in the given data tree node.
    /// </summary>
    /// <param name="node">The data tree node.</param>
    /// <returns>A <see cref="Task"/> representing the result of the asynchronous operation.</returns>
    public static async Task LoadOrGenerateShaderTypesAsync(DataTreeNode node)
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

                            _ = await GetOrGenerateShaderTypeAsync(new Uri($"resdb:///{typename}.unityshader"));
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
    public static async Task<Type?> GetOrGenerateShaderTypeAsync(Uri shaderUrl)
    {
        var shaderHash = Path.GetFileNameWithoutExtension(shaderUrl.ToString());
        if (_dynamicShaderTypes.TryGetValue(shaderHash, out var shaderType))
        {
            return shaderType;
        }

        var shader = await LoadUnityShaderAsync(shaderUrl);
        if (shader is null)
        {
            return null;
        }

        await _typeGenerationLock.WaitAsync();
        try
        {
            if (_dynamicShaderTypes.TryGetValue(shaderHash, out shaderType))
            {
                return shaderType;
            }

            return _dynamicShaderTypes.GetOrAdd
            (
                shaderHash,
                hash => TryLoadCachedShaderType(hash, out var cachedShader)
                    ? cachedShader
                    : DefineDynamicShaderType(shaderHash, shader)
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

        var assemblyPath = GetCachedShaderPath(shaderHash);
        if (!File.Exists(assemblyPath))
        {
            return false;
        }

        try
        {
            var assembly = Assembly.LoadFrom(assemblyPath);
            if (assembly.GetName().Version.Major != _generatedShaderVersion.Major)
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
    /// Defines a dynamic shader type based on the given Unity shader.
    /// </summary>
    /// <param name="shaderHash">The Resonite cloud URI to the shader.</param>
    /// <param name="shader">The Unity shader that corresponds to the URI.</param>
    /// <returns>The shader type.</returns>
    private static Type DefineDynamicShaderType(string shaderHash, Shader shader)
    {
        UniLog.Log($"Creating new dynamic shader type for {shaderHash}");
        var dynamicAssemblyName = new AssemblyName(_dynamicAssemblyName)
        {
            Version = _generatedShaderVersion
        };

        var cachedShaderPath = GetCachedShaderPath(shaderHash);
        var cachedShaderDirectory = Path.GetDirectoryName(cachedShaderPath);
        var cachedShaderFilename = Path.GetFileName(cachedShaderPath);

        var dynamicAssembly = AppDomain.CurrentDomain.DefineDynamicAssembly
        (
            dynamicAssemblyName,
            AssemblyBuilderAccess.RunAndSave,
            cachedShaderDirectory
        );

        var dynamicModule = dynamicAssembly.DefineDynamicModule
        (
            _dynamicModuleName,
            cachedShaderFilename,
            false
        );

        var typeBuilder = dynamicModule.DefineType
        (
            shaderHash,
            TypeAttributes.Class | TypeAttributes.Sealed,
            typeof(DynamicShader)
        );

        var materialPropertyFields = typeBuilder.DefineDynamicMaterialPropertyFields(shader);
        var propertyMembersField = typeBuilder.EmitGetMaterialPropertyMembers();
        var propertyMemberNamesField = typeBuilder.EmitGetMaterialPropertyNames();

        typeBuilder.EmitConstructor(materialPropertyFields, propertyMembersField, propertyMemberNamesField);

        var type = typeBuilder.CreateType();

        // Cache the generated shader
        Directory.CreateDirectory(cachedShaderDirectory!);
        dynamicAssembly.Save(cachedShaderFilename);

        return type;
    }

    private static string GetCachedShaderPath(string shaderHash) => Path.Combine
    (
        Engine.Current.CachePath,
        "DynamicShaders",
        $"{shaderHash}.dll"
    );

    /// <summary>
    /// Defines <see cref="Sync{T}"/> fields for each material property in the given shader.
    /// </summary>
    /// <param name="typeBuilder">The type builder to define the fields in.</param>
    /// <param name="shader">The Unity shader to define fields for.</param>
    /// <returns>The defined fields.</returns>
    private static List<FieldBuilder> DefineDynamicMaterialPropertyFields(this TypeBuilder typeBuilder, Shader shader)
    {
        var nativeProperties = NativeMaterialProperty.GetProperties(shader);

        var materialPropertyFields = new List<FieldBuilder>();
        foreach (var nativeProperty in nativeProperties)
        {
            if (nativeProperty.Flags.HasFlag(ShaderPropertyFlags.HideInInspector))
            {
                UniLog.Log($"Shader property \"{nativeProperty.Name}\" is marked as HideInInspector - skipping");
                continue;
            }

            var propertyRuntimeType = nativeProperty.Type switch
            {
                ShaderPropertyType.Color => typeof(colorX),
                ShaderPropertyType.Vector => typeof(float4),
                ShaderPropertyType.Float => typeof(float),
                ShaderPropertyType.Range => typeof(float),
                ShaderPropertyType.Texture => nativeProperty.TextureDimension switch
                {
                    TextureDimension.Tex2D => typeof(ITexture2D),
                    TextureDimension.Cube => typeof(Cubemap),
                    _ => null
                },
                _ => null
            };

            if (propertyRuntimeType is null)
            {
                // not a type we support
                UniLog.Log($"Shader property \"{nativeProperty.Name}\" has an unsupported type \"{nativeProperty.Type}\" - skipping");
                continue;
            }

            UniLog.Log
            (
                $"Adding shader property \"{nativeProperty.Name}\" with shader type \"{nativeProperty.Type}\" and runtime type "
                + $"\"{propertyRuntimeType}\""
            );

            var propertyRefType = propertyRuntimeType.IsValueType ? typeof(Sync<>) : typeof(AssetRef<>);

            var fieldBuilder = typeBuilder.DefineField
            (
                nativeProperty.Name,
                propertyRefType.MakeGenericType(propertyRuntimeType),
                FieldAttributes.Public | FieldAttributes.InitOnly
            );

            if (nativeProperty.IsRange)
            {
                var rangeLimits = nativeProperty.RangeLimits.Value;
                var rangeConstructor = typeof(RangeAttribute).GetConstructors()[0];
                var attributeBuilder = new CustomAttributeBuilder
                (
                    rangeConstructor,
                    [
                        rangeLimits.x, rangeLimits.y, rangeConstructor.GetParameters()[2].DefaultValue
                    ]
                );

                UniLog.Log($"Adding range limitations {rangeLimits.x:F2} to {rangeLimits.y:F2} (display rounded)");
                fieldBuilder.SetCustomAttribute(attributeBuilder);
            }

            object? defaultValue = nativeProperty.IsScalar ? nativeProperty.DefaultValue.Value : null;
            if (defaultValue is not null)
            {
                var defaultValueConstructor = typeof(DefaultValue).GetConstructors()[0];
                var attributeBuilder = new CustomAttributeBuilder
                (
                    defaultValueConstructor,
                    [defaultValue]
                );

                UniLog.Log($"Adding default value {defaultValue}");
                fieldBuilder.SetCustomAttribute(attributeBuilder);
            }

            if (nativeProperty.Flags.HasFlag(ShaderPropertyFlags.Normal))
            {
                var normalMapConstructor = typeof(NormalMapAttribute).GetConstructors()[0];
                var attributeBuilder = new CustomAttributeBuilder
                (
                    normalMapConstructor,
                    []
                );

                UniLog.Log("Marking property as a normal map");
                fieldBuilder.SetCustomAttribute(attributeBuilder);
            }

            materialPropertyFields.Add(fieldBuilder);
        }

        return materialPropertyFields;
    }

    /// <summary>
    /// Defines and emits a constructor that initializes each material property field, along with storing them in the
    /// property members field and their material property names in the property names field.
    /// </summary>
    /// <param name="typeBuilder">The type to define the constructor in.</param>
    /// <param name="materialPropertyFields">The fields defined for the shader's material properties.</param>
    /// <param name="propertyMembersField">The field that holds a list of each material property field.</param>
    /// <param name="propertyNamesField">
    /// The field that holds a mapping of each material property's <see cref="Sync{T}"/> value to its material property
    /// name.
    /// </param>
    private static void EmitConstructor
    (
        this TypeBuilder typeBuilder,
        List<FieldBuilder> materialPropertyFields,
        FieldInfo propertyMembersField,
        FieldInfo propertyNamesField
    )
    {
        var constructorBuilder = typeBuilder.DefineConstructor
        (
            MethodAttributes.Public,
            CallingConventions.Standard,
            Type.EmptyTypes
        );

        var constructorIL = constructorBuilder.GetILGenerator();
        var syncMemberListLocal = constructorIL.DeclareLocal(typeof(List<ISyncMember>));
        var syncMemberNameMapLocal = constructorIL.DeclareLocal(typeof(Dictionary<ISyncMember, MaterialProperty>));

        // stack: <empty>
        constructorIL.EmitLoadArgument(0);

        // stack:
        //   this
        var baseConstructor = typeof(DynamicShader).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, []);
        constructorIL.EmitCallDirect(baseConstructor);

        // stack: <empty>
        constructorIL.EmitNewObject<List<ISyncMember>>();

        // stack:
        //   List<ISyncMember>
        constructorIL.EmitSetLocalVariable(syncMemberListLocal);

        // stack: <empty>
        constructorIL.EmitNewObject<Dictionary<ISyncMember, MaterialProperty>>();

        // stack:
        //   Dictionary<ISyncMember, MaterialProperty>
        constructorIL.EmitSetLocalVariable(syncMemberNameMapLocal);

        foreach (var syncField in materialPropertyFields)
        {
            var syncFieldConstructor = syncField.FieldType.GetConstructor([])!;

            // create syncField, store it in a local
            var local = constructorIL.DeclareLocal(syncField.FieldType);
            constructorIL.EmitNewObject(syncFieldConstructor);
            constructorIL.EmitSetLocalVariable(local);

            // stack: <empty>
            constructorIL.EmitLoadLocalVariable(syncMemberListLocal);

            // stack:
            //   List<ISyncMember>
            constructorIL.EmitLoadLocalVariable(local);

            // stack:
            //   List<ISyncMember>
            //   ISyncMember
            constructorIL.EmitCallVirtual<List<ISyncMember>>(nameof(List<ISyncMember>.Add));

            // stack: <empty>
            constructorIL.EmitLoadLocalVariable(syncMemberNameMapLocal);

            // stack:
            //   Dictionary<ISyncMember, MaterialProperty>
            constructorIL.EmitLoadLocalVariable(local);

            // stack:
            //   Dictionary<ISyncMember, MaterialProperty>
            //   ISyncMember
            constructorIL.EmitConstantString(syncField.Name);

            // stack:
            //   Dictionary<ISyncMember, MaterialProperty>
            //   ISyncMember
            //   string
            constructorIL.EmitNewObject<MaterialProperty>(typeof(string));

            // stack:
            //   Dictionary<ISyncMember, MaterialProperty>
            //   ISyncMember
            //   MaterialProperty
            constructorIL.EmitCallVirtual<Dictionary<ISyncMember, MaterialProperty>>(nameof(Dictionary<ISyncMember, MaterialProperty>.Add));

            // stack: <empty>
            constructorIL.EmitLoadArgument(0);

            // stack:
            //   this
            constructorIL.EmitLoadLocalVariable(local);

            // stack:
            //   this
            //   ISyncMember
            constructorIL.EmitSetField(syncField);
        }

        // stack: <empty>
        constructorIL.EmitLoadArgument(0);

        // stack:
        //   this
        constructorIL.EmitLoadLocalVariable(syncMemberListLocal);

        // stack:
        //   this
        //   List<ISyncMember>
        constructorIL.EmitSetField(propertyMembersField);

        // stack: <empty>
        constructorIL.EmitLoadArgument(0);

        // stack:
        //   this
        constructorIL.EmitLoadLocalVariable(syncMemberNameMapLocal);

        // stack:
        //   this
        //   Dictionary<ISyncMember, MaterialProperty>
        constructorIL.EmitSetField(propertyNamesField);

        // stack: <empty>
        constructorIL.EmitReturn();
    }

    /// <summary>
    /// Defines and emits an implementing method for <see cref="DynamicShader.GetMaterialPropertyMembers"/>, along with
    /// its corresponding backing field.
    /// </summary>
    /// <param name="typeBuilder">The type to emit the method in.</param>
    private static FieldBuilder EmitGetMaterialPropertyMembers(this TypeBuilder typeBuilder)
    {
        var propertyMembersField = typeBuilder.DefineField
        (
            "_materialProperties",
            typeof(IReadOnlyList<ISyncMember>),
            FieldAttributes.Private
        );

        var getMaterialPropertyMembers = typeBuilder.DefineMethod
        (
            nameof(DynamicShader.GetMaterialPropertyMembers),
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final,
            CallingConventions.Standard,
            typeof(IReadOnlyList<ISyncMember>),
            []
        );

        var getMaterialPropertyMembersIL = getMaterialPropertyMembers.GetILGenerator();
        getMaterialPropertyMembersIL.EmitLoadArgument(0);
        getMaterialPropertyMembersIL.EmitLoadField(propertyMembersField);
        getMaterialPropertyMembersIL.EmitReturn();

        typeBuilder.DefineMethodOverride
        (
            getMaterialPropertyMembers,
            typeof(DynamicShader).GetMethod(nameof(DynamicShader.GetMaterialPropertyMembers))!
        );

        return propertyMembersField;
    }

    /// <summary>
    /// Defines and emits an implementing method for <see cref="DynamicShader.GetMaterialPropertyNames"/>, along with
    /// its corresponding backing field.
    /// </summary>
    /// <param name="typeBuilder">The type to emit the method in.</param>
    private static FieldBuilder EmitGetMaterialPropertyNames(this TypeBuilder typeBuilder)
    {
        var propertyNamesField = typeBuilder.DefineField
        (
            "_materialPropertyNames",
            typeof(IReadOnlyDictionary<ISyncMember, MaterialProperty>),
            FieldAttributes.Private
        );

        var getMaterialPropertyNames = typeBuilder.DefineMethod
        (
            nameof(DynamicShader.GetMaterialPropertyNames),
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final,
            CallingConventions.Standard,
            typeof(IReadOnlyDictionary<ISyncMember, MaterialProperty>),
            []
        );

        var getMaterialPropertyNamesIL = getMaterialPropertyNames.GetILGenerator();
        getMaterialPropertyNamesIL.EmitLoadArgument(0);
        getMaterialPropertyNamesIL.EmitLoadField(propertyNamesField);
        getMaterialPropertyNamesIL.EmitReturn();

        typeBuilder.DefineMethodOverride
        (
            getMaterialPropertyNames,
            typeof(DynamicShader).GetMethod(nameof(DynamicShader.GetMaterialPropertyNames))!
        );

        return propertyNamesField;
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
