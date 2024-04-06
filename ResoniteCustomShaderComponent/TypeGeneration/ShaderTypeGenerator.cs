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
            switch (propertyGroup)
            {
                case SimpleMaterialPropertyGroup simpleGroup:
                {
                    if (!simpleGroup.Native.HasDefaultValue)
                    {
                        continue;
                    }

                    if (simpleGroup.Native.IsTexture)
                    {
                        // texture defaults are provided non-statically
                        continue;
                    }

                    if (simpleGroup.Property.Field is null)
                    {
                        throw new InvalidOperationException();
                    }

                    // stack:
                    //   <empty>
                    initializeSyncMemberDefaultsIL.EmitLoadArgument(0);

                    // stack:
                    //   this
                    initializeSyncMemberDefaultsIL.EmitLoadField(simpleGroup.Property.Field);

                    // stack:
                    //   ISyncMember
                    initializeSyncMemberDefaultsIL.EmitInlineDefault(simpleGroup.Property.Type, simpleGroup.Native);

                    // stack:
                    //   ISyncMember
                    //   T
                    initializeSyncMemberDefaultsIL.EmitSetProperty
                    (
                        simpleGroup.Property.Field.FieldType,
                        simpleGroup.Property.Type.IsValueType ? "Value" : "Target"
                    );

                    break;
                }
            }
        }

        // stack:
        //   <empty>
        initializeSyncMemberDefaultsIL.EmitReturn();
    }

    private static void EmitInlineDefault
    (
        this ILGenerator generator,
        Type targetType,
        NativeMaterialProperty nativeProperty
    )
    {
        if (targetType.IsScalar() && nativeProperty.IsScalar)
        {
            generator.EmitDefaultScalar(targetType, nativeProperty.DefaultValue.Value);
        }
        else if (nativeProperty.IsVector)
        {
            generator.EmitDefaultVectorLike(targetType, nativeProperty.DefaultVector.Value);
        }
        else if (targetType.IsEnum && nativeProperty.IsScalar)
        {
            generator.EmitDefaultEnum(targetType, nativeProperty.DefaultValue.Value);
        }
        else
        {
            throw new InvalidOperationException();
        }
    }

    private static void EmitDefaultEnum(this ILGenerator generator, Type targetType, float defaultValue)
    {
        var backingType = targetType.GetEnumUnderlyingType();
        if (backingType == typeof(long) || backingType == typeof(ulong))
        {
            generator.EmitConstantLong((long)defaultValue);
        }
        else
        {
            generator.EmitConstantInt((int)defaultValue);
        }
    }

    private static void EmitDefaultVectorLike(this ILGenerator generator, Type targetType, Vector4 defaultValue)
    {
        var elementCount = targetType switch
        {
            _ when targetType == typeof(float2) => 2,
            _ when targetType == typeof(float3) => 3,
            _ when targetType == typeof(float4) => 4,
            _ when targetType == typeof(colorX) => 4,
            _ when targetType == typeof(Rect) => 4,
            _ => throw new ArgumentOutOfRangeException(nameof(targetType))
        };

        var elementConstructor = targetType switch
        {
            _ when targetType == typeof(float2) => typeof(float2).GetConstructor([typeof(float), typeof(float)])!,
            _ when targetType == typeof(float3) => typeof(float3).GetConstructor([typeof(float), typeof(float), typeof(float)])!,
            _ when targetType == typeof(float4) => typeof(float4).GetConstructor([typeof(float), typeof(float), typeof(float), typeof(float)])!,
            _ when targetType == typeof(colorX) => typeof(colorX).GetConstructor([typeof(float), typeof(float), typeof(float), typeof(float), typeof(ColorProfile)])!,
            _ when targetType == typeof(Rect) => typeof(Rect).GetConstructor([typeof(float), typeof(float), typeof(float), typeof(float)])!,
            _ => throw new ArgumentOutOfRangeException(nameof(targetType))
        };

        for (var i = 0; i < elementCount; i++)
        {
            generator.EmitConstantFloat(defaultValue[i]);
        }

        if (targetType == typeof(colorX))
        {
            generator.EmitConstantInt((int)ColorProfile.sRGB);
        }

        generator.EmitNewObject(elementConstructor);
    }

    private static void EmitDefaultTexture(this ILGenerator generator, NativeMaterialProperty nativeProperty)
    {
        if (nativeProperty.DefaultTextureName is null)
        {
            throw new ArgumentNullException(nameof(nativeProperty.DefaultTextureName));
        }

        generator.EmitGetProperty<Engine>(nameof(Engine.Current), BindingFlags.Static | BindingFlags.Public);
        generator.EmitGetProperty<Engine>(nameof(Engine.AssetManager));
        switch (nativeProperty.DefaultTextureName.ToLowerInvariant())
        {
            case "white":
            {
                generator.EmitGetProperty<AssetManager>(nameof(AssetManager.WhiteTexture));
                break;
            }
            case "black":
            {
                generator.EmitGetProperty<AssetManager>(nameof(AssetManager.BlackTexture));
                break;
            }
            case "clear":
            {
                generator.EmitGetProperty<AssetManager>(nameof(AssetManager.ClearTexture));
                break;
            }
            case "darkchecker":
            {
                generator.EmitGetProperty<AssetManager>(nameof(AssetManager.DarkCheckerTexture));
                break;
            }
            default:
            {
                generator.EmitLoadNull();
                break;
            }
        }
    }

    private static void EmitDefaultCubemap(this ILGenerator generator, NativeMaterialProperty nativeProperty)
    {
        if (nativeProperty.DefaultTextureName is null)
        {
            throw new ArgumentNullException(nameof(nativeProperty.DefaultTextureName));
        }

        generator.EmitGetProperty<Engine>(nameof(Engine.Current), BindingFlags.Static | BindingFlags.Public);
        generator.EmitGetProperty<Engine>(nameof(Engine.AssetManager));
        switch (nativeProperty.DefaultTextureName.ToLowerInvariant())
        {
            case "darkchecker":
            {
                generator.EmitGetProperty<AssetManager>(nameof(AssetManager.DarkCheckerCubemap));
                break;
            }
            default:
            {
                generator.EmitLoadNull();
                break;
            }
        }
    }

    private static void EmitDefaultScalar(this ILGenerator generator, Type targetType, float defaultValue)
    {
        switch (targetType)
        {
            case var _ when targetType == typeof(sbyte):
            {
                generator.EmitConstantInt((int)defaultValue);
                generator.EmitConvertToByte();
                break;
            }
            case var _ when targetType == typeof(byte):
            {
                generator.EmitConstantInt((int)defaultValue);
                generator.EmitConvertToUByte();
                break;
            }
            case var _ when targetType == typeof(ushort):
            {
                generator.EmitConstantInt((int)defaultValue);
                generator.EmitConvertToUShort();
                break;
            }
            case var _ when targetType == typeof(short):
            {
                generator.EmitConstantInt((int)defaultValue);
                generator.EmitConvertToShort();
                break;
            }
            case var _ when targetType == typeof(uint):
            {
                generator.EmitConstantInt((int)defaultValue);
                generator.EmitConvertToUInt();
                break;
            }
            case var _ when targetType == typeof(int):
            {
                generator.EmitConstantInt((int)defaultValue);
                break;
            }
            case var _ when targetType == typeof(ulong):
            {
                generator.EmitConstantLong((long)defaultValue);
                generator.EmitConvertToULong();
                break;
            }
            case var _ when targetType == typeof(long):
            {
                generator.EmitConstantLong((long)defaultValue);
                break;
            }
            case var _ when targetType == typeof(float):
            {
                generator.EmitConstantFloat(defaultValue);
                break;
            }
            case var _ when targetType == typeof(double):
            {
                generator.EmitConstantDouble(defaultValue);
                break;
            }
            case var _ when targetType == typeof(decimal):
            {
                generator.EmitConstantFloat(defaultValue);
                generator.EmitCallDirect<decimal>("op_Implicit", typeof(float));
                break;
            }
            default:
            {
                throw new ArgumentOutOfRangeException(nameof(targetType));
            }
        }
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

        var implicitConversion = typeof(MaterialProperty)
            .GetMethods()
            .Single(m => m.Name is "op_Implicit" && m.ReturnType == typeof(int));

        var updateMaterialIL = updateMaterial.GetILGenerator();
        foreach (var propertyGroup in propertyGroups)
        {
            switch (propertyGroup)
            {
                case SimpleMaterialPropertyGroup simpleGroup:
                {
                    // material.UpdateXXX(property, field);
                    if (simpleGroup.Native.IsTexture)
                    {
                        EmitTextureUpdateCall
                        (
                            updateMaterialIL,
                            implicitConversion,
                            simpleGroup
                        );
                    }
                    else
                    {
                        EmitSimpleUpdateCall
                        (
                            updateMaterialIL,
                            implicitConversion,
                            simpleGroup
                        );
                    }

                    break;
                }
            }
        }

        // stack:
        //  <empty>
        updateMaterialIL.EmitReturn();
    }

    private static void EmitTextureUpdateCall
    (
        ILGenerator updateMaterialIL,
        MethodInfo implicitConversion,
        SimpleMaterialPropertyGroup simpleGroup
    )
    {
        if (simpleGroup.Property.Field is null || simpleGroup.Native.PropertyNameField is null)
        {
            throw new InvalidOperationException();
        }

        // stack:
        //  <empty>
        updateMaterialIL.EmitLoadArgument(1);

        // stack:
        //  Material
        updateMaterialIL.EmitLoadStaticField(simpleGroup.Native.PropertyNameField);

        // stack:
        //  Material
        //  MaterialProperty
        updateMaterialIL.EmitCallDirect(implicitConversion);

        // stack:
        //  Material
        //  int
        updateMaterialIL.EmitLoadArgument(0);

        // stack:
        //  Material
        //  int
        //  this
        updateMaterialIL.EmitLoadField(simpleGroup.Property.Field);

        // stack:
        //  Material
        //  int
        //  ISyncMember
        if (simpleGroup.Native.TextureDimension is TextureDimension.Cube)
        {
            updateMaterialIL.EmitDefaultCubemap(simpleGroup.Native);
        }
        else
        {
            updateMaterialIL.EmitDefaultTexture(simpleGroup.Native);
        }

        // stack:
        //  Material
        //  int
        //  ISyncMember
        //  ITexture2D? | Cubemap?
        updateMaterialIL.EmitCallVirtual(MaterialPropertyMapper.GetMaterialPropertyUpdateMethod(simpleGroup));
    }

    private static void EmitSimpleUpdateCall
    (
        ILGenerator updateMaterialIL,
        MethodInfo implicitConversion,
        SimpleMaterialPropertyGroup simpleGroup
    )
    {
        if (simpleGroup.Property.Field is null || simpleGroup.Native.PropertyNameField is null)
        {
            throw new InvalidOperationException();
        }

        // stack:
        //  <empty>
        updateMaterialIL.EmitLoadArgument(1);

        // stack:
        //  <empty>
        updateMaterialIL.EmitLoadArgument(1);

        // stack:
        //  Material
        updateMaterialIL.EmitLoadStaticField(simpleGroup.Native.PropertyNameField);

        // stack:
        //  Material
        //  MaterialProperty
        updateMaterialIL.EmitCallDirect(implicitConversion);

        // stack:
        //  Material
        //  MaterialProperty
        updateMaterialIL.EmitCallDirect(implicitConversion);

        // stack:
        //  Material
        //  int
        updateMaterialIL.EmitLoadArgument(0);

        // stack:
        //  Material
        //  int
        //  this
        updateMaterialIL.EmitLoadField(simpleGroup.Property.Field);

        // stack:
        //  Material
        //  int
        //  ISyncMember
        updateMaterialIL.EmitCallVirtual(MaterialPropertyMapper.GetMaterialPropertyUpdateMethod(simpleGroup));
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
