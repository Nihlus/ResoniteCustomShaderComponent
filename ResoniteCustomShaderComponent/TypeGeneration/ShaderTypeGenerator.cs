//
//  SPDX-FileName: ShaderTypeGenerator.cs
//  SPDX-FileCopyrightText: Copyright (c) Jarl Gullberg
//  SPDX-License-Identifier: AGPL-3.0-or-later
//

using System.Reflection;
using System.Reflection.Emit;
using Elements.Core;
using FrooxEngine;
using ResoniteCustomShaderComponent.Shaders;
using StrictEmit;
using UnityEngine.Rendering;
using Cubemap = FrooxEngine.Cubemap;
using Material = FrooxEngine.Material;
using RangeAttribute = FrooxEngine.RangeAttribute;
using Shader = UnityEngine.Shader;

namespace ResoniteCustomShaderComponent.TypeGeneration;

/// <summary>
/// Generates FrooxEngine material types based on unity shaders.
/// </summary>
internal static class ShaderTypeGenerator
{
    private const string _dynamicAssemblyName = "__customShaders";
    private const string _dynamicModuleName = "__customShaderModule";

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
    /// Gets the ABI version of generated shaders.
    /// </summary>
    public static Version GeneratedShaderVersion { get; } = new(1, 0, 0);

    /// <summary>
    /// Defines a dynamic shader type based on the given Unity shader.
    /// </summary>
    /// <param name="shaderCacheDirectory">The directory in which generated shaders are cached.</param>
    /// <param name="shaderHash">The Resonite cloud URI to the shader.</param>
    /// <param name="shader">The Unity shader that corresponds to the URI.</param>
    /// <returns>The shader type.</returns>
    public static Type DefineDynamicShaderType(string shaderCacheDirectory, string shaderHash, Shader shader)
    {
        UniLog.Log($"Creating new dynamic shader type for {shaderHash}");
        var dynamicAssemblyName = new AssemblyName(_dynamicAssemblyName)
        {
            Version = GeneratedShaderVersion
        };

        var dynamicAssembly = AppDomain.CurrentDomain.DefineDynamicAssembly
        (
            dynamicAssemblyName,
            AssemblyBuilderAccess.RunAndSave,
            shaderCacheDirectory
        );

        var cachedShaderFilename = $"{shaderHash}.dll";
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
        Directory.CreateDirectory(shaderCacheDirectory!);
        dynamicAssembly.Save(cachedShaderFilename);

        return type;
    }

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
}
