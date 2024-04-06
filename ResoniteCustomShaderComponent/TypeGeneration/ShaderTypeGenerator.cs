//
//  SPDX-FileName: ShaderTypeGenerator.cs
//  SPDX-FileCopyrightText: Copyright (c) Jarl Gullberg
//  SPDX-License-Identifier: AGPL-3.0-or-later
//

using System.Reflection;
using System.Reflection.Emit;
using Elements.Core;
using FrooxEngine;
using ResoniteCustomShaderComponent.Extensions;
using ResoniteCustomShaderComponent.Shaders;
using ResoniteCustomShaderComponent.TypeGeneration.Properties;
using StrictEmit;
using UnityEngine;
using UnityEngine.Rendering;
using Material = FrooxEngine.Material;
using Rect = Elements.Core.Rect;
using Shader = UnityEngine.Shader;

namespace ResoniteCustomShaderComponent.TypeGeneration;

/// <summary>
/// Generates FrooxEngine material types based on unity shaders.
/// </summary>
internal static class ShaderTypeGenerator
{
    private const string _dynamicAssemblyName = "__customShaders";
    private const string _dynamicModuleName = "__customShaderModule";

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

        for (var i = 0; i < shader.passCount; ++i)
        {
            var tag = shader.FindPassTagValue(0, new ShaderTagId("RenderType"));
        }

        var propertyGroups = MaterialPropertyMapper.GetPropertyGroups(shader);

        typeBuilder.DefineDynamicMaterialPropertyFields
        (
            propertyGroups.SelectMany(p => p.GetManagedProperties())
        );

        var propertyMembersField = typeBuilder.EmitGetMaterialPropertyMembers();
        var propertyNamesField = typeBuilder.EmitGetMaterialPropertyNames();

        typeBuilder.EmitStaticConstructor
        (
            propertyNamesField,
            propertyGroups.SelectMany(p => p.GetNativeProperties())
        );

        typeBuilder.EmitConstructor
        (
            propertyMembersField,
            propertyGroups.SelectMany(p => p.GetManagedProperties())
        );

        typeBuilder.EmitInitializeSyncMemberDefaults(propertyGroups);
        typeBuilder.EmitUpdateMaterial(propertyGroups);

        var type = typeBuilder.CreateType();

        // Cache the generated shader
        Directory.CreateDirectory(shaderCacheDirectory!);
        dynamicAssembly.Save(cachedShaderFilename);

