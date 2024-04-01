//
//  SPDX-FileName: CustomShader.cs
//  SPDX-FileCopyrightText: Copyright (c) Jarl Gullberg
//  SPDX-License-Identifier: AGPL-3.0-or-later
//

using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;
using Elements.Core;
using FrooxEngine;
using UnityEngine.Rendering;
using UnityFrooxEngineRunner;

#pragma warning disable SA1401

namespace ResoniteCustomShaderComponent;

/// <summary>
/// Represents a custom, dynamically loaded shader.
/// </summary>
[Category(["Assets/Materials"])]
public class CustomShader : Component
{
    private static readonly ModuleBuilder _dynamicModule;

    private static readonly ConcurrentDictionary<Uri, Type> _dynamicShaders = new();

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

    static CustomShader()
    {
        var dynamicAssembly = AppDomain.CurrentDomain.DefineDynamicAssembly
        (
            new AssemblyName("__customShaders"),
            AssemblyBuilderAccess.Run
        );

        _dynamicModule = dynamicAssembly.DefineDynamicModule
        (
            "__customShaderModule"
        );
    }

    /// <inheritdoc />
    protected override void OnInit()
    {
        base.OnInit();

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

        if (_dynamicShaders.TryGetValue(assetRef.Target.Asset!.AssetURL, out var existingShaderType))
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
                        var shaderType = _dynamicShaders.GetOrAdd
                        (
                            assetRef.Target.Asset.AssetURL,
                            _ => CreateDynamicShaderType(assetRef.Target!, loadShader.Result!)
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
        assetRef.Target.Asset.RequestVariant(0, (metadata, index, variant) =>
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

    private static Type CreateDynamicShaderType(IAssetProvider<Shader> metadataVariant, UnityEngine.Shader shader)
    {
        UniLog.Log($"Creating new dynamic shader type for {metadataVariant.Asset.AssetURL}");

        var dynamicTypeName = Path.GetFileNameWithoutExtension(metadataVariant.Asset.AssetURL.ToString());

        var typeBuilder = _dynamicModule.DefineType
        (
            dynamicTypeName,
            TypeAttributes.Class | TypeAttributes.Sealed,
            typeof(DynamicShader)
        );

        var materialPropertyFields = EmitDynamicMaterialPropertyFields(shader, typeBuilder);

        var propertyMembersField = typeBuilder.DefineField
        (
            "_materialProperties",
            typeof(IReadOnlyList<ISyncMember>),
            FieldAttributes.Private
        );

        var propertyMemberNamesField = typeBuilder.DefineField
        (
            "_materialPropertyNames",
            typeof(IReadOnlyDictionary<ISyncMember, MaterialProperty>),
            FieldAttributes.Private
        );

        EmitConstructor(typeBuilder, materialPropertyFields, propertyMembersField, propertyMemberNamesField);
        EmitGetMaterialPropertyMembers(typeBuilder, propertyMembersField);
        EmitGetMaterialPropertyNames(typeBuilder, propertyMemberNamesField);

        return typeBuilder.CreateType();
    }

    private static void EmitGetMaterialPropertyMembers(TypeBuilder typeBuilder, FieldInfo propertyMembersField)
    {
        var getMaterialPropertyMembers = typeBuilder.DefineMethod
        (
            nameof(DynamicShader.GetMaterialPropertyMembers),
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final,
            CallingConventions.Standard,
            typeof(IReadOnlyList<ISyncMember>),
            []
        );

        var getMaterialPropertyMembersIL = getMaterialPropertyMembers.GetILGenerator();
        getMaterialPropertyMembersIL.Emit(OpCodes.Ldarg_0);
        getMaterialPropertyMembersIL.Emit(OpCodes.Ldfld, propertyMembersField);
        getMaterialPropertyMembersIL.Emit(OpCodes.Ret);

        typeBuilder.DefineMethodOverride
        (
            getMaterialPropertyMembers,
            typeof(DynamicShader).GetMethod(nameof(DynamicShader.GetMaterialPropertyMembers))!
        );
    }

    private static void EmitGetMaterialPropertyNames(TypeBuilder typeBuilder, FieldInfo propertyNamesField)
    {
        var getMaterialPropertyNames = typeBuilder.DefineMethod
        (
            nameof(DynamicShader.GetMaterialPropertyNames),
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final,
            CallingConventions.Standard,
            typeof(IReadOnlyDictionary<ISyncMember, MaterialProperty>),
            []
        );

        var getMaterialPropertyNamesIL = getMaterialPropertyNames.GetILGenerator();
        getMaterialPropertyNamesIL.Emit(OpCodes.Ldarg_0);
        getMaterialPropertyNamesIL.Emit(OpCodes.Ldfld, propertyNamesField);
        getMaterialPropertyNamesIL.Emit(OpCodes.Ret);

        typeBuilder.DefineMethodOverride
        (
            getMaterialPropertyNames,
            typeof(DynamicShader).GetMethod(nameof(DynamicShader.GetMaterialPropertyNames))!
        );
    }

    private static void EmitConstructor
    (
        TypeBuilder typeBuilder,
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

        // stack: <empty>
        constructorIL.Emit(OpCodes.Ldarg_0);

        // stack:
        //   this
        var type = typeof(DynamicShader);
        constructorIL.Emit(OpCodes.Call, typeof(DynamicShader).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, [])!);

        var syncMemberListConstructor = typeof(List<ISyncMember>).GetConstructor([])!;
        var syncMemberListAdd = typeof(List<ISyncMember>).GetMethod
        (
            nameof(List<ISyncMember>.Add), [typeof(ISyncMember)]
        )!;

        var syncMemberNameMapConstructor = typeof(Dictionary<ISyncMember, MaterialProperty>).GetConstructor([])!;
        var syncMemberNameMapAdd = typeof(Dictionary<ISyncMember, MaterialProperty>).GetMethod
        (
            nameof(Dictionary<ISyncMember, MaterialProperty>.Add)
        )!;

        var syncMemberListLocal = constructorIL.DeclareLocal(typeof(List<ISyncMember>));
        constructorIL.Emit(OpCodes.Newobj, syncMemberListConstructor);
        constructorIL.Emit(OpCodes.Stloc, syncMemberListLocal);

        var syncMemberNameMapLocal = constructorIL.DeclareLocal(typeof(Dictionary<ISyncMember, MaterialProperty>));
        constructorIL.Emit(OpCodes.Newobj, syncMemberNameMapConstructor);
        constructorIL.Emit(OpCodes.Stloc, syncMemberNameMapLocal);

        var materialPropertyConstructor = typeof(MaterialProperty).GetConstructor([typeof(string)])!;

        foreach (var syncField in materialPropertyFields)
        {
            var syncFieldConstructor = syncField.FieldType.GetConstructor([])!;

            // create syncField, store it in a local
            var local = constructorIL.DeclareLocal(syncField.FieldType);
            constructorIL.Emit(OpCodes.Newobj, syncFieldConstructor);
            constructorIL.Emit(OpCodes.Stloc, local);

            // stack: <empty>
            constructorIL.Emit(OpCodes.Ldloc, syncMemberListLocal);

            // stack:
            //   List<ISyncMember>
            constructorIL.Emit(OpCodes.Ldloc, local);

            // stack:
            //   List<ISyncMember>
            //   ISyncMember
            constructorIL.Emit(OpCodes.Callvirt, syncMemberListAdd);

            // stack: <empty>
            constructorIL.Emit(OpCodes.Ldloc, syncMemberNameMapLocal);

            // stack:
            //   Dictionary<ISyncMember, MaterialProperty>
            constructorIL.Emit(OpCodes.Ldloc, local);

            // stack:
            //   Dictionary<ISyncMember, MaterialProperty>
            //   ISyncMember
            constructorIL.Emit(OpCodes.Ldstr, syncField.Name);

            // stack:
            //   Dictionary<ISyncMember, MaterialProperty>
            //   ISyncMember
            //   string
            constructorIL.Emit(OpCodes.Newobj, materialPropertyConstructor);

            // stack:
            //   Dictionary<ISyncMember, MaterialProperty>
            //   ISyncMember
            //   MaterialProperty
            constructorIL.Emit(OpCodes.Callvirt, syncMemberNameMapAdd);

            // stack: <empty>
            constructorIL.Emit(OpCodes.Ldarg_0);

            // stack:
            //   this
            constructorIL.Emit(OpCodes.Ldloc, local);

            // stack:
            //   this
            //   ISyncMember
            constructorIL.Emit(OpCodes.Stfld, syncField);
        }

        // stack: <empty>
        constructorIL.Emit(OpCodes.Ldarg_0);

        // stack:
        //   this
        constructorIL.Emit(OpCodes.Ldloc, syncMemberListLocal);

        // stack:
        //   this
        //   List<ISyncMember>
        constructorIL.Emit(OpCodes.Stfld, propertyMembersField);

        // stack: <empty>
        constructorIL.Emit(OpCodes.Ldarg_0);

        // stack:
        //   this
        constructorIL.Emit(OpCodes.Ldloc, syncMemberNameMapLocal);

        // stack:
        //   this
        //   Dictionary<ISyncMember, MaterialProperty>
        constructorIL.Emit(OpCodes.Stfld, propertyNamesField);

        // stack: <empty>
        constructorIL.Emit(OpCodes.Ret);
    }

    private static List<FieldBuilder> EmitDynamicMaterialPropertyFields(UnityEngine.Shader shader, TypeBuilder typeBuilder)
    {
        var materialPropertyFields = new List<FieldBuilder>();
        for (var i = 0; i < shader.GetPropertyCount(); ++i)
        {
            var propertyName = shader.GetPropertyName(i);
            var propertyType = shader.GetPropertyType(i);
            var propertyFlags = shader.GetPropertyFlags(i);

            if (propertyFlags.HasFlag(ShaderPropertyFlags.HideInInspector))
            {
                UniLog.Log($"Shader property \"{propertyName}\" is marked as HideInInspector - skipping");
                continue;
            }

            var propertyRuntimeType = propertyType switch
            {
                ShaderPropertyType.Color => typeof(colorX),
                ShaderPropertyType.Vector => typeof(float4),
                ShaderPropertyType.Float => typeof(float),
                ShaderPropertyType.Range => typeof(float),
                ShaderPropertyType.Texture => shader.GetPropertyTextureDimension(i) switch
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
                UniLog.Log($"Shader property \"{propertyName}\" has an unsupported type \"{propertyType}\" - skipping");
                continue;
            }

            UniLog.Log($"Adding shader property \"{propertyName}\" with shader type \"{propertyType}\" and runtime type \"{propertyRuntimeType}\"");

            var propertyRefType = propertyRuntimeType.IsValueType ? typeof(Sync<>) : typeof(AssetRef<>);

            var fieldBuilder = typeBuilder.DefineField
            (
                propertyName,
                propertyRefType.MakeGenericType(propertyRuntimeType),
                FieldAttributes.Public | FieldAttributes.InitOnly
            );

            if (propertyType is ShaderPropertyType.Range)
            {
                var rangeLimits = shader.GetPropertyRangeLimits(i);
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

            object? defaultValue = propertyType switch
            {
                ShaderPropertyType.Float or ShaderPropertyType.Range => shader.GetPropertyDefaultFloatValue(i),
                //ShaderPropertyType.Texture => shader.GetPropertyTextureDefaultName(i), TODO: map names to textures somewhere somehow
                _ => null
            };

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

            if (propertyFlags.HasFlag(ShaderPropertyFlags.Normal))
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
}