        return type;
    }

    /// <summary>
    /// Defines <see cref="Sync{T}"/> fields for each given material property.
    /// </summary>
    /// <param name="typeBuilder">The type builder to define the fields in.</param>
    /// <param name="managedProperties">The managed properties to define fields for..</param>
    private static void DefineDynamicMaterialPropertyFields
    (
        this TypeBuilder typeBuilder,
        IEnumerable<ManagedMaterialProperty> managedProperties
    )
    {
        foreach (var managedProperty in managedProperties)
        {
            var propertyRefType = managedProperty.Type.IsValueType ? typeof(Sync<>) : typeof(AssetRef<>);

            var fieldBuilder = typeBuilder.DefineField
            (
                managedProperty.Name,
                propertyRefType.MakeGenericType(managedProperty.Type),
                FieldAttributes.Public | FieldAttributes.InitOnly
            );

            foreach (var customAttribute in managedProperty.CustomAttributes)
            {
                fieldBuilder.SetCustomAttribute(customAttribute);
            }

            managedProperty.Field = fieldBuilder;
        }
    }

    private static void EmitInitializeSyncMemberDefaults
    (
        this TypeBuilder typeBuilder,
        IEnumerable<MaterialPropertyGroup> propertyGroups
    )
    {
        var initializeSyncMemberDefaults = typeBuilder.DefineMethod
        (
            "InitializeSyncMemberDefaults",
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final,
            CallingConventions.Standard,
            typeof(void),
            []
        );

        typeBuilder.DefineMethodOverride
        (
            initializeSyncMemberDefaults,
            typeof(MaterialProvider).GetMethod("InitializeSyncMemberDefaults", BindingFlags.Instance | BindingFlags.NonPublic)!
        );

        var initializeSyncMemberDefaultsIL = initializeSyncMemberDefaults.GetILGenerator();
        foreach (var propertyGroup in propertyGroups)
        {
            propertyGroup.EmitInitializeSyncMemberDefaults(initializeSyncMemberDefaultsIL);
        }

        // stack:
        //   <empty>
        initializeSyncMemberDefaultsIL.EmitReturn();
    }

    private static void EmitUpdateMaterial
    (
        this TypeBuilder typeBuilder,
        IEnumerable<MaterialPropertyGroup> propertyGroups
    )
    {
        var updateMaterial = typeBuilder.DefineMethod
        (
            "UpdateMaterial",
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final,
            CallingConventions.Standard,
            typeof(void),
            [typeof(Material)]
        );

        typeBuilder.DefineMethodOverride
        (
            updateMaterial,
            typeof(MaterialProvider).GetMethod("UpdateMaterial", BindingFlags.Instance | BindingFlags.NonPublic)!
        );

        var updateMaterialIL = updateMaterial.GetILGenerator();
        foreach (var propertyGroup in propertyGroups)
        {
            propertyGroup.EmitUpdateMaterial(updateMaterialIL);
        }

        // stack:
        //  <empty>
        updateMaterialIL.EmitReturn();
    }

    private static void EmitStaticConstructor
    (
        this TypeBuilder typeBuilder,
        FieldInfo propertyNamesField,
        IEnumerable<NativeMaterialProperty> nativeProperties
    )
    {
        var constructorBuilder = typeBuilder.DefineConstructor
        (
            MethodAttributes.Public | MethodAttributes.Static,
            CallingConventions.Standard,
            Type.EmptyTypes
        );

        var constructorIL = constructorBuilder.GetILGenerator();
        var propertyNamesLocal = constructorIL.DeclareLocal(typeof(List<MaterialProperty>));

        constructorIL.EmitNewObject<List<MaterialProperty>>();
        constructorIL.EmitSetLocalVariable(propertyNamesLocal);

        foreach (var nativeProperty in nativeProperties)
        {
            var fieldBuilder = typeBuilder.DefineField
            (
                nativeProperty.Name,
                typeof(MaterialProperty),
                FieldAttributes.Private | FieldAttributes.Static | FieldAttributes.InitOnly
            );

            var nameLocal = constructorIL.DeclareLocal(typeof(MaterialProperty));

            // stack:
            //   <empty>
            constructorIL.EmitConstantString(nativeProperty.Name);

            // stack:
            //   string
            constructorIL.EmitNewObject<MaterialProperty>(typeof(string));

            // stack:
            //   MaterialProperty
            constructorIL.EmitSetLocalVariable(nameLocal);

            // stack:
            //   <empty>
            constructorIL.EmitLoadLocalVariable(nameLocal);

            // stack:
            //   MaterialProperty
            constructorIL.EmitSetStaticField(fieldBuilder);

            // stack:
            //   <empty>
            constructorIL.EmitLoadLocalVariable(propertyNamesLocal);

            // stack:
            //   List<MaterialProperty>
            constructorIL.EmitLoadLocalVariable(nameLocal);

            // stack:
            //   List<MaterialProperty>
            //   MaterialProperty
            constructorIL.EmitCallVirtual<List<MaterialProperty>>(nameof(List<MaterialProperty>.Add));

            nativeProperty.PropertyNameField = fieldBuilder;
        }

        constructorIL.EmitLoadLocalVariable(propertyNamesLocal);
        constructorIL.EmitSetStaticField(propertyNamesField);

        constructorIL.EmitReturn();
    }

    /// <summary>
    /// Defines and emits a constructor that initializes each material property field, along with storing them in the
    /// property members field and their material property names in the property names field.
    /// </summary>
    /// <param name="typeBuilder">The type to define the constructor in.</param>
    /// <param name="propertyMembersField">The field that holds a list of each material property field.</param>
    /// <param name="managedProperties">The fields defined for the shader's material properties.</param>
    private static void EmitConstructor
    (
        this TypeBuilder typeBuilder,
        FieldInfo propertyMembersField,
        IEnumerable<ManagedMaterialProperty> managedProperties
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

        // stack: <empty>
        constructorIL.EmitLoadArgument(0);

        // stack:
        //   this
        var baseConstructor = typeof(DynamicShader).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, [])!;
        constructorIL.EmitCallDirect(baseConstructor);

        // stack: <empty>
        constructorIL.EmitNewObject<List<ISyncMember>>();

        // stack:
        //   List<ISyncMember>
        constructorIL.EmitSetLocalVariable(syncMemberListLocal);

        foreach (var property in managedProperties)
        {
            if (property.Field is null)
            {
                throw new InvalidOperationException();
            }

            var syncFieldConstructor = property.Field.FieldType.GetConstructor([])!;

            // create syncField, store it in a local
            var local = constructorIL.DeclareLocal(property.Field.FieldType);
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
            constructorIL.EmitLoadArgument(0);

            // stack:
            //   this
            constructorIL.EmitLoadLocalVariable(local);

            // stack:
            //   this
            //   ISyncMember
            constructorIL.EmitSetField(property.Field);
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

        typeBuilder.DefineMethodOverride
        (
            getMaterialPropertyMembers,
            typeof(DynamicShader).GetMethod(nameof(DynamicShader.GetMaterialPropertyMembers))!
        );

        var getMaterialPropertyMembersIL = getMaterialPropertyMembers.GetILGenerator();
        getMaterialPropertyMembersIL.EmitLoadArgument(0);
        getMaterialPropertyMembersIL.EmitLoadField(propertyMembersField);
        getMaterialPropertyMembersIL.EmitReturn();

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
            typeof(IReadOnlyList<MaterialProperty>),
            FieldAttributes.Private | FieldAttributes.Static | FieldAttributes.InitOnly
        );

        var getMaterialPropertyNames = typeBuilder.DefineMethod
        (
            nameof(DynamicShader.GetMaterialPropertyNames),
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final,
            CallingConventions.Standard,
            typeof(IReadOnlyList<MaterialProperty>),
            []
        );

        typeBuilder.DefineMethodOverride
        (
            getMaterialPropertyNames,
            typeof(DynamicShader).GetMethod(nameof(DynamicShader.GetMaterialPropertyNames))!
        );

        var getMaterialPropertyNamesIL = getMaterialPropertyNames.GetILGenerator();
        getMaterialPropertyNamesIL.EmitLoadArgument(0);
        getMaterialPropertyNamesIL.EmitLoadField(propertyNamesField);
        getMaterialPropertyNamesIL.EmitReturn();

        return propertyNamesField;
    }
}
